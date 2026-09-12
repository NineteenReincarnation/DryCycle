using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;

namespace DryCycle.DevUI.DevTool.Map;

public sealed class EditorMapRoomSnapshot
{
    public int RoomIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public int Layer { get; init; }
    public string Subregion { get; init; } = string.Empty;
    public bool OffScreenDen { get; init; }
    public bool Disabled { get; init; }
    public bool CurrentRoom { get; init; }
    public bool Selected { get; init; }
}

public sealed class EditorMapConnectionSnapshot
{
    public string ConnectionId { get; init; } = string.Empty;
    public int FromRoomIndex { get; init; }
    public int FromNodeIndex { get; init; } = -1;
    public int ToRoomIndex { get; init; }
    public int ToNodeIndex { get; init; } = -1;
    public WorldConnectionDirection Direction { get; init; } = WorldConnectionDirection.Bidirectional;
    public bool Explicit { get; init; }
    public bool Ambiguous { get; init; }
}

public sealed class EditorMapPresentationSnapshot
{
    public static readonly EditorMapPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public int SelectedRoomIndex { get; init; } = -1;
    public EditorMapRoomSnapshot[] Rooms { get; init; } = Array.Empty<EditorMapRoomSnapshot>();
    public EditorMapConnectionSnapshot[] Connections { get; init; } = Array.Empty<EditorMapConnectionSnapshot>();
}

internal sealed class MapEditorState
{
    internal int SelectedRoomIndex = -1;
}

internal static class MapEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, MapEditorState> states = new();

    internal static MapEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new MapEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, MapEditorState>();
}

public static class MapEditorPresentationHub
{
    private sealed class DirectedConnectionArc
    {
        internal int FromRoom;
        internal int FromNode;
        internal int ToRoom;
    }

    private static volatile EditorMapPresentationSnapshot current = EditorMapPresentationSnapshot.Empty;

    public static EditorMapPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Map || session.Owner?.activePage is not MapPage page || page.world == null)
        {
            current = EditorMapPresentationSnapshot.Empty;
            return;
        }

        MapEditorState state = MapEditorStateHub.Get(session);
        List<EditorMapRoomSnapshot> rooms = new();
        HashSet<int> roomIndices = new();
        Dictionary<string, int> roomIndexByName = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> disabled = new(StringComparer.Ordinal);
        if (page.world.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrEmpty(name)) disabled.Add(name);
            }
        }

        int currentRoomIndex = session.Room?.abstractRoom?.index ?? -1;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            roomIndices.Add(room.index);
            if (!string.IsNullOrWhiteSpace(room.name)) roomIndexByName[room.name] = room.index;
            rooms.Add(new EditorMapRoomSnapshot
            {
                RoomIndex = room.index,
                Name = room.name ?? string.Empty,
                X = panel.devPos.x,
                Y = panel.devPos.y,
                Layer = panel.layer,
                Subregion = room.subregionName ?? string.Empty,
                OffScreenDen = room.offScreenDen,
                Disabled = disabled.Contains(room.name ?? string.Empty),
                CurrentRoom = room.index == currentRoomIndex,
                Selected = room.index == state.SelectedRoomIndex
            });
        }

        if (state.SelectedRoomIndex >= 0 && !roomIndices.Contains(state.SelectedRoomIndex))
            state.SelectedRoomIndex = -1;

        List<EditorMapConnectionSnapshot> connections = BuildConnections(
            page.world,
            rooms,
            roomIndices,
            roomIndexByName);

        current = new EditorMapPresentationSnapshot
        {
            Available = true,
            RegionName = page.world.name ?? string.Empty,
            SelectedRoomIndex = state.SelectedRoomIndex,
            Rooms = rooms.ToArray(),
            Connections = connections.ToArray()
        };
    }

    private static List<EditorMapConnectionSnapshot> BuildConnections(
        World world,
        List<EditorMapRoomSnapshot> rooms,
        HashSet<int> roomIndices,
        Dictionary<string, int> roomIndexByName)
    {
        List<EditorMapConnectionSnapshot> result = new();
        HashSet<long> reservedEndpoints = new();

        WorldConnectionEdge[] explicitEdges = WorldTopologyRegistry.GetRegionEdges(world.name);
        for (int i = 0; i < explicitEdges.Length; i++)
        {
            WorldConnectionEdge edge = explicitEdges[i];
            if (!roomIndexByName.TryGetValue(edge.A.Room, out int aRoom) ||
                !roomIndexByName.TryGetValue(edge.B.Room, out int bRoom))
                continue;

            reservedEndpoints.Add(EndpointKey(aRoom, edge.A.NodeIndex));
            reservedEndpoints.Add(EndpointKey(bRoom, edge.B.NodeIndex));
            result.Add(new EditorMapConnectionSnapshot
            {
                ConnectionId = "explicit:" + edge.Id,
                FromRoomIndex = aRoom,
                FromNodeIndex = edge.A.NodeIndex,
                ToRoomIndex = bRoom,
                ToNodeIndex = edge.B.NodeIndex,
                Direction = edge.Direction,
                Explicit = true,
                Ambiguous = false
            });
        }

        List<DirectedConnectionArc> arcs = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            AbstractRoom room = world.GetAbstractRoom(rooms[i].RoomIndex);
            if (room?.connections == null) continue;

            for (int node = 0; node < room.connections.Length; node++)
            {
                if (reservedEndpoints.Contains(EndpointKey(room.index, node))) continue;
                int other = room.connections[node];
                if (other < 0 || !roomIndices.Contains(other)) continue;
                arcs.Add(new DirectedConnectionArc
                {
                    FromRoom = room.index,
                    FromNode = node,
                    ToRoom = other
                });
            }
        }

        bool[] used = new bool[arcs.Count];
        for (int i = 0; i < arcs.Count; i++)
        {
            if (used[i]) continue;
            DirectedConnectionArc arc = arcs[i];
            List<int> reverse = new();
            for (int j = 0; j < arcs.Count; j++)
            {
                if (i == j || used[j]) continue;
                if (arcs[j].FromRoom == arc.ToRoom && arcs[j].ToRoom == arc.FromRoom)
                    reverse.Add(j);
            }

            if (reverse.Count == 1)
            {
                int reverseIndex = reverse[0];
                DirectedConnectionArc back = arcs[reverseIndex];
                used[i] = true;
                used[reverseIndex] = true;
                result.Add(new EditorMapConnectionSnapshot
                {
                    ConnectionId = LegacyId(arc.FromRoom, arc.FromNode, back.FromRoom, back.FromNode, true),
                    FromRoomIndex = arc.FromRoom,
                    FromNodeIndex = arc.FromNode,
                    ToRoomIndex = arc.ToRoom,
                    ToNodeIndex = back.FromNode,
                    Direction = WorldConnectionDirection.Bidirectional,
                    Explicit = false,
                    Ambiguous = false
                });
                continue;
            }

            used[i] = true;
            result.Add(new EditorMapConnectionSnapshot
            {
                ConnectionId = LegacyId(arc.FromRoom, arc.FromNode, arc.ToRoom, -1, false),
                FromRoomIndex = arc.FromRoom,
                FromNodeIndex = arc.FromNode,
                ToRoomIndex = arc.ToRoom,
                ToNodeIndex = -1,
                Direction = WorldConnectionDirection.AToB,
                Explicit = false,
                Ambiguous = true
            });
        }

        return result;
    }

    private static long EndpointKey(int roomIndex, int nodeIndex) =>
        ((long)(uint)roomIndex << 32) | (uint)nodeIndex;

    private static string LegacyId(int aRoom, int aNode, int bRoom, int bNode, bool bidirectional) =>
        "legacy:" + aRoom + ":" + aNode + ":" + bRoom + ":" + bNode + ":" +
        (bidirectional ? "both" : "oneway");

    internal static void Clear() => current = EditorMapPresentationSnapshot.Empty;
}

public enum MapEditorCommandKind
{
    SelectRoom,
    SetRoomPosition,
    SetRoomLayer,
    SetRoomSubregion
}

public readonly struct MapEditorCommand
{
    public MapEditorCommand(
        MapEditorCommandKind kind,
        int roomIndex = -1,
        string text = null,
        EditorPropertyValue value = default)
    {
        Kind = kind;
        RoomIndex = roomIndex;
        Text = text;
        Value = value;
    }

    public MapEditorCommandKind Kind { get; }
    public int RoomIndex { get; }
    public string Text { get; }
    public EditorPropertyValue Value { get; }
}

public static class MapEditorCommandQueue
{
    private static readonly ConcurrentQueue<MapEditorCommand> queue = new();

    public static void Enqueue(MapEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out MapEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case MapEditorCommandKind.SelectRoom:
                        MapEditorActions.SelectRoom(session, command.RoomIndex);
                        break;
                    case MapEditorCommandKind.SetRoomPosition:
                        MapEditorActions.SetRoomPosition(session, command.RoomIndex, command.Value);
                        break;
                    case MapEditorCommandKind.SetRoomLayer:
                        MapEditorActions.SetRoomLayer(session, command.RoomIndex, command.Value.Integer);
                        break;
                    case MapEditorCommandKind.SetRoomSubregion:
                        MapEditorActions.SetRoomSubregion(session, command.RoomIndex, command.Text);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool map command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
