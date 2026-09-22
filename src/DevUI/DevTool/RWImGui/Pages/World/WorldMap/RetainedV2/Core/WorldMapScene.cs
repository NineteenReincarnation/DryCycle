using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained frontend projection of the detached map presentation snapshot.
/// It owns no authoring data and never writes back to the backend.
/// </summary>
internal sealed class WorldMapScene
{
    internal sealed class RoomNode
    {
        internal int RoomIndex;
        internal string Name = string.Empty;
        internal Num.Vector2 WorldPosition;
        internal int Layer;
        internal string Subregion = string.Empty;
        internal bool Disabled;
        internal bool OffScreenDen;
        internal bool CurrentRoom;
        internal bool Selected;
        internal EditorMapRoomNodeSnapshot[] Ports = Array.Empty<EditorMapRoomNodeSnapshot>();
        internal int PortFingerprint;
        internal long TransformRevision;
        internal long MetadataRevision;
        internal long PortRevision;
    }

    internal sealed class ConnectionNode
    {
        internal string Id = string.Empty;
        internal int FromRoomIndex;
        internal int FromNodeIndex;
        internal int ToRoomIndex;
        internal int ToNodeIndex;
        internal WorldConnectionDirection Direction;
        internal bool Ambiguous;
        internal long Revision;
    }

    private readonly Dictionary<int, RoomNode> rooms = new();
    private readonly Dictionary<string, ConnectionNode> connections =
        new(StringComparer.Ordinal);

    internal string Region { get; private set; } = string.Empty;
    internal IReadOnlyDictionary<int, RoomNode> Rooms => rooms;
    internal IReadOnlyDictionary<string, ConnectionNode> Connections => connections;
    internal WorldMapViewTransform ViewTransform { get; private set; }
    internal long SceneRevision { get; private set; }
    internal long ViewRevision { get; private set; }

    internal bool TryGetRoom(int roomIndex, out RoomNode room) =>
        rooms.TryGetValue(roomIndex, out room);

    internal bool TryGetConnection(string id, out ConnectionNode connection)
    {
        connection = null;
        return !string.IsNullOrEmpty(id) && connections.TryGetValue(id, out connection);
    }

    internal void SetRegion(string region)
    {
        Region = region ?? string.Empty;
    }

    internal void SetViewTransform(WorldMapViewTransform transform)
    {
        if (ViewTransform.Equals(transform)) return;
        ViewTransform = transform;
        unchecked { ViewRevision++; }
    }

    internal RoomNode GetOrCreateRoom(int roomIndex)
    {
        if (rooms.TryGetValue(roomIndex, out RoomNode node))
            return node;

        node = new RoomNode { RoomIndex = roomIndex };
        rooms.Add(roomIndex, node);
        unchecked { SceneRevision++; }
        return node;
    }

    internal ConnectionNode GetOrCreateConnection(string id)
    {
        id ??= string.Empty;
        if (connections.TryGetValue(id, out ConnectionNode node))
            return node;

        node = new ConnectionNode { Id = id };
        connections.Add(id, node);
        unchecked { SceneRevision++; }
        return node;
    }

    internal bool RemoveRoom(int roomIndex)
    {
        if (!rooms.Remove(roomIndex)) return false;
        unchecked { SceneRevision++; }
        return true;
    }

    internal bool RemoveConnection(string id)
    {
        if (string.IsNullOrEmpty(id) || !connections.Remove(id)) return false;
        unchecked { SceneRevision++; }
        return true;
    }

    internal void MarkSceneChanged()
    {
        unchecked { SceneRevision++; }
    }

    internal void Reset(string region = "")
    {
        rooms.Clear();
        connections.Clear();
        Region = region ?? string.Empty;
        ViewTransform = default;
        unchecked
        {
            SceneRevision++;
            ViewRevision++;
        }
    }
}
