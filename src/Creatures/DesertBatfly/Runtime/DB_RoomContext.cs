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
///
/// Snapshot discovery is low-frequency, but membership validity is cheap and live: an object
/// that dies, changes room, enters deletion, or stops being a thrown weapon is pruned when a
/// consumer asks for that view. Cached candidate discovery must never become cached legality.
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

    internal IReadOnlyList<Creature> Creatures
    {
        get { PruneCreatures(); return creatures; }
    }

    internal IReadOnlyList<DesertBatfly> Bats
    {
        get { PruneBats(); return bats; }
    }

    internal IReadOnlyList<Player> Players
    {
        get { PrunePlayers(); return players; }
    }

    internal IReadOnlyList<Weapon> Weapons
    {
        get { PruneWeapons(weapons, false); return weapons; }
    }

    internal IReadOnlyList<Weapon> ThrownWeapons
    {
        get { PruneWeapons(thrownWeapons, true); return thrownWeapons; }
    }

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
        PrunePlayers();
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
        PruneBats();
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
            if (!CurrentCreature(creature)) continue;

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
                if (objects[i] is not Weapon weapon || !CurrentWeapon(weapon))
                    continue;

                weapons.Add(weapon);
                if (weapon.mode == Weapon.Mode.Thrown)
                    thrownWeapons.Add(weapon);
            }
        }
    }

    private bool CurrentCreature(Creature creature)
        => creature != null && !creature.slatedForDeletetion && creature.room == room;

    private bool CurrentWeapon(Weapon weapon)
        => weapon != null && !weapon.slatedForDeletetion && weapon.room == room;

    private void PruneCreatures()
    {
        for (int i = creatures.Count - 1; i >= 0; i--)
            if (!CurrentCreature(creatures[i])) creatures.RemoveAt(i);
    }

    private void PruneBats()
    {
        for (int i = bats.Count - 1; i >= 0; i--)
        {
            DesertBatfly bat = bats[i];
            if (!CurrentCreature(bat) || bat.dead) bats.RemoveAt(i);
        }
    }

    private void PrunePlayers()
    {
        for (int i = players.Count - 1; i >= 0; i--)
        {
            Player player = players[i];
            if (!CurrentCreature(player) || player.dead) players.RemoveAt(i);
        }
    }

    private void PruneWeapons(List<Weapon> list, bool thrownOnly)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            Weapon weapon = list[i];
            if (!CurrentWeapon(weapon) || (thrownOnly && weapon.mode != Weapon.Mode.Thrown))
                list.RemoveAt(i);
        }
    }
}
