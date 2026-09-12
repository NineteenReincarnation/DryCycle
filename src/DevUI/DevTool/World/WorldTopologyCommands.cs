using System;
using System.Collections.Concurrent;
using DevInterface;

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
/// RWImGui describes requested edits; validation and live world mutation happen on DevUI.Update.
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
                AddExplicitMapping(page.world, region, command);
                break;

            case WorldTopologyCommandKind.CreateConnection:
                CreateConnection(page.world, region, command);
                break;

            case WorldTopologyCommandKind.DeleteConnection:
                DeleteConnection(page.world, region, command);
                break;

            case WorldTopologyCommandKind.RemoveExplicitMapping:
                if (WorldTopologyRegistry.RemoveEdge(region, command.EdgeId))
                    Succeed("Removed explicit connection mapping.");
                else
                    Fail("The explicit connection mapping no longer exists.");
                break;

            case WorldTopologyCommandKind.SetDirection:
                if (WorldTopologyRegistry.SetDirection(region, command.EdgeId, command.Direction))
                    Succeed("Changed connection direction to " + DirectionText(command.Direction) + ".");
                else
                    Fail("Connection direction was unchanged or the mapping no longer exists.");
                break;
        }
    }

    private static void AddExplicitMapping(World world, string region, WorldTopologyCommand command)
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

        if (!TryAddTopologyEdge(region, roomA, command.NodeA, roomB, command.NodeB, command.Direction, out string edgeId, out string error))
        {
            Fail(error);
            return;
        }

        Succeed(
            "Mapped " + roomA.name + ":" + command.NodeA + " " + DirectionGlyph(command.Direction) + " " +
            roomB.name + ":" + command.NodeB + " (" + ShortId(edgeId) + ").");
    }

    private static void CreateConnection(World world, string region, WorldTopologyCommand command)
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

        if (!WorldTextRegistry.EnsureLoaded(region) ||
            !WorldTextRegistry.HasRoom(region, roomA.name) ||
            !WorldTextRegistry.HasRoom(region, roomB.name))
        {
            Fail(WorldTextRegistry.LoadError ?? "The editable world.txt does not contain both rooms.");
            return;
        }

        if (!TryAddTopologyEdge(region, roomA, command.NodeA, roomB, command.NodeB, command.Direction, out string edgeId, out string error))
        {
            Fail(error);
            return;
        }

        if (!WorldTextRegistry.TrySetConnection(region, roomA.name, command.NodeA, roomB.name, out error) ||
            !WorldTextRegistry.TrySetConnection(region, roomB.name, command.NodeB, roomA.name, out error))
        {
            WorldTopologyRegistry.RemoveEdge(region, edgeId);
            Fail(error ?? "Could not update world.txt.");
            return;
        }

        SetLiveConnection(roomA, command.NodeA, roomB.index);
        SetLiveConnection(roomB, command.NodeB, roomA.index);

        Succeed(
            "Created " + roomA.name + ":" + command.NodeA + " " + DirectionGlyph(command.Direction) + " " +
            roomB.name + ":" + command.NodeB + ".");
    }

    private static void DeleteConnection(World world, string region, WorldTopologyCommand command)
    {
        if (!TryResolveEndpoints(world, command, out AbstractRoom roomA, out AbstractRoom roomB, out string endpointError))
        {
            Fail(endpointError);
            return;
        }

        if (!WorldTextRegistry.EnsureLoaded(region))
        {
            Fail(WorldTextRegistry.LoadError ?? "world.txt is unavailable.");
            return;
        }

        if (!WorldTextRegistry.TrySetConnection(region, roomA.name, command.NodeA, "DISCONNECTED", out string error) ||
            !WorldTextRegistry.TrySetConnection(region, roomB.name, command.NodeB, "DISCONNECTED", out error))
        {
            Fail(error ?? "Could not disconnect the endpoints in world.txt.");
            return;
        }

        SetLiveConnection(roomA, command.NodeA, -1);
        SetLiveConnection(roomB, command.NodeB, -1);

        if (!string.IsNullOrWhiteSpace(command.EdgeId))
            WorldTopologyRegistry.RemoveEdge(region, command.EdgeId);

        Succeed(
            "Disconnected " + roomA.name + ":" + command.NodeA + " and " +
            roomB.name + ":" + command.NodeB + ".");
    }

    private static bool TryResolveEndpoints(
        World world,
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
