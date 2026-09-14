using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;

namespace DryCycle.DevUI.DevTool.Map;

public sealed class EditorMapRoomNodeSnapshot
{
    public int NodeIndex { get; init; }
    public string Type { get; init; } = string.Empty;
    public bool Exit { get; init; }
    public int ConnectedRoomIndex { get; init; } = -1;
}

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
    public EditorMapRoomNodeSnapshot[] Nodes { get; init; } = Array.Empty<EditorMapRoomNodeSnapshot>();
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
        internal int ToNode = -1;
        internal bool AmbiguousPair;
    }

    // Revision invalidation is the normal fast path. The full semantic fingerprint remains as a
    // bounded compatibility audit for external code that mutates map data without using DryCycle's
    // commands. At 40 FPS this checks roughly every 0.75 s on otherwise completely stable maps.
    private const int IntegrityAuditInterval = 30;

    private static volatile EditorMapPresentationSnapshot current = EditorMapPresentationSnapshot.Empty;
    private static EditorSession observedSession;
    private static MapPage observedPage;
    private static global::World observedWorld;
    private static long observedRevision;
    private static ulong observedFingerprint;
    private static int observedWorldTextRevision = int.MinValue;
    private static int observedCurrentRoomIndex = int.MinValue;
    private static int observedSelectedRoomIndex = int.MinValue;
    private static int framesUntilIntegrityAudit = IntegrityAuditInterval;
    private static bool retainedValid;

    public static EditorMapPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Map || session.Owner?.activePage is not MapPage page || page.world == null)
        {
            if (retainedValid || !ReferenceEquals(current, EditorMapPresentationSnapshot.Empty))
            {
                current = EditorMapPresentationSnapshot.Empty;
                ResetRetainedState();
            }
            return;
        }

        MapEditorState state = MapEditorStateHub.Get(session);
        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
            EditorRevisionHub.Mark(session, EditorRevisionKind.Map);

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        int worldTextRevision = WorldTextRegistry.Revision;
        int currentRoomIndex = session.Room?.abstractRoom?.index ?? -1;
        int selectedRoomIndex = state?.SelectedRoomIndex ?? -1;

        bool fastStable =
            retainedValid &&
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedPage, page) &&
            ReferenceEquals(observedWorld, page.world) &&
            observedRevision == revision &&
            observedWorldTextRevision == worldTextRevision &&
            observedCurrentRoomIndex == currentRoomIndex &&
            observedSelectedRoomIndex == selectedRoomIndex &&
            current.Available;

        ulong fingerprint;
        if (fastStable)
        {
            framesUntilIntegrityAudit--;
            if (framesUntilIntegrityAudit > 0)
                return;

            framesUntilIntegrityAudit = IntegrityAuditInterval;
            fingerprint = ComputePresentationFingerprint(session, page, state);
            if (observedFingerprint == fingerprint)
                return;
        }
        else
        {
            framesUntilIntegrityAudit = IntegrityAuditInterval;
            fingerprint = ComputePresentationFingerprint(session, page, state);

            // A revision can be conservatively invalidated by an opaque compatibility writer even
            // when no semantic value changed. Preserve the existing fingerprint short-circuit so
            // such a dirty signal does not force allocation of the full room/connection graph.
            if (retainedValid &&
                ReferenceEquals(observedSession, session) &&
                ReferenceEquals(observedPage, page) &&
                ReferenceEquals(observedWorld, page.world) &&
                observedFingerprint == fingerprint &&
                observedWorldTextRevision == worldTextRevision &&
                current.Available)
            {
                observedRevision = revision;
                observedCurrentRoomIndex = currentRoomIndex;
                observedSelectedRoomIndex = selectedRoomIndex;
                return;
            }
        }

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
                Selected = room.index == state.SelectedRoomIndex,
                Nodes = BuildNodes(room)
            });
        }

        bool correctedSelection = false;
        if (state.SelectedRoomIndex >= 0 && !roomIndices.Contains(state.SelectedRoomIndex))
        {
            state.SelectedRoomIndex = -1;
            correctedSelection = true;
        }

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

        observedSession = session;
        observedPage = page;
        observedWorld = page.world;
        observedRevision = revision;
        observedFingerprint = correctedSelection
            ? ComputePresentationFingerprint(session, page, state)
            : fingerprint;
        // BuildConnections may lazily load world.txt for exact target-node resolution. Capture the
        // post-build revision so that first load does not cause an unnecessary second rebuild.
        observedWorldTextRevision = WorldTextRegistry.Revision;
        observedCurrentRoomIndex = currentRoomIndex;
        observedSelectedRoomIndex = state.SelectedRoomIndex;
        framesUntilIntegrityAudit = IntegrityAuditInterval;
        retainedValid = true;
    }

    /// <summary>
    /// Allocation-free semantic signature retained as an integrity audit. Normal stable frames are
    /// filtered by revisions before reaching this scan; the audit periodically detects external
    /// compatibility writes that cannot participate in DryCycle's revision protocol.
    /// </summary>
    private static ulong ComputePresentationFingerprint(
        EditorSession session,
        MapPage page,
        MapEditorState state)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, StringHash(page.world?.name));
            hash = Mix(hash, state?.SelectedRoomIndex ?? -1);
            hash = Mix(hash, session?.Room?.abstractRoom?.index ?? -1);

            var disabled = page.world?.DisabledMapRooms;
            hash = Mix(hash, disabled?.Count ?? 0);
            if (disabled != null)
            {
                for (int i = 0; i < disabled.Count; i++)
                    hash = Mix(hash, StringHash(disabled[i]));
            }

            hash = Mix(hash, page.subNodes?.Count ?? 0);
            if (page.subNodes == null) return hash;
            for (int i = 0; i < page.subNodes.Count; i++)
            {
                if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
                AbstractRoom room = panel.roomRep.room;
                hash = Mix(hash, room.index);
                hash = Mix(hash, StringHash(room.name));
                hash = Mix(hash, panel.devPos.x.GetHashCode());
                hash = Mix(hash, panel.devPos.y.GetHashCode());
                hash = Mix(hash, panel.layer);
                hash = Mix(hash, StringHash(room.subregionName));
                hash = Mix(hash, room.offScreenDen ? 1 : 0);

                AbstractRoomNode[] nodes = room.nodes;
                hash = Mix(hash, nodes?.Length ?? 0);
                if (nodes != null)
                {
                    for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                        hash = Mix(hash, StringHash(nodes[nodeIndex].type?.value));
                }

                int[] connections = room.connections;
                hash = Mix(hash, connections?.Length ?? 0);
                if (connections != null)
                {
                    for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                        hash = Mix(hash, connections[connectionIndex]);
                }
            }
            return hash;
        }
    }

    private static ulong Mix(ulong hash, int value) =>
        unchecked((hash ^ (uint)value) * 1099511628211UL);

    private static int StringHash(string value) =>
        string.IsNullOrEmpty(value) ? 0 : StringComparer.Ordinal.GetHashCode(value);

    private static void ResetRetainedState()
    {
        observedSession = null;
        observedPage = null;
        observedWorld = null;
        observedRevision = 0L;
        observedFingerprint = 0UL;
        observedWorldTextRevision = int.MinValue;
        observedCurrentRoomIndex = int.MinValue;
        observedSelectedRoomIndex = int.MinValue;
        framesUntilIntegrityAudit = IntegrityAuditInterval;
        retainedValid = false;
    }

    private static EditorMapRoomNodeSnapshot[] BuildNodes(AbstractRoom room)
    {
        if (room?.nodes == null || room.nodes.Length == 0)
            return Array.Empty<EditorMapRoomNodeSnapshot>();

        EditorMapRoomNodeSnapshot[] result = new EditorMapRoomNodeSnapshot[room.nodes.Length];
        for (int i = 0; i < room.nodes.Length; i++)
        {
            AbstractRoomNode node = room.nodes[i];
            bool exit = node.type == AbstractRoomNode.Type.Exit;
            result[i] = new EditorMapRoomNodeSnapshot
            {
                NodeIndex = i,
                Type = node.type?.value ?? string.Empty,
                Exit = exit,
                ConnectedRoomIndex = exit && room.connections != null && i < room.connections.Length
                    ? room.connections[i]
                    : -1
            };
        }
        return result;
    }

    private static List<EditorMapConnectionSnapshot> BuildConnections(
        global::World world,
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

                AbstractRoom target = world.GetAbstractRoom(other);
                int exactTargetNode = -1;
                if (WorldTextRegistry.TryGetConnectionEndpoint(
                        world.name,
                        room.name,
                        node,
                        out string destinationRoom,
                        out int destinationNode) &&
                    destinationNode >= 0 &&
                    roomIndexByName.TryGetValue(destinationRoom, out int destinationRoomIndex) &&
                    destinationRoomIndex == other)
                {
                    exactTargetNode = destinationNode;
                }

                bool repeated = exactTargetNode < 0 &&
                                (CountDestination(room.connections, other) > 1 ||
                                 CountDestination(target?.connections, room.index) > 1);
                arcs.Add(new DirectedConnectionArc
                {
                    FromRoom = room.index,
                    FromNode = node,
                    ToRoom = other,
                    ToNode = exactTargetNode,
                    AmbiguousPair = repeated
                });
            }
        }

        bool[] used = new bool[arcs.Count];
        for (int i = 0; i < arcs.Count; i++)
        {
            if (used[i]) continue;
            DirectedConnectionArc arc = arcs[i];

            if (arc.ToNode >= 0)
            {
                int exactReverse = -1;
                for (int j = 0; j < arcs.Count; j++)
                {
                    if (i == j || used[j]) continue;
                    DirectedConnectionArc back = arcs[j];
                    if (back.FromRoom != arc.ToRoom ||
                        back.FromNode != arc.ToNode ||
                        back.ToRoom != arc.FromRoom ||
                        back.ToNode != arc.FromNode)
                        continue;
                    exactReverse = j;
                    break;
                }

                used[i] = true;
                if (exactReverse >= 0)
                {
                    used[exactReverse] = true;
                    result.Add(new EditorMapConnectionSnapshot
                    {
                        ConnectionId = LegacyId(arc.FromRoom, arc.FromNode, arc.ToRoom, arc.ToNode, true),
                        FromRoomIndex = arc.FromRoom,
                        FromNodeIndex = arc.FromNode,
                        ToRoomIndex = arc.ToRoom,
                        ToNodeIndex = arc.ToNode,
                        Direction = WorldConnectionDirection.Bidirectional,
                        Explicit = false,
                        Ambiguous = false
                    });
                }
                else
                {
                    result.Add(new EditorMapConnectionSnapshot
                    {
                        ConnectionId = LegacyId(arc.FromRoom, arc.FromNode, arc.ToRoom, arc.ToNode, false),
                        FromRoomIndex = arc.FromRoom,
                        FromNodeIndex = arc.FromNode,
                        ToRoomIndex = arc.ToRoom,
                        ToNodeIndex = arc.ToNode,
                        Direction = WorldConnectionDirection.AToB,
                        Explicit = false,
                        Ambiguous = false
                    });
                }
                continue;
            }

            if (arc.AmbiguousPair)
            {
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
                continue;
            }

            int reverseIndex = -1;
            for (int j = 0; j < arcs.Count; j++)
            {
                if (i == j || used[j] || arcs[j].AmbiguousPair || arcs[j].ToNode >= 0) continue;
                if (arcs[j].FromRoom != arc.ToRoom || arcs[j].ToRoom != arc.FromRoom) continue;
                if (reverseIndex >= 0)
                {
                    reverseIndex = -2;
                    break;
                }
                reverseIndex = j;
            }

            if (reverseIndex >= 0)
            {
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

    private static int CountDestination(int[] connections, int roomIndex)
    {
        if (connections == null) return 0;
        int count = 0;
        for (int i = 0; i < connections.Length; i++)
            if (connections[i] == roomIndex) count++;
        return count;
    }

    private static long EndpointKey(int roomIndex, int nodeIndex) =>
        ((long)(uint)roomIndex << 32) | (uint)nodeIndex;

    private static string LegacyId(int aRoom, int aNode, int bRoom, int bNode, bool bidirectional) =>
        "legacy:" + aRoom + ":" + aNode + ":" + bRoom + ":" + bNode + ":" +
        (bidirectional ? "both" : "oneway");

    internal static void Clear()
    {
        current = EditorMapPresentationSnapshot.Empty;
        ResetRetainedState();
    }
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

        WorldTopologyCommandQueue.Process(session);
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
        WorldTopologyCommandQueue.Clear();
    }
}
