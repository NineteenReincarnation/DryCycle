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
            if (member is DesertBatfly resting && !resting.dead)
                resting.Injury.Recover(0.0032f / 40f);

        Hive.Update(eu);

        if (--flockRefresh <= 0)
        {
            Flock = DB_FlockSnapshot.Capture(room, Hive.flies, Flock.PanicRatio);
            flockRefresh = 30;
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
            if (fly is not DesertBatfly bat || bat.dead || bat.slatedForDeletetion ||
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
