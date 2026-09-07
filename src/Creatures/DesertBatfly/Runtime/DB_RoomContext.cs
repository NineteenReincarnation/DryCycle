using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Shared, lazy realized-room observation cache for Desert Batfly systems.
///
/// The context owns only low-level observations (realized creatures, active Desert Batflies,
/// players and weapons). Domain state remains in Combat/Social/Threat/Environment/etc.
/// A room is never scanned merely because it exists: the first Desert Batfly consumer asks
/// for the context, and later consumers in the same refresh window reuse that snapshot.
/// </summary>
internal sealed class DB_RoomContext
{
    internal const int RefreshIntervalTicks = 8;

    private static ConditionalWeakTable<Room, DB_RoomContext> contexts = new();

    private readonly Room room;
    private readonly List<Creature> creatures = new(32);
    private readonly List<DesertBatfly> bats = new(24);
    private readonly List<Player> players = new(4);
    private readonly List<Weapon> weapons = new(16);
    private readonly List<Weapon> thrownWeapons = new(12);

    internal int LastRefreshClock { get; private set; } = int.MinValue;
    internal int RefreshCount { get; private set; }
    internal int CreatureScanCount { get; private set; }
    internal int PhysicalObjectScanCount { get; private set; }

    internal IReadOnlyList<Creature> Creatures => creatures;
    internal IReadOnlyList<DesertBatfly> Bats => bats;
    internal IReadOnlyList<Player> Players => players;
    internal IReadOnlyList<Weapon> Weapons => weapons;
    internal IReadOnlyList<Weapon> ThrownWeapons => thrownWeapons;

    private DB_RoomContext(Room room)
    {
        this.room = room;
    }

    internal static DB_RoomContext For(Room room)
    {
        if (room == null) return null;
        DB_RoomContext context = contexts.GetValue(room, key => new DB_RoomContext(key));
        context.RefreshIfNeeded();
        return context;
    }

    internal static bool TryGetExisting(Room room, out DB_RoomContext context)
    {
        context = null;
        if (room == null || !contexts.TryGetValue(room, out context)) return false;
        context.RefreshIfNeeded();
        return true;
    }

    internal static void Reset()
    {
        contexts = new ConditionalWeakTable<Room, DB_RoomContext>();
    }

    internal Player PlayerBySlot(int slot)
    {
        if (slot < 0) return null;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if ((player.playerState?.playerNumber ?? -1) == slot)
                return player;
        }
        return null;
    }

    internal bool Contains(DesertBatfly bat)
    {
        if (bat == null) return false;
        for (int i = 0; i < bats.Count; i++)
            if (ReferenceEquals(bats[i], bat)) return true;
        return false;
    }

    internal void ForceRefreshForTest()
    {
        LastRefreshClock = int.MinValue;
        RefreshIfNeeded();
    }

    private void RefreshIfNeeded()
    {
        if (room == null) return;
        int clock = room.game?.clock ?? 0;
        bool stale = LastRefreshClock == int.MinValue ||
                     clock < LastRefreshClock ||
                     clock - LastRefreshClock >= RefreshIntervalTicks;
        if (!stale) return;

        LastRefreshClock = clock;
        RefreshCount++;
        creatures.Clear();
        bats.Clear();
        players.Clear();
        weapons.Clear();
        thrownWeapons.Clear();

        RefreshCreatures();
        RefreshWeapons();
    }

    private void RefreshCreatures()
    {
        if (room.abstractRoom?.creatures == null) return;
        CreatureScanCount++;

        for (int i = 0; i < room.abstractRoom.creatures.Count; i++)
        {
            Creature creature = room.abstractRoom.creatures[i]?.realizedCreature;
            if (creature == null || creature.slatedForDeletetion || creature.room != room)
                continue;

            creatures.Add(creature);
            if (creature is DesertBatfly bat && !bat.dead)
                bats.Add(bat);
            if (creature is Player player && !player.dead)
                players.Add(player);
        }
    }

    private void RefreshWeapons()
    {
        if (room.physicalObjects == null) return;
        PhysicalObjectScanCount++;

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = room.physicalObjects[layer];
            if (objects == null) continue;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not Weapon weapon || weapon.slatedForDeletetion)
                    continue;

                weapons.Add(weapon);
                if (weapon.mode == Weapon.Mode.Thrown)
                    thrownWeapons.Add(weapon);
            }
        }
    }
}
