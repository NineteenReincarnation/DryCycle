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
        DesertBatflyIntimidation.Reset();
        DesertBatflyRefuge.Reset();
        DesertBatflySocialLife.Reset();
        DesertBatflySignalRuntime.Reset();
        DesertBatflyEnvironmentalRoomRuntime.Reset();
        DesertBatflyEnvironmentalBehavior.Reset();
        DB_EventHub.Enable();
        DB_EventConsumers.Enable();
        DesertBatflyEnvironmentalDenseFogBridge.Enable();
        DesertBatflyEnvironmentalTask09Bridge.Enable();
        DesertBatflyEnvironmentalSurvivalBridge.Enable();
        DesertBatflyEnvironmentalIntegration.Enable();
        DesertBatflySignalIntegration.Enable();
        DesertBatflySignalVengeanceBridge.Enable();
        DesertBatflyThreatRuntime.Enable();
        DesertBatflyThreatVengeanceBridge.Enable();
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
        DesertBatflyThreatVengeanceBridge.Disable();
        DesertBatflyThreatRuntime.Disable();
        DesertBatflySignalVengeanceBridge.Disable();
        DesertBatflySignalIntegration.Disable();
        DesertBatflyEnvironmentalIntegration.Disable();
        DesertBatflyEnvironmentalSurvivalBridge.Disable();
        DesertBatflyEnvironmentalTask09Bridge.Disable();
        DesertBatflyEnvironmentalDenseFogBridge.Disable();
        DesertBatflySignalRuntime.Reset();
        DesertBatflyEnvironmentalBehavior.Reset();
        DesertBatflyEnvironmentalRoomRuntime.Reset();
        DesertBatflySocialLife.Reset();
        DesertBatflyPlatformRoostRuntime.Disable();
        DesertBatflyColonyRuntime.Disable();
        DesertBatflyRefuge.Reset();
        DB_CorpseWarningRuntime.Reset();
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
        if (self.fly is DesertBatfly suspended &&
            (suspended.Emergence.Active || RestrainedByNonFly(suspended)))
        {
            DesertBatflySocialLife.CancelForPriority(suspended, "unavailable / restraint / emergence");
            suspended.DesertAI.Update();
            DesertBatflySocialLife.SampleTrace(suspended);
            DesertBatflyDebugTrace.Sample(suspended);
            return;
        }

        orig(self);
        if (self.fly is not DesertBatfly desert) return;
        DesertBatflyEnvironmentalIntegration.Register(desert);

        if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
        {
            DesertBatflyThreatVengeanceBridge.MarkTravelOwnedFrame(desert);
            DesertBatflySocialLife.CancelForPriority(desert, "Task09 travel priority");
            desert.DesertAI.CancelAttack();
            if (AIDebugTrace.IsWatched(desert.abstractCreature))
                AIDebugTrace.RecordChange(
                    desert.abstractCreature,
                    AIDebugEventCategory.Decision,
                    "ThreatMemoryIgnoredDueToPriority",
                    "Task09 travel",
                    "EmergencyRefuge / ReturnHome / ColonyMigration owns this frame");
            DesertBatflySocialLife.SampleTrace(desert);
            DesertBatflyDebugTrace.Sample(desert);
            return;
        }

        // Existing DesertBatflyAI gets first refusal for immediate danger/combat/injury.
        // Task11 adjusts learned threat tactics, Task12 updates local social information,
        // then Task13 applies realized-room environmental pressure before Task10 runs as
        // the final neutral-life layer. Task13 never owns cross-room travel or velocity.
        desert.DesertAI.Update();
        DesertBatflyThreatRuntime.Update(desert);
        DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade(desert);
        DesertBatflyThreatTrace.Sample(desert);
        DesertBatflySignalRuntime.Update(desert);
        DesertBatflyEnvironmentalBehavior.Update(desert);
        DesertBatflySocialLife.Update(desert);
        DesertBatflySocialLife.SampleTrace(desert);
        DesertBatflyDebugTrace.Sample(desert);
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

        if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
        {
            DesertBatflyThreatVengeanceBridge.MarkTravelOwnedFrame(desert);
            DesertBatflySocialLife.CancelForPriority(desert, "Task09 weather travel priority");
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
        if (self.readyForAI && self.aimap != null)
            DesertBatflyRefuge.ObserveRoom(self);
        DesertSwarmRoom.UpdateRoom(self, self.game.evenUpdate);
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
