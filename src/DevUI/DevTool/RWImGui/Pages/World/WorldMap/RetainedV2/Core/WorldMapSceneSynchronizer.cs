using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Converts detached presentation snapshots into a retained frontend scene.
///
/// Stable frame fast path is O(1): if the snapshot identity and frontend room-layout revision are
/// unchanged, only the view transform is synchronized. Therefore pan/zoom cannot trigger room or
/// connection comparison/rebuild work.
/// </summary>
internal sealed class WorldMapSceneSynchronizer
{
    private readonly HashSet<int> aliveRooms = new();
    private readonly HashSet<string> aliveConnections = new(StringComparer.Ordinal);
    private readonly List<int> staleRooms = new();
    private readonly List<string> staleConnections = new();

    private EditorMapPresentationSnapshot observedSnapshot;
    private EditorMapRoomSnapshot[] observedRooms;
    private EditorMapConnectionSnapshot[] observedConnections;
    private long observedLayoutRevision = long.MinValue;
    private string observedRegion = string.Empty;

    internal WorldMapDirtySet Synchronize(
        WorldMapScene scene,
        EditorMapPresentationSnapshot snapshot,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        long layoutRevision,
        int interactiveRoom,
        WorldMapViewTransform viewTransform)
    {
        WorldMapDirtySet dirty = new();
        if (scene == null) return dirty;

        scene.SetViewTransform(viewTransform);

        if (snapshot?.Available != true)
        {
            if (scene.Rooms.Count > 0 || scene.Connections.Count > 0 || scene.Region.Length > 0)
            {
                scene.Reset();
                dirty.FullRebuild = true;
            }
            ResetObservedState();
            return dirty;
        }

        string region = snapshot.RegionName ?? string.Empty;
        bool regionChanged = !string.Equals(
            observedRegion,
            region,
            StringComparison.OrdinalIgnoreCase);

        if (regionChanged)
        {
            scene.Reset(region);
            observedSnapshot = null;
            observedRooms = null;
            observedConnections = null;
            observedLayoutRevision = long.MinValue;
            dirty.FullRebuild = true;
        }
        else
        {
            scene.SetRegion(region);
        }

        bool sameSnapshot = ReferenceEquals(observedSnapshot, snapshot);
        bool sameLayout = observedLayoutRevision == layoutRevision;

        // Critical Phase 1 invariant: pan/zoom reaches this branch and stops here.
        if (!regionChanged && sameSnapshot && sameLayout)
            return dirty;

        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapConnectionSnapshot[] connections =
            snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();

        bool roomsChanged = regionChanged || !ReferenceEquals(observedRooms, rooms) || !sameSnapshot;
        bool connectionsChanged =
            regionChanged ||
            !ReferenceEquals(observedConnections, connections) ||
            !sameSnapshot;

        if (roomsChanged)
        {
            SynchronizeRooms(scene, rooms, localPositions, dirty, removeMissing: true);
        }
        else if (!sameLayout)
        {
            // During an interactive drag only one room can move. Avoid scanning the region merely
            // because the frontend layout revision advanced.
            if (interactiveRoom >= 0)
                SynchronizeSingleRoomPosition(
                    scene,
                    interactiveRoom,
                    localPositions,
                    dirty);
            else
                SynchronizeRoomPositions(scene, rooms, localPositions, dirty);
        }

        if (connectionsChanged)
            SynchronizeConnections(scene, connections, dirty);

        if (!dirty.IsEmpty)
            scene.MarkSceneChanged();

        observedSnapshot = snapshot;
        observedRooms = rooms;
        observedConnections = connections;
        observedLayoutRevision = layoutRevision;
        observedRegion = region;
        return dirty;
    }

    internal void Reset()
    {
        ResetObservedState();
        aliveRooms.Clear();
        aliveConnections.Clear();
        staleRooms.Clear();
        staleConnections.Clear();
    }

    private void SynchronizeRooms(
        WorldMapScene scene,
        EditorMapRoomSnapshot[] rooms,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        WorldMapDirtySet dirty,
        bool removeMissing)
    {
        aliveRooms.Clear();

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null) continue;

            aliveRooms.Add(room.RoomIndex);
            bool existed = scene.TryGetRoom(room.RoomIndex, out WorldMapScene.RoomNode node);
            node ??= scene.GetOrCreateRoom(room.RoomIndex);
            Num.Vector2 position = ResolvePosition(room, localPositions);

            if (!existed || !Approximately(node.WorldPosition, position))
            {
                node.WorldPosition = position;
                unchecked { node.TransformRevision++; }
                dirty.RoomTransforms.Add(room.RoomIndex);
            }

            if (!existed || ApplyRoomMetadata(node, room))
            {
                if (!existed) ApplyRoomMetadata(node, room);
                dirty.RoomMetadata.Add(room.RoomIndex);
            }

            int portFingerprint = ComputePortFingerprint(room.Nodes);
            if (!existed ||
                node.PortFingerprint != portFingerprint ||
                !ReferenceEquals(node.Ports, room.Nodes))
            {
                node.Ports = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
                node.PortFingerprint = portFingerprint;
                unchecked { node.PortRevision++; }
                dirty.RoomPorts.Add(room.RoomIndex);
            }
        }

        if (!removeMissing) return;

        staleRooms.Clear();
        foreach (int roomIndex in scene.Rooms.Keys)
            if (!aliveRooms.Contains(roomIndex))
                staleRooms.Add(roomIndex);

        for (int i = 0; i < staleRooms.Count; i++)
        {
            int roomIndex = staleRooms[i];
            if (scene.RemoveRoom(roomIndex))
                dirty.RemovedRooms.Add(roomIndex);
        }
    }

    private static void SynchronizeSingleRoomPosition(
        WorldMapScene scene,
        int roomIndex,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        WorldMapDirtySet dirty)
    {
        if (!scene.TryGetRoom(roomIndex, out WorldMapScene.RoomNode node) ||
            localPositions == null ||
            !localPositions.TryGetValue(roomIndex, out Num.Vector2 position) ||
            Approximately(node.WorldPosition, position))
            return;

        node.WorldPosition = position;
        unchecked { node.TransformRevision++; }
        dirty.RoomTransforms.Add(roomIndex);
    }

    private static void SynchronizeRoomPositions(
        WorldMapScene scene,
        EditorMapRoomSnapshot[] rooms,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        WorldMapDirtySet dirty)
    {
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !scene.TryGetRoom(room.RoomIndex, out WorldMapScene.RoomNode node))
                continue;

            Num.Vector2 position = ResolvePosition(room, localPositions);
            if (Approximately(node.WorldPosition, position))
                continue;

            node.WorldPosition = position;
            unchecked { node.TransformRevision++; }
            dirty.RoomTransforms.Add(room.RoomIndex);
        }
    }

    private void SynchronizeConnections(
        WorldMapScene scene,
        EditorMapConnectionSnapshot[] connections,
        WorldMapDirtySet dirty)
    {
        aliveConnections.Clear();

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null || string.IsNullOrEmpty(connection.ConnectionId))
                continue;

            string id = connection.ConnectionId;
            aliveConnections.Add(id);
            bool existed = scene.TryGetConnection(id, out WorldMapScene.ConnectionNode node);
            node ??= scene.GetOrCreateConnection(id);

            if (!existed || !ConnectionEquals(node, connection))
            {
                node.FromRoomIndex = connection.FromRoomIndex;
                node.FromNodeIndex = connection.FromNodeIndex;
                node.ToRoomIndex = connection.ToRoomIndex;
                node.ToNodeIndex = connection.ToNodeIndex;
                node.Direction = connection.Direction;
                node.Ambiguous = connection.Ambiguous;
                unchecked { node.Revision++; }
                dirty.Connections.Add(id);
                dirty.TopologyChanged = true;
            }
        }

        staleConnections.Clear();
        foreach (string id in scene.Connections.Keys)
            if (!aliveConnections.Contains(id))
                staleConnections.Add(id);

        for (int i = 0; i < staleConnections.Count; i++)
        {
            string id = staleConnections[i];
            if (!scene.RemoveConnection(id)) continue;
            dirty.RemovedConnections.Add(id);
            dirty.TopologyChanged = true;
        }
    }

    private static bool ApplyRoomMetadata(
        WorldMapScene.RoomNode node,
        EditorMapRoomSnapshot room)
    {
        string name = room.Name ?? string.Empty;
        string subregion = room.Subregion ?? string.Empty;

        bool changed =
            !string.Equals(node.Name, name, StringComparison.Ordinal) ||
            node.Layer != room.Layer ||
            !string.Equals(node.Subregion, subregion, StringComparison.Ordinal) ||
            node.Disabled != room.Disabled ||
            node.OffScreenDen != room.OffScreenDen ||
            node.CurrentRoom != room.CurrentRoom ||
            node.Selected != room.Selected;

        if (!changed) return false;

        node.Name = name;
        node.Layer = room.Layer;
        node.Subregion = subregion;
        node.Disabled = room.Disabled;
        node.OffScreenDen = room.OffScreenDen;
        node.CurrentRoom = room.CurrentRoom;
        node.Selected = room.Selected;
        unchecked { node.MetadataRevision++; }
        return true;
    }

    private static bool ConnectionEquals(
        WorldMapScene.ConnectionNode node,
        EditorMapConnectionSnapshot connection) =>
        node.FromRoomIndex == connection.FromRoomIndex &&
        node.FromNodeIndex == connection.FromNodeIndex &&
        node.ToRoomIndex == connection.ToRoomIndex &&
        node.ToNodeIndex == connection.ToNodeIndex &&
        node.Direction == connection.Direction &&
        node.Ambiguous == connection.Ambiguous;

    private static Num.Vector2 ResolvePosition(
        EditorMapRoomSnapshot room,
        IReadOnlyDictionary<int, Num.Vector2> localPositions)
    {
        if (localPositions != null &&
            localPositions.TryGetValue(room.RoomIndex, out Num.Vector2 local))
            return local;

        return new Num.Vector2(room.X, room.Y);
    }

    private static int ComputePortFingerprint(EditorMapRoomNodeSnapshot[] ports)
    {
        unchecked
        {
            int hash = 17;
            int count = ports?.Length ?? 0;
            hash = hash * 397 ^ count;
            for (int i = 0; i < count; i++)
            {
                EditorMapRoomNodeSnapshot port = ports[i];
                if (port == null)
                {
                    hash = hash * 397;
                    continue;
                }

                hash = hash * 397 ^ port.NodeIndex;
                hash = hash * 397 ^ (port.Exit ? 1 : 0);
                hash = hash * 397 ^ port.ConnectedRoomIndex;
            }
            return hash;
        }
    }

    private static bool Approximately(Num.Vector2 a, Num.Vector2 b) =>
        Num.Vector2.DistanceSquared(a, b) <= 0.0001f;

    private void ResetObservedState()
    {
        observedSnapshot = null;
        observedRooms = null;
        observedConnections = null;
        observedLayoutRevision = long.MinValue;
        observedRegion = string.Empty;
    }
}
