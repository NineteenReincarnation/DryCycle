using DryCycle.Debugging.AI;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Only bridge nonvirtual vanilla entry points here. All species decisions live
// in its Creature, AI, Graphics, State or colony classes.
internal static class DesertBatflyHooks
{
    private static bool enabled;
    private static bool debugRegistered;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        DesertBatflyIntimidation.Reset();
        DesertBatflyRefuge.Reset();
        DesertBatflySocialLife.Reset();
        DesertBatflyThreatRuntime.Enable();
        DesertBatflyColonyRuntime.Enable();
        DesertBatflyPlatformRoostRuntime.Enable();
        if (!debugRegistered)
        {
            AIDebugRegistry.Register(new DesertBatflyTask11DebugSource());
            debugRegistered = true;
        }
        On.Fly.ReportToFliesRoomAI += Report;
        On.Fly.NewRoom += FlyNewRoom;
        On.Fly.Grabbed += FlyGrabbed;
        On.Fly.Burrowed += Burrow;
        On.FliesRoomAI.FlyEmergeFromHive += Emerge;
        On.FlyAI.Update += UpdateAI;
        On.FlyAI.UpdateThreats += Threats;
        On.FlyAI.IdleUpdate += Idle;
        On.FlyAI.UpdateFollowDijsktra += Follow;
        On.FlyAI.FleeFromRainUpdate += Rain;
        On.Creature.Die += CreatureDie;
        On.LizardTongue.Update += TongueUpdate;
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
        On.Fly.Burrowed -= Burrow;
        On.FliesRoomAI.FlyEmergeFromHive -= Emerge;
        On.FlyAI.Update -= UpdateAI;
        On.FlyAI.UpdateThreats -= Threats;
        On.FlyAI.IdleUpdate -= Idle;
        On.FlyAI.UpdateFollowDijsktra -= Follow;
        On.FlyAI.FleeFromRainUpdate -= Rain;
        On.Creature.Die -= CreatureDie;
        On.LizardTongue.Update -= TongueUpdate;
        On.Room.Update -= UpdateRoom;
        On.SlugcatStats.NourishmentOfObjectEaten -= Nourishment;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        DesertBatflyThreatRuntime.Disable();
        DesertBatflySocialLife.Reset();
        DesertBatflyPlatformRoostRuntime.Disable();
        DesertBatflyColonyRuntime.Disable();
        DesertBatflyRefuge.Reset();
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
            DesertBatflyThreatRuntime.Forget(desert);
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
        desert.DesertState.InHive = false;
        try { orig(self, fly); }
        finally { desert.DesertState.InHive = self.inHive.Contains(fly); }
    }

    private static void TongueUpdate(On.LizardTongue.orig_Update orig, LizardTongue self)
    {
        LizardTongue.State previousState = self.state;
        PhysicalObject previousOwner = self.attached?.owner;
        orig(self);

        if (self?.lizard == null ||
            self.state != LizardTongue.State.AttachedInSmallObject ||
            self.attached?.owner is not DesertBatfly desert ||
            desert.dead || desert.slatedForDeletetion ||
            !DesertBatflyIntimidation.IsSupportedLethalThreat(self.lizard))
            return;

        if (previousState == LizardTongue.State.AttachedInSmallObject && previousOwner == desert)
            return;

        DesertBatflySocialLife.CancelForPriority(desert, "Peach tongue capture");
        DesertBatflyIntimidation.BroadcastPredatorCapture(desert, self.lizard, self);
        desert.DesertAI.Threatened(self.lizard, true);
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

        if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
        {
            DesertBatflySocialLife.CancelForPriority(desert, "Task09 travel priority");
            desert.DesertAI.CancelAttack();
            DesertBatflySocialLife.SampleTrace(desert);
            DesertBatflyDebugTrace.Sample(desert);
            return;
        }

        // Existing DesertBatflyAI gets first refusal for danger, combat, injury and
        // committed roost behavior. Task 11 then learns/adjusts only the realized local
        // combat geometry. Task 10 runs last and can only shape a remaining neutral frame.
        desert.DesertAI.Update();
        DesertBatflyThreatRuntime.Update(desert);
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

        // Task 10 may put an active neutral interaction into Idle while still using
        // vanilla BatFlight. Do not immediately force it back to Swarm on the next
        // FlyAI tick; the reservation ends with the interaction and normal fallback
        // resumes automatically afterwards.
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
            DesertBatflySocialLife.CancelForPriority(desert, "Task09 weather travel priority");
            return;
        }

        DesertBatflySocialLife.CancelForPriority(desert, "rain priority");
        if (self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        // No committed cross-room refuge route: remain locally afraid rather than
        // inventing an unplanned one-hop permanent move.
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

    private static void CreatureDie(On.Creature.orig_Die orig, Creature self)
    {
        bool wasDead = self?.dead ?? true;
        Creature likelyPredator = null;
        if (!wasDead && self is DesertBatfly batBefore && batBefore.grabbedBy != null)
        {
            for (int i = 0; i < batBefore.grabbedBy.Count; i++)
            {
                Creature grabber = batBefore.grabbedBy[i]?.grabber;
                if (grabber == null || grabber is Fly) continue;
                likelyPredator = grabber;
                if (grabber is Lizard) break;
            }
        }

        orig(self);

        if (!wasDead && self is DesertBatfly bat && bat.dead)
        {
            DesertBatflySocialLife.CancelForPriority(bat, "death");
            DesertBatflyColonyRuntime.ReportDeath(bat, likelyPredator);
            DesertBatflyThreatRuntime.Forget(bat);
        }
    }

    private static void UpdateRoom(On.Room.orig_Update orig, Room self)
    {
        orig(self);
        if (self.readyForAI && self.aimap != null)
            DesertBatflyRefuge.ObserveRoom(self);
        DesertSwarmRoom.UpdateRoom(self, self.game.evenUpdate);
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
