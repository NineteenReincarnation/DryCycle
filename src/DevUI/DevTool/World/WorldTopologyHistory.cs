using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Small transactional snapshot for one topology edit. Only the two edited endpoints and
/// explicit edges touching them are captured, so Undo/Redo does not replace unrelated region data.
/// Runtime synchronization is intentionally delegated to WorldTextRegistry/WorldConnectionSyntax,
/// the same path used by normal edits and Save.
/// </summary>
internal sealed class WorldTopologyEditSnapshot
{
    private sealed class EndpointState
    {
        internal string Room = string.Empty;
        internal int Node = -1;
        internal string Destination = "DISCONNECTED";
    }

    private readonly string region;
    private readonly EndpointState[] endpoints;
    private readonly WorldConnectionEdge[] explicitEdges;

    private WorldTopologyEditSnapshot(
        string region,
        EndpointState[] endpoints,
        WorldConnectionEdge[] explicitEdges)
    {
        this.region = region ?? string.Empty;
        this.endpoints = endpoints ?? Array.Empty<EndpointState>();
        this.explicitEdges = explicitEdges ?? Array.Empty<WorldConnectionEdge>();
    }

    internal static WorldTopologyEditSnapshot Capture(
        global::World world,
        string region,
        string roomA,
        int nodeA,
        string roomB,
        int nodeB)
    {
        if (world == null || string.IsNullOrWhiteSpace(region)) return null;
        if (!WorldTextRegistry.EnsureLoaded(region)) return null;

        EndpointState a = CaptureEndpoint(world, region, roomA, nodeA);
        EndpointState b = CaptureEndpoint(world, region, roomB, nodeB);
        if (a == null || b == null) return null;

        WorldConnectionEndpoint endpointA = new(roomA, nodeA);
        WorldConnectionEndpoint endpointB = new(roomB, nodeB);
        List<WorldConnectionEdge> edges = new();
        WorldConnectionEdge[] regionEdges = WorldTopologyRegistry.GetRegionEdges(region);
        for (int i = 0; i < regionEdges.Length; i++)
        {
            WorldConnectionEdge edge = regionEdges[i];
            if (Touches(edge, endpointA) || Touches(edge, endpointB))
                edges.Add(edge.Clone());
        }

        return new WorldTopologyEditSnapshot(
            region,
            new[] { a, b },
            edges.ToArray());
    }

    internal bool Apply(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null) return false;
        if (!string.Equals(page.world.name, region, StringComparison.OrdinalIgnoreCase)) return false;
        if (!WorldTextRegistry.EnsureLoaded(region)) return false;

        WorldConnectionEndpoint[] edited = new WorldConnectionEndpoint[endpoints.Length];
        for (int i = 0; i < endpoints.Length; i++)
            edited[i] = new WorldConnectionEndpoint(endpoints[i].Room, endpoints[i].Node);

        WorldConnectionEdge[] currentEdges = WorldTopologyRegistry.GetRegionEdges(region);
        for (int i = 0; i < currentEdges.Length; i++)
        {
            bool remove = false;
            for (int endpointIndex = 0; endpointIndex < edited.Length; endpointIndex++)
            {
                if (!Touches(currentEdges[i], edited[endpointIndex])) continue;
                remove = true;
                break;
            }
            if (remove) WorldTopologyRegistry.RemoveEdge(region, currentEdges[i].Id);
        }

        for (int i = 0; i < endpoints.Length; i++)
        {
            EndpointState endpoint = endpoints[i];
            if (page.world.GetAbstractRoom(endpoint.Room) == null) return false;
            if (!WorldTextRegistry.TrySetConnection(
                    region,
                    endpoint.Room,
                    endpoint.Node,
                    endpoint.Destination,
                    out _))
                return false;
        }

        for (int i = 0; i < explicitEdges.Length; i++)
        {
            WorldConnectionEdge edge = explicitEdges[i];
            if (!WorldTopologyRegistry.TryAddEdge(
                    region,
                    edge.A,
                    edge.B,
                    edge.Direction,
                    out _,
                    out _))
                return false;
        }

        return true;
    }

    private static EndpointState CaptureEndpoint(
        global::World world,
        string region,
        string room,
        int node)
    {
        AbstractRoom abstractRoom = world.GetAbstractRoom(room);
        if (abstractRoom?.nodes == null ||
            node < 0 || node >= abstractRoom.nodes.Length ||
            abstractRoom.nodes[node].type != AbstractRoomNode.Type.Exit)
            return null;

        string destination;
        if (!WorldTextRegistry.TryGetConnection(region, room, node, out destination))
        {
            int target = abstractRoom.connections != null && node < abstractRoom.connections.Length
                ? abstractRoom.connections[node]
                : -1;
            destination = target >= 0
                ? world.GetAbstractRoom(target)?.name ?? "DISCONNECTED"
                : "DISCONNECTED";
        }

        return new EndpointState
        {
            Room = abstractRoom.name,
            Node = node,
            Destination = string.IsNullOrWhiteSpace(destination) ? "DISCONNECTED" : destination.Trim()
        };
    }

    private static bool Touches(WorldConnectionEdge edge, WorldConnectionEndpoint endpoint) =>
        edge != null && (edge.A.Equals(endpoint) || edge.B.Equals(endpoint));
}

internal sealed class WorldTopologyHistoryEntry : IEditorHistoryEntry
{
    private readonly WorldTopologyEditSnapshot before;
    private readonly WorldTopologyEditSnapshot after;

    internal WorldTopologyHistoryEntry(
        string label,
        WorldTopologyEditSnapshot before,
        WorldTopologyEditSnapshot after)
    {
        Label = string.IsNullOrWhiteSpace(label) ? "Edit world topology" : label;
        this.before = before;
        this.after = after;
    }

    public string Label { get; }

    public bool Undo(EditorSession session) => before?.Apply(session) == true;
    public bool Redo(EditorSession session) => after?.Apply(session) == true;

    internal static void Push(
        EditorSession session,
        string label,
        WorldTopologyEditSnapshot before,
        WorldTopologyEditSnapshot after)
    {
        if (session == null || before == null || after == null) return;
        session.History.Push(new WorldTopologyHistoryEntry(label, before, after));
    }
}
