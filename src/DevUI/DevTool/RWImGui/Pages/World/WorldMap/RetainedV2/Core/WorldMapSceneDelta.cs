using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Immutable cross-thread scene delta.
///
/// The RWImGUI render thread captures only changed retained nodes. Unity's main thread applies the
/// delta to its own scene mirror, so GPU/resource code never enumerates dictionaries that the render
/// thread is mutating.
/// </summary>
internal sealed class WorldMapSceneDelta
{
    internal readonly struct RoomState
    {
        internal RoomState(WorldMapScene.RoomNode room)
        {
            RoomIndex = room.RoomIndex;
            Name = room.Name ?? string.Empty;
            WorldPosition = room.WorldPosition;
            Layer = room.Layer;
            Subregion = room.Subregion ?? string.Empty;
            Disabled = room.Disabled;
            OffScreenDen = room.OffScreenDen;
            CurrentRoom = room.CurrentRoom;
            Selected = room.Selected;
            Ports = room.Ports ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            PortFingerprint = room.PortFingerprint;
            TransformRevision = room.TransformRevision;
            MetadataRevision = room.MetadataRevision;
            PortRevision = room.PortRevision;
        }

        internal int RoomIndex { get; }
        internal string Name { get; }
        internal Num.Vector2 WorldPosition { get; }
        internal int Layer { get; }
        internal string Subregion { get; }
        internal bool Disabled { get; }
        internal bool OffScreenDen { get; }
        internal bool CurrentRoom { get; }
        internal bool Selected { get; }
        internal EditorMapRoomNodeSnapshot[] Ports { get; }
        internal int PortFingerprint { get; }
        internal long TransformRevision { get; }
        internal long MetadataRevision { get; }
        internal long PortRevision { get; }
    }

    internal readonly struct ConnectionState
    {
        internal ConnectionState(WorldMapScene.ConnectionNode connection)
        {
            Id = connection.Id ?? string.Empty;
            FromRoomIndex = connection.FromRoomIndex;
            FromNodeIndex = connection.FromNodeIndex;
            ToRoomIndex = connection.ToRoomIndex;
            ToNodeIndex = connection.ToNodeIndex;
            Direction = connection.Direction;
            Ambiguous = connection.Ambiguous;
            Revision = connection.Revision;
        }

        internal string Id { get; }
        internal int FromRoomIndex { get; }
        internal int FromNodeIndex { get; }
        internal int ToRoomIndex { get; }
        internal int ToNodeIndex { get; }
        internal WorldConnectionDirection Direction { get; }
        internal bool Ambiguous { get; }
        internal long Revision { get; }
    }

    private WorldMapSceneDelta(
        string region,
        bool fullRebuild,
        bool topologyChanged,
        RoomState[] rooms,
        int[] removedRooms,
        ConnectionState[] connections,
        string[] removedConnections)
    {
        Region = region ?? string.Empty;
        FullRebuild = fullRebuild;
        TopologyChanged = topologyChanged;
        Rooms = rooms ?? Array.Empty<RoomState>();
        RemovedRooms = removedRooms ?? Array.Empty<int>();
        Connections = connections ?? Array.Empty<ConnectionState>();
        RemovedConnections = removedConnections ?? Array.Empty<string>();
    }

    internal string Region { get; }
    internal bool FullRebuild { get; }
    internal bool TopologyChanged { get; }
    internal RoomState[] Rooms { get; }
    internal int[] RemovedRooms { get; }
    internal ConnectionState[] Connections { get; }
    internal string[] RemovedConnections { get; }

    internal bool IsEmpty =>
        !FullRebuild &&
        Rooms.Length == 0 &&
        RemovedRooms.Length == 0 &&
        Connections.Length == 0 &&
        RemovedConnections.Length == 0;

    internal static WorldMapSceneDelta Capture(
        WorldMapScene scene,
        WorldMapDirtySet dirty)
    {
        if (scene == null || dirty == null || dirty.IsEmpty)
            return null;

        HashSet<int> roomIds = new();
        HashSet<string> connectionIds = new(StringComparer.Ordinal);

        if (dirty.FullRebuild)
        {
            foreach (int id in scene.Rooms.Keys)
                roomIds.Add(id);
            foreach (string id in scene.Connections.Keys)
                connectionIds.Add(id);
        }
        else
        {
            roomIds.UnionWith(dirty.RoomTransforms);
            roomIds.UnionWith(dirty.RoomMetadata);
            roomIds.UnionWith(dirty.RoomPorts);
            connectionIds.UnionWith(dirty.Connections);
        }

        List<RoomState> rooms = new(roomIds.Count);
        foreach (int id in roomIds)
        {
            if (scene.TryGetRoom(id, out WorldMapScene.RoomNode room))
                rooms.Add(new RoomState(room));
        }

        List<ConnectionState> connections = new(connectionIds.Count);
        foreach (string id in connectionIds)
        {
            if (scene.TryGetConnection(id, out WorldMapScene.ConnectionNode connection))
                connections.Add(new ConnectionState(connection));
        }

        int[] removedRooms = new int[dirty.RemovedRooms.Count];
        dirty.RemovedRooms.CopyTo(removedRooms);

        string[] removedConnections = new string[dirty.RemovedConnections.Count];
        dirty.RemovedConnections.CopyTo(removedConnections);

        return new WorldMapSceneDelta(
            scene.Region,
            dirty.FullRebuild,
            dirty.TopologyChanged,
            rooms.ToArray(),
            removedRooms,
            connections.ToArray(),
            removedConnections);
    }

    internal void Apply(WorldMapScene target, WorldMapDirtySet applied)
    {
        if (target == null || applied == null)
            return;

        if (FullRebuild)
        {
            target.Reset(Region);
            applied.FullRebuild = true;
        }
        else
        {
            target.SetRegion(Region);
        }

        applied.TopologyChanged |= TopologyChanged;

        for (int i = 0; i < RemovedConnections.Length; i++)
        {
            string id = RemovedConnections[i];
            if (target.RemoveConnection(id))
                applied.RemovedConnections.Add(id);
        }

        for (int i = 0; i < RemovedRooms.Length; i++)
        {
            int roomIndex = RemovedRooms[i];
            if (target.RemoveRoom(roomIndex))
                applied.RemovedRooms.Add(roomIndex);
        }

        for (int i = 0; i < Rooms.Length; i++)
        {
            RoomState state = Rooms[i];
            WorldMapScene.RoomNode node = target.GetOrCreateRoom(state.RoomIndex);

            bool transformChanged =
                node.TransformRevision != state.TransformRevision ||
                Num.Vector2.DistanceSquared(node.WorldPosition, state.WorldPosition) > 0.0001f;
            bool metadataChanged =
                node.MetadataRevision != state.MetadataRevision;
            bool portsChanged =
                node.PortRevision != state.PortRevision ||
                node.PortFingerprint != state.PortFingerprint;

            node.Name = state.Name;
            node.WorldPosition = state.WorldPosition;
            node.Layer = state.Layer;
            node.Subregion = state.Subregion;
            node.Disabled = state.Disabled;
            node.OffScreenDen = state.OffScreenDen;
            node.CurrentRoom = state.CurrentRoom;
            node.Selected = state.Selected;
            node.Ports = state.Ports;
            node.PortFingerprint = state.PortFingerprint;
            node.TransformRevision = state.TransformRevision;
            node.MetadataRevision = state.MetadataRevision;
            node.PortRevision = state.PortRevision;

            if (FullRebuild || transformChanged) applied.RoomTransforms.Add(state.RoomIndex);
            if (FullRebuild || metadataChanged) applied.RoomMetadata.Add(state.RoomIndex);
            if (FullRebuild || portsChanged) applied.RoomPorts.Add(state.RoomIndex);
        }

        for (int i = 0; i < Connections.Length; i++)
        {
            ConnectionState state = Connections[i];
            WorldMapScene.ConnectionNode node = target.GetOrCreateConnection(state.Id);
            bool changed =
                node.Revision != state.Revision ||
                node.FromRoomIndex != state.FromRoomIndex ||
                node.FromNodeIndex != state.FromNodeIndex ||
                node.ToRoomIndex != state.ToRoomIndex ||
                node.ToNodeIndex != state.ToNodeIndex ||
                node.Direction != state.Direction ||
                node.Ambiguous != state.Ambiguous;

            node.FromRoomIndex = state.FromRoomIndex;
            node.FromNodeIndex = state.FromNodeIndex;
            node.ToRoomIndex = state.ToRoomIndex;
            node.ToNodeIndex = state.ToNodeIndex;
            node.Direction = state.Direction;
            node.Ambiguous = state.Ambiguous;
            node.Revision = state.Revision;

            if (FullRebuild || changed)
                applied.Connections.Add(state.Id);
        }

        if (!applied.IsEmpty)
            target.MarkSceneChanged();
    }
}
