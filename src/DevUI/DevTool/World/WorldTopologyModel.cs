using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Direction of one explicit world connection edge.
/// A/B are stable edge endpoints, not implied source/destination names.
/// </summary>
public enum WorldConnectionDirection
{
    Bidirectional,
    AToB,
    BToA
}

/// <summary>
/// One concrete room node participating in a world connection.
/// For ordinary room-to-room pipes NodeIndex is the room exit/connection index.
/// Keeping the endpoint explicit is what allows two rooms to connect through multiple pipes.
/// </summary>
internal readonly struct WorldConnectionEndpoint : IEquatable<WorldConnectionEndpoint>
{
    internal WorldConnectionEndpoint(string room, int nodeIndex)
    {
        Room = NormalizeRoom(room);
        NodeIndex = nodeIndex;
    }

    internal string Room { get; }
    internal int NodeIndex { get; }
    internal bool IsValid => !string.IsNullOrEmpty(Room) && NodeIndex >= 0;
    internal string Key => (Room ?? string.Empty) + ":" + NodeIndex;

    public bool Equals(WorldConnectionEndpoint other) =>
        NodeIndex == other.NodeIndex &&
        string.Equals(Room, other.Room, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object obj) =>
        obj is WorldConnectionEndpoint other && Equals(other);

    public override int GetHashCode() =>
        ((Room == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Room)) * 397) ^ NodeIndex;

    public override string ToString() => Key;

    private static string NormalizeRoom(string room) =>
        string.IsNullOrWhiteSpace(room) ? string.Empty : room.Trim();
}

/// <summary>
/// Independent graph edge. Connections belong to the world graph, not to either room.
/// </summary>
internal sealed class WorldConnectionEdge
{
    internal string Id = string.Empty;
    internal WorldConnectionEndpoint A;
    internal WorldConnectionEndpoint B;
    internal WorldConnectionDirection Direction = WorldConnectionDirection.Bidirectional;

    internal bool AllowsFromA =>
        Direction == WorldConnectionDirection.Bidirectional ||
        Direction == WorldConnectionDirection.AToB;

    internal bool AllowsFromB =>
        Direction == WorldConnectionDirection.Bidirectional ||
        Direction == WorldConnectionDirection.BToA;

    internal WorldConnectionEdge Clone() => new()
    {
        Id = Id,
        A = A,
        B = B,
        Direction = Direction
    };
}

internal enum WorldTopologyIssueKind
{
    InvalidEndpoint,
    DuplicateId,
    DuplicateEdge,
    EndpointConflict,
    MissingRoom,
    MissingNode
}

internal sealed class WorldTopologyIssue
{
    internal WorldTopologyIssueKind Kind;
    internal string EdgeId = string.Empty;
    internal string Message = string.Empty;
}

internal sealed class WorldTopologyDocument
{
    internal readonly Dictionary<string, List<WorldConnectionEdge>> Regions =
        new(StringComparer.OrdinalIgnoreCase);

    internal List<WorldConnectionEdge> GetOrCreateRegion(string region)
    {
        string key = NormalizeRegion(region);
        if (!Regions.TryGetValue(key, out List<WorldConnectionEdge> edges))
        {
            edges = new List<WorldConnectionEdge>();
            Regions[key] = edges;
        }
        return edges;
    }

    internal bool TryGetRegion(string region, out List<WorldConnectionEdge> edges) =>
        Regions.TryGetValue(NormalizeRegion(region), out edges);

    internal static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
