using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertSwarmRoom
{
    private sealed class Population
    {
        internal bool Seeded;
        internal int CurveRemaining;
    }

    private static ConditionalWeakTable<Room, DesertSwarmRoom> rooms = new();
    private static ConditionalWeakTable<AbstractRoom, Population> populations = new();
    internal readonly FliesRoomAI Hive;
    private readonly Room room;
    private int curveTimer = 100, flockRefresh;
    internal DesertBatflyFlockSnapshot Flock { get; private set; }
    internal int SnapshotAge => 30 - flockRefresh;

    private DesertSwarmRoom(Room room)
    {
        this.room = room;
        Hive = new FliesRoomAI(room);
    }

    internal static bool IsDesertSwarmRoom(AbstractRoom room) => room?.roomTags?.Contains("DESERTSWARMROOM") == true;
    internal static DesertSwarmRoom For(Room room) => rooms.GetValue(room, value => new DesertSwarmRoom(value));
    internal static void Reset() { rooms = new(); populations = new(); }

    internal static void UpdateRoom(Room room, bool eu)
    {
        if (!room.readyForAI || room.aimap == null) return;
        if (IsDesertSwarmRoom(room.abstractRoom)) For(room).Update(eu);
        else if (rooms.TryGetValue(room, out var colony)) colony.Update(eu);
    }

    private void Update(bool eu)
    {
        if (IsDesertSwarmRoom(room.abstractRoom))
        {
            Population population = populations.GetOrCreateValue(room.abstractRoom);
            if (!population.Seeded)
            {
                population.Seeded = true;
                int existing = 0;
                foreach (var creature in room.abstractRoom.creatures)
                    if (creature.creatureTemplate.type == DesertBatflyDefinition.CreatureType) existing++;
                int desired = DesertBatflyTuning.HivePopulation + DesertBatflyTuning.CurvePopulation;
                population.CurveRemaining = Mathf.Min(DesertBatflyTuning.CurvePopulation, Mathf.Max(0, desired - existing));
                if (room.hives.Length > 0)
                    for (int i = existing; i < DesertBatflyTuning.HivePopulation; i++)
                    {
                        var creature = Create(Hive.RandomHiveNode());
                        ((DesertBatflyState)creature.state).InHive = true;
                        creature.Realize();
                        Hive.inHive.Add((DesertBatfly)creature.realizedCreature);
                    }
            }
            if (population.CurveRemaining > 0 && --curveTimer <= 0)
            {
                curveTimer = Random.Range(180, 420);
                if (SafeWeather() && DesertBatflyEmergence.TryChoose(room, out Vector2 point, out Vector2 normal))
                {
                    var creature = Create(room.GetWorldCoordinate(point + normal * 20f));
                    creature.RealizeInRoom();
                    ((DesertBatfly)creature.realizedCreature).Emergence.Begin(point, normal);
                    population.CurveRemaining--;
                }
            }
        }
        // Native hive emergence respects rain, grass nodes, predators and sounds.
        // Clean up consumed/dead entries rather than resurrecting them on exit.
        Hive.inHive.RemoveAll(fly => fly.slatedForDeletetion || fly.dead);
        Hive.Update(eu);
        if (--flockRefresh <= 0)
        {
            Flock = DesertBatflyFlockSnapshot.Capture(room, Hive.flies, Flock.PanicRatio);
            flockRefresh = 30;
        }
    }

    internal bool SafeWeather() => FlyAI.RoomNotACycleHazard(room) ||
        (room.world.rainCycle.RainApproaching >= 0.3f && !room.world.rainCycle.RainGameOver && room.world.rainCycle.preTimer <= 0);

    private AbstractCreature Create(WorldCoordinate coordinate)
    {
        var creature = new AbstractCreature(room.world,
            StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType), null, coordinate, room.game.GetNewID());
        room.abstractRoom.AddEntity(creature);
        return creature;
    }
}

// Value-only snapshot: does not retain dead, absent, or unrealized creature references.
internal readonly struct DesertBatflyFlockSnapshot
{
    internal readonly Vector2 Center, AverageVelocity;
    internal readonly int ActiveCount, ExpressedRoleCount;
    internal readonly float PanicRatio, PreviousPanicRatio, RoostRatio;
    internal DesertBatflyFlockSnapshot(Vector2 center, Vector2 velocity, int active, int roles,
        float panic, float previousPanic, float roost)
    {
        Center = center; AverageVelocity = velocity; ActiveCount = active; ExpressedRoleCount = roles;
        PanicRatio = panic; PreviousPanicRatio = previousPanic; RoostRatio = roost;
    }
    private static bool Finite(Vector2 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) &&
        !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    internal static DesertBatflyFlockSnapshot Capture(Room room, System.Collections.Generic.IEnumerable<Fly> flies, float previousPanic)
    {
        Vector2 center = Vector2.zero, velocity = Vector2.zero;
        int count = 0, roles = 0, panic = 0, roost = 0;
        foreach (Fly fly in flies)
        {
            if (fly is not DesertBatfly bat || bat.dead || bat.slatedForDeletetion || bat.room != room ||
                bat.inShortcut || bat.DesertState.InHive || bat.mainBodyChunk == null ||
                !Finite(bat.mainBodyChunk.pos) || !Finite(bat.mainBodyChunk.vel)) continue;
            count++;
            center += (bat.mainBodyChunk.pos - center) / count;
            velocity += (bat.mainBodyChunk.vel - velocity) / count;
            if (bat.DesertAI.Roles.Expressed != ExpressedSocialRole.None) roles++;
            if (bat.DesertAI.HasImmediateDanger || DesertBatflyIntimidation.BlocksSocialRoles(bat)) panic++;
            if (bat.AI?.behavior == FlyAI.Behavior.Chain || bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost) roost++;
        }
        return new DesertBatflyFlockSnapshot(center, velocity, count, roles,
            count == 0 ? 0f : (float)panic / count, previousPanic, count == 0 ? 0f : (float)roost / count);
    }
}
