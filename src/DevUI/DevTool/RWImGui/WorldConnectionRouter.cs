using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Screen-space orthogonal connection router for the World Map.
/// Rooms are obstacles, shortcut mouths are constrained ports, and cached routes are biased toward
/// their previous corridor so interactive room dragging does not make links jump from side to side.
/// </summary>
internal static class WorldConnectionRouter
{
    internal enum RouteKind
    {
        Bridge,
        Orthogonal,
        Fallback
    }

    internal readonly struct Obstacle
    {
        internal Obstacle(int roomIndex, Num.Vector2 min, Num.Vector2 max)
        {
            RoomIndex = roomIndex;
            Min = Num.Vector2.Min(min, max);
            Max = Num.Vector2.Max(min, max);
        }

        internal int RoomIndex { get; }
        internal Num.Vector2 Min { get; }
        internal Num.Vector2 Max { get; }

        internal Obstacle Inflate(float amount) => new(
            RoomIndex,
            Min - new Num.Vector2(amount, amount),
            Max + new Num.Vector2(amount, amount));

        internal bool Contains(Num.Vector2 point) =>
            point.X > Min.X && point.X < Max.X && point.Y > Min.Y && point.Y < Max.Y;

        internal bool IntersectsBounds(Num.Vector2 min, Num.Vector2 max, float padding = 0f) =>
            Max.X >= min.X - padding && Min.X <= max.X + padding &&
            Max.Y >= min.Y - padding && Min.Y <= max.Y + padding;
    }

    internal sealed class Request
    {
        internal string Id = string.Empty;
        internal int StartRoom;
        internal int EndRoom;
        internal Num.Vector2 Start;
        internal Num.Vector2 End;
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
    }

    internal sealed class Route
    {
        internal string Id = string.Empty;
        internal RouteKind Kind;
        internal Num.Vector2[] Points = Array.Empty<Num.Vector2>();
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal bool Reused;
    }

    private sealed class CachedRoute
    {
        internal Route Route;
        internal Num.Vector2 Start;
        internal Num.Vector2 End;
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal int LastSeenGeneration;
    }

    private sealed class SearchNode
    {
        internal int X;
        internal int Y;
        internal int Direction;
        internal float G;
        internal float F;
        internal int Parent = -1;
        internal bool Closed;
    }

    private sealed class MinHeap
    {
        private readonly List<int> items = new();
        private readonly List<SearchNode> nodes;

        internal MinHeap(List<SearchNode> nodes) => this.nodes = nodes;

        internal int Count => items.Count;

        internal void Push(int nodeIndex)
        {
            items.Add(nodeIndex);
            int index = items.Count - 1;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (Compare(items[parent], items[index]) <= 0) break;
                int swap = items[parent];
                items[parent] = items[index];
                items[index] = swap;
                index = parent;
            }
        }

        internal int Pop()
        {
            int result = items[0];
            int last = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            if (items.Count == 0) return result;
            items[0] = last;
            int index = 0;
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= items.Count) break;
                int right = left + 1;
                int best = right < items.Count && Compare(items[right], items[left]) < 0 ? right : left;
                if (Compare(items[index], items[best]) <= 0) break;
                int swap = items[index];
                items[index] = items[best];
                items[best] = swap;
                index = best;
            }
            return result;
        }

        private int Compare(int a, int b)
        {
            int byF = nodes[a].F.CompareTo(nodes[b].F);
            if (byF != 0) return byF;
            int byG = nodes[a].G.CompareTo(nodes[b].G);
            if (byG != 0) return byG;
            int byY = nodes[a].Y.CompareTo(nodes[b].Y);
            return byY != 0 ? byY : nodes[a].X.CompareTo(nodes[b].X);
        }
    }

    private readonly struct Occupancy
    {
        internal Occupancy(byte directionMask, byte count)
        {
            DirectionMask = directionMask;
            Count = count;
        }

        internal byte DirectionMask { get; }
        internal byte Count { get; }
    }

    private const float ObstacleMargin = 15f;
    private const float PortNeck = 22f;
    private const float BridgeDistance = 170f;
    private const float BridgeAlignmentTolerance = 56f;
    private const float BendPenalty = 0.72f;
    private const float BacktrackPenalty = 2.65f;
    private const float CrossingPenalty = 7.5f;
    private const float ParallelCongestionPenalty = 1.15f;
    private const float ProximityPenalty = 0.22f;
    private const float StabilityBonus = 0.22f;
    private const float SearchPadding = 150f;
    private const int MaxGridExtent = 112;

    private static readonly Dictionary<string, CachedRoute> cache = new(StringComparer.Ordinal);
    private static int generation;

    internal static Route[] BuildRoutes(IReadOnlyList<Request> requests, IReadOnlyList<Obstacle> sourceObstacles)
    {
        generation++;
        if (requests == null || requests.Count == 0)
        {
            PruneCache();
            return Array.Empty<Route>();
        }

        List<Obstacle> obstacles = new(sourceObstacles?.Count ?? 0);
        if (sourceObstacles != null)
        {
            for (int i = 0; i < sourceObstacles.Count; i++)
                obstacles.Add(sourceObstacles[i].Inflate(ObstacleMargin));
        }

        Dictionary<long, Occupancy> occupancy = new();
        Route[] result = new Route[requests.Count];

        for (int i = 0; i < requests.Count; i++)
        {
            Request request = requests[i];
            CachedRoute previous = null;
            if (!string.IsNullOrEmpty(request.Id)) cache.TryGetValue(request.Id, out previous);

            Route route;
            if (TryReuse(request, obstacles, previous, out route))
            {
                route.Reused = true;
            }
            else
            {
                route = BuildRoute(request, obstacles, occupancy, previous?.Route);
                if (!string.IsNullOrEmpty(request.Id))
                {
                    cache[request.Id] = new CachedRoute
                    {
                        Route = route,
                        Start = request.Start,
                        End = request.End,
                        StartDirection = request.StartDirection,
                        EndDirection = request.EndDirection,
                        LaneOffset = request.LaneOffset,
                        LastSeenGeneration = generation
                    };
                }
            }

            if (!string.IsNullOrEmpty(request.Id) && cache.TryGetValue(request.Id, out CachedRoute stored))
                stored.LastSeenGeneration = generation;

            result[i] = route;
            RegisterOccupancy(route, occupancy);
        }

        PruneCache();
        return result;
    }

    internal static void Clear()
    {
        cache.Clear();
        generation = 0;
    }

    internal static Num.Vector2 InferPortDirection(Num.Vector2 point, Num.Vector2 roomMin, Num.Vector2 roomMax)
    {
        float left = Math.Abs(point.X - roomMin.X);
        float right = Math.Abs(roomMax.X - point.X);
        float top = Math.Abs(point.Y - roomMin.Y);
        float bottom = Math.Abs(roomMax.Y - point.Y);
        float best = Math.Min(Math.Min(left, right), Math.Min(top, bottom));

        if (best == left) return new Num.Vector2(-1f, 0f);
        if (best == right) return new Num.Vector2(1f, 0f);
        if (best == top) return new Num.Vector2(0f, -1f);
        return new Num.Vector2(0f, 1f);
    }

    private static Route BuildRoute(
        Request request,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        Route previous)
    {
        Num.Vector2 startDirection = Cardinalize(request.StartDirection, request.End - request.Start);
        Num.Vector2 endDirection = Cardinalize(request.EndDirection, request.Start - request.End);
        Num.Vector2 startPerp = new(-startDirection.Y, startDirection.X);
        Num.Vector2 endPerp = new(-endDirection.Y, endDirection.X);

        // Facing ports naturally produce opposite local normals. Align their lane normals before
        // applying the lane offset, otherwise lane +1 leaves one room above the centreline and
        // enters the other room below it, causing multi-links to cross each other in the middle.
        float perpAgreement = Num.Vector2.Dot(startPerp, endPerp);
        if (perpAgreement < -0.25f)
        {
            endPerp = -endPerp;
        }
        else if (Math.Abs(perpAgreement) <= 0.25f)
        {
            Num.Vector2 pairDelta = request.End - request.Start;
            Num.Vector2 stableNormal = Math.Abs(pairDelta.X) >= Math.Abs(pairDelta.Y)
                ? new Num.Vector2(0f, 1f)
                : new Num.Vector2(1f, 0f);
            if (Num.Vector2.Dot(startPerp, stableNormal) < 0f) startPerp = -startPerp;
            if (Num.Vector2.Dot(endPerp, stableNormal) < 0f) endPerp = -endPerp;
        }

        Num.Vector2 startBaseEscape = EscapeOutsideRoom(request.Start, startDirection, request.StartRoom, obstacles);
        Num.Vector2 endBaseEscape = EscapeOutsideRoom(request.End, endDirection, request.EndRoom, obstacles);
        Num.Vector2 startEscape = startBaseEscape + startPerp * request.LaneOffset;
        Num.Vector2 endEscape = endBaseEscape + endPerp * request.LaneOffset;

        if (CanUseBridge(request, startDirection, endDirection, startEscape, endEscape, obstacles))
        {
            Num.Vector2[] bridge = BuildBridgePath(
                request.Start, startBaseEscape, startEscape, endEscape, endBaseEscape, request.End);
            return NewRoute(request, RouteKind.Bridge, Simplify(bridge), startDirection, endDirection);
        }

        if (TrySimpleOrthogonal(
                startEscape,
                endEscape,
                request.StartRoom,
                request.EndRoom,
                obstacles,
                out Num.Vector2[] simple))
        {
            List<Num.Vector2> points = new(simple.Length + 6) { request.Start, startBaseEscape };
            if (Num.Vector2.DistanceSquared(startBaseEscape, startEscape) > 0.25f) points.Add(startEscape);
            for (int i = 1; i < simple.Length - 1; i++) points.Add(simple[i]);
            if (Num.Vector2.DistanceSquared(endEscape, endBaseEscape) > 0.25f) points.Add(endEscape);
            points.Add(endBaseEscape);
            points.Add(request.End);
            return NewRoute(request, RouteKind.Orthogonal, Simplify(points.ToArray()), startDirection, endDirection);
        }

        Num.Vector2[] searched = SearchOrthogonal(
            startEscape,
            endEscape,
            request.StartRoom,
            request.EndRoom,
            obstacles,
            occupancy,
            previous);

        if (searched.Length > 0)
        {
            List<Num.Vector2> points = new(searched.Length + 6) { request.Start, startBaseEscape };
            if (Num.Vector2.DistanceSquared(startBaseEscape, startEscape) > 0.25f) points.Add(startEscape);
            for (int i = 1; i < searched.Length - 1; i++) points.Add(searched[i]);
            if (Num.Vector2.DistanceSquared(endEscape, endBaseEscape) > 0.25f) points.Add(endEscape);
            points.Add(endBaseEscape);
            points.Add(request.End);
            return NewRoute(request, RouteKind.Orthogonal, Simplify(points.ToArray()), startDirection, endDirection);
        }

        Num.Vector2[] fallback = BuildOuterFallback(
            request.Start,
            startBaseEscape,
            startEscape,
            endEscape,
            endBaseEscape,
            request.End,
            request.StartRoom,
            request.EndRoom,
            obstacles);
        return NewRoute(request, RouteKind.Fallback, Simplify(fallback), startDirection, endDirection);
    }

    private static Route NewRoute(
        Request request,
        RouteKind kind,
        Num.Vector2[] points,
        Num.Vector2 startDirection,
        Num.Vector2 endDirection)
    {
        return new Route
        {
            Id = request.Id ?? string.Empty,
            Kind = kind,
            Points = points ?? Array.Empty<Num.Vector2>(),
            StartDirection = startDirection,
            EndDirection = endDirection,
            LaneOffset = request.LaneOffset
        };
    }

    private static bool TryReuse(
        Request request,
        List<Obstacle> obstacles,
        CachedRoute cached,
        out Route route)
    {
        route = null;
        if (cached?.Route?.Points == null || cached.Route.Points.Length < 2) return false;
        if (Num.Vector2.DistanceSquared(cached.Start, request.Start) > 0.25f ||
            Num.Vector2.DistanceSquared(cached.End, request.End) > 0.25f ||
            Num.Vector2.DistanceSquared(cached.StartDirection, request.StartDirection) > 0.01f ||
            Num.Vector2.DistanceSquared(cached.EndDirection, request.EndDirection) > 0.01f ||
            Math.Abs(cached.LaneOffset - request.LaneOffset) > 0.01f)
            return false;

        if (!RouteClear(cached.Route.Points, request.StartRoom, request.EndRoom, obstacles)) return false;
        route = Clone(cached.Route);
        return true;
    }

    private static Route Clone(Route route)
    {
        return new Route
        {
            Id = route.Id,
            Kind = route.Kind,
            Points = (Num.Vector2[])route.Points.Clone(),
            StartDirection = route.StartDirection,
            EndDirection = route.EndDirection,
            LaneOffset = route.LaneOffset,
            Reused = route.Reused
        };
    }

    private static Num.Vector2 EscapeOutsideRoom(
        Num.Vector2 mouth,
        Num.Vector2 direction,
        int roomIndex,
        List<Obstacle> obstacles)
    {
        Num.Vector2 escape = mouth + direction * PortNeck;
        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (obstacle.RoomIndex != roomIndex) continue;

            const float clearance = 5f;
            if (direction.X < -0.5f) escape.X = Math.Min(escape.X, obstacle.Min.X - clearance);
            else if (direction.X > 0.5f) escape.X = Math.Max(escape.X, obstacle.Max.X + clearance);
            else if (direction.Y < -0.5f) escape.Y = Math.Min(escape.Y, obstacle.Min.Y - clearance);
            else escape.Y = Math.Max(escape.Y, obstacle.Max.Y + clearance);
            break;
        }
        return escape;
    }

    private static bool CanUseBridge(
        Request request,
        Num.Vector2 startDirection,
        Num.Vector2 endDirection,
        Num.Vector2 startEscape,
        Num.Vector2 endEscape,
        List<Obstacle> obstacles)
    {
        Num.Vector2 delta = request.End - request.Start;
        float distance = delta.Length();
        if (distance > BridgeDistance || distance < 1f) return false;
        Num.Vector2 forward = delta / distance;
        if (Num.Vector2.Dot(startDirection, forward) < 0.45f ||
            Num.Vector2.Dot(endDirection, -forward) < 0.45f)
            return false;

        bool mostlyHorizontal = Math.Abs(forward.X) >= Math.Abs(forward.Y);
        if (mostlyHorizontal && Math.Abs(request.Start.Y - request.End.Y) > BridgeAlignmentTolerance) return false;
        if (!mostlyHorizontal && Math.Abs(request.Start.X - request.End.X) > BridgeAlignmentTolerance) return false;

        return !SegmentBlocked(startEscape, endEscape, request.StartRoom, request.EndRoom, obstacles);
    }

    private static Num.Vector2[] BuildBridgePath(
        Num.Vector2 start,
        Num.Vector2 startBaseEscape,
        Num.Vector2 startEscape,
        Num.Vector2 endEscape,
        Num.Vector2 endBaseEscape,
        Num.Vector2 end)
    {
        Num.Vector2 delta = endEscape - startEscape;
        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            float midX = (startEscape.X + endEscape.X) * 0.5f;
            return new[]
            {
                start,
                startBaseEscape,
                startEscape,
                new Num.Vector2(midX, startEscape.Y),
                new Num.Vector2(midX, endEscape.Y),
                endEscape,
                endBaseEscape,
                end
            };
        }

        float midY = (startEscape.Y + endEscape.Y) * 0.5f;
        return new[]
        {
            start,
            startBaseEscape,
            startEscape,
            new Num.Vector2(startEscape.X, midY),
            new Num.Vector2(endEscape.X, midY),
            endEscape,
            endBaseEscape,
            end
        };
    }

    private static bool TrySimpleOrthogonal(
        Num.Vector2 start,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles,
        out Num.Vector2[] points)
    {
        points = null;
        if (Math.Abs(start.X - end.X) < 0.5f || Math.Abs(start.Y - end.Y) < 0.5f)
        {
            if (!SegmentBlocked(start, end, startRoom, endRoom, obstacles))
            {
                points = new[] { start, end };
                return true;
            }
        }

        Num.Vector2 hv = new(end.X, start.Y);
        Num.Vector2 vh = new(start.X, end.Y);
        bool hvClear = !SegmentBlocked(start, hv, startRoom, endRoom, obstacles) &&
                       !SegmentBlocked(hv, end, startRoom, endRoom, obstacles);
        bool vhClear = !SegmentBlocked(start, vh, startRoom, endRoom, obstacles) &&
                       !SegmentBlocked(vh, end, startRoom, endRoom, obstacles);

        if (!hvClear && !vhClear) return false;
        if (hvClear && !vhClear)
        {
            points = new[] { start, hv, end };
            return true;
        }
        if (vhClear && !hvClear)
        {
            points = new[] { start, vh, end };
            return true;
        }

        float hvShape = Math.Abs(start.Y - end.Y) + Math.Abs(start.X - end.X) * 0.001f;
        float vhShape = Math.Abs(start.X - end.X) + Math.Abs(start.Y - end.Y) * 0.001f;
        points = hvShape <= vhShape ? new[] { start, hv, end } : new[] { start, vh, end };
        return true;
    }

    private static Num.Vector2[] SearchOrthogonal(
        Num.Vector2 start,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        Route previous)
    {
        Num.Vector2 min = Num.Vector2.Min(start, end) - new Num.Vector2(SearchPadding, SearchPadding);
        Num.Vector2 max = Num.Vector2.Max(start, end) + new Num.Vector2(SearchPadding, SearchPadding);

        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (!obstacle.IntersectsBounds(min, max, 40f)) continue;
            min = Num.Vector2.Min(min, obstacle.Min - new Num.Vector2(36f, 36f));
            max = Num.Vector2.Max(max, obstacle.Max + new Num.Vector2(36f, 36f));
        }

        Num.Vector2 span = Num.Vector2.Max(max - min, new Num.Vector2(1f, 1f));
        float cell = Math.Max(14f, Math.Min(30f, Math.Max(span.X, span.Y) / 90f));
        int width = Math.Max(3, Math.Min(MaxGridExtent, (int)Math.Ceiling(span.X / cell) + 1));
        int height = Math.Max(3, Math.Min(MaxGridExtent, (int)Math.Ceiling(span.Y / cell) + 1));
        if (width >= MaxGridExtent || height >= MaxGridExtent)
        {
            cell = Math.Max(cell, Math.Max(span.X / (MaxGridExtent - 2f), span.Y / (MaxGridExtent - 2f)));
            width = Math.Max(3, Math.Min(MaxGridExtent, (int)Math.Ceiling(span.X / cell) + 1));
            height = Math.Max(3, Math.Min(MaxGridExtent, (int)Math.Ceiling(span.Y / cell) + 1));
        }

        int sx = Clamp((int)Math.Round((start.X - min.X) / cell), 0, width - 1);
        int sy = Clamp((int)Math.Round((start.Y - min.Y) / cell), 0, height - 1);
        int ex = Clamp((int)Math.Round((end.X - min.X) / cell), 0, width - 1);
        int ey = Clamp((int)Math.Round((end.Y - min.Y) / cell), 0, height - 1);

        bool[] blocked = BuildBlockedGrid(min, cell, width, height, obstacles);
        blocked[sy * width + sx] = false;
        blocked[ey * width + ex] = false;

        HashSet<long> stableCells = BuildStableCells(previous, min, cell, width, height);
        List<SearchNode> nodes = new(width * height * 2);
        Dictionary<int, int> bestByState = new();
        MinHeap open = new(nodes);

        SearchNode seed = new()
        {
            X = sx,
            Y = sy,
            Direction = 4,
            G = 0f,
            F = Manhattan(sx, sy, ex, ey)
        };
        nodes.Add(seed);
        bestByState[StateKey(sx, sy, 4, width, height)] = 0;
        open.Push(0);

        int goalIndex = -1;
        int[] dx = { 1, 0, -1, 0 };
        int[] dy = { 0, 1, 0, -1 };

        while (open.Count > 0)
        {
            int currentIndex = open.Pop();
            SearchNode current = nodes[currentIndex];
            if (current.Closed) continue;
            current.Closed = true;

            if (current.X == ex && current.Y == ey)
            {
                goalIndex = currentIndex;
                break;
            }

            for (int direction = 0; direction < 4; direction++)
            {
                int nx = current.X + dx[direction];
                int ny = current.Y + dy[direction];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (blocked[ny * width + nx]) continue;

                float step = 1f;
                if (current.Direction < 4)
                {
                    if (current.Direction != direction) step += BendPenalty;
                    if (((current.Direction + 2) & 3) == direction) step += BacktrackPenalty;
                }
                if (IsNearBlockedCell(nx, ny, blocked, width, height))
                    step += ProximityPenalty;

                Num.Vector2 worldNeighbor = new(min.X + nx * cell, min.Y + ny * cell);
                long occupancyKey = GridKey((int)Math.Round(worldNeighbor.X / 18f), (int)Math.Round(worldNeighbor.Y / 18f));
                if (occupancy.TryGetValue(occupancyKey, out Occupancy occupied))
                {
                    byte perpendicular = (byte)(occupied.DirectionMask & PerpendicularMask(direction));
                    step += perpendicular != 0
                        ? CrossingPenalty * Math.Max(1, (int)occupied.Count)
                        : ParallelCongestionPenalty * Math.Max(1, (int)occupied.Count);
                }

                if (stableCells.Contains(GridKey(nx, ny)))
                    step = Math.Max(0.25f, step - StabilityBonus);

                float g = current.G + step;
                int stateKey = StateKey(nx, ny, direction, width, height);
                if (bestByState.TryGetValue(stateKey, out int existingIndex) && nodes[existingIndex].G <= g)
                    continue;

                SearchNode next = new()
                {
                    X = nx,
                    Y = ny,
                    Direction = direction,
                    G = g,
                    F = g + Manhattan(nx, ny, ex, ey),
                    Parent = currentIndex
                };
                int nextIndex = nodes.Count;
                nodes.Add(next);
                bestByState[stateKey] = nextIndex;
                open.Push(nextIndex);
            }
        }

        if (goalIndex < 0) return Array.Empty<Num.Vector2>();

        List<Num.Vector2> reversed = new();
        int cursor = goalIndex;
        while (cursor >= 0)
        {
            SearchNode node = nodes[cursor];
            reversed.Add(new Num.Vector2(min.X + node.X * cell, min.Y + node.Y * cell));
            cursor = node.Parent;
        }
        reversed.Reverse();

        List<Num.Vector2> route = new(reversed.Count + 6) { start };
        if (reversed.Count > 0)
        {
            Num.Vector2 first = reversed[0];
            if (Math.Abs(start.X - first.X) > 0.5f && Math.Abs(start.Y - first.Y) > 0.5f)
                route.Add(new Num.Vector2(first.X, start.Y));
            route.AddRange(reversed);
            Num.Vector2 last = reversed[reversed.Count - 1];
            if (Math.Abs(end.X - last.X) > 0.5f && Math.Abs(end.Y - last.Y) > 0.5f)
                route.Add(new Num.Vector2(end.X, last.Y));
        }
        route.Add(end);

        Num.Vector2[] simplified = Simplify(route.ToArray());
        return RouteClear(simplified, startRoom, endRoom, obstacles)
            ? simplified
            : Array.Empty<Num.Vector2>();
    }

    private static bool[] BuildBlockedGrid(
        Num.Vector2 origin,
        float cell,
        int width,
        int height,
        List<Obstacle> obstacles)
    {
        bool[] blocked = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            float py = origin.Y + y * cell;
            for (int x = 0; x < width; x++)
            {
                Num.Vector2 point = new(origin.X + x * cell, py);
                for (int i = 0; i < obstacles.Count; i++)
                {
                    if (!obstacles[i].Contains(point)) continue;
                    blocked[y * width + x] = true;
                    break;
                }
            }
        }
        return blocked;
    }

    private static bool IsNearBlockedCell(int x, int y, bool[] blocked, int width, int height)
    {
        for (int oy = -1; oy <= 1; oy++)
        {
            int py = y + oy;
            if (py < 0 || py >= height) continue;
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int px = x + ox;
                if (px < 0 || px >= width) continue;
                if (blocked[py * width + px]) return true;
            }
        }
        return false;
    }

    private static HashSet<long> BuildStableCells(
        Route previous,
        Num.Vector2 origin,
        float cell,
        int width,
        int height)
    {
        HashSet<long> cells = new();
        Num.Vector2[] points = previous?.Points;
        if (points == null || points.Length < 2) return cells;

        for (int i = 0; i < points.Length - 1; i++)
        {
            Num.Vector2 a = points[i];
            Num.Vector2 b = points[i + 1];
            float length = Num.Vector2.Distance(a, b);
            int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(4f, cell * 0.5f)));
            for (int s = 0; s <= steps; s++)
            {
                Num.Vector2 p = Num.Vector2.Lerp(a, b, s / (float)steps);
                int x = Clamp((int)Math.Round((p.X - origin.X) / cell), 0, width - 1);
                int y = Clamp((int)Math.Round((p.Y - origin.Y) / cell), 0, height - 1);
                cells.Add(GridKey(x, y));
            }
        }
        return cells;
    }

    private static Num.Vector2[] BuildOuterFallback(
        Num.Vector2 start,
        Num.Vector2 startBaseEscape,
        Num.Vector2 startEscape,
        Num.Vector2 endEscape,
        Num.Vector2 endBaseEscape,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles)
    {
        Num.Vector2 min = Num.Vector2.Min(startEscape, endEscape);
        Num.Vector2 max = Num.Vector2.Max(startEscape, endEscape);
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (!obstacles[i].IntersectsBounds(min, max, 80f)) continue;
            min = Num.Vector2.Min(min, obstacles[i].Min);
            max = Num.Vector2.Max(max, obstacles[i].Max);
        }

        float left = min.X - 34f;
        float right = max.X + 34f;
        float top = min.Y - 34f;
        float bottom = max.Y + 34f;

        Num.Vector2[][] candidates =
        {
            new[] { start, startBaseEscape, startEscape, new Num.Vector2(left, startEscape.Y), new Num.Vector2(left, endEscape.Y), endEscape, endBaseEscape, end },
            new[] { start, startBaseEscape, startEscape, new Num.Vector2(right, startEscape.Y), new Num.Vector2(right, endEscape.Y), endEscape, endBaseEscape, end },
            new[] { start, startBaseEscape, startEscape, new Num.Vector2(startEscape.X, top), new Num.Vector2(endEscape.X, top), endEscape, endBaseEscape, end },
            new[] { start, startBaseEscape, startEscape, new Num.Vector2(startEscape.X, bottom), new Num.Vector2(endEscape.X, bottom), endEscape, endBaseEscape, end }
        };

        float bestCost = float.MaxValue;
        Num.Vector2[] best = candidates[0];
        for (int i = 0; i < candidates.Length; i++)
        {
            Num.Vector2[] candidate = Simplify(candidates[i]);
            float cost = RouteClear(candidate, startRoom, endRoom, obstacles) ? PathLength(candidate) : PathLength(candidate) + 100000f;
            if (cost >= bestCost) continue;
            bestCost = cost;
            best = candidate;
        }
        return best;
    }

    private static void RegisterOccupancy(Route route, Dictionary<long, Occupancy> occupancy)
    {
        Num.Vector2[] points = route?.Points;
        if (points == null || points.Length < 2) return;

        for (int i = 0; i < points.Length - 1; i++)
        {
            Num.Vector2 a = points[i];
            Num.Vector2 b = points[i + 1];
            Num.Vector2 delta = b - a;
            float length = delta.Length();
            if (length < 1f) continue;
            int direction = Math.Abs(delta.X) >= Math.Abs(delta.Y)
                ? (delta.X >= 0f ? 0 : 2)
                : (delta.Y >= 0f ? 1 : 3);
            int steps = Math.Max(1, (int)Math.Ceiling(length / 18f));
            for (int s = 0; s <= steps; s++)
            {
                Num.Vector2 p = Num.Vector2.Lerp(a, b, s / (float)steps);
                int gx = (int)Math.Round(p.X / 18f);
                int gy = (int)Math.Round(p.Y / 18f);
                long key = GridKey(gx, gy);
                byte mask = DirectionBit(direction);
                if (occupancy.TryGetValue(key, out Occupancy current))
                    occupancy[key] = new Occupancy((byte)(current.DirectionMask | mask), (byte)Math.Min(255, current.Count + 1));
                else
                    occupancy[key] = new Occupancy(mask, 1);
            }
        }
    }

    private static bool RouteClear(Num.Vector2[] points, int startRoom, int endRoom, List<Obstacle> obstacles)
    {
        if (points == null || points.Length < 2) return false;
        for (int i = 1; i < points.Length - 2; i++)
        {
            if (SegmentBlocked(points[i], points[i + 1], startRoom, endRoom, obstacles))
                return false;
        }
        return true;
    }

    private static bool SegmentBlocked(
        Num.Vector2 a,
        Num.Vector2 b,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles)
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (SegmentIntersectsRect(a, b, obstacle.Min, obstacle.Max)) return true;
        }
        return false;
    }

    private static bool SegmentIntersectsRect(Num.Vector2 a, Num.Vector2 b, Num.Vector2 min, Num.Vector2 max)
    {
        if (Math.Abs(a.X - b.X) < 0.01f)
        {
            if (a.X <= min.X || a.X >= max.X) return false;
            float segMin = Math.Min(a.Y, b.Y);
            float segMax = Math.Max(a.Y, b.Y);
            return segMax > min.Y && segMin < max.Y;
        }
        if (Math.Abs(a.Y - b.Y) < 0.01f)
        {
            if (a.Y <= min.Y || a.Y >= max.Y) return false;
            float segMin = Math.Min(a.X, b.X);
            float segMax = Math.Max(a.X, b.X);
            return segMax > min.X && segMin < max.X;
        }

        float t0 = 0f;
        float t1 = 1f;
        Num.Vector2 d = b - a;
        return Clip(-d.X, a.X - min.X, ref t0, ref t1) &&
               Clip(d.X, max.X - a.X, ref t0, ref t1) &&
               Clip(-d.Y, a.Y - min.Y, ref t0, ref t1) &&
               Clip(d.Y, max.Y - a.Y, ref t0, ref t1) &&
               t1 > t0;
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (Math.Abs(p) < 0.00001f) return q >= 0f;
        float r = q / p;
        if (p < 0f)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    internal static Num.Vector2[] Simplify(Num.Vector2[] source)
    {
        if (source == null || source.Length <= 2) return source ?? Array.Empty<Num.Vector2>();
        List<Num.Vector2> result = new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            Num.Vector2 point = source[i];
            if (result.Count > 0 && Num.Vector2.DistanceSquared(result[result.Count - 1], point) < 0.25f) continue;
            result.Add(point);
            while (result.Count >= 3)
            {
                Num.Vector2 a = result[result.Count - 3];
                Num.Vector2 b = result[result.Count - 2];
                Num.Vector2 c = result[result.Count - 1];
                Num.Vector2 ab = b - a;
                Num.Vector2 bc = c - b;
                if (Math.Abs(Cross(ab, bc)) > 0.01f || Num.Vector2.Dot(ab, bc) < 0f) break;
                result.RemoveAt(result.Count - 2);
            }
        }
        return result.ToArray();
    }

    internal static float PathLength(Num.Vector2[] points)
    {
        float total = 0f;
        if (points == null) return total;
        for (int i = 0; i < points.Length - 1; i++) total += Num.Vector2.Distance(points[i], points[i + 1]);
        return total;
    }

    private static Num.Vector2 Cardinalize(Num.Vector2 value, Num.Vector2 fallback)
    {
        if (value.LengthSquared() < 0.001f) value = fallback;
        if (Math.Abs(value.X) >= Math.Abs(value.Y)) return new Num.Vector2(value.X >= 0f ? 1f : -1f, 0f);
        return new Num.Vector2(0f, value.Y >= 0f ? 1f : -1f);
    }

    private static float Cross(Num.Vector2 a, Num.Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static int Manhattan(int x, int y, int ex, int ey) => Math.Abs(ex - x) + Math.Abs(ey - y);

    private static int StateKey(int x, int y, int direction, int width, int height) =>
        ((y * width + x) * 5) + direction;

    private static long GridKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static byte DirectionBit(int direction) => (byte)(1 << (direction & 3));

    private static byte PerpendicularMask(int direction) =>
        direction % 2 == 0 ? (byte)((1 << 1) | (1 << 3)) : (byte)((1 << 0) | (1 << 2));

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static void PruneCache()
    {
        if (cache.Count == 0) return;
        List<string> stale = null;
        foreach (KeyValuePair<string, CachedRoute> pair in cache)
        {
            if (generation - pair.Value.LastSeenGeneration <= 2) continue;
            stale ??= new List<string>();
            stale.Add(pair.Key);
        }
        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
    }
}
