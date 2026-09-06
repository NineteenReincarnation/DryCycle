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
        DesertBatflyColonyRuntime.Enable();
        if (!debugRegistered)
        {
            AIDebugRegistry.Register(new DesertBatflyTask09DebugSource());
            debugRegistered = true;
        }
        On.Fly.ReportToFliesRoomAI += Report;
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
        DesertBatflyColonyRuntime.Disable();
        DesertBatflyIntimidation.Reset();
        DesertBatflyWarpCompatibility.Disable();
        DesertBatflySandbox.Disable();
        DesertSwarmRoom.Reset();
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        // RainWorld.Awake has built CreatureUnlockList by this point, and optional
        // Warp assemblies are already loaded. Keep both integrations soft.
        DesertBatflySandbox.Enable();
        DesertBatflyWarpCompatibility.Enable();
    }

    private static void Report(On.Fly.orig_ReportToFliesRoomAI orig, Fly self, Room room)
    {
        if (self is DesertBatfly) DesertSwarmRoom.For(room).Hive.AddFly(self);
        else orig(self, room);
    }

    private static void Burrow(On.Fly.orig_Burrowed orig, Fly self)
    {
        if (self is DesertBatfly desert) desert.DesertState.InHive = true;
        orig(self);
    }

    private static void Emerge(On.FliesRoomAI.orig_FlyEmergeFromHive orig, FliesRoomAI self, Fly fly)
    {
        if (fly is not DesertBatfly desert)
        {
            orig(self, fly);
            return;
        }

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

        // Only register the actual attach transition. The later Lizard.Grabbed path may
        // report the same capture again, but BroadcastPredatorCapture already owns its
        // event de-duplication and keeping BeginCapture alive across both phases.
        if (previousState == LizardTongue.State.AttachedInSmallObject && previousOwner == desert)
            return;

        DesertBatflyIntimidation.BroadcastPredatorCapture(desert, self.lizard, self);
        desert.DesertAI.Threatened(self.lizard, true);
    }

    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)
    {
        if (self.fly is DesertBatfly suspended &&
            (suspended.Emergence.Active || RestrainedByNonFly(suspended)))
        {
            suspended.DesertAI.Update();
            return;
        }

        // Fly-on-Fly grabbedBy entries are the vanilla hanging-chain structure and
        // must keep running the normal FlyAI chain update. Treat only non-Fly grabs
        // (player/predator/etc.) as an AI suspension.
        orig(self);
        if (self.fly is not DesertBatfly desert) return;

        // Task 09 owns only the cross-room destination. Immediate danger and severe
        // injury cause TryDriveRealized to yield, after which DesertBatflyAI handles
        // Escape / InjuryRecovery normally. When travel is active, LeaveRoom uses the
        // native FlyAI/AImap/shortcut path and ordinary harass/roost logic must not
        // overwrite that room exit intent in the same tick.
        if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
        {
            desert.DesertAI.CancelAttack();
            return;
        }

        desert.DesertAI.Update();
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
        // Dedicated threat perception distinguishes casual passing from pursuit.
    }

    private static void Idle(On.FlyAI.orig_IdleUpdate orig, FlyAI self)
    {
        orig(self);
        if (self.fly is not DesertBatfly) return;
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

        // A Task 09 refuge/return route always wins over the old one-hop desert-room
        // fallback. The navigator only calls FlyAI.LeaveRoom and never controls velocity.
        if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
            return;

        if (self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        // No reachable Refuge is currently committed. Do not make a random permanent
        // move merely because rain/danger exists; remain locally afraid and allow the
        // next low-frequency weather evaluation to pick a survivable Refuge route.
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
            DesertBatflyColonyRuntime.ReportDeath(bat, likelyPredator);
    }

    private static void UpdateRoom(On.Room.orig_Update orig, Room self)
    {
        orig(self);
        DesertSwarmRoom.UpdateRoom(self, self.game.evenUpdate);
    }

    private static int Nourishment(
        On.SlugcatStats.orig_NourishmentOfObjectEaten orig,
        SlugcatStats.Name name,
        IPlayerEdible edible)
    {
        int value = orig(name, edible);
        if (edible is not DesertBatfly || value <= 0) return value;
        // Vanilla returns quarter-food units: reduced nutrition is the existing
        // carnivorous diet rule. Preserve inedible diets and avoid character lists.
        return value < edible.FoodPoints * 4 ? 4 : 8;
    }
}
