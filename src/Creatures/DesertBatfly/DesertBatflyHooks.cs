using DryCycle.Debugging.AI;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Only bridge nonvirtual vanilla entry points here. All species decisions live
// in its Creature, AI, Graphics, State or domain runtime classes.
internal static class DesertBatflyHooks
{
    private static bool enabled;
    private static bool debugRegistered;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DB_FlightMotor.Reset();
        DesertBatflyIntimidation.Reset();
        DesertBatflyRefuge.Reset();
        DesertBatflySocialLife.Reset();
        DesertBatflySignalRuntime.Reset();
        DesertBatflyEnvironmentalRoomRuntime.Reset();
        DesertBatflyEnvironmentalBehavior.Reset();
        DB_EventHub.Enable();
        DB_EventConsumers.Enable();
        DesertBatflyThreatRuntime.Enable();
        DesertBatflyColonyRuntime.Enable();
        DesertBatflyPlatformRoostRuntime.Enable();
        if (!debugRegistered)
        {
            AIDebugRegistry.Register(new DesertBatflyTask13DebugSource());
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
        DB_EventConsumers.Disable();
        DB_EventHub.Disable();
        DesertBatflyThreatRuntime.Disable();
        DesertBatflySignalRuntime.Reset();
        DesertBatflyEnvironmentalBehavior.Reset();
        DesertBatflyEnvironmentalRoomRuntime.Reset();
        DesertBatflySocialLife.Reset();
        DesertBatflyPlatformRoostRuntime.Disable();
        DesertBatflyColonyRuntime.Disable();
        DesertBatflyRefuge.Reset();
        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DB_FlightMotor.Reset();
        DesertBatflyIntimidation.Reset();
        DesertBatflyWarpCompatibility.Disable();
        DesertBatflySandbox.Disable();
        DesertSwarmRoom.Reset();
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        DesertBatflySandbox.Enable();
        DesertBatflyWarpCompatibility.Enable();
    }

    private static void Report(On.Fly.orig_ReportToFliesRoomAI orig, Fly self, Room room)
    {
        if (self is DesertBatfly desert)
        {
            if (room?.world != null)
            {
                DesertBatflyColonyRuntime.EnsureWorld(room.world);
                DesertBatflyColonyRuntime.EnsureIndividualOwnership(desert.abstractCreature);
            }
            DesertSwarmRoom.For(room).Hive.AddFly(self);
        }
        else
        {
            orig(self, room);
        }
    }

    private static void FlyNewRoom(On.Fly.orig_NewRoom orig, Fly self, Room room)
    {
        if (self is DesertBatfly desert)
        {
            DesertBatflySocialLife.CancelForPriority(desert, "room transition");
            DesertBatflySignalRuntime.Forget(desert);
            DesertBatflyThreatRuntime.Forget(desert);
            DesertBatflyEnvironmentalBehavior.Forget(desert);
            DB_FrameContextRuntime.Forget(desert);
            DB_BehaviorArbiter.Forget(desert);
            DB_FlightMotor.Forget(desert);
        }
        orig(self, room);
    }

    private static void FlyGrabbed(On.Fly.orig_Grabbed orig, Fly self, Creature.Grasp grasp)
    {
        if (self is DesertBatfly desert)
            DesertBatflySocialLife.CancelForPriority(desert, "grabbed / restraint");
        orig(self, grasp);
    }

    private static void Burrow(On.Fly.orig_Burrowed orig, Fly self)
    {
        if (self is DesertBatfly desert)
        {
            DesertBatflySocialLife.CancelForPriority(desert, "burrow priority");
            DesertBatflySignalRuntime.Forget(desert);
            desert.DesertState.InHive = true;
        }
        orig(self);
    }

    private static void Emerge(On.FliesRoomAI.orig_FlyEmergeFromHive orig, FliesRoomAI self, Fly fly)
    {
        if (fly is not DesertBatfly desert)
        {
            orig(self, fly);
            return;
        }

        DesertBatflySocialLife.CancelForPriority(desert, "emergence priority");
        DesertBatflySignalRuntime.Forget(desert);
        DesertBatflyEnvironmentalBehavior.Forget(desert);
        desert.DesertState.InHive = false;
        try { orig(self, fly); }
        finally { desert.DesertState.InHive = self.inHive.Contains(fly); }
    }

    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)
    {
        if (self.fly is not DesertBatfly desert)
        {
            orig(self);
            return;
        }

        // R3 order is deliberate: refresh state/facts first, resolve one owner, then execute.
        // Vanilla FlyAI.Update is no longer allowed to write an ordinary goal before Arbiter.
        DesertBatflyEnvironmentalBehavior.RefreshInfluence(desert);
        desert.DesertAI.RefreshDecisionState();
        DesertBatflyThreatRuntime.RefreshState(desert);
        DesertBatflySocialLife.RefreshState(desert);

        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);

        if (ownership.PrimaryOwner is DB_BehaviorOwner.CreaturePhysics or
            DB_BehaviorOwner.Restraint or DB_BehaviorOwner.Shortcut or DB_BehaviorOwner.Emergence)
        {
            DesertBatflySocialLife.CancelForPriority(desert, $"R3 PrimaryOwner={ownership.PrimaryOwner}");
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
            if (DB_ImmediateDangerExecutor.TryExecute(desert, ownership))
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
            if (DB_InjuryRecoveryExecutor.TryExecute(desert, ownership))
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
            if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
            {
                DesertBatflySocialLife.CancelForPriority(desert, "R3 PrimaryOwner=Travel");
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
            if (DB_EnvironmentExecutor.TryExecute(desert, ownership))
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
            if (DB_FearExecutor.TryExecute(desert, ownership))
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
            if (DB_VengeanceExecutor.TryExecute(desert, ownership))
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
            if (DB_EnvironmentExecutor.TryExecute(desert, ownership))
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
            if (DB_ProjectileEvadeExecutor.TryExecute(desert, ownership))
            {
                CompleteR3Frame(desert, ownership);
                return;
            }
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert, DB_BehaviorOwner.ImmediateProjectileEvade,
                "Projectile evade executor yielded after current projectile recheck");
        }

        if (ownership.PrimaryOwner == DB_BehaviorOwner.Combat)
        {
            if (DB_CombatExecutor.TryExecute(desert, ownership))
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
            if (DB_RoostExecutor.TryExecute(desert, ownership))
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
            if (DB_SocialExecutor.TryExecute(desert, ownership))
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

    private static bool ExecuteNativeOwned(
        On.FlyAI.orig_Update orig,
        FlyAI self,
        DesertBatfly desert,
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
        DesertBatfly desert,
        in DB_BehaviorResolution ownership)
    {
        if (desert == null) return;
        DesertBatflyThreatRuntime.CommitFrame(desert);
        DesertBatflyThreatTrace.Sample(desert);
        DesertBatflySignalRuntime.Update(desert);
        DesertBatflySocialLife.SampleTrace(desert);
        DesertBatflyDebugTrace.Sample(desert);
        if (desert.abstractCreature != null && AIDebugTrace.IsWatched(desert.abstractCreature))
            AIDebugTrace.RecordChange(
                desert.abstractCreature,
                AIDebugEventCategory.Decision,
                "PrimaryOwner",
                ownership.PrimaryOwner,
                ownership.Reason);
    }

    private static bool RestrainedByNonFly(DesertBatfly fly)
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
        if (self.fly is not DesertBatfly) orig(self);
    }

    private static void Idle(On.FlyAI.orig_IdleUpdate orig, FlyAI self)
    {
        orig(self);
        if (self.fly is not DesertBatfly desert) return;

        DesertBatflySocialRoomRuntime.RoomState socialRoom =
            DesertBatflySocialRoomRuntime.For(self.room);
        if (socialRoom?.IsReserved(desert) == true)
            return;

        if (!DesertSwarmRoom.IsDesertSwarmRoom(self.room.abstractRoom))
        {
            if (self.behavior == FlyAI.Behavior.Swarm) self.ChangeBehavior(FlyAI.Behavior.Idle);
            return;
        }
        if (self.behavior == FlyAI.Behavior.Idle && !self.fleeFromRain && self.ValidSwarmPosition(self.localGoal))
            self.ChangeBehavior(FlyAI.Behavior.Swarm);
    }

    private static void Rain(On.FlyAI.orig_FleeFromRainUpdate orig, FlyAI self)
    {
        if (self.fly is not DesertBatfly desert)
        {
            orig(self);
            return;
        }

        // Do not execute Travel from this nested vanilla callback. If Travel wins, suppress
        // native rain steering here and let the enclosing UpdateAI execute Travel exactly once.
        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            DesertBatflySocialLife.CancelForPriority(desert, "R3 Travel owns enclosing AI frame");
            return;
        }

        DesertBatflySocialLife.CancelForPriority(desert, "rain priority");
        if (self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        self.afraid = Mathf.Max(self.afraid, 2f);
    }

    private static void Follow(On.FlyAI.orig_UpdateFollowDijsktra orig, FlyAI self)
    {
        if (self.fly is not DesertBatfly ||
            !DesertSwarmRoom.IsDesertSwarmRoom(self.room.abstractRoom) ||
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

        // DesertSwarmRoom still owns room-tag/hive spawning semantics and is intentionally
        // not gated by realized bats. The remaining systems are DesertBatfly-only and must
        // not allocate/scan ordinary rooms that never activated DB_RoomContext.
        DesertSwarmRoom.UpdateRoom(self, self.game.evenUpdate);
        if (!DB_RoomContext.TryGetExisting(self, out DB_RoomContext context) ||
            context.Bats.Count == 0)
            return;

        if (self.readyForAI && self.aimap != null)
            DesertBatflyRefuge.ObserveRoom(self);
        DesertBatflySignalRoomRuntime.For(self)?.Prune(self);
        DesertBatflyEnvironmentalRoomRuntime.Update(self);
    }

    private static int Nourishment(
        On.SlugcatStats.orig_NourishmentOfObjectEaten orig,
        SlugcatStats.Name name,
        IPlayerEdible edible)
    {
        int value = orig(name, edible);
        if (edible is not DesertBatfly || value <= 0) return value;
        return value < edible.FoodPoints * 4 ? 4 : 8;
    }
}
