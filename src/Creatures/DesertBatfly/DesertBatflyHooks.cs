namespace DryCycle.Creatures.DesertBatfly;

// Only bridge nonvirtual vanilla entry points here. All species decisions live
// in its Creature, AI, Graphics, State or colony classes.
internal static class DesertBatflyHooks
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        DesertBatflyIntimidation.Reset();
        On.Fly.ReportToFliesRoomAI += Report;
        On.Fly.Burrowed += Burrow;
        On.FliesRoomAI.FlyEmergeFromHive += Emerge;
        On.FlyAI.Update += UpdateAI;
        On.FlyAI.UpdateThreats += Threats;
        On.FlyAI.IdleUpdate += Idle;
        On.FlyAI.UpdateFollowDijsktra += Follow;
        On.FlyAI.FleeFromRainUpdate += Rain;
        On.Rock.HitSomething += RockHitSomething;
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
        On.Rock.HitSomething -= RockHitSomething;
        On.Room.Update -= UpdateRoom;
        On.SlugcatStats.NourishmentOfObjectEaten -= Nourishment;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
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
        if (self.fly is DesertBatfly desert) desert.DesertAI.Update();
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
        if (self.fly is not DesertBatfly || self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        // Never ask the ordinary world swarm manager to route a desert colony.
        // Prefer a connected desert room; otherwise use a real mapped exit.
        self.afraid = 2f;
        int chosen = -1;
        for (int i = 0; i < self.room.abstractRoom.connections.Length; i++)
        {
            int connected = self.room.abstractRoom.connections[i];
            if (connected < 0) continue;
            int mapped = self.room.abstractRoom.CommonToCreatureSpecificNodeIndex(i, self.Template);
            if (mapped < 0) continue;
            chosen = mapped;
            if (DesertSwarmRoom.IsDesertSwarmRoom(self.room.world.GetAbstractRoom(connected))) break;
        }
        self.followingDijkstraMap = self.leaveRoomDijkstra = chosen;
        if (chosen >= 0) self.localGoal = self.ProgressLocalGoalAlongDijkstraMap(self.localGoal, chosen);
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

    private static bool RockHitSomething(
        On.Rock.orig_HitSomething orig,
        Rock self,
        SharedPhysics.CollisionResult result,
        bool eu)
    {
        // Start protection before vanilla Rock.HitSomething runs. That method owns
        // the complete impact stack (weapon bookkeeping, Creature.Violence, rebound,
        // sound and VFX), so guarding here catches any death path triggered anywhere
        // inside the same Rock collision while preserving the original hit behavior.
        if (result.obj is DesertBatfly desert && !desert.dead)
            desert.BeginRockStunGuard();

        return orig(self, result, eu);
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
