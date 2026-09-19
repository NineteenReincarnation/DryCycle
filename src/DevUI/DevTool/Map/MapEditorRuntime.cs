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

    private readonly struct MapFingerprints
    {
        internal MapFingerprints(ulong presentation, ulong topology)
        {
            Presentation = presentation;
            Topology = topology;
        }

        internal ulong Presentation { get; }
        internal ulong Topology { get; }
    }

    // Revision invalidation is the normal fast path. The semantic scan remains as a bounded
    // compatibility audit for external code that mutates MapPage/AbstractRoom directly without
    // participating in DryCycle's revision protocol. At 40 FPS this runs roughly every 0.75 s on
    // otherwise stable maps.
    private const int IntegrityAuditInterval = 30;

    private static volatile EditorMapPresentationSnapshot current = EditorMapPresentationSnapshot.Empty;
    private static readonly Dictionary<int, EditorMapRoomNodeSnapshot[]> retainedNodes = new();
    private static EditorSession observedSession;
    private static global::World observedWorld;
    private static long observedRevision;
    private static long observedAuthoringRevision;
    private static ulong observedPresentationFingerprint;
    private static ulong observedTopologyFingerprint;
    private static int observedWorldTextRevision = int.MinValue;
    private static int observedWorldTopologyRevision = int.MinValue;
    private static int observedCurrentRoomIndex = int.MinValue;
    private static int observedSelectedRoomIndex = int.MinValue;
    private static int framesUntilIntegrityAudit = IntegrityAuditInterval;
    private static bool retainedValid;

    public static EditorMapPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        global::World world = session?.World;
        if (session?.ToolMode != EditorToolMode.Map || world == null)
        {
            if (retainedValid || !ReferenceEquals(current, EditorMapPresentationSnapshot.Empty))
                Clear();
            return;
        }

        MapEditorState state = MapEditorStateHub.Get(session);
        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
            EditorRevisionHub.Mark(session, EditorRevisionKind.Map);

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Map);
        long authoringRevision = NativeMapAuthoringStateHub.GetRevision(session);
        int worldTextRevision = WorldTextRegistry.Revision;
        int worldTopologyRevision = WorldTopologyRegistry.Revision;
        int currentRoomIndex = session.Room?.abstractRoom?.index ?? -1;
        int selectedRoomIndex = state?.SelectedRoomIndex ?? -1;

        bool sameIdentity =
            retainedValid &&
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedWorld, world) &&
            current.Available;
        bool modelRevisionsStable =
            sameIdentity &&
            observedRevision == revision &&
            observedAuthoringRevision == authoringRevision &&
            observedWorldTextRevision == worldTextRevision &&
            observedWorldTopologyRevision == worldTopologyRevision;
        bool flagsChanged =
            sameIdentity &&
            (observedCurrentRoomIndex != currentRoomIndex ||
             observedSelectedRoomIndex != selectedRoomIndex);

        // Selection/current-room flags are presentation-only. Native room placement/layer state has
        // its own revision, so stable frames return without touching MapPage or scanning room nodes.
        if (modelRevisionsStable)
        {
            framesUntilIntegrityAudit--;
            if (framesUntilIntegrityAudit > 0)
            {
                if (flagsChanged)
                    PublishRoomFlagsOnly(world, state, currentRoomIndex);
                return;
            }

            framesUntilIntegrityAudit = IntegrityAuditInterval;

            // Compatibility-only audit: if a third-party writer still edits a live RoomPanel, import
            // that edge into the Native state once. Page-less Native mode does no legacy scan here.
            NativeMapAuthoringStateHub.AuditLegacy(session);
            authoringRevision = NativeMapAuthoringStateHub.GetRevision(session);
            if (authoringRevision == observedAuthoringRevision)
            {
                MapFingerprints audit = ComputeFingerprints(session, world);
                if (observedPresentationFingerprint == audit.Presentation &&
                    observedTopologyFingerprint == audit.Topology)
                {
                    if (flagsChanged)
                        PublishRoomFlagsOnly(world, state, currentRoomIndex);
                    return;
                }
            }
        }

        framesUntilIntegrityAudit = IntegrityAuditInterval;
        MapFingerprints fingerprints = ComputeFingerprints(session, world);

        bool topologyChanged =
            !sameIdentity ||
            observedTopologyFingerprint != fingerprints.Topology ||
            observedWorldTextRevision != worldTextRevision ||
            observedWorldTopologyRevision != worldTopologyRevision;
        bool roomPresentationChanged =
            !sameIdentity ||
            observedPresentationFingerprint != fingerprints.Presentation ||
            observedAuthoringRevision != authoringRevision ||
            flagsChanged;

        if (!topologyChanged && !roomPresentationChanged)
        {
            observedRevision = revision;
            observedAuthoringRevision = authoringRevision;
            return;
        }

        List<EditorMapRoomSnapshot> rooms = new();
        HashSet<int> roomIndices = new();
        Dictionary<string, int> roomIndexByName = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> disabled = BuildDisabledSet(world);

        if (topologyChanged)
            retainedNodes.Clear();

        int roomEnd = world.firstRoomIndex + world.NumberOfRooms;
        for (int roomIndex = world.firstRoomIndex; roomIndex < roomEnd; roomIndex++)
        {
            AbstractRoom room = world.GetAbstractRoom(roomIndex);
            if (room == null ||
                !NativeMapAuthoringStateHub.TryGet(session, roomIndex, out NativeMapRoomAuthoringValue authoring))
                continue;

            roomIndices.Add(room.index);
            if (!string.IsNullOrWhiteSpace(room.name))
                roomIndexByName[room.name] = room.index;

            EditorMapRoomNodeSnapshot[] nodes;
            if (topologyChanged || !retainedNodes.TryGetValue(room.index, out nodes))
            {
                nodes = BuildNodes(room);
                retainedNodes[room.index] = nodes;
            }

            rooms.Add(new EditorMapRoomSnapshot
            {
                RoomIndex = room.index,
                Name = room.name ?? string.Empty,
                X = authoring.DevPosition.x,
                Y = authoring.DevPosition.y,
                Layer = authoring.Layer,
                Subregion = room.subregionName ?? string.Empty,
                OffScreenDen = room.offScreenDen,
                Disabled = disabled.Contains(room.name ?? string.Empty),
                CurrentRoom = room.index == currentRoomIndex,
                Selected = room.index == state.SelectedRoomIndex,
                Nodes = nodes
            });
        }

        bool correctedSelection = false;
        if (state.SelectedRoomIndex >= 0 && !roomIndices.Contains(state.SelectedRoomIndex))
        {
            state.SelectedRoomIndex = -1;
            selectedRoomIndex = -1;
            correctedSelection = true;
        }

        EditorMapConnectionSnapshot[] connections;
        if (topologyChanged || current.Connections == null)
        {
            connections = BuildConnections(
                world,
                rooms,
                roomIndices,
                roomIndexByName).ToArray();
        }
        else
        {
            connections = current.Connections;
        }

        current = new EditorMapPresentationSnapshot
        {
            Available = true,
            RegionName = world.name ?? string.Empty,
            SelectedRoomIndex = state.SelectedRoomIndex,
            Rooms = rooms.ToArray(),
            Connections = connections
        };

        observedSession = session;
        observedWorld = world;
        observedRevision = revision;
        observedAuthoringRevision = NativeMapAuthoringStateHub.GetRevision(session);
        if (correctedSelection)
            fingerprints = ComputeFingerprints(session, world);
        observedPresentationFingerprint = fingerprints.Presentation;
        observedTopologyFingerprint = fingerprints.Topology;
        observedWorldTextRevision = WorldTextRegistry.Revision;
        observedWorldTopologyRevision = WorldTopologyRegistry.Revision;
        observedCurrentRoomIndex = currentRoomIndex;
        observedSelectedRoomIndex = state.SelectedRoomIndex;
        framesUntilIntegrityAudit = IntegrityAuditInterval;
        retainedValid = true;
    }

    private static HashSet<string> BuildDisabledSet(global::World world)
    {
        HashSet<string> disabled = new(StringComparer.Ordinal);
        if (world?.DisabledMapRooms == null) return disabled;
        for (int i = 0; i < world.DisabledMapRooms.Count; i++)
        {
            string name = world.DisabledMapRooms[i];
            if (!string.IsNullOrEmpty(name)) disabled.Add(name);
        }
        return disabled;
    }

    private static void PublishRoomFlagsOnly(global::World world, MapEditorState state, int currentRoomIndex)
    {
        EditorMapRoomSnapshot[] source = current.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapRoomSnapshot[] next = null;
        int selectedRoomIndex = state?.SelectedRoomIndex ?? -1;

        for (int i = 0; i < source.Length; i++)
        {
            EditorMapRoomSnapshot room = source[i];
            bool nextCurrent = room.RoomIndex == currentRoomIndex;
            bool nextSelected = room.RoomIndex == selectedRoomIndex;
            if (room.CurrentRoom == nextCurrent && room.Selected == nextSelected)
                continue;

            next ??= (EditorMapRoomSnapshot[])source.Clone();
            next[i] = new EditorMapRoomSnapshot
            {
                RoomIndex = room.RoomIndex,
                Name = room.Name,
                X = room.X,
                Y = room.Y,
                Layer = room.Layer,
                Subregion = room.Subregion,
                OffScreenDen = room.OffScreenDen,
                Disabled = room.Disabled,
                CurrentRoom = nextCurrent,
                Selected = nextSelected,
                Nodes = room.Nodes
            };
        }

        if (next != null)
        {
            current = new EditorMapPresentationSnapshot
            {
                Available = true,
                RegionName = world?.name ?? current.RegionName,
                SelectedRoomIndex = selectedRoomIndex,
                Rooms = next,
                Connections = current.Connections
            };
        }
        else if (current.SelectedRoomIndex != selectedRoomIndex)
        {
            current = new EditorMapPresentationSnapshot
            {
                Available = true,
                RegionName = world?.name ?? current.RegionName,
                SelectedRoomIndex = selectedRoomIndex,
                Rooms = source,
                Connections = current.Connections
            };
        }

        observedCurrentRoomIndex = currentRoomIndex;
        observedSelectedRoomIndex = selectedRoomIndex;
    }

    /// <summary>
    /// Computes visual-room and topology signatures in one pass. Selection/current-room state is
    /// tracked explicitly and is intentionally excluded so changing those flags does not require a
    /// full room scan. The topology signature excludes room positions/layers/subregions, allowing
    /// visual map edits to retain exact node arrays and the expensive connection graph.
    /// </summary>
    private static MapFingerprints ComputeFingerprints(EditorSession session, global::World world)
    {
        unchecked
        {
            ulong presentation = 1469598103934665603UL;
            ulong topology = 1469598103934665603UL;
            int worldNameHash = StringHash(world?.name);
            presentation = Mix(presentation, worldNameHash);
            topology = Mix(topology, worldNameHash);

            var disabled = world?.DisabledMapRooms;
            presentation = Mix(presentation, disabled?.Count ?? 0);
            if (disabled != null)
            {
                for (int i = 0; i < disabled.Count; i++)
                    presentation = Mix(presentation, StringHash(disabled[i]));
            }

            int roomCount = world?.NumberOfRooms ?? 0;
            presentation = Mix(presentation, roomCount);
            topology = Mix(topology, roomCount);
            if (world == null)
                return new MapFingerprints(presentation, topology);

            int end = world.firstRoomIndex + world.NumberOfRooms;
            for (int roomIndex = world.firstRoomIndex; roomIndex < end; roomIndex++)
            {
                AbstractRoom room = world.GetAbstractRoom(roomIndex);
                if (room == null) continue;

                int roomNameHash = StringHash(room.name);
                presentation = Mix(presentation, room.index);
                presentation = Mix(presentation, roomNameHash);
                if (NativeMapAuthoringStateHub.TryGet(
                        session,
                        room.index,
                        out NativeMapRoomAuthoringValue authoring))
                {
                    presentation = Mix(presentation, authoring.DevPosition.x.GetHashCode());
                    presentation = Mix(presentation, authoring.DevPosition.y.GetHashCode());
                    presentation = Mix(presentation, authoring.Layer);
                }
                presentation = Mix(presentation, StringHash(room.subregionName));
                presentation = Mix(presentation, room.offScreenDen ? 1 : 0);

                topology = Mix(topology, room.index);
                topology = Mix(topology, roomNameHash);

                AbstractRoomNode[] nodes = room.nodes;
                topology = Mix(topology, nodes?.Length ?? 0);
                if (nodes != null)
                {
                    for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                        topology = Mix(topology, StringHash(nodes[nodeIndex].type?.value));
                }

                int[] connections = room.connections;
                topology = Mix(topology, connections?.Length ?? 0);
                if (connections != null)
                {
                    for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                        topology = Mix(topology, connections[connectionIndex]);
                }
            }

            return new MapFingerprints(presentation, topology);
        }
    }

    private static ulong Mix(ulong hash, int value) =>
        unchecked((hash ^ (uint)value) * 1099511628211UL);

    private static int StringHash(string value) =>
        string.IsNullOrEmpty(value) ? 0 : StringComparer.Ordinal.GetHashCode(value);

    private static void ResetRetainedState()
    {
        retainedNodes.Clear();
        observedSession = null;
        observedWorld = null;
        observedRevision = 0L;
        observedAuthoringRevision = 0L;
        observedPresentationFingerprint = 0UL;
        observedTopologyFingerprint = 0UL;
        observedWorldTextRevision = int.MinValue;
        observedWorldTopologyRevision = int.MinValue;
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
