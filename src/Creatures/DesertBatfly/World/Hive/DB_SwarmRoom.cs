using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DB_SwarmRoom
{
    private static ConditionalWeakTable<Room, DB_SwarmRoom> rooms = new();
    internal readonly FliesRoomAI Hive;
    private readonly Room room;
    private bool ecologyInitialized;
    private int ecologySampleTimer;
    private int flockRefresh;
    private int neutralRouteSerial;
    private int nextHiveReleaseClock = -1;
    private int hiveReleaseSerial;
    internal DB_FlockSnapshot Flock { get; private set; }
    internal int SnapshotAge => 30 - flockRefresh;

    private DB_SwarmRoom(Room room)
    {
        this.room = room;
        Hive = new FliesRoomAI(room);
        ecologySampleTimer = 1;
    }

    internal static bool IsDB_SwarmRoom(AbstractRoom room) => room?.roomTags?.Contains("DESERTSWARMROOM") == true;
    internal static DB_SwarmRoom For(Room room) => rooms.GetValue(room, value => new DB_SwarmRoom(value));

    // Debug/read-only callers must not create a colony merely by inspecting it.
    internal static bool TryGet(Room room, out DB_SwarmRoom colony)
    {
        colony = null;
        return room != null && rooms.TryGetValue(room, out colony);
    }

    /// <summary>
    /// DESERTSWARMROOM is not a vanilla swarmRoom, so FlyAI.InActiveSwarmRoom cannot choose
    /// the intended node set by itself. Explicit leave-room routing is preserved, but ordinary
    /// Idle navigation is restricted to exit/den maps. BatHive maps belong to explicit
    /// ReturnHome/environment docking intent and must not become neutral roaming destinations.
    /// </summary>
    internal static bool TryHandleNativeFollowDijkstra(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null || !ReferenceEquals(ai.fly, bat) ||
            !IsDB_SwarmRoom(ai.room.abstractRoom))
            return false;

        if (ai.leaveRoomDijkstra >= 0)
        {
            ai.followingDijkstraMap = ai.leaveRoomDijkstra;
            return true;
        }

        int relevant = ai.room.abstractRoom.NodesRelevantToCreature(bat.Template);
        if (relevant <= 0)
        {
            ai.followingDijkstraMap = -1;
            return true;
        }

        int current = ai.followingDijkstraMap;
        if (current >= 0 && current < relevant)
        {
            if (ai.behavior != FlyAI.Behavior.Idle)
                return true;

            if (IsOrdinaryFollowMap(ai.room, bat.Template, current))
            {
                int distance = ai.room.aimap.ExitDistanceForCreature(ai.FlyPos, current, bat.Template);
                if (distance >= 18)
                    return true;
            }
        }

        ai.followingDijkstraMap = For(ai.room).NextNeutralFollowMap(ai, bat, relevant, current);
        return true;
    }

    /// <summary>
    /// Executes the local-hive branch of vanilla FleeFromRain under the already accepted
    /// NativeSpecial owner. Keeping this at the enclosing FlyAI.Update ownership boundary avoids
    /// a nested FleeFromRain hook while still routing the physical BatHive approach through the
    /// same traffic gate used by Environment, Travel and InjuryRecovery. Returns false when no
    /// usable local hive exists so vanilla can retain its cross-room rain fallback.
    /// </summary>
    internal static bool TryExecuteNativeRain(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room?.aimap == null || bat == null || !ReferenceEquals(ai.fly, bat) ||
            !ai.fleeFromRain || ai.room.hives == null || ai.room.hives.Length == 0 ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.NativeSpecial))
            return false;

        ai.afraid = 2f;
        bat.DesertAI.CancelAttack();

        // Since this method consumes the accepted NativeSpecial frame instead of calling the
        // whole vanilla FlyAI.Update, it must also preserve vanilla's final hive-tile transition.
        if (ai.room.GetTile(bat.mainBodyChunk.pos).hive)
            return DB_HiveDocking.TryHandleNativeRain(bat);

        int bestMap = -1;
        int bestDistance = int.MaxValue;
        int currentHive = ai.followingDijkstraMap - ai.room.exitAndDenIndex.Length;
        if (currentHive >= 0 && currentHive < ai.room.hives.Length &&
            ai.room.hives[currentHive] != null && ai.room.hives[currentHive].Length > 0)
        {
            int currentDistance = ai.room.aimap.ExitDistanceForCreature(
                bat.abstractCreature.pos.Tile,
                ai.followingDijkstraMap,
                bat.Template);
            if (currentDistance >= 0)
            {
                bestMap = ai.followingDijkstraMap;
                bestDistance = currentDistance;
            }
        }

        if (bestMap < 0)
        {
            for (int i = 0; i < ai.room.hives.Length; i++)
            {
                if (ai.room.hives[i] == null || ai.room.hives[i].Length == 0) continue;
                int map = ai.room.exitAndDenIndex.Length + i;
                int distance = ai.room.aimap.ExitDistanceForCreature(
                    bat.abstractCreature.pos.Tile,
                    map,
                    bat.Template);
                if (distance < 0 || distance >= bestDistance) continue;
                bestDistance = distance;
                bestMap = map;
            }
        }

        if (bestMap < 0) return false;

        // Native rain can pre-empt a normal Chain/Hang state. Do the same minimal state cleanup
        // as other owned flight routes so a stale roost movement mode cannot fight the Dijkstra
        // goal while the bat is trying to reach the hive.
        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (ai.behavior == FlyAI.Behavior.Chain)
            ai.ChangeBehavior(FlyAI.Behavior.Idle);
        else if (ai.behavior != FlyAI.Behavior.Burrow)
            ai.behavior = FlyAI.Behavior.Idle;
        bat.movMode = Fly.MovementMode.BatFlight;
        ai.noSwarmCounter = Mathf.Max(ai.noSwarmCounter, 90);
        ai.leaveRoomDijkstra = -1;
        ai.followingDijkstraMap = bestMap;
        Vector2 nextGoal = ai.ProgressLocalGoalAlongDijkstraMap(ai.localGoal, bestMap);
        return DB_FlightMotor.TryGuideNative(
            bat,
            DB_BehaviorOwner.NativeSpecial,
            nextGoal);
    }

    private int NextNeutralFollowMap(FlyAI ai, DB_Creature bat, int relevant, int current)
    {
        int ordinaryCount = 0;
        for (int specific = 0; specific < relevant; specific++)
        {
            if (IsOrdinaryFollowMap(ai.room, bat.Template, specific))
                ordinaryCount++;
        }

        if (ordinaryCount <= 0)
            return -1;

        int serial = neutralRouteSerial++;
        int seedOffset = bat.Personality?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        int ordinal = (int)(((uint)serial + (uint)seedOffset) % (uint)ordinaryCount);
        int next = SpecificMapAtOrdinaryOrdinal(ai.room, bat.Template, relevant, ordinal);
        if (next < 0)
            return -1;

        if (next != current || ordinaryCount <= 1)
            return next;

        ordinal = (ordinal + 1) % ordinaryCount;
        next = SpecificMapAtOrdinaryOrdinal(ai.room, bat.Template, relevant, ordinal);
        return next >= 0 ? next : -1;
    }

    private static int SpecificMapAtOrdinaryOrdinal(
        Room room,
        CreatureTemplate template,
        int relevant,
        int ordinal)
    {
        for (int specific = 0; specific < relevant; specific++)
        {
            if (!IsOrdinaryFollowMap(room, template, specific)) continue;
            if (ordinal-- == 0) return specific;
        }
        return -1;
    }

    private static bool IsOrdinaryFollowMap(Room room, CreatureTemplate template, int specific)
    {
        if (room?.abstractRoom == null || template == null || specific < 0) return false;
        int common = room.abstractRoom.CreatureSpecificToCommonNodeIndex(specific, template);
        return common >= 0 && common < room.exitAndDenIndex.Length;
    }

    internal static void Reset()
    {
        rooms = new();
    }

    internal static void UpdateRoom(Room room, bool eu)
    {
        if (!room.readyForAI || room.aimap == null) return;
        if (IsDB_SwarmRoom(room.abstractRoom)) For(room).Update(eu);
        else if (rooms.TryGetValue(room, out var colony)) colony.Update(eu);
    }

    /// <summary>
    /// Passive colony emergence is closed whenever the current weather ecology is asking bats to
    /// contract into shelter. Sandstorms close from Advisory onward because their species profile
    /// already starts Home/Burrow preparation there; emitting new bats during that phase creates
    /// an immediate leave-hive/return-hive loop. Other severe weather closes from Preparation.
    /// Explicit Travel departures are not routed through this passive queue.
    /// </summary>
    internal static bool ShouldSuppressPassiveHiveEmergence(Room room)
    {
        if (room == null ||
            !DB_EnvironmentRoomRuntime.TryGetContext(room, out DB_EnvironmentContext context) ||
            context.Phase is DB_EnvironmentPhase.Calm or DB_EnvironmentPhase.Recovery)
            return false;

        return context.Weather switch
        {
            DB_EnvironmentWeather.Sandstorm or DB_EnvironmentWeather.DeathSandstorm =>
                context.Phase is DB_EnvironmentPhase.Advisory or
                    DB_EnvironmentPhase.Preparation or
                    DB_EnvironmentPhase.Sheltering or
                    DB_EnvironmentPhase.Acute,
            DB_EnvironmentWeather.DenseFog or
            DB_EnvironmentWeather.HeavyRain or
            DB_EnvironmentWeather.HeatWave or
            DB_EnvironmentWeather.IntenseHeat or
            DB_EnvironmentWeather.DeathRain =>
                context.Phase is DB_EnvironmentPhase.Preparation or
                    DB_EnvironmentPhase.Sheltering or
                    DB_EnvironmentPhase.Acute,
            _ => false
        };
    }

    private void Update(bool eu)
    {
        bool authoredColony = IsDB_SwarmRoom(room.abstractRoom);
        if (authoredColony)
        {
            if (!ecologyInitialized)
            {
                ecologyInitialized = true;
                DB_ColonyRuntime.EnsureWorld(room.world);
            }

            if (--ecologySampleTimer <= 0)
            {
                ecologySampleTimer = 40;
                DB_ColonyRuntime.SampleRoom(room, 1f);
            }
        }

        Hive.inHive.RemoveAll(fly => fly == null || fly.slatedForDeletetion || fly.dead);

        foreach (Fly member in Hive.inHive)
            if (member is DB_Creature resting && !resting.dead)
                resting.Injury.Recover(0.0032f / 40f);

        // Never call FliesRoomAI.Update for Desert Batfly colonies. Vanilla gives every single
        // hive occupant an independent 2.5% emergence roll every frame; with a persistent colony
        // this releases most of the population in seconds and creates a permanent cloud directly
        // above the same BatHive tiles. We keep the bookkeeping part of that update here and own
        // emergence throughput explicitly below.
        UpdateHiveWithoutEmergence();

        if (ShouldSuppressPassiveHiveEmergence(room))
        {
            // Re-open with a fresh delay instead of dumping a queued release immediately when the
            // weather clears.
            nextHiveReleaseClock = -1;
        }
        else
        {
            TryReleaseQueuedHiveMember();
        }

        if (--flockRefresh <= 0)
        {
            Flock = DB_FlockSnapshot.Capture(room, Hive.flies, Flock.PanicRatio);
            flockRefresh = 30;
        }
    }

    private void TryReleaseQueuedHiveMember()
    {
        if (Hive.inHive.Count == 0 || room.hives == null || room.hives.Length == 0)
            return;

        // Preserve vanilla hard hazard gates before attempting FlyEmergeFromHive. The method
        // checks them again, but doing it here prevents repeatedly consuming the room queue while
        // emergence is impossible.
        if ((!FlyAI.RoomNotACycleHazard(room) &&
             ((ModManager.MSC && room.world.rainCycle.preTimer > 0) ||
              room.world.rainCycle.RainApproaching < 0.3f ||
              room.world.rainCycle.RainGameOver)) || room.VoidWeaverActive)
            return;

        int clock = Math.Max(0, room.game?.clock ?? 0);
        if (nextHiveReleaseClock < 0)
            nextHiveReleaseClock = clock + 20;
        if (clock < nextHiveReleaseClock)
            return;

        DB_Creature candidate = SelectReleaseCandidate();
        // One shared room timer: release at most one bat roughly every 0.9-1.7 seconds at 40 Hz.
        // This remains independent of colony population, so a larger colony no longer becomes an
        // exponentially stronger emitter at its entrance.
        nextHiveReleaseClock = clock + StableReleaseInterval(++hiveReleaseSerial);
        if (candidate == null)
            return;

        // DB_RainWorldHooks.Emerge owns all post-hive departure initialization. Keeping that
        // lifecycle at the actual FlyEmergeFromHive boundary also covers explicit Travel exits.
        Hive.FlyEmergeFromHive(candidate);
    }

    private DB_Creature SelectReleaseCandidate()
    {
        int count = Hive.inHive.Count;
        if (count == 0) return null;
        int start = count == 1
            ? 0
            : (int)(((uint)(hiveReleaseSerial * 1103515245 + 12345)) % (uint)count);
        for (int i = 0; i < count; i++)
        {
            Fly member = Hive.inHive[(start + i) % count];
            if (member is not DB_Creature bat || bat.dead || bat.slatedForDeletetion)
                continue;

            // Passive ecology must never steal a bat from a committed cross-room trip or force a
            // severely injured individual out of the recovery space it deliberately entered.
            if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering ||
                DB_TravelRuntime.HasIntent(bat.abstractCreature))
                continue;
            return bat;
        }
        return null;
    }

    private int StableReleaseInterval(int serial)
    {
        int seed = room.abstractRoom?.index ?? 0;
        uint value = (uint)(seed * 73856093) ^ (uint)(serial * 19349663) ^ 0x9E3779B9u;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        return 36 + (int)(value % 33u); // 36..68 ticks
    }

    private void UpdateHiveWithoutEmergence()
    {
        for (int i = Hive.inHive.Count - 1; i >= 0; i--)
        {
            Fly member = Hive.inHive[i];
            if (member == null || member.slatedForDeletetion || member.dead)
            {
                Hive.inHive.RemoveAt(i);
                continue;
            }
            if (member is DB_Creature resting)
                resting.DesertState.InHive = true;
            if (member.room == room)
                member.RemoveFromRoom();
        }

        for (int i = Hive.flies.Count - 1; i >= 0; i--)
        {
            Fly member = Hive.flies[i];
            if (member == null || member.room != room)
            {
                Hive.flies.RemoveAt(i);
                continue;
            }
            if (member is DB_Creature active)
                active.DesertState.InHive = false;
        }
    }
}

// Value-only room snapshot for ordinary flock/environment state.
internal readonly struct DB_FlockSnapshot
{
    internal readonly Vector2 Center, AverageVelocity;
    internal readonly int ActiveCount;
    internal readonly float PanicRatio, PreviousPanicRatio, RoostRatio;

    internal DB_FlockSnapshot(
        Vector2 center,
        Vector2 velocity,
        int active,
        float panic,
        float previousPanic,
        float roost)
    {
        Center = center;
        AverageVelocity = velocity;
        ActiveCount = active;
        PanicRatio = panic;
        PreviousPanicRatio = previousPanic;
        RoostRatio = roost;
    }

    private static bool Finite(Vector2 value) =>
        !float.IsNaN(value.x) && !float.IsNaN(value.y) &&
        !float.IsInfinity(value.x) && !float.IsInfinity(value.y);

    internal static DB_FlockSnapshot Capture(
        Room room,
        System.Collections.Generic.IEnumerable<Fly> flies,
        float previousPanic)
    {
        Vector2 center = Vector2.zero;
        Vector2 velocity = Vector2.zero;
        int count = 0, panic = 0, roost = 0;

        foreach (Fly fly in flies)
        {
            if (fly is not DB_Creature bat || bat.dead || bat.slatedForDeletetion ||
                bat.room != room || bat.inShortcut || bat.DesertState.InHive ||
                bat.mainBodyChunk == null || !Finite(bat.mainBodyChunk.pos) ||
                !Finite(bat.mainBodyChunk.vel))
                continue;

            count++;
            center += (bat.mainBodyChunk.pos - center) / count;
            velocity += (bat.mainBodyChunk.vel - velocity) / count;

            if (bat.DesertAI.HasImmediateDanger || DB_FearRuntime.HasActiveFearSuppression(bat))
                panic++;
            if (bat.AI?.behavior == FlyAI.Behavior.Chain ||
                bat.DesertAI.Mode == DB_AI.Activity.Roost)
                roost++;
        }

        return new DB_FlockSnapshot(
            center,
            velocity,
            count,
            count == 0 ? 0f : (float)panic / count,
            previousPanic,
            count == 0 ? 0f : (float)roost / count);
    }
}
