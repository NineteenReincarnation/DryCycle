using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Frontend-only retained scene invalidation. This is derived state, never an authoring revision.
/// Pan/zoom is intentionally absent: view-transform changes do not belong to the scene dirty graph.
/// </summary>
internal sealed class WorldMapDirtySet
{
    internal readonly HashSet<int> RoomTransforms = new();
    internal readonly HashSet<int> RoomMetadata = new();
    internal readonly HashSet<int> RoomPorts = new();
    internal readonly HashSet<int> RemovedRooms = new();
    internal readonly HashSet<string> Connections = new(StringComparer.Ordinal);
    internal readonly HashSet<string> RemovedConnections = new(StringComparer.Ordinal);

    internal bool FullRebuild { get; set; }
    internal bool TopologyChanged { get; set; }

    internal bool IsEmpty =>
        !FullRebuild &&
        !TopologyChanged &&
        RoomTransforms.Count == 0 &&
        RoomMetadata.Count == 0 &&
        RoomPorts.Count == 0 &&
        RemovedRooms.Count == 0 &&
        Connections.Count == 0 &&
        RemovedConnections.Count == 0;

    internal int ChangeCount =>
        RoomTransforms.Count +
        RoomMetadata.Count +
        RoomPorts.Count +
        RemovedRooms.Count +
        Connections.Count +
        RemovedConnections.Count;

    internal void Clear()
    {
        RoomTransforms.Clear();
        RoomMetadata.Clear();
        RoomPorts.Clear();
        RemovedRooms.Clear();
        Connections.Clear();
        RemovedConnections.Clear();
        FullRebuild = false;
        TopologyChanged = false;
    }

    internal void MergeFrom(WorldMapDirtySet other)
    {
        if (other == null || other.IsEmpty) return;

        FullRebuild |= other.FullRebuild;
        TopologyChanged |= other.TopologyChanged;
        RoomTransforms.UnionWith(other.RoomTransforms);
        RoomMetadata.UnionWith(other.RoomMetadata);
        RoomPorts.UnionWith(other.RoomPorts);
        RemovedRooms.UnionWith(other.RemovedRooms);
        Connections.UnionWith(other.Connections);
        RemovedConnections.UnionWith(other.RemovedConnections);
    }

    internal WorldMapDirtySet Clone()
    {
        WorldMapDirtySet clone = new()
        {
            FullRebuild = FullRebuild,
            TopologyChanged = TopologyChanged
        };
        clone.RoomTransforms.UnionWith(RoomTransforms);
        clone.RoomMetadata.UnionWith(RoomMetadata);
        clone.RoomPorts.UnionWith(RoomPorts);
        clone.RemovedRooms.UnionWith(RemovedRooms);
        clone.Connections.UnionWith(Connections);
        clone.RemovedConnections.UnionWith(RemovedConnections);
        return clone;
    }
}
