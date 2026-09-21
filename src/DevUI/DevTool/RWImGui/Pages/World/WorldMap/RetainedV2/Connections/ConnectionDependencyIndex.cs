using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Room -> connection dependency index used for local route invalidation.
/// </summary>
internal sealed class ConnectionDependencyIndex
{
    private readonly Dictionary<int, HashSet<string>> byRoom = new();

    internal void Rebuild(WorldMapScene scene)
    {
        byRoom.Clear();
        if (scene == null) return;

        foreach (WorldMapScene.ConnectionNode connection in scene.Connections.Values)
        {
            Add(connection.FromRoomIndex, connection.Id);
            if (connection.ToRoomIndex != connection.FromRoomIndex)
                Add(connection.ToRoomIndex, connection.Id);
        }
    }

    internal IEnumerable<string> GetForRoom(int roomIndex)
    {
        if (byRoom.TryGetValue(roomIndex, out HashSet<string> ids))
            return ids;
        return Array.Empty<string>();
    }

    internal void Reset() => byRoom.Clear();

    private void Add(int roomIndex, string id)
    {
        if (roomIndex < 0 || string.IsNullOrEmpty(id)) return;
        if (!byRoom.TryGetValue(roomIndex, out HashSet<string> ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            byRoom.Add(roomIndex, ids);
        }
        ids.Add(id);
    }
}
