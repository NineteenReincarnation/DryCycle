using DryCycle.Debugging.AI;
using UnityEngine;

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
        On.Fly.NewRoom += FlyNewRoom;
        On.Fly.Grabbed += FlyGrabbed;
        On.FliesRoomAI.FlyEmergeFromHive += Emerge;
        On.Fly.Burrowed += Burrow;
        On.FlyAI.Update += UpdateAI;
        On.FlyAI.UpdateThreats += Threats;
        On.FlyAI.IdleUpdate += Idle;
        On.FlyAI.UpdateFollowDijsktra += Follow;
        On.FlyAI.FleeFromRainUpdate += Rain;
        On.Room.Update += UpdateRoom;
        On.SlugcatStats.NourishmentOfObjectEaten += Nourishment;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.Fly.ReportToFliesRoomAI -= Report;
        On.Fly.NewRoom -= FlyNewRoom;
        On.Fly.Grabbed -= FlyGrabbed;
        On.FliesRoomAI.FlyEmergeFromHive -= Emerge;
        On.Fly.Burrowed -= Burrow;
        On.FlyAI.Update -= UpdateAI;
        On.FlyAI.UpdateThreats -= Threats;
        On.FlyAI.IdleUpdate -= Idle;
        On.FlyAI.UpdateFollowDijsktra -= Follow;
        On.FlyAI.FleeFromRainUpdate -= Rain;
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

    private static void FlyNewRoom(On.Fly.orig_NewRoom orig, Fly self, Room room)
    {
        if (self is DB_Creature desert)
        {
            DB_SocialRuntime.CancelForPriority(desert, "room transition");
            DB_SignalRuntime.Forget(desert);
            DB_ThreatRuntime.Forget(desert);
            DB_EnvironmentRuntime.Forget(desert);
            DB_FrameContextRuntime.Forget(desert);
            DB_BehaviorArbiter.Forget(desert);
            DB_FlightMotor.Forget(desert);
        }
        orig(self, room);
    }

    private static void FlyGrabbed(On.Fly.orig_Grabbed orig, Fly self, Creature.Grasp grasp)
    {
        if (self is DB_Creature desert)
            DB_SocialRuntime.CancelForPriority(desert, "grabbed / restraint");
        orig(self, grasp);
    }

    private static void Burrow(On.Fly.orig_Burrowed orig, Fly self)
    {
        if (self is DB_Creature desert)
        {
            desert.Feeding.ClearTransient();
            DB_SocialRuntime.CancelForPriority(desert, "burrow priority");
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

    private static bool RestrainedByNonFly(DB_Creature fly)
    {
        for (int i = 0; i < fly.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = fly.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly) return true;
        }
        return false;
    }

    private static void Threats(On.FlyAI.orig_UpdateThreats orig, FlyAI self)
    {
        if (self.fly is not DB_Creature) orig(self);
    }

    private static void Idle(On.FlyAI.orig_IdleUpdate orig, FlyAI self)
    {
        orig(self);
        if (self.fly is not DB_Creature desert) return;

        DB_SocialRoomRuntime.RoomState socialRoom =
            DB_SocialRoomRuntime.For(self.room);
        if (socialRoom?.IsReserved(desert) == true)
            return;

        if (!DB_SwarmRoom.IsDB_SwarmRoom(self.room.abstractRoom))
        {
            if (self.behavior == FlyAI.Behavior.Swarm) self.ChangeBehavior(FlyAI.Behavior.Idle);
            return;
        }
        if (self.behavior == FlyAI.Behavior.Idle && !self.fleeFromRain && self.ValidSwarmPosition(self.localGoal))
            self.ChangeBehavior(FlyAI.Behavior.Swarm);
    }

    private static void Rain(On.FlyAI.orig_FleeFromRainUpdate orig, FlyAI self)
    {
        if (self.fly is not DB_Creature desert)
        {
            orig(self);
            return;
        }

        // Do not execute Travel from this nested vanilla callback. If Travel wins, suppress
        // native rain steering here and let the enclosing UpdateAI execute Travel exactly once.
        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            DB_SocialRuntime.CancelForPriority(desert, "travel owns enclosing AI frame");
            return;
        }

        DB_SocialRuntime.CancelForPriority(desert, "rain priority");
        if (self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        self.afraid = Mathf.Max(self.afraid, 2f);
    }

    private static void Follow(On.FlyAI.orig_UpdateFollowDijsktra orig, FlyAI self)
    {
        if (self.fly is not DB_Creature ||
            !DB_SwarmRoom.IsDB_SwarmRoom(self.room.abstractRoom) ||
            self.room.hives.Length == 0)
        {
            orig(self);
            return;
        }
        if (self.followingDijkstraMap < 0)
            self.followingDijkstraMap = self.room.exitAndDenIndex.Length + UnityEngine.Random.Range(0, self.room.hives.Length);
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
