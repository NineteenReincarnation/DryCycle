using System;
using System.Collections.Concurrent;
using DevInterface;

namespace DryCycle.DevUI.DevTool.World;

internal enum WorldTopologyCommandKind
{
    AddExplicitMapping,
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
/// Main-thread mutation queue for WorldTopology.json authoring.
/// RWImGui only describes the requested edit; validation and the actual mutation happen
/// alongside the other DevTool command queues on DevUI.Update.
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
        AbstractRoom roomA = world.GetAbstractRoom(command.RoomA);
        AbstractRoom roomB = world.GetAbstractRoom(command.RoomB);
        if (roomA == null || roomB == null)
        {
            Fail("Both rooms must exist in the currently loaded region.");
            return;
        }

        if (!IsExit(roomA, command.NodeA) || !IsExit(roomB, command.NodeB))
        {
            Fail("Both endpoints must be room Exit nodes.");
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

        WorldConnectionEndpoint a = new(roomA.name, command.NodeA);
        WorldConnectionEndpoint b = new(roomB.name, command.NodeB);
        if (!WorldTopologyRegistry.TryAddEdge(
                region,
                a,
                b,
                command.Direction,
                out string edgeId,
                out string error))
        {
            Fail(error ?? "Could not create explicit connection mapping.");
            return;
        }

        Succeed(
            "Mapped " + a + " " + DirectionGlyph(command.Direction) + " " + b +
            " (" + edgeId.Substring(0, Math.Min(8, edgeId.Length)) + ").");
    }

    private static bool IsExit(AbstractRoom room, int nodeIndex) =>
        room?.nodes != null &&
        nodeIndex >= 0 &&
        nodeIndex < room.nodes.Length &&
        room.nodes[nodeIndex].type == AbstractRoomNode.Type.Exit;

    private static bool PointsTo(AbstractRoom room, int nodeIndex, int targetRoomIndex) =>
        room?.connections != null &&
        nodeIndex >= 0 &&
        nodeIndex < room.connections.Length &&
        room.connections[nodeIndex] == targetRoomIndex;

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
