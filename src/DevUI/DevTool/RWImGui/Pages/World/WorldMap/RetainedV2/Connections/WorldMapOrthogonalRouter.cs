using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Coordinate-system-agnostic orthogonal router owned by Retained V2.
/// V2 supplies map-world room bounds and ports, so pan/zoom never enter routing or its cache.
/// </summary>
internal static class WorldMapOrthogonalRouter
{
    internal enum RouteKind
    {
        Compact,
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
        internal Num.Vector2 StartRoomMin;
        internal Num.Vector2 StartRoomMax;
        internal Num.Vector2 EndRoomMin;
        internal Num.Vector2 EndRoomMax;
        internal float LaneOffset;
        internal int StartTerminalLaneIndex;
        internal int StartTerminalLaneCount;
        internal float StartTerminalExtraDepth;
        internal int EndTerminalLaneIndex;
        internal int EndTerminalLaneCount;
        internal float EndTerminalExtraDepth;
    }

    internal sealed class Route
    {
        internal string Id = string.Empty;
        internal RouteKind Kind;
        internal Num.Vector2[] Points = Array.Empty<Num.Vector2>();
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal int StartTerminalLaneIndex;
        internal int StartTerminalLaneCount;
        internal float StartTerminalExtraDepth;
        internal int EndTerminalLaneIndex;
        internal int EndTerminalLaneCount;
        internal float EndTerminalExtraDepth;
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
        internal float StartTerminalExtraDepth;
        internal float EndTerminalExtraDepth;
        internal int PolicyVersion;
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
        internal Occupancy(
            byte directionMask,
            byte count,
            byte bendCount)
        {
            DirectionMask = directionMask;
            Count = count;
            BendCount = bendCount;
        }

        internal byte DirectionMask { get; }
        internal byte Count { get; }
        internal byte BendCount { get; }
    }

    // Keep routing corridors visibly detached from room silhouettes. The old 15/22px margins were
    // only large enough for one centreline; once several connections shared a corridor, lane offsets
    // were forced back onto the same line and produced false visual junctions.
    private const float ObstacleMargin = 24f;
    private const float PortNeck = 28f;
    private const float CompactRoomGap = 52f;
    private const float CompactEndpointDistance = 150f;
    private const float CompactAdjacentEndpointDistance = 220f;
    private const float CompactDirectionPenalty = 18f;
    private const float CompactBendPenalty = 3f;
    private const int CacheRetentionGenerations = 32;
    private const int RoutingPolicyVersion = 14;
    internal static int PersistentPolicyVersion => RoutingPolicyVersion;
    private const float BridgeDistance = 170f;
    private const float BridgeAlignmentTolerance = 56f;
    // Readability-first costs: fewer deliberate bends are preferable to a slightly shorter path;
    // crossings are expensive, while parallel corridor sharing is cheap because the lane allocator
    // separates those routes after the base path is solved.
    private const float BendPenalty = 1.60f;
    private const float BacktrackPenalty = 3.40f;
    private const float CrossingPenalty = 11.0f;
    private const float JunctionHotspotTurnPenalty = 2.8f;
    private const float JunctionHotspotPassPenalty = 0.38f;
    private const float ParallelCongestionPenalty = 0.26f;
    private const int PreferredParallelCapacity = 8;
    private const float ParallelOverflowPenalty = 0.92f;
    private const float DirectRouteCongestionLimit = 28f;
    private const int RerouteAvoidanceOccupancyWeight = 12;
    private const float ProximityPenalty = 0.50f;
    private const float StabilityBonus = 0.22f;
    private const float SearchPadding = 150f;
    private const float CongestionRerouteSearchPadding = 240f;
    private const int MaxGridExtent = 112;

    private static readonly Dictionary<string, CachedRoute> cache = new(StringComparer.Ordinal);
    private static int generation;

    internal static Route[] BuildRoutes(IReadOnlyList<Request> requests, IReadOnlyList<Obstacle> sourceObstacles) =>
        BuildRoutesCore(requests, sourceObstacles);

    internal static Obstacle CreateRoutingObstacle(
        int roomIndex,
        Num.Vector2 min,
        Num.Vector2 max) =>
        new Obstacle(roomIndex, min, max).Inflate(ObstacleMargin);

    internal static Route[] BuildRoutesCore(
        IReadOnlyList<Request> requests,
        IReadOnlyList<Obstacle> sourceObstacles,
        bool sourceObstaclesAlreadyInflated = false,
        IReadOnlyList<Num.Vector2[]> occupancySeedPaths = null,
        IReadOnlyList<Num.Vector2[]> avoidanceSeedPaths = null)
    {
        generation++;
        if (requests == null || requests.Count == 0)
        {
            PruneCache();
            return Array.Empty<Route>();
        }

        List<Obstacle> obstacles;
        if (sourceObstaclesAlreadyInflated &&
            sourceObstacles is List<Obstacle> retainedList)
        {
            obstacles = retainedList;
        }
        else
        {
            obstacles = new List<Obstacle>(sourceObstacles?.Count ?? 0);
            if (sourceObstacles != null)
            {
                for (int i = 0; i < sourceObstacles.Count; i++)
                obstacles.Add(
                    sourceObstaclesAlreadyInflated
                        ? sourceObstacles[i]
                        : sourceObstacles[i].Inflate(ObstacleMargin));
            }
        }

        Dictionary<long, Occupancy> occupancy = new();

        if (occupancySeedPaths != null)
        {
            for (int i = 0; i < occupancySeedPaths.Count; i++)
                RegisterOccupancy(occupancySeedPaths[i], occupancy);
        }

        if (avoidanceSeedPaths != null)
        {
            for (int i = 0; i < avoidanceSeedPaths.Count; i++)
            {
                RegisterOccupancy(
                    avoidanceSeedPaths[i],
                    occupancy,
                    RerouteAvoidanceOccupancyWeight);
            }
        }

        bool hasAvoidancePressure =
            avoidanceSeedPaths != null &&
            avoidanceSeedPaths.Count > 0;

        bool hasSeedCongestion =
            occupancySeedPaths != null &&
            occupancySeedPaths.Count > 0 ||
            hasAvoidancePressure;

        Route[] result = new Route[requests.Count];

        for (int i = 0; i < requests.Count; i++)
        {
            Request request = requests[i];
            CachedRoute previous = null;
            if (!string.IsNullOrEmpty(request.Id)) cache.TryGetValue(request.Id, out previous);

            Route route;
            if (!hasSeedCongestion &&
                TryReuse(request, obstacles, previous, out route))
            {
                route.Reused = true;
            }
            else
            {
                route =
                    BuildRoute(
                        request,
                        obstacles,
                        occupancy,
                        previous?.Route,
                        hasAvoidancePressure);
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
                        StartTerminalExtraDepth = request.StartTerminalExtraDepth,
                        EndTerminalExtraDepth = request.EndTerminalExtraDepth,
                        PolicyVersion = RoutingPolicyVersion,
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
        Route previous,
        bool preferAlternativeCorridor)
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

        // Every connection gets a real terminal stub before any global routing decision. Compact
        // routes used to bypass this block entirely, so adjacent rooms could leave the socket and
        // turn immediately on top of other links. That was the main source of "all arrows on one
        // line" and ambiguous T-junction shapes around dense room edges.
        Num.Vector2 startBaseEscape =
            EscapeOutsideRoom(
                request.Start,
                startDirection,
                request.StartRoom,
                obstacles,
                request.StartTerminalExtraDepth);
        Num.Vector2 endBaseEscape =
            EscapeOutsideRoom(
                request.End,
                endDirection,
                request.EndRoom,
                obstacles,
                request.EndTerminalExtraDepth);
        Num.Vector2 startEscape = startBaseEscape + startPerp * request.LaneOffset;
        Num.Vector2 endEscape = endBaseEscape + endPerp * request.LaneOffset;

        if (TryBuildCompactRoute(
                request,
                startDirection,
                endDirection,
                startBaseEscape,
                startEscape,
                endEscape,
                endBaseEscape,
                obstacles,
                occupancy,
                out Num.Vector2[] compact))
        {
            return NewRoute(
                request,
                RouteKind.Compact,
                compact,
                startDirection,
                endDirection);
        }

        if (CanUseBridge(request, startDirection, endDirection, startEscape, endEscape, obstacles))
        {
            Num.Vector2[] bridge =
                SimplifyRoute(
                    BuildBridgePath(
                        request.Start,
                        startBaseEscape,
                        startEscape,
                        endEscape,
                        endBaseEscape,
                        request.End));

            if (RouteClear(
                    bridge,
                    request.StartRoom,
                    request.EndRoom,
                    obstacles) &&
                RouteCongestionPenalty(
                    bridge,
                    occupancy) <=
                DirectRouteCongestionLimit)
            {
                return NewRoute(
                    request,
                    RouteKind.Bridge,
                    bridge,
                    startDirection,
                    endDirection);
            }
        }

        if (TrySimpleOrthogonal(
                startEscape,
                endEscape,
                request.StartRoom,
                request.EndRoom,
                obstacles,
                occupancy,
                out Num.Vector2[] simple))
        {
            List<Num.Vector2> points = new(simple.Length + 6) { request.Start, startBaseEscape };
            if (Num.Vector2.DistanceSquared(startBaseEscape, startEscape) > 0.25f) points.Add(startEscape);
            for (int i = 1; i < simple.Length - 1; i++) points.Add(simple[i]);
            if (Num.Vector2.DistanceSquared(endEscape, endBaseEscape) > 0.25f) points.Add(endEscape);
            points.Add(endBaseEscape);
            points.Add(request.End);
            return NewRoute(request, RouteKind.Orthogonal, SimplifyRoute(points.ToArray()), startDirection, endDirection);
        }

        Num.Vector2[] searched = SearchOrthogonal(
            startEscape,
            endEscape,
            request.StartRoom,
            request.EndRoom,
            obstacles,
            occupancy,
            previous,
            preferAlternativeCorridor);

        if (searched.Length > 0)
        {
            List<Num.Vector2> points = new(searched.Length + 6) { request.Start, startBaseEscape };
            if (Num.Vector2.DistanceSquared(startBaseEscape, startEscape) > 0.25f) points.Add(startEscape);
            for (int i = 1; i < searched.Length - 1; i++) points.Add(searched[i]);
            if (Num.Vector2.DistanceSquared(endEscape, endBaseEscape) > 0.25f) points.Add(endEscape);
            points.Add(endBaseEscape);
            points.Add(request.End);
            return NewRoute(request, RouteKind.Orthogonal, SimplifyRoute(points.ToArray()), startDirection, endDirection);
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
            obstacles,
            occupancy,
            preferAlternativeCorridor);
        return NewRoute(request, RouteKind.Fallback, SimplifyRoute(fallback), startDirection, endDirection);
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
            LaneOffset = request.LaneOffset,
            StartTerminalLaneIndex = request.StartTerminalLaneIndex,
            StartTerminalLaneCount = request.StartTerminalLaneCount,
            StartTerminalExtraDepth = request.StartTerminalExtraDepth,
            EndTerminalLaneIndex = request.EndTerminalLaneIndex,
            EndTerminalLaneCount = request.EndTerminalLaneCount,
            EndTerminalExtraDepth = request.EndTerminalExtraDepth
        };
    }

    private static bool TryReuse(
        Request request,
        List<Obstacle> obstacles,
        CachedRoute cached,
        out Route route)
    {
        route = null;
        if (cached?.Route?.Points == null ||
            cached.Route.Points.Length < 2 ||
            cached.PolicyVersion != RoutingPolicyVersion)
            return false;
        if (Num.Vector2.DistanceSquared(cached.Start, request.Start) > 0.25f ||
            Num.Vector2.DistanceSquared(cached.End, request.End) > 0.25f ||
            Num.Vector2.DistanceSquared(cached.StartDirection, request.StartDirection) > 0.01f ||
            Num.Vector2.DistanceSquared(cached.EndDirection, request.EndDirection) > 0.01f ||
            Math.Abs(cached.LaneOffset - request.LaneOffset) > 0.01f ||
            Math.Abs(
                cached.StartTerminalExtraDepth -
                request.StartTerminalExtraDepth) > 0.01f ||
            Math.Abs(
                cached.EndTerminalExtraDepth -
                request.EndTerminalExtraDepth) > 0.01f)
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
            StartTerminalLaneIndex = route.StartTerminalLaneIndex,
            StartTerminalLaneCount = route.StartTerminalLaneCount,
            StartTerminalExtraDepth = route.StartTerminalExtraDepth,
            EndTerminalLaneIndex = route.EndTerminalLaneIndex,
            EndTerminalLaneCount = route.EndTerminalLaneCount,
            EndTerminalExtraDepth = route.EndTerminalExtraDepth,
            Reused = route.Reused
        };
    }

    private static bool TryBuildCompactRoute(
        Request request,
        Num.Vector2 startDirection,
        Num.Vector2 endDirection,
        Num.Vector2 startBaseEscape,
        Num.Vector2 startEscape,
        Num.Vector2 endEscape,
        Num.Vector2 endBaseEscape,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        out Num.Vector2[] route)
    {
        route = null;

        float gapX = Math.Max(
            0f,
            Math.Max(
                request.StartRoomMin.X - request.EndRoomMax.X,
                request.EndRoomMin.X - request.StartRoomMax.X));
        float gapY = Math.Max(
            0f,
            Math.Max(
                request.StartRoomMin.Y - request.EndRoomMax.Y,
                request.EndRoomMin.Y - request.StartRoomMax.Y));
        float roomGap = (float)Math.Sqrt(gapX * gapX + gapY * gapY);
        float endpointDistance = Num.Vector2.Distance(request.Start, request.End);
        bool closeEndpoints =
            endpointDistance <= CompactEndpointDistance;
        bool adjacentRooms =
            roomGap <= CompactRoomGap &&
            endpointDistance <= CompactAdjacentEndpointDistance;
        if (!closeEndpoints && !adjacentRooms)
            return false;

        // Compact means "small corridor", not "skip the terminal grammar". All candidates are solved
        // between the escaped/lane-shifted points, then the fixed socket stubs are prepended/appended.
        // This preserves a readable ownership cue at both ends and keeps sibling connections apart.
        List<Num.Vector2[]> middles = new(4);

        if (Math.Abs(startEscape.X - endEscape.X) < 0.5f ||
            Math.Abs(startEscape.Y - endEscape.Y) < 0.5f)
        {
            middles.Add(new[] { startEscape, endEscape });
        }

        middles.Add(new[]
        {
            startEscape,
            new Num.Vector2(endEscape.X, startEscape.Y),
            endEscape
        });
        middles.Add(new[]
        {
            startEscape,
            new Num.Vector2(startEscape.X, endEscape.Y),
            endEscape
        });

        Num.Vector2 delta = endEscape - startEscape;
        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            float midX = (startEscape.X + endEscape.X) * 0.5f;
            middles.Add(new[]
            {
                startEscape,
                new Num.Vector2(midX, startEscape.Y),
                new Num.Vector2(midX, endEscape.Y),
                endEscape
            });
        }
        else
        {
            float midY = (startEscape.Y + endEscape.Y) * 0.5f;
            middles.Add(new[]
            {
                startEscape,
                new Num.Vector2(startEscape.X, midY),
                new Num.Vector2(endEscape.X, midY),
                endEscape
            });
        }

        float bestScore = float.MaxValue;
        Num.Vector2[] best = null;

        for (int i = 0; i < middles.Count; i++)
        {
            Num.Vector2[] middle = Simplify(middles[i]);
            if (middle == null || middle.Length < 2)
                continue;

            List<Num.Vector2> full =
                new(middle.Length + 4)
                {
                    request.Start,
                    startBaseEscape
                };

            if (Num.Vector2.DistanceSquared(
                    startBaseEscape,
                    startEscape) > 0.25f)
                full.Add(startEscape);

            for (int p = 1; p < middle.Length - 1; p++)
                full.Add(middle[p]);

            if (Num.Vector2.DistanceSquared(
                    endEscape,
                    endBaseEscape) > 0.25f)
                full.Add(endEscape);

            full.Add(endBaseEscape);
            full.Add(request.End);

            Num.Vector2[] candidate =
                SimplifyRoute(
                    full.ToArray());

            if (candidate == null ||
                candidate.Length < 2 ||
                !RouteClear(
                    candidate,
                    request.StartRoom,
                    request.EndRoom,
                    obstacles))
                continue;

            float congestion =
                RouteCongestionPenalty(
                    candidate,
                    occupancy);
            if (congestion >
                DirectRouteCongestionLimit)
                continue;

            float score =
                PathLength(candidate) +
                Math.Max(0, candidate.Length - 2) * CompactBendPenalty +
                congestion;
            score += EndpointDirectionPenalty(
                candidate,
                startDirection,
                endDirection);

            if (score >= bestScore)
                continue;

            bestScore = score;
            best = candidate;
        }

        if (best == null)
            return false;

        route = best;
        return true;
    }

    private static float EndpointDirectionPenalty(
        Num.Vector2[] points,
        Num.Vector2 startDirection,
        Num.Vector2 endDirection)
    {
        if (points == null || points.Length < 2)
            return CompactDirectionPenalty * 2f;

        Num.Vector2 first = points[1] - points[0];
        Num.Vector2 last = points[points.Length - 1] - points[points.Length - 2];
        float penalty = 0f;

        if (first.LengthSquared() > 0.001f)
        {
            first = Cardinalize(first, startDirection);
            if (Num.Vector2.Dot(first, startDirection) < 0.5f)
                penalty += CompactDirectionPenalty;
        }

        if (last.LengthSquared() > 0.001f)
        {
            last = Cardinalize(last, -endDirection);
            if (Num.Vector2.Dot(last, -endDirection) < 0.5f)
                penalty += CompactDirectionPenalty;
        }

        return penalty;
    }

    private static Num.Vector2 EscapeOutsideRoom(
        Num.Vector2 mouth,
        Num.Vector2 direction,
        int roomIndex,
        List<Obstacle> obstacles,
        float terminalExtraDepth)
    {
        const float clearance = 5f;

        float ownRequiredDistance = PortNeck;
        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (obstacle.RoomIndex != roomIndex)
                continue;

            if (direction.X < -0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        mouth.X - obstacle.Min.X + clearance);
            }
            else if (direction.X > 0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        obstacle.Max.X - mouth.X + clearance);
            }
            else if (direction.Y < -0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        mouth.Y - obstacle.Min.Y + clearance);
            }
            else
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        obstacle.Max.Y - mouth.Y + clearance);
            }

            break;
        }

        float distance =
            Math.Max(
                ownRequiredDistance,
                PortNeck +
                Math.Max(
                    0f,
                    terminalExtraDepth));

        // Terminal fan-out is visual/readability infrastructure, not permission to cut through the
        // next room. Cap the outward neck at the nearest foreign routing obstacle when necessary.
        // The later orthogonal router can still fan around that obstacle from the safe point.
        for (int i = 0; i < obstacles.Count; i++)
        {
            Obstacle obstacle = obstacles[i];
            if (obstacle.RoomIndex == roomIndex)
                continue;

            if (!TryForwardObstacleDistance(
                    mouth,
                    direction,
                    obstacle,
                    clearance,
                    out float safeDistance))
                continue;

            if (safeDistance < ownRequiredDistance)
                continue;

            distance =
                Math.Min(
                    distance,
                    safeDistance);
        }

        return mouth + direction * distance;
    }

    private static bool TryForwardObstacleDistance(
        Num.Vector2 mouth,
        Num.Vector2 direction,
        Obstacle obstacle,
        float clearance,
        out float safeDistance)
    {
        safeDistance = float.MaxValue;

        if (direction.X > 0.5f)
        {
            if (mouth.Y < obstacle.Min.Y - clearance ||
                mouth.Y > obstacle.Max.Y + clearance ||
                obstacle.Min.X <= mouth.X)
                return false;

            safeDistance =
                obstacle.Min.X -
                mouth.X -
                clearance;
            return true;
        }

        if (direction.X < -0.5f)
        {
            if (mouth.Y < obstacle.Min.Y - clearance ||
                mouth.Y > obstacle.Max.Y + clearance ||
                obstacle.Max.X >= mouth.X)
                return false;

            safeDistance =
                mouth.X -
                obstacle.Max.X -
                clearance;
            return true;
        }

        if (direction.Y > 0.5f)
        {
            if (mouth.X < obstacle.Min.X - clearance ||
                mouth.X > obstacle.Max.X + clearance ||
                obstacle.Min.Y <= mouth.Y)
                return false;

            safeDistance =
                obstacle.Min.Y -
                mouth.Y -
                clearance;
            return true;
        }

        if (mouth.X < obstacle.Min.X - clearance ||
            mouth.X > obstacle.Max.X + clearance ||
            obstacle.Max.Y >= mouth.Y)
            return false;

        safeDistance =
            mouth.Y -
            obstacle.Max.Y -
            clearance;
        return true;
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
        Dictionary<long, Occupancy> occupancy,
        out Num.Vector2[] points)
    {
        points = null;
        List<Num.Vector2[]> candidates =
            new(3);

        if (Math.Abs(start.X - end.X) < 0.5f ||
            Math.Abs(start.Y - end.Y) < 0.5f)
        {
            if (!SegmentBlocked(
                    start,
                    end,
                    startRoom,
                    endRoom,
                    obstacles))
            {
                candidates.Add(
                    new[]
                    {
                        start,
                        end
                    });
            }
        }

        Num.Vector2 hv =
            new(
                end.X,
                start.Y);
        Num.Vector2 vh =
            new(
                start.X,
                end.Y);

        bool hvClear =
            !SegmentBlocked(
                start,
                hv,
                startRoom,
                endRoom,
                obstacles) &&
            !SegmentBlocked(
                hv,
                end,
                startRoom,
                endRoom,
                obstacles);

        bool vhClear =
            !SegmentBlocked(
                start,
                vh,
                startRoom,
                endRoom,
                obstacles) &&
            !SegmentBlocked(
                vh,
                end,
                startRoom,
                endRoom,
                obstacles);

        if (hvClear)
        {
            candidates.Add(
                new[]
                {
                    start,
                    hv,
                    end
                });
        }

        if (vhClear)
        {
            candidates.Add(
                new[]
                {
                    start,
                    vh,
                    end
                });
        }

        float bestScore =
            float.MaxValue;
        Num.Vector2[] best =
            null;

        for (int i = 0; i < candidates.Count; i++)
        {
            Num.Vector2[] candidate =
                Simplify(
                    candidates[i]);
            if (candidate == null ||
                candidate.Length < 2)
                continue;

            float congestion =
                RouteCongestionPenalty(
                    candidate,
                    occupancy);

            // A direct L/straight path is only preferred while the corridor still has useful visual
            // capacity. Once crowded, hand control to the search router so it can choose a slightly
            // longer independent corridor rather than stacking another centreline.
            if (congestion >
                DirectRouteCongestionLimit)
                continue;

            float score =
                PathLength(candidate) +
                Math.Max(
                    0,
                    candidate.Length - 2) *
                BendPenalty +
                congestion;

            if (score >= bestScore)
                continue;

            bestScore =
                score;
            best =
                candidate;
        }

        if (best == null)
            return false;

        points =
            best;
        return true;
    }

    private static Num.Vector2[] SearchOrthogonal(
        Num.Vector2 start,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        Route previous,
        bool preferAlternativeCorridor)
    {
        float searchPadding =
            preferAlternativeCorridor
                ? CongestionRerouteSearchPadding
                : SearchPadding;

        Num.Vector2 min =
            Num.Vector2.Min(start, end) -
            new Num.Vector2(
                searchPadding,
                searchPadding);
        Num.Vector2 max =
            Num.Vector2.Max(start, end) +
            new Num.Vector2(
                searchPadding,
                searchPadding);

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
                    bool turning =
                        current.Direction < 4 &&
                        current.Direction != direction;

                    step +=
                        OccupancyPenalty(
                            occupied,
                            direction,
                            turning);
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
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        bool preferAlternativeCorridor)
    {
        Num.Vector2 min = Num.Vector2.Min(startEscape, endEscape);
        Num.Vector2 max = Num.Vector2.Max(startEscape, endEscape);
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (!obstacles[i].IntersectsBounds(min, max, 80f))
                continue;

            min = Num.Vector2.Min(min, obstacles[i].Min);
            max = Num.Vector2.Max(max, obstacles[i].Max);
        }

        // The fallback is still part of the routing policy, not an emergency "draw anything" path.
        // Give it several progressively wider outside corridors and score them against occupancy.
        // This matters most after a compressed bundle asks for an alternate corridor: the previous
        // implementation ignored congestion here, so several failed searches could all collapse
        // onto the same outer edge again.
        float[] gutters =
            preferAlternativeCorridor
                ? new[] { 42f, 72f, 108f }
                : new[] { 34f, 58f };

        float bestCost = float.MaxValue;
        Num.Vector2[] best = null;

        for (int g = 0; g < gutters.Length; g++)
        {
            float gutter = gutters[g];
            float left = min.X - gutter;
            float right = max.X + gutter;
            float top = min.Y - gutter;
            float bottom = max.Y + gutter;

            Num.Vector2[][] candidates =
            {
                new[]
                {
                    start,
                    startBaseEscape,
                    startEscape,
                    new Num.Vector2(left, startEscape.Y),
                    new Num.Vector2(left, endEscape.Y),
                    endEscape,
                    endBaseEscape,
                    end
                },
                new[]
                {
                    start,
                    startBaseEscape,
                    startEscape,
                    new Num.Vector2(right, startEscape.Y),
                    new Num.Vector2(right, endEscape.Y),
                    endEscape,
                    endBaseEscape,
                    end
                },
                new[]
                {
                    start,
                    startBaseEscape,
                    startEscape,
                    new Num.Vector2(startEscape.X, top),
                    new Num.Vector2(endEscape.X, top),
                    endEscape,
                    endBaseEscape,
                    end
                },
                new[]
                {
                    start,
                    startBaseEscape,
                    startEscape,
                    new Num.Vector2(startEscape.X, bottom),
                    new Num.Vector2(endEscape.X, bottom),
                    endEscape,
                    endBaseEscape,
                    end
                }
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Num.Vector2[] candidate =
                    SimplifyRoute(candidates[i]);

                if (!RouteClear(
                        candidate,
                        startRoom,
                        endRoom,
                        obstacles))
                    continue;

                float congestion =
                    RouteCongestionPenalty(
                        candidate,
                        occupancy);

                // Wider gutters cost a little so routes do not drift outward for no reason, but
                // congestion dominates once an existing corridor becomes unreadable.
                float widthCost =
                    g * 18f;
                float cost =
                    PathLength(candidate) +
                    congestion * 1.35f +
                    widthCost;

                if (cost >= bestCost)
                    continue;

                bestCost = cost;
                best = candidate;
            }
        }

        if (best != null)
            return best;

        // Last-resort deterministic path. It may intersect a room only if no valid fallback exists,
        // matching the previous failure semantics while keeping the choice stable.
        float emergencyLeft =
            min.X -
            gutters[gutters.Length - 1];

        return SimplifyRoute(
            new[]
            {
                start,
                startBaseEscape,
                startEscape,
                new Num.Vector2(
                    emergencyLeft,
                    startEscape.Y),
                new Num.Vector2(
                    emergencyLeft,
                    endEscape.Y),
                endEscape,
                endBaseEscape,
                end
            });
    }

    private static float RouteCongestionPenalty(
        Num.Vector2[] points,
        Dictionary<long, Occupancy> occupancy)
    {
        if (points == null ||
            points.Length < 2 ||
            occupancy == null ||
            occupancy.Count == 0)
            return 0f;

        float penalty =
            0f;
        HashSet<long> sampled =
            new();

        int firstSegment =
            points.Length >= 4
                ? 1
                : 0;
        int lastSegment =
            points.Length >= 4
                ? points.Length - 3
                : points.Length - 2;

        for (int i = firstSegment;
             i <= lastSegment;
             i++)
        {
            Num.Vector2 a =
                points[i];
            Num.Vector2 b =
                points[i + 1];
            Num.Vector2 delta =
                b - a;
            float length =
                delta.Length();

            if (length < 1f)
                continue;

            int direction =
                Math.Abs(delta.X) >=
                Math.Abs(delta.Y)
                    ? (delta.X >= 0f ? 0 : 2)
                    : (delta.Y >= 0f ? 1 : 3);

            int steps =
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        length / 18f));

            for (int s = 0; s <= steps; s++)
            {
                Num.Vector2 point =
                    Num.Vector2.Lerp(
                        a,
                        b,
                        s / (float)steps);
                long key =
                    GridKey(
                        (int)Math.Round(
                            point.X / 18f),
                        (int)Math.Round(
                            point.Y / 18f));

                if (!sampled.Add(key) ||
                    !occupancy.TryGetValue(
                        key,
                        out Occupancy occupied))
                    continue;

                penalty +=
                    OccupancyPenalty(
                        occupied,
                        direction);
            }
        }

        // Segment sampling above sees a bend hotspot as ordinary pass-through congestion. If this
        // candidate actually turns at the same occupied vertex, promote that cell to the stronger
        // junction penalty so compact/L-shaped fast paths obey the same anti-solder-joint rule as
        // the full A* search.
        int firstVertex =
            firstSegment + 1;
        int lastVertex =
            lastSegment;

        for (int i = firstVertex;
             i <= lastVertex &&
             i > 0 &&
             i + 1 < points.Length;
             i++)
        {
            Num.Vector2 before =
                points[i] -
                points[i - 1];
            Num.Vector2 after =
                points[i + 1] -
                points[i];

            if (before.LengthSquared() < 0.01f ||
                after.LengthSquared() < 0.01f)
                continue;

            bool beforeHorizontal =
                Math.Abs(before.X) >=
                Math.Abs(before.Y);
            bool afterHorizontal =
                Math.Abs(after.X) >=
                Math.Abs(after.Y);

            if (beforeHorizontal ==
                afterHorizontal)
                continue;

            long key =
                GridKey(
                    (int)Math.Round(
                        points[i].X / 18f),
                    (int)Math.Round(
                        points[i].Y / 18f));

            if (!occupancy.TryGetValue(
                    key,
                    out Occupancy occupied) ||
                occupied.BendCount == 0)
                continue;

            penalty +=
                Math.Max(
                    0f,
                    JunctionHotspotTurnPenalty -
                    JunctionHotspotPassPenalty) *
                occupied.BendCount;
        }

        return penalty;
    }

    private static float OccupancyPenalty(
        Occupancy occupied,
        int direction,
        bool turning = false)
    {
        int occupancyCount =
            Math.Max(
                1,
                (int)occupied.Count);
        byte perpendicular =
            (byte)(
                occupied.DirectionMask &
                PerpendicularMask(direction));

        float penalty;

        if (perpendicular != 0)
        {
            penalty =
                CrossingPenalty *
                occupancyCount;
        }
        else
        {
            int preferred =
                Math.Min(
                    occupancyCount,
                    PreferredParallelCapacity);
            int overflow =
                Math.Max(
                    0,
                    occupancyCount -
                    PreferredParallelCapacity);

            penalty =
                ParallelCongestionPenalty *
                preferred;

            if (overflow > 0)
            {
                penalty +=
                    ParallelOverflowPenalty *
                    overflow *
                    overflow;
            }
        }

        if (occupied.BendCount > 0)
        {
            penalty +=
                (turning
                    ? JunctionHotspotTurnPenalty
                    : JunctionHotspotPassPenalty) *
                occupied.BendCount;
        }

        return penalty;
    }

    private static void RegisterOccupancy(Route route, Dictionary<long, Occupancy> occupancy)
    {
        RegisterOccupancy(
            route?.Points,
            occupancy);
    }

    private static void RegisterOccupancy(
        Num.Vector2[] points,
        Dictionary<long, Occupancy> occupancy,
        int weight = 1)
    {
        if (points == null ||
            points.Length < 2 ||
            occupancy == null)
            return;

        weight =
            Math.Max(
                1,
                weight);

        int firstSegment =
            points.Length >= 4
                ? 1
                : 0;
        int lastSegment =
            points.Length >= 4
                ? points.Length - 3
                : points.Length - 2;

        for (int i = firstSegment;
             i <= lastSegment;
             i++)
        {
            Num.Vector2 a = points[i];
            Num.Vector2 b = points[i + 1];
            Num.Vector2 delta = b - a;
            float length = delta.Length();
            if (length < 1f)
                continue;

            int direction =
                Math.Abs(delta.X) >=
                Math.Abs(delta.Y)
                    ? (delta.X >= 0f ? 0 : 2)
                    : (delta.Y >= 0f ? 1 : 3);

            int steps =
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        length / 18f));

            for (int s = 0; s <= steps; s++)
            {
                Num.Vector2 p =
                    Num.Vector2.Lerp(
                        a,
                        b,
                        s / (float)steps);
                int gx =
                    (int)Math.Round(
                        p.X / 18f);
                int gy =
                    (int)Math.Round(
                        p.Y / 18f);
                long key =
                    GridKey(
                        gx,
                        gy);
                byte mask =
                    DirectionBit(
                        direction);

                if (occupancy.TryGetValue(
                        key,
                        out Occupancy current))
                {
                    occupancy[key] =
                        new Occupancy(
                            (byte)(
                                current.DirectionMask |
                                mask),
                            (byte)Math.Min(
                                255,
                                current.Count +
                                weight),
                            current.BendCount);
                }
                else
                {
                    occupancy[key] =
                        new Occupancy(
                            mask,
                            (byte)Math.Min(
                                255,
                                weight),
                            0);
                }
            }
        }

        // Register corridor bends separately. A dozen unrelated routes turning on the same grid
        // point creates the visual equivalent of an electrical solder joint even when none of
        // those routes are topologically connected. Future routes should prefer a nearby bend
        // location instead of piling another corner onto that hotspot.
        int firstVertex =
            firstSegment + 1;
        int lastVertex =
            lastSegment;

        for (int i = firstVertex;
             i <= lastVertex &&
             i > 0 &&
             i + 1 < points.Length;
             i++)
        {
            Num.Vector2 before =
                points[i] -
                points[i - 1];
            Num.Vector2 after =
                points[i + 1] -
                points[i];

            if (before.LengthSquared() < 0.01f ||
                after.LengthSquared() < 0.01f)
                continue;

            bool beforeHorizontal =
                Math.Abs(before.X) >=
                Math.Abs(before.Y);
            bool afterHorizontal =
                Math.Abs(after.X) >=
                Math.Abs(after.Y);

            if (beforeHorizontal ==
                afterHorizontal)
                continue;

            int gx =
                (int)Math.Round(
                    points[i].X / 18f);
            int gy =
                (int)Math.Round(
                    points[i].Y / 18f);
            long key =
                GridKey(
                    gx,
                    gy);

            if (occupancy.TryGetValue(
                    key,
                    out Occupancy current))
            {
                occupancy[key] =
                    new Occupancy(
                        current.DirectionMask,
                        current.Count,
                        (byte)Math.Min(
                            255,
                            current.BendCount +
                            weight));
            }
            else
            {
                occupancy[key] =
                    new Occupancy(
                        0,
                        0,
                        (byte)Math.Min(
                            255,
                            weight));
            }
        }
    }

    internal static bool IsDerivedRouteClear(
        Num.Vector2[] points,
        int startRoom,
        int endRoom,
        IReadOnlyList<Obstacle> obstacles) =>
        RouteClear(points, startRoom, endRoom, obstacles);

    private static bool RouteClear(
        Num.Vector2[] points,
        int startRoom,
        int endRoom,
        IReadOnlyList<Obstacle> obstacles)
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
        IReadOnlyList<Obstacle> obstacles)
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

    private static Num.Vector2[] SimplifyRoute(
        Num.Vector2[] source)
    {
        if (source == null ||
            source.Length <= 3)
            return source == null
                ? Array.Empty<Num.Vector2>()
                : (Num.Vector2[])source.Clone();

        // Route point 1 and point N-2 are semantic terminal anchors: they mark the end of the
        // socket-owned stub and the beginning of corridor-owned geometry. Generic collinear
        // simplification used to erase those anchors on straight/compact links, which made the lane
        // allocator and crossing resolver treat the whole connection as one inseparable centreline.
        Num.Vector2[] middle =
            new Num.Vector2[
                source.Length - 2];
        Array.Copy(
            source,
            1,
            middle,
            0,
            middle.Length);

        Num.Vector2[] simplifiedMiddle =
            Simplify(middle);

        List<Num.Vector2> result =
            new(
                simplifiedMiddle.Length + 2);

        result.Add(source[0]);

        for (int i = 0; i < simplifiedMiddle.Length; i++)
        {
            Num.Vector2 point =
                simplifiedMiddle[i];

            if (Num.Vector2.DistanceSquared(
                    result[result.Count - 1],
                    point) >= 0.0001f)
            {
                result.Add(point);
            }
        }

        Num.Vector2 last =
            source[source.Length - 1];
        if (Num.Vector2.DistanceSquared(
                result[result.Count - 1],
                last) >= 0.0001f)
        {
            result.Add(last);
        }

        return result.ToArray();
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
            if (generation - pair.Value.LastSeenGeneration <= CacheRetentionGenerations) continue;
            stale ??= new List<string>();
            stale.Add(pair.Key);
        }
        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
    }
}
