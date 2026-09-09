using DryCycle.Debugging.AI;

namespace DryCycle.Creatures.DesertBatfly;

// Only adapt nonvirtual vanilla entry points here. All species decisions live
// in its Creature, AI, Graphics, State or domain runtime classes.
internal static class DB_RainWorldHooks
{
    private static bool enabled;
    private static bool debugRegistered;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_PerformanceProbe.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DB_FlightMotor.Reset();
        DB_FearRuntime.Reset();
        DB_RefugePolicy.Reset();
        DB_SocialRuntime.Reset();
        DB_SwarmLifecycleRuntime.Reset();
        DB_SignalRuntime.Reset();
        DB_EnvironmentRoomRuntime.Reset();
        DB_EnvironmentRuntime.Reset();
        DB_FeedingCoordinator.Reset();
        DB_DehydrationGripRuntime.Enable();
        DB_EventHub.Enable();
        DB_EventConsumers.Enable();
        DB_ThreatRuntime.Enable();
        DB_ColonyRuntime.Enable();
        if (!debugRegistered)
        {
            AIDebugRegistry.Register(new DB_EnvironmentDebugSource());
            debugRegistered = true;
        }
        On.Fly.ReportToFliesRoomAI += Report;
        On.FliesRoomAI.FlyEmergeFromHive += Emerge;
        On.Fly.Burrowed += Burrow;
        On.FlyAI.Update += UpdateAI;
        On.FlyAI.UpdateThreats += Threats;
        On.FlyAI.IdleUpdate += Idle;
        On.FlyAI.SwarmUpdate += Swarm;
        On.FlyAI.UpdateFollowDijsktra += Follow;
        On.Room.Update += UpdateRoom;
        On.SlugcatStats.NourishmentOfObjectEaten += Nourishment;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.Fly.ReportToFliesRoomAI -= Report;
        On.FliesRoomAI.FlyEmergeFromHive -= Emerge;
        On.Fly.Burrowed -= Burrow;
        On.FlyAI.Update -= UpdateAI;
        On.FlyAI.UpdateThreats -= Threats;
        On.FlyAI.IdleUpdate -= Idle;
        On.FlyAI.SwarmUpdate -= Swarm;
        On.FlyAI.UpdateFollowDijsktra -= Follow;
        On.Room.Update -= UpdateRoom;
        On.SlugcatStats.NourishmentOfObjectEaten -= Nourishment;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        DB_DehydrationGripRuntime.Disable();
        DB_EventConsumers.Disable();
        DB_EventHub.Disable();
        DB_ThreatRuntime.Disable();
        DB_SignalRuntime.Reset();
        DB_EnvironmentRuntime.Reset();
        DB_EnvironmentRoomRuntime.Reset();
        DB_FeedingCoordinator.Reset();
        DB_SocialRuntime.Reset();
        DB_SwarmLifecycleRuntime.Reset();
        DB_ColonyRuntime.Disable();
        DB_RefugePolicy.Reset();
        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_PerformanceProbe.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DB_FlightMotor.Reset();
        DB_FearRuntime.Reset();
        DB_WarpCompatibility.Disable();
        DB_Sandbox.Disable();
        DB_SwarmRoom.Reset();
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        DB_Sandbox.Enable();
        DB_WarpCompatibility.Enable();
    }

    private static void Report(On.Fly.orig_ReportToFliesRoomAI orig, Fly self, Room room)
    {
        if (self is DB_Creature desert)
        {
            if (room?.world != null)
            {
                DB_ColonyRuntime.EnsureWorld(room.world);
                DB_ColonyRuntime.EnsureIndividualOwnership(desert.abstractCreature);
            }
            DB_SwarmRoom.For(room).Hive.AddFly(self);
        }
        else
        {
            orig(self, room);
        }
    }

    private static void Burrow(On.Fly.orig_Burrowed orig, Fly self)
    {
        if (self is DB_Creature desert)
        {
            desert.Feeding.ClearTransient();
            DB_SocialRuntime.CancelForPriority(desert, "burrow priority");
            DB_SwarmLifecycleRuntime.Forget(desert);
            DB_SignalRuntime.Forget(desert);
            desert.DesertState.InHive = true;
        }
        orig(self);
    }

    private static void Emerge(On.FliesRoomAI.orig_FlyEmergeFromHive orig, FliesRoomAI self, Fly fly)
    {
        if (fly is not DB_Creature desert)
        {
            orig(self, fly);
            return;
        }

        desert.Feeding.ClearTransient();
        DB_SocialRuntime.CancelForPriority(desert, "emergence priority");
        DB_SwarmLifecycleRuntime.Forget(desert);
        DB_SignalRuntime.Forget(desert);
        DB_EnvironmentRuntime.Forget(desert);
        desert.DesertState.InHive = false;
        try { orig(self, fly); }
        finally { desert.DesertState.InHive = self.inHive.Contains(fly); }
    }

    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)
    {
        if (self.fly is not DB_Creature desert)
        {
            orig(self);
            return;
        }

        // R3 order is deliberate: refresh state/facts first, resolve one owner, then execute.
        // Vanilla FlyAI.Update is no longer allowed to write an ordinary goal before Arbiter.
        DB_EnvironmentRuntime.RefreshInfluence(desert);
        desert.DesertAI.RefreshDecisionState();
        DB_ThreatRuntime.RefreshState(desert);
        DB_SocialRuntime.RefreshState(desert);
        desert.Feeding.RefreshState();

        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);

        if (desert.Feeding.Attached && ownership.PrimaryOwner != DB_BehaviorOwner.Feeding)
        {
            desert.Feeding.YieldAttachmentForHigherPriority();
            ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        }

        if (ownership.PrimaryOwner is DB_BehaviorOwner.CreaturePhysics or
            DB_BehaviorOwner.Restraint or DB_BehaviorOwner.Shortcut or DB_BehaviorOwner.Emergence)
        {
            DB_SocialRuntime.CancelForPriority(desert, PrimaryOwnerBlockReason(ownership.PrimaryOwner));
            CompleteR3Frame(desert, ownership);
            return;
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.NativeSpecial &&
            ExecuteNativeOwned(orig, self, desert, ownership))
        {
            CompleteR3Frame(desert, ownership);
            return;
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.ImmediateDanger)
        {
            if (DB_BehaviorExecution.TryImmediateDanger(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.ImmediateDanger,
                "ImmediateDanger executor yielded after current danger recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.InjuryRecovery)
        {
            if (DB_BehaviorExecution.TryInjuryRecovery(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.InjuryRecovery,
                "InjuryRecovery executor yielded after current physical recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            if (DB_TravelRuntime.TryDriveRealized(desert))
            {
                DB_SocialRuntime.CancelForPriority(desert, "travel priority");
                desert.DesertAI.CancelAttack();
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Travel,
                "Travel executor yielded after preflight / route validation");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.EnvironmentHardSurvival)
        {
            if (DB_BehaviorExecution.TryEnvironment(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.EnvironmentHardSurvival,
                "HardSurvival executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.FearResponse)
        {
            if (DB_BehaviorExecution.TryFear(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.FearResponse,
                "Fear executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Vengeance)
        {
            if (DB_BehaviorExecution.TryVengeance(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Vengeance,
                "Vengeance executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.EnvironmentLocalSurvival)
        {
            if (DB_BehaviorExecution.TryEnvironment(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.EnvironmentLocalSurvival,
                "Environment executor yielded after current state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.ImmediateProjectileEvade)
        {
            if (DB_BehaviorExecution.TryProjectileEvade(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.ImmediateProjectileEvade,
                "Projectile evade executor yielded after current projectile recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Feeding)
        {
            if (DB_BehaviorExecution.TryFeeding(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Feeding,
                "Feeding executor yielded after target/slot recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Combat)
        {
            if (DB_BehaviorExecution.TryCombat(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Combat,
                "Combat executor yielded after target/state recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Roost)
        {
            if (DB_BehaviorExecution.TryRoost(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Roost,
                "Roost executor yielded after chain/roost recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Social)
        {
            if (DB_BehaviorExecution.TrySocial(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.Social,
                "Social executor yielded after reservation/state recheck");
        }

        if (ExecuteNativeOwned(orig, self, desert, ownership))
        {
            CompleteR3Frame(desert, ownership);
            return;
        }

        // Defensive fallback only; record the actual fallback owner rather than executing an
        // unowned second controller.
        ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner is DB_BehaviorOwner.Ordinary or DB_BehaviorOwner.VanillaFallback)
            ExecuteNativeOwned(orig, self, desert, ownership);
        CompleteR3Frame(desert, ownership);
    }

    private static string PrimaryOwnerBlockReason(DB_BehaviorOwner owner)
    {
        return owner switch
        {
            DB_BehaviorOwner.CreaturePhysics => "creature physics priority",
            DB_BehaviorOwner.Restraint => "restraint priority",
            DB_BehaviorOwner.Shortcut => "shortcut priority",
            DB_BehaviorOwner.Emergence => "emergence priority",
            _ => "higher-priority owner"
        };
    }

    private static bool ExecuteNativeOwned(
        On.FlyAI.orig_Update orig,
        FlyAI self,
        DB_Creature desert,
        in DB_BehaviorResolution ownership)
    {
        if (orig == null || self == null || desert == null) return false;
        DB_BehaviorOwner owner = ownership.PrimaryOwner;
        if (owner is not (DB_BehaviorOwner.NativeSpecial or DB_BehaviorOwner.Ordinary or
            DB_BehaviorOwner.VanillaFallback))
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(desert, owner)) return false;

        // NativeSpecial already declares its social suppression in the winning proposal.
        // Keep that semantic at the accepted top-level owner instead of re-hooking nested
        // FleeFromRainUpdate solely to cancel Social a second time.
        if (ownership.WinningProposal.SuppressSocial)
            DB_SocialRuntime.CancelForPriority(desert, "R3 PrimaryOwner=" + owner);

        orig(self);
        return true;
    }

    private static void CompleteR3Frame(
        DB_Creature desert,
        in DB_BehaviorResolution ownership)
    {
        if (desert == null) return;
        DB_ThreatRuntime.CommitFrame(desert);
        DB_ThreatTrace.Sample(desert);
        DB_SignalRuntime.Update(desert);
        DB_SocialRuntime.SampleTrace(desert);
        DB_Trace.Sample(desert);
        if (desert.abstractCreature != null && AIDebugTrace.IsWatched(desert.abstractCreature))
            AIDebugTrace.RecordChange(
                desert.abstractCreature,
                AIDebugEventCategory.Decision,
                "PrimaryOwner",
                ownership.PrimaryOwner,
                ownership.Reason);
    }

    private static void Threats(On.FlyAI.orig_UpdateThreats orig, FlyAI self)
    {
        if (self.fly is not DB_Creature) orig(self);
    }

    private static void Idle(On.FlyAI.orig_IdleUpdate orig, FlyAI self)
    {
        orig(self);
        if (self.fly is DB_Creature desert)
            DB_SwarmLifecycleRuntime.AfterNativeIdleUpdate(self, desert);
    }

    private static void Swarm(On.FlyAI.orig_SwarmUpdate orig, FlyAI self)
    {
        orig(self);
        if (self.fly is DB_Creature desert)
            DB_SwarmLifecycleRuntime.AfterNativeSwarmUpdate(self, desert);
    }

    private static void Follow(On.FlyAI.orig_UpdateFollowDijsktra orig, FlyAI self)
    {
        if (self.fly is DB_Creature desert &&
            DB_SwarmRoom.TryHandleNativeFollowDijkstra(self, desert))
            return;
        orig(self);
    }

    private static void UpdateRoom(On.Room.orig_Update orig, Room self)
    {
        orig(self);

        // DB_SwarmRoom still owns room-tag/hive spawning semantics and is intentionally
        // not gated by realized bats. The remaining systems are DesertBatfly-only and must
        // not allocate/scan ordinary rooms that never activated DB_RoomContext.
        DB_SwarmRoom.UpdateRoom(self, self.game.evenUpdate);
        if (!DB_RoomContext.TryGetExisting(self, out DB_RoomContext context) ||
            context.Bats.Count == 0)
            return;

        if (self.readyForAI && self.aimap != null)
            DB_RefugePolicy.ObserveRoom(self);
        DB_SignalRoomRuntime.For(self)?.Prune(self);
        DB_EnvironmentRoomRuntime.Update(self);
        DB_FeedingCoordinator.UpdateRoom(self);
    }

    private static int Nourishment(
        On.SlugcatStats.orig_NourishmentOfObjectEaten orig,
        SlugcatStats.Name name,
        IPlayerEdible edible)
    {
        int value = orig(name, edible);
        if (edible is not DB_Creature || value <= 0) return value;
        return value < edible.FoodPoints * 4 ? 4 : 8;
    }
}
