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
    private int curveTimer = 100;

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
