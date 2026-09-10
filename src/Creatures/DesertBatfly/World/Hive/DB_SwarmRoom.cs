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
    /// the intended node set by itself. Mirror vanilla route retention while avoiding the old
    /// specialization that always picked a BatHive and collapsed the colony onto one point.
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
            int distance = ai.room.aimap.ExitDistanceForCreature(ai.FlyPos, current, bat.Template);
            int completionDistance = ai.CurrentFollowDijkstraIsToHive ? 7 : 18;
            if (ai.behavior != FlyAI.Behavior.Idle || distance >= completionDistance)
                return true;
        }

        // Independent Random.Range calls let a large flock repeatedly land on the same map by
        // chance. Use one room-scoped rotor instead: ordinary exits/dens are still preferred,
        // hives remain occasional legal destinations, but consecutive completed routes are
        // spread across the available maps instead of producing a new artificial gathering
        // point. No per-bat list allocation or room-wide scan is required here.
        ai.followingDijkstraMap = For(ai.room).NextNeutralFollowMap(ai, bat, relevant, current);
        return true;
    }

    private int NextNeutralFollowMap(FlyAI ai, DB_Creature bat, int relevant, int current)
    {
        int ordinaryCount = 0;
        for (int specific = 0; specific < relevant; specific++)
        {
            if (IsOrdinaryFollowMap(ai.room, bat.Template, specific))
                ordinaryCount++;
        }

        // Keep some hive traffic so DESERTSWARMROOM still behaves like a real colony room,
        // while making normal exits/dens the dominant neutral routing target.
        bool ordinaryPool = ordinaryCount > 0 &&
            (ordinaryCount == relevant || Random.value < 0.72f);
        int poolCount = ordinaryPool ? ordinaryCount : relevant;
        if (poolCount <= 0) return -1;

        int serial = neutralRouteSerial++;
        int seedOffset = bat.Personality?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        int ordinal = (int)(((uint)serial + (uint)seedOffset) % (uint)poolCount);
        int next = ordinaryPool
            ? SpecificMapAtOrdinaryOrdinal(ai.room, bat.Template, relevant, ordinal)
            : ordinal;

        if (next < 0 || next >= relevant)
            next = (int)((uint)serial % (uint)relevant);

        if (next != current || relevant <= 1)
            return next;

        if (ordinaryPool && ordinaryCount > 1)
        {
            ordinal = (ordinal + 1) % ordinaryCount;
            next = SpecificMapAtOrdinaryOrdinal(ai.room, bat.Template, relevant, ordinal);
            if (next >= 0 && next != current)
                return next;
        }

        // A room can have only one ordinary exit/den. Once that route completes, do not pin the
        // bat to the same Dijkstra map forever: rotate through the remaining relevant maps.
        int step = 1 + (int)((uint)serial % (uint)(relevant - 1));
        return (current + step) % relevant;
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

    private void Update(bool eu)
    {
        bool authoredColony = IsDB_SwarmRoom(room.abstractRoom);
        if (authoredColony)
        {
            // Colony/Travel own population bootstrap, recovery and migration. Realizing a room
            // must never recreate the old HivePopulation + CurvePopulation refill because
            // that would erase cross-cycle mortality and migration history.
            if (!ecologyInitialized)
            {
                ecologyInitialized = true;
                DB_ColonyRuntime.EnsureWorld(room.world);
            }

            // Long-term pressure uses one low-frequency room sample per simulated second.
            // No region-wide scan is performed here; the save-backed runtime folds these
            // bounded accumulators at cycle settlement.
            if (--ecologySampleTimer <= 0)
            {
                ecologySampleTimer = 40;
                DB_ColonyRuntime.SampleRoom(room, 1f);
            }
        }

        // Native hive emergence respects rain, grass nodes, predators and sounds.
        // Clean up consumed/dead entries rather than resurrecting them on exit.
        Hive.inHive.RemoveAll(fly => fly.slatedForDeletetion || fly.dead);

        // Hive occupants are removed from Room.Update by vanilla: recover them here once,
        // only while still inHive; entering a hive never grants an instant heal.
        foreach (Fly member in Hive.inHive)
            if (member is DB_Creature resting && !resting.dead)
                resting.Injury.Recover(0.0032f / 40f);

        if (SuppressThermalHiveEmergence())
            UpdateHiveWithoutEmergence();
        else
            Hive.Update(eu);

        if (--flockRefresh <= 0)
        {
            Flock = DB_FlockSnapshot.Capture(room, Hive.flies, Flock.PanicRatio);
            flockRefresh = 30;
        }
    }

    private bool SuppressThermalHiveEmergence()
    {
        if (!DB_EnvironmentRoomRuntime.TryGetContext(
                room, out DB_EnvironmentContext context))
            return false;

        if (context.Weather is not (
                DB_EnvironmentWeather.HeatWave or DB_EnvironmentWeather.IntenseHeat))
            return false;

        // Early Advisory heat remains ecologically active. Once the room reaches actual
        // preparation/shelter pressure, a bat that committed to the hive must be allowed to
        // stay there instead of FliesRoomAI's vanilla 2.5%-per-frame random emergence undoing
        // the environmental decision immediately.
        return context.Phase is DB_EnvironmentPhase.Preparation or
               DB_EnvironmentPhase.Sheltering or
               DB_EnvironmentPhase.Acute;
    }

    private void UpdateHiveWithoutEmergence()
    {
        // This mirrors the non-emergence maintenance portion of FliesRoomAI.Update. Burrowed
        // occupants are kept unrealized in the room while active-list bookkeeping remains
        // clean. Normal Hive.Update resumes as soon as thermal shelter pressure ends.
        for (int i = Hive.inHive.Count - 1; i >= 0; i--)
        {
            Fly member = Hive.inHive[i];
            if (member == null || member.slatedForDeletetion || member.dead)
            {
                Hive.inHive.RemoveAt(i);
                continue;
            }
            if (member.room == room)
                member.RemoveFromRoom();
        }

        for (int i = Hive.flies.Count - 1; i >= 0; i--)
        {
            Fly member = Hive.flies[i];
            if (member == null || member.room != room)
                Hive.flies.RemoveAt(i);
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
