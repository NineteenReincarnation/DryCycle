using System;
using System.Collections.Concurrent;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.World;

internal enum WorldTopologyCommandKind
{
    AddExplicitMapping,
    CreateConnection,
    DeleteConnection,
    RemoveExplicitMapping,
    SetDirection
}

internal readonly struct WorldTopologyCommand
{
    internal WorldTopologyCommand(
        WorldTopologyCommandKind kind,
        string region = null,
        string edgeId = null,
        string roomA = null,
        int nodeA = -1,
        string roomB = null,
        int nodeB = -1,
        WorldConnectionDirection direction = WorldConnectionDirection.Bidirectional)
    {
        Kind = kind;
        Region = region ?? string.Empty;
        EdgeId = edgeId ?? string.Empty;
        RoomA = roomA ?? string.Empty;
        NodeA = nodeA;
        RoomB = roomB ?? string.Empty;
        NodeB = nodeB;
        Direction = direction;
    }

    internal WorldTopologyCommandKind Kind { get; }
    internal string Region { get; }
    internal string EdgeId { get; }
    internal string RoomA { get; }
    internal int NodeA { get; }
    internal string RoomB { get; }
    internal int NodeB { get; }
    internal WorldConnectionDirection Direction { get; }
}

/// <summary>
/// Main-thread mutation queue for endpoint-based world topology authoring.
/// Every successful edit is captured as one history transaction spanning live AbstractRoom data,
/// lossless world.txt edits and WorldTopology.json endpoint metadata.
/// </summary>
internal static class WorldTopologyCommandQueue
{
    private static readonly ConcurrentQueue<WorldTopologyCommand> queue = new();

    internal static string LastStatus { get; private set; } = string.Empty;
    internal static bool LastSucceeded { get; private set; } = true;

    internal static void Enqueue(WorldTopologyCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out WorldTopologyCommand command))
        {
            try
            {
                Execute(session, command);
            }
            catch (Exception error)
            {
                LastSucceeded = false;
                LastStatus = error.Message;
                global::DryCycle.Plugin.Logger?.LogWarning("WorldTopology command failed: " + error);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
        LastSucceeded = true;
        LastStatus = string.Empty;
    }

    private static void Execute(EditorSession session, WorldTopologyCommand command)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null)
        {
            Fail("World topology editing requires the Map page.");
            return;
        }

        string region = string.IsNullOrWhiteSpace(command.Region)
            ? page.world.name ?? string.Empty
            : command.Region.Trim();

        switch (command.Kind)
        {
            case WorldTopologyCommandKind.AddExplicitMapping:
                AddExplicitMapping(session, page.world, region, command);
                break;

            case WorldTopologyCommandKind.CreateConnection:
                CreateConnection(session, page.world, region, command);
                break;

            case WorldTopologyCommandKind.DeleteConnection:
                DeleteConnection(session, page.world, region, command);
                break;

            case WorldTopologyCommandKind.RemoveExplicitMapping:
                RemoveExplicitMapping(session, page.world, region, command.EdgeId);
                break;

            case WorldTopologyCommandKind.SetDirection:
                SetDirection(session, page.world, region, command.EdgeId, command.Direction);
                break;
        }
    }

    private static void AddExplicitMapping(
        EditorSession session,
        global::World world,
        string region,
        WorldTopologyCommand command)
    {
        if (!TryResolveEndpoints(world, command, out AbstractRoom roomA, out AbstractRoom roomB, out string endpointError))
        {
            Fail(endpointError);
            return;
        }

        if (!PointsTo(roomA, command.NodeA, roomB.index))
        {
            Fail(roomA.name + ":" + command.NodeA + " does not point to " + roomB.name + " in world.txt.");
            return;
        }

        if (!PointsTo(roomB, command.NodeB, roomA.index))
        {
            Fail(roomB.name + ":" + command.NodeB + " does not point back to " + roomA.name + " in world.txt.");
            return;
        }

        if (!EnsureWorldTextRooms(region, roomA, roomB, out string error))
        {
            Fail(error);
            return;
        }

        WorldTopologyEditSnapshot before = Capture(world, region, roomA, command.NodeA, roomB, command.NodeB);
        if (before == null)
        {
            Fail("Could not capture the current topology state.");
            return;
        }

        if (!TryAddTopologyEdge(region, roomA, command.NodeA, roomB, command.NodeB, command.Direction, out string edgeId, out error))
        {
            Fail(error);
            return;
        }

        if (!ApplyDirectionToWorld(
                region,
                roomA,
                command.NodeA,
                roomB,
                command.NodeB,
                command.Direction,
                out error))
        {
            before.Apply(session);
            Fail(error);
            return;
        }

        PushHistory(
            session,
            "Resolve world connection",
            before,
            Capture(world, region, roomA, command.NodeA, roomB, command.NodeB));

        Succeed(
            "Mapped " + roomA.name + ":" + command.NodeA + " " + DirectionGlyph(command.Direction) + " " +
            roomB.name + ":" + command.NodeB + " (" + ShortId(edgeId) + ").");
    }

    private static void CreateConnection(
        EditorSession session,
        global::World world,
        string region,
        WorldTopologyCommand command)
    {
        if (!TryResolveEndpoints(world, command, out AbstractRoom roomA, out AbstractRoom roomB, out string endpointError))
        {
            Fail(endpointError);
            return;
        }

        int oldA = ConnectionTarget(roomA, command.NodeA);
        int oldB = ConnectionTarget(roomB, command.NodeB);
        if (oldA >= 0 && oldA != roomB.index)
        {
            Fail(roomA.name + ":" + command.NodeA + " is already connected to another room.");
            return;
        }
        if (oldB >= 0 && oldB != roomA.index)
        {
            Fail(roomB.name + ":" + command.NodeB + " is already connected to another room.");
            return;
        }

        if (!EnsureWorldTextRooms(region, roomA, roomB, out string error))
        {
            Fail(error);
            return;
        }

        WorldTopologyEditSnapshot before = Capture(world, region, roomA, command.NodeA, roomB, command.NodeB);
        if (before == null)
        {
            Fail("Could not capture the current topology state.");
            return;
        }

        if (!TryAddTopologyEdge(region, roomA, command.NodeA, roomB, command.NodeB, command.Direction, out _, out error))
        {
            Fail(error);
            return;
        }

        if (!ApplyDirectionToWorld(
                region,
                roomA,
                command.NodeA,
                roomB,
                command.NodeB,
                command.Direction,
                out error))
        {
            before.Apply(session);
            Fail(error);
            return;
        }

        PushHistory(
            session,
            "Create world connection",
            before,
            Capture(world, region, roomA, command.NodeA, roomB, command.NodeB));

        Succeed(
            "Created " + roomA.name + ":" + command.NodeA + " " + DirectionGlyph(command.Direction) + " " +
            roomB.name + ":" + command.NodeB + ".");
    }

    private static void DeleteConnection(
        EditorSession session,
        global::World world,
        string region,
        WorldTopologyCommand command)
    {
        if (!TryResolveEndpoints(world, command, out AbstractRoom roomA, out AbstractRoom roomB, out string endpointError))
        {
            Fail(endpointError);
            return;
        }

        if (!EnsureWorldTextRooms(region, roomA, roomB, out string error))
        {
            Fail(error);
            return;
        }

        WorldTopologyEditSnapshot before = Capture(world, region, roomA, command.NodeA, roomB, command.NodeB);
        if (before == null)
        {
            Fail("Could not capture the current topology state.");
            return;
        }

        if (!SetWorldConnection(region, roomA, command.NodeA, null, out error) ||
            !SetWorldConnection(region, roomB, command.NodeB, null, out error))
        {
            before.Apply(session);
            Fail(error ?? "Could not disconnect the endpoints in world.txt.");
            return;
        }

        SetLiveConnection(roomA, command.NodeA, -1);
        SetLiveConnection(roomB, command.NodeB, -1);

        if (!string.IsNullOrWhiteSpace(command.EdgeId) &&
            !WorldTopologyRegistry.RemoveEdge(region, command.EdgeId))
        {
            before.Apply(session);
            Fail("The explicit connection mapping no longer exists.");
            return;
        }

        PushHistory(
            session,
            "Disconnect world connection",
            before,
            Capture(world, region, roomA, command.NodeA, roomB, command.NodeB));

        Succeed(
            "Disconnected " + roomA.name + ":" + command.NodeA + " and " +
            roomB.name + ":" + command.NodeB + ".");
    }

    private static void SetDirection(
        EditorSession session,
        global::World world,
        string region,
        string edgeId,
        WorldConnectionDirection direction)
    {
        if (!TryFindEdge(region, edgeId, out WorldConnectionEdge edge))
        {
            Fail("The explicit connection mapping no longer exists.");
            return;
        }
        if (edge.Direction == direction)
        {
            Succeed("Connection direction is already " + DirectionText(direction) + ".");
            return;
        }

        AbstractRoom roomA = world.GetAbstractRoom(edge.A.Room);
        AbstractRoom roomB = world.GetAbstractRoom(edge.B.Room);
        if (roomA == null || roomB == null ||
            !IsExit(roomA, edge.A.NodeIndex) ||
            !IsExit(roomB, edge.B.NodeIndex))
        {
            Fail("The connection endpoints are no longer valid in the loaded world.");
            return;
        }

        if (!EnsureWorldTextRooms(region, roomA, roomB, out string error))
        {
            Fail(error);
            return;
        }

        WorldTopologyEditSnapshot before = Capture(world, region, roomA, edge.A.NodeIndex, roomB, edge.B.NodeIndex);
        if (before == null)
        {
            Fail("Could not capture the current topology state.");
            return;
        }

        if (!ApplyDirectionToWorld(
                region,
                roomA,
                edge.A.NodeIndex,
                roomB,
                edge.B.NodeIndex,
                direction,
                out error) ||
            !WorldTopologyRegistry.SetDirection(region, edgeId, direction))
        {
            before.Apply(session);
            Fail(error ?? "Could not update the connection direction.");
            return;
        }

        PushHistory(
            session,
            "Change world connection direction",
            before,
            Capture(world, region, roomA, edge.A.NodeIndex, roomB, edge.B.NodeIndex));

        Succeed("Changed connection direction to " + DirectionText(direction) + ".");
    }

    private static void RemoveExplicitMapping(
        EditorSession session,
        global::World world,
        string region,
        string edgeId)
    {
        if (!TryFindEdge(region, edgeId, out WorldConnectionEdge edge))
        {
            Fail("The explicit connection mapping no longer exists.");
            return;
        }

        AbstractRoom roomA = world.GetAbstractRoom(edge.A.Room);
        AbstractRoom roomB = world.GetAbstractRoom(edge.B.Room);
        if (roomA == null || roomB == null ||
            !IsExit(roomA, edge.A.NodeIndex) ||
            !IsExit(roomB, edge.B.NodeIndex))
        {
            Fail("The connection endpoints are no longer valid in the loaded world.");
            return;
        }

        if (!EnsureWorldTextRooms(region, roomA, roomB, out string error))
        {
            Fail(error);
            return;
        }

        WorldTopologyEditSnapshot before = Capture(world, region, roomA, edge.A.NodeIndex, roomB, edge.B.NodeIndex);
        if (before == null)
        {
            Fail("Could not capture the current topology state.");
            return;
        }

        if (!ApplyDirectionToWorld(
                region,
                roomA,
                edge.A.NodeIndex,
                roomB,
                edge.B.NodeIndex,
                WorldConnectionDirection.Bidirectional,
                out error) ||
            !WorldTopologyRegistry.RemoveEdge(region, edgeId))
        {
            before.Apply(session);
            Fail(error ?? "Could not remove the explicit connection mapping.");
            return;
        }

        PushHistory(
            session,
            "Remove world connection mapping",
            before,
            Capture(world, region, roomA, edge.A.NodeIndex, roomB, edge.B.NodeIndex));

        Succeed("Removed explicit mapping and restored a vanilla bidirectional room link.");
    }

    private static WorldTopologyEditSnapshot Capture(
        global::World world,
        string region,
        AbstractRoom roomA,
        int nodeA,
        AbstractRoom roomB,
        int nodeB) =>
        WorldTopologyEditSnapshot.Capture(world, region, roomA.name, nodeA, roomB.name, nodeB);

    private static void PushHistory(
        EditorSession session,
        string label,
        WorldTopologyEditSnapshot before,
        WorldTopologyEditSnapshot after) =>
        WorldTopologyHistoryEntry.Push(session, label, before, after);

    private static bool ApplyDirectionToWorld(
        string region,
        AbstractRoom roomA,
        int nodeA,
        AbstractRoom roomB,
        int nodeB,
        WorldConnectionDirection direction,
        out string error)
    {
        error = null;
        string targetFromA = direction == WorldConnectionDirection.BToA ? null : roomB.name;
        string targetFromB = direction == WorldConnectionDirection.AToB ? null : roomA.name;

        if (!SetWorldConnection(region, roomA, nodeA, targetFromA, out error)) return false;
        if (!SetWorldConnection(region, roomB, nodeB, targetFromB, out error)) return false;

        SetLiveConnection(roomA, nodeA, targetFromA == null ? -1 : roomB.index);
        SetLiveConnection(roomB, nodeB, targetFromB == null ? -1 : roomA.index);
        return true;
    }

    private static bool SetWorldConnection(
        string region,
        AbstractRoom room,
        int nodeIndex,
        string targetRoom,
        out string error)
    {
        return WorldTextRegistry.TrySetConnection(
            region,
            room.name,
            nodeIndex,
            targetRoom ?? "DISCONNECTED",
            out error);
    }

    private static bool EnsureWorldTextRooms(
        string region,
        AbstractRoom roomA,
        AbstractRoom roomB,
        out string error)
    {
        error = null;
        if (!WorldTextRegistry.EnsureLoaded(region))
        {
            error = WorldTextRegistry.LoadError ?? "world.txt is unavailable.";
            return false;
        }
        if (!WorldTextRegistry.HasRoom(region, roomA.name) ||
            !WorldTextRegistry.HasRoom(region, roomB.name))
        {
            error = "The editable world.txt does not contain both endpoint rooms.";
            return false;
        }
        return true;
    }

    private static bool TryFindEdge(string region, string edgeId, out WorldConnectionEdge edge)
    {
        edge = null;
        if (string.IsNullOrWhiteSpace(edgeId)) return false;
        WorldConnectionEdge[] edges = WorldTopologyRegistry.GetRegionEdges(region);
        for (int i = 0; i < edges.Length; i++)
        {
            if (!string.Equals(edges[i].Id, edgeId, StringComparison.OrdinalIgnoreCase)) continue;
            edge = edges[i];
            return true;
        }
        return false;
    }

    private static bool TryResolveEndpoints(
        global::World world,
        WorldTopologyCommand command,
        out AbstractRoom roomA,
        out AbstractRoom roomB,
        out string error)
    {
        roomA = world.GetAbstractRoom(command.RoomA);
        roomB = world.GetAbstractRoom(command.RoomB);
        error = null;

        if (roomA == null || roomB == null)
        {
            error = "Both rooms must exist in the currently loaded region.";
            return false;
        }
        if (roomA.index == roomB.index)
        {
            error = "A room connection must use two different rooms.";
            return false;
        }
        if (!IsExit(roomA, command.NodeA) || !IsExit(roomB, command.NodeB))
        {
            error = "Both endpoints must be room Exit nodes.";
            return false;
        }
        return true;
    }

    private static bool TryAddTopologyEdge(
        string region,
        AbstractRoom roomA,
        int nodeA,
        AbstractRoom roomB,
        int nodeB,
        WorldConnectionDirection direction,
        out string edgeId,
        out string error)
    {
        return WorldTopologyRegistry.TryAddEdge(
            region,
            new WorldConnectionEndpoint(roomA.name, nodeA),
            new WorldConnectionEndpoint(roomB.name, nodeB),
            direction,
            out edgeId,
            out error);
    }

    private static bool IsExit(AbstractRoom room, int nodeIndex) =>
        room?.nodes != null &&
        nodeIndex >= 0 &&
        nodeIndex < room.nodes.Length &&
        room.nodes[nodeIndex].type == AbstractRoomNode.Type.Exit;

    private static bool PointsTo(AbstractRoom room, int nodeIndex, int targetRoomIndex) =>
        ConnectionTarget(room, nodeIndex) == targetRoomIndex;

    private static int ConnectionTarget(AbstractRoom room, int nodeIndex) =>
        room?.connections != null && nodeIndex >= 0 && nodeIndex < room.connections.Length
            ? room.connections[nodeIndex]
            : -1;

    private static void SetLiveConnection(AbstractRoom room, int nodeIndex, int targetRoomIndex)
    {
        if (room == null || nodeIndex < 0) return;
        if (room.connections == null || nodeIndex >= room.connections.Length)
        {
            int oldLength = room.connections?.Length ?? 0;
            int nextLength = Math.Max(nodeIndex + 1, oldLength);
            int[] next = new int[nextLength];
            for (int i = 0; i < next.Length; i++) next[i] = -1;
            if (room.connections != null) Array.Copy(room.connections, next, room.connections.Length);
            room.connections = next;
        }
        room.connections[nodeIndex] = targetRoomIndex;
    }

    private static void Succeed(string message)
    {
        LastSucceeded = true;
        LastStatus = message ?? string.Empty;
    }

    private static void Fail(string message)
    {
        LastSucceeded = false;
        LastStatus = message ?? string.Empty;
    }

    private static string ShortId(string edgeId) =>
        string.IsNullOrEmpty(edgeId) ? string.Empty : edgeId.Substring(0, Math.Min(8, edgeId.Length));

    private static string DirectionText(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "A to B",
        WorldConnectionDirection.BToA => "B to A",
        _ => "bidirectional"
    };

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "→",
        WorldConnectionDirection.BToA => "←",
        _ => "↔"
    };
}
