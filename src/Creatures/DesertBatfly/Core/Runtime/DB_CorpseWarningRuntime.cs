using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Lifecycle owner for realized death-site warning objects created by Intimidation.
///
/// Intimidation still owns warning behavior. This registry owns only teardown: mortality
/// consumers weakly remember rooms that may receive a warning, and Reset/Disable destroys
/// any surviving CorpseWarning already inserted into those Room update lists. No Room is
/// kept alive solely by this cleanup registry.
/// </summary>
internal static class DB_CorpseWarningRuntime
{
    private sealed class RoomStamp { }

    private static ConditionalWeakTable<Room, RoomStamp> knownRooms = new();
    private static readonly List<WeakReference> rooms = new(8);

    internal static int TrackedRoomCount => rooms.Count;

    internal static void TrackRoom(Room room)
    {
        if (room == null || knownRooms.TryGetValue(room, out _)) return;
        knownRooms.Add(room, new RoomStamp());
        rooms.Add(new WeakReference(room));
    }

    internal static void Reset()
    {
        for (int i = rooms.Count - 1; i >= 0; i--)
        {
            Room room = rooms[i].Target as Room;
            if (room?.updateList == null) continue;

            // Snapshot the current list because Room removes slated objects during its own
            // update pass. Destroy() only marks them, preserving Rain World's normal removal.
            for (int j = room.updateList.Count - 1; j >= 0; j--)
            {
                UpdatableAndDeletable item = room.updateList[j];
                if (!IsCorpseWarning(item) || item.slatedForDeletetion) continue;
                try { item.Destroy(); }
                catch { }
            }
        }

        rooms.Clear();
        knownRooms = new ConditionalWeakTable<Room, RoomStamp>();
    }

    internal static bool IsCorpseWarning(UpdatableAndDeletable item)
    {
        Type type = item?.GetType();
        return type != null &&
               type.DeclaringType == typeof(DB_FearRuntime) &&
               type.Name == "CorpseWarning";
    }
}
