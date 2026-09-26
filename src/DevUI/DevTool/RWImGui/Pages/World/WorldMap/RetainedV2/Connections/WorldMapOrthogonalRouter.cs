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
    private const int RoutingPolicyVersion = 19;
    internal static int PersistentPolicyVersion => RoutingPolicyVersion;
    private const float BridgeDistance = 170f;
    private const float BridgeAlignmentTolerance = 56f;
    // Readability costs refine a geometrically short route; they must not dominate distance.
    // Crossings remain expensive and shared corridors remain cheap, but a clear straight/L route is
    // retained as an upper bound so soft congestion can never justify a screen-spanning detour.
    private const float BendPenalty = 1.60f;
    private const float BacktrackPenalty = 3.40f;
    private const float CrossingPenalty = 11.0f;
    private const float JunctionHotspotTurnPenalty = 2.8f;
    private const float JunctionHotspotPassPenalty = 0.38f;
    private const float JunctionNeighborTurnPenalty = 1.15f;
    private const float JunctionNeighborPassPenalty = 0.14f;
    private const float ParallelCongestionPenalty = 0.26f;
    private const int PreferredParallelCapacity = 8;
    private const float ParallelOverflowPenalty = 0.92f;
    private const float DirectRouteCongestionLimit = 28f;
    private const float DirectRouteDetourRatio = 1.38f;
    private const float DirectRouteDetourExtra = 72f;
    private const float CongestedRerouteDetourRatio = 1.52f;
    private const float CongestedRerouteDetourExtra = 112f;
    private const float DirectRouteDetourSlack = 24f;
    private const float CongestedRerouteDetourSlack = 32f;
    private const float SearchCostReferenceCell = 18f;
    private const float LocalDetourClearance = 12f;
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
        Num.Vector2 startDirection =
            Cardinalize(
                request.StartDirection,
                request.End -
                request.Start);
        Num.Vector2 endDirection =
            Cardinalize(
                request.EndDirection,
                request.Start -
                request.End);

        Num.Vector2 startPerp =
            new(
                -startDirection.Y,
                startDirection.X);
        Num.Vector2 endPerp =
            new(
                -endDirection.Y,
                endDirection.X);

        // Facing ports naturally produce opposite local normals. Align their lane normals before
        // applying the lane offset, otherwise lane +1 leaves one room above the centreline and
        // enters the other room below it, causing multi-links to cross each other in the middle.
        float perpAgreement =
            Num.Vector2.Dot(
                startPerp,
                endPerp);
        if (perpAgreement < -0.25f)
        {
            endPerp =
                -endPerp;
        }
        else if (Math.Abs(perpAgreement) <= 0.25f)
        {
            Num.Vector2 pairDelta =
                request.End -
                request.Start;
            Num.Vector2 stableNormal =
                Math.Abs(pairDelta.X) >=
                Math.Abs(pairDelta.Y)
                    ? new Num.Vector2(0f, 1f)
                    : new Num.Vector2(1f, 0f);

            if (Num.Vector2.Dot(
                    startPerp,
                    stableNormal) < 0f)
            {
                startPerp =
                    -startPerp;
            }

            if (Num.Vector2.Dot(
                    endPerp,
                    stableNormal) < 0f)
            {
                endPerp =
                    -endPerp;
            }
        }

        // Every connection gets a real terminal stub before any global routing decision.
        Num.Vector2 startBaseEscape =
            EscapeOutsideRoom(
                request.Start,
                startDirection,
                request.StartRoom,
                request.EndRoom,
                obstacles,
                request.StartTerminalExtraDepth);
        Num.Vector2 endBaseEscape =
            EscapeOutsideRoom(
                request.End,
                endDirection,
                request.EndRoom,
                request.StartRoom,
                obstacles,
                request.EndTerminalExtraDepth);
        Num.Vector2 startEscape =
            startBaseEscape +
            startPerp *
            request.LaneOffset;
        Num.Vector2 endEscape =
            endBaseEscape +
            endPerp *
            request.LaneOffset;

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

        // Straight / one-bend geometry is the canonical baseline and must be established before
        // Bridge or A*. Previously Bridge could win first and add two unnecessary bends.
        Num.Vector2[] directSimple =
            null;
        float directSimpleCongestion =
            float.MaxValue;

        if (TrySimpleOrthogonal(
                startEscape,
                endEscape,
                request.StartRoom,
                request.EndRoom,
                obstacles,
                occupancy,
                out Num.Vector2[] simple,
                out directSimpleCongestion))
        {
            Num.Vector2[] candidate =
                BuildFullRoute(
                    request,
                    startBaseEscape,
                    startEscape,
                    simple,
                    endEscape,
                    endBaseEscape);

            if (FullRouteClear(
                    request,
                    candidate,
                    obstacles))
            {
                directSimple =
                    candidate;

                if (directSimpleCongestion <=
                    DirectRouteCongestionLimit)
                {
                    return NewRoute(
                        request,
                        RouteKind.Orthogonal,
                        directSimple,
                        startDirection,
                        endDirection);
                }
            }
        }

        // Bridge remains a compact visual alternative for facing ports, but only after the simpler
        // straight/L answer had a chance to win and only while the bridge stays inside the same
        // bounded-detour envelope.
        Num.Vector2[] bridgeCandidate =
            null;

        if (CanUseBridge(
                request,
                startDirection,
                endDirection,
                startEscape,
                endEscape,
                obstacles))
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

            if (FullRouteClear(
                    request,
                    bridge,
                    obstacles) &&
                RouteCongestionPenalty(
                    bridge,
                    occupancy) <=
                DirectRouteCongestionLimit &&
                (directSimple == null ||
                 !PreferDirectRouteOverDetour(
                     directSimple,
                     bridge,
                     congestionReroute: false)))
            {
                bridgeCandidate =
                    bridge;

                return NewRoute(
                    request,
                    RouteKind.Bridge,
                    bridgeCandidate,
                    startDirection,
                    endDirection);
            }
        }

        // If both one-bend candidates are blocked, probe obstacle-edge two-bend corridors before
        // invoking A*. This gives the search a local geometric upper bound instead of letting soft
        // congestion invent a screen-spanning route simply because no L-path exists.
        Num.Vector2[] localDetour =
            null;
        float localDetourCongestion =
            float.MaxValue;

        if (TryLocalOrthogonalDetour(
                startEscape,
                endEscape,
                request.StartRoom,
                request.EndRoom,
                obstacles,
                occupancy,
                out Num.Vector2[] localMiddle,
                out localDetourCongestion))
        {
            Num.Vector2[] candidate =
                BuildFullRoute(
                    request,
                    startBaseEscape,
                    startEscape,
                    localMiddle,
                    endEscape,
                    endBaseEscape);

            if (FullRouteClear(
                    request,
                    candidate,
                    obstacles))
            {
                localDetour =
                    candidate;

                if (directSimple == null &&
                    localDetourCongestion <=
                    DirectRouteCongestionLimit)
                {
                    return NewRoute(
                        request,
                        RouteKind.Orthogonal,
                        localDetour,
                        startDirection,
                        endDirection);
                }
            }
        }

        Num.Vector2[] geometricBound =
            ShorterRoute(
                directSimple,
                localDetour);

        Num.Vector2[] searched =
            SearchOrthogonal(
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
            Num.Vector2[] searchedFull =
                BuildFullRoute(
                    request,
                    startBaseEscape,
                    startEscape,
                    searched,
                    endEscape,
                    endBaseEscape);

            if (FullRouteClear(
                    request,
                    searchedFull,
                    obstacles))
            {
                if (geometricBound != null &&
                    PreferDirectRouteOverDetour(
                        geometricBound,
                        searchedFull,
                        preferAlternativeCorridor))
                {
                    return NewRoute(
                        request,
                        RouteKind.Orthogonal,
                        geometricBound,
                        startDirection,
                        endDirection);
                }

                return NewRoute(
                    request,
                    RouteKind.Orthogonal,
                    searchedFull,
                    startDirection,
                    endDirection);
            }
        }

        // A geometrically valid local route is always safer than escalating to an outer fallback.
        if (geometricBound != null)
        {
            return NewRoute(
                request,
                RouteKind.Orthogonal,
                geometricBound,
                startDirection,
                endDirection);
        }

        Num.Vector2[] fallback =
            BuildOuterFallback(
                request,
                startBaseEscape,
                startEscape,
                endEscape,
                endBaseEscape,
                obstacles,
                occupancy,
                preferAlternativeCorridor);

        return NewRoute(
            request,
            RouteKind.Fallback,
            fallback,
            startDirection,
            endDirection);
    }

    private static Route NewRoute(
        Request request,
        RouteKind kind,
        Num.Vector2[] points,
        Num.Vector2 startDirection,
        Num.Vector2 endDirection)
    {
        Num.Vector2[] normalized =
            CollapseImmediateBacktracks(
                points ?? Array.Empty<Num.Vector2>());

        return new Route
        {
            Id = request.Id ?? string.Empty,
            Kind = kind,
            Points = normalized,
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

        if (!FullRouteClear(
                request,
                cached.Route.Points,
                obstacles))
            return false;
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
                !FullRouteClear(
                    request,
                    candidate,
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
        int counterpartRoom,
        List<Obstacle> obstacles,
        float terminalExtraDepth)
    {
        const float clearance = 5f;

        float ownRequiredDistance =
            PortNeck;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];

            if (obstacle.RoomIndex !=
                roomIndex)
                continue;

            if (direction.X < -0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        mouth.X -
                        obstacle.Min.X +
                        clearance);
            }
            else if (direction.X > 0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        obstacle.Max.X -
                        mouth.X +
                        clearance);
            }
            else if (direction.Y < -0.5f)
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        mouth.Y -
                        obstacle.Min.Y +
                        clearance);
            }
            else
            {
                ownRequiredDistance =
                    Math.Max(
                        ownRequiredDistance,
                        obstacle.Max.Y -
                        mouth.Y +
                        clearance);
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

        // Inflated routing margins are visual clearance, not hard room bodies. When another room
        // sits closer than the normal terminal neck, shorten the stub instead of tunnelling through
        // the room. Counterpart terminals meet roughly in the physical gap; unrelated rooms reserve
        // a small body clearance before the route turns.
        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];

            if (obstacle.RoomIndex ==
                roomIndex)
                continue;

            if (!TryForwardRawRoomDistance(
                    mouth,
                    direction,
                    obstacle,
                    out float rawDistance))
                continue;

            float safeDistance =
                obstacle.RoomIndex ==
                counterpartRoom
                    ? rawDistance *
                      0.5f
                    : rawDistance -
                      clearance;

            if (safeDistance <= 0f)
                continue;

            distance =
                Math.Min(
                    distance,
                    Math.Max(
                        1f,
                        safeDistance));
        }

        return mouth +
               direction *
               distance;
    }

    private static bool TryForwardRawRoomDistance(
        Num.Vector2 mouth,
        Num.Vector2 direction,
        Obstacle obstacle,
        out float distance)
    {
        distance =
            float.MaxValue;

        Num.Vector2 rawMin =
            obstacle.Min +
            new Num.Vector2(
                ObstacleMargin,
                ObstacleMargin);
        Num.Vector2 rawMax =
            obstacle.Max -
            new Num.Vector2(
                ObstacleMargin,
                ObstacleMargin);

        if (direction.X > 0.5f)
        {
            if (mouth.Y < rawMin.Y ||
                mouth.Y > rawMax.Y ||
                rawMin.X <= mouth.X)
                return false;

            distance =
                rawMin.X -
                mouth.X;
            return true;
        }

        if (direction.X < -0.5f)
        {
            if (mouth.Y < rawMin.Y ||
                mouth.Y > rawMax.Y ||
                rawMax.X >= mouth.X)
                return false;

            distance =
                mouth.X -
                rawMax.X;
            return true;
        }

        if (direction.Y > 0.5f)
        {
            if (mouth.X < rawMin.X ||
                mouth.X > rawMax.X ||
                rawMin.Y <= mouth.Y)
                return false;

            distance =
                rawMin.Y -
                mouth.Y;
            return true;
        }

        if (mouth.X < rawMin.X ||
            mouth.X > rawMax.X ||
            rawMax.Y >= mouth.Y)
            return false;

        distance =
            mouth.Y -
            rawMax.Y;
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
        out Num.Vector2[] points,
        out float congestionScore)
    {
        points = null;
        congestionScore = float.MaxValue;
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

            // Geometry is primary here. Keep the best clear straight/L candidate even when the
            // corridor is crowded so BuildRoute can compare any A* alternative against a concrete
            // shortest-path upper bound. Congestion remains a tie-breaker, not a license for a huge
            // detour.
            float score =
                PathLength(candidate) +
                Math.Max(
                    0,
                    candidate.Length - 2) *
                BendPenalty +
                Math.Min(
                    congestion,
                    DirectRouteCongestionLimit) *
                0.20f;

            if (score >= bestScore)
                continue;

            bestScore =
                score;
            best =
                candidate;
            congestionScore =
                congestion;
        }

        if (best == null)
            return false;

        points =
            best;
        return true;
    }

    private static Num.Vector2[] BuildFullRoute(
        Request request,
        Num.Vector2 startBaseEscape,
        Num.Vector2 startEscape,
        Num.Vector2[] middle,
        Num.Vector2 endEscape,
        Num.Vector2 endBaseEscape)
    {
        List<Num.Vector2> points =
            new(
                (middle?.Length ?? 0) +
                6)
            {
                request.Start,
                startBaseEscape
            };

        if (Num.Vector2.DistanceSquared(
                startBaseEscape,
                startEscape) > 0.25f)
        {
            points.Add(
                startEscape);
        }

        if (middle != null)
        {
            for (int i = 1;
                 i < middle.Length - 1;
                 i++)
            {
                points.Add(
                    middle[i]);
            }
        }

        if (Num.Vector2.DistanceSquared(
                endEscape,
                endBaseEscape) > 0.25f)
        {
            points.Add(
                endEscape);
        }

        points.Add(
            endBaseEscape);
        points.Add(
            request.End);

        return SimplifyRoute(
            points.ToArray());
    }

    private static bool PreferDirectRouteOverDetour(
        Num.Vector2[] direct,
        Num.Vector2[] detour,
        bool congestionReroute)
    {
        if (direct == null ||
            direct.Length < 2)
            return false;

        if (detour == null ||
            detour.Length < 2)
            return true;

        float directLength =
            PathLength(
                direct);
        float detourLength =
            PathLength(
                detour);

        float ratio =
            congestionReroute
                ? CongestedRerouteDetourRatio
                : DirectRouteDetourRatio;
        float extra =
            congestionReroute
                ? CongestedRerouteDetourExtra
                : DirectRouteDetourExtra;

        float slack =
            congestionReroute
                ? CongestedRerouteDetourSlack
                : DirectRouteDetourSlack;
        float allowedExtra =
            Math.Min(
                directLength *
                Math.Max(
                    0f,
                    ratio - 1f),
                extra) +
            slack;
        float maximumReasonableDetour =
            directLength +
            allowedExtra;

        return detourLength >
               maximumReasonableDetour;
    }

    private static Num.Vector2[] ShorterRoute(
        Num.Vector2[] a,
        Num.Vector2[] b)
    {
        if (a == null ||
            a.Length < 2)
        {
            return b;
        }

        if (b == null ||
            b.Length < 2)
        {
            return a;
        }

        return PathLength(a) <=
               PathLength(b)
            ? a
            : b;
    }

    private static bool TryLocalOrthogonalDetour(
        Num.Vector2 start,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        out Num.Vector2[] points,
        out float congestionScore)
    {
        points =
            null;
        congestionScore =
            float.MaxValue;

        if (obstacles == null ||
            obstacles.Count == 0)
            return false;

        Num.Vector2 corridorMin =
            Num.Vector2.Min(
                start,
                end) -
            new Num.Vector2(
                48f,
                48f);
        Num.Vector2 corridorMax =
            Num.Vector2.Max(
                start,
                end) +
            new Num.Vector2(
                48f,
                48f);

        List<float> xs =
            new();
        List<float> ys =
            new();

        float envelopeLeft =
            float.MaxValue;
        float envelopeRight =
            float.MinValue;
        float envelopeTop =
            float.MaxValue;
        float envelopeBottom =
            float.MinValue;
        bool hasRelevantObstacle =
            false;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];

            if (!obstacle.IntersectsBounds(
                    corridorMin,
                    corridorMax,
                    0f))
                continue;

            hasRelevantObstacle =
                true;

            float left =
                obstacle.Min.X -
                LocalDetourClearance;
            float right =
                obstacle.Max.X +
                LocalDetourClearance;
            float top =
                obstacle.Min.Y -
                LocalDetourClearance;
            float bottom =
                obstacle.Max.Y +
                LocalDetourClearance;

            xs.Add(left);
            xs.Add(right);
            ys.Add(top);
            ys.Add(bottom);

            envelopeLeft =
                Math.Min(
                    envelopeLeft,
                    left);
            envelopeRight =
                Math.Max(
                    envelopeRight,
                    right);
            envelopeTop =
                Math.Min(
                    envelopeTop,
                    top);
            envelopeBottom =
                Math.Max(
                    envelopeBottom,
                    bottom);
        }

        if (!hasRelevantObstacle)
            return false;

        xs.Add(envelopeLeft);
        xs.Add(envelopeRight);
        ys.Add(envelopeTop);
        ys.Add(envelopeBottom);

        float bestScore =
            float.MaxValue;

        for (int i = 0;
             i < xs.Count;
             i++)
        {
            Num.Vector2[] candidate =
                Simplify(
                    new[]
                    {
                        start,
                        new Num.Vector2(
                            xs[i],
                            start.Y),
                        new Num.Vector2(
                            xs[i],
                            end.Y),
                        end
                    });

            ScoreLocalDetour(
                candidate,
                startRoom,
                endRoom,
                obstacles,
                occupancy,
                ref bestScore,
                ref points,
                ref congestionScore);
        }

        for (int i = 0;
             i < ys.Count;
             i++)
        {
            Num.Vector2[] candidate =
                Simplify(
                    new[]
                    {
                        start,
                        new Num.Vector2(
                            start.X,
                            ys[i]),
                        new Num.Vector2(
                            end.X,
                            ys[i]),
                        end
                    });

            ScoreLocalDetour(
                candidate,
                startRoom,
                endRoom,
                obstacles,
                occupancy,
                ref bestScore,
                ref points,
                ref congestionScore);
        }

        return points != null &&
               points.Length >= 2;
    }

    private static void ScoreLocalDetour(
        Num.Vector2[] candidate,
        int startRoom,
        int endRoom,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        ref float bestScore,
        ref Num.Vector2[] best,
        ref float bestCongestion)
    {
        if (candidate == null ||
            candidate.Length < 2 ||
            !CorridorRouteClear(
                candidate,
                startRoom,
                endRoom,
                obstacles))
        {
            return;
        }

        float congestion =
            RouteCongestionPenalty(
                candidate,
                occupancy);
        float score =
            PathLength(
                candidate) +
            Math.Max(
                0,
                candidate.Length - 2) *
            BendPenalty *
            SearchCostReferenceCell +
            Math.Min(
                congestion,
                DirectRouteCongestionLimit) *
            0.20f *
            SearchCostReferenceCell;

        if (score >= bestScore)
            return;

        bestScore =
            score;
        best =
            candidate;
        bestCongestion =
            congestion;
    }

    private static bool FullRouteClear(
        Request request,
        Num.Vector2[] points,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (request == null ||
            points == null ||
            points.Length < 2)
            return false;

        int lastSegment =
            points.Length - 2;

        if (lastSegment == 0)
        {
            return
                !TerminalSegmentBlocked(
                    points[0],
                    points[1],
                    request.StartRoom,
                    request.EndRoom,
                    request.EndRoomMin,
                    request.EndRoomMax,
                    obstacles) &&
                !TerminalSegmentBlocked(
                    points[0],
                    points[1],
                    request.EndRoom,
                    request.StartRoom,
                    request.StartRoomMin,
                    request.StartRoomMax,
                    obstacles);
        }

        for (int i = 0;
             i <= lastSegment;
             i++)
        {
            Num.Vector2 a =
                points[i];
            Num.Vector2 b =
                points[i + 1];

            if (i == 0)
            {
                if (TerminalSegmentBlocked(
                        a,
                        b,
                        request.StartRoom,
                        request.EndRoom,
                        request.EndRoomMin,
                        request.EndRoomMax,
                        obstacles))
                    return false;

                continue;
            }

            if (i == lastSegment)
            {
                if (TerminalSegmentBlocked(
                        a,
                        b,
                        request.EndRoom,
                        request.StartRoom,
                        request.StartRoomMin,
                        request.StartRoomMax,
                        obstacles))
                    return false;

                continue;
            }

            if (SegmentBlocked(
                    a,
                    b,
                    request.StartRoom,
                    request.EndRoom,
                    obstacles))
                return false;
        }

        return true;
    }

    private static bool TerminalSegmentBlocked(
        Num.Vector2 a,
        Num.Vector2 b,
        int ownRoom,
        int counterpartRoom,
        Num.Vector2 counterpartRawMin,
        Num.Vector2 counterpartRawMax,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (obstacles == null)
            return false;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];

            if (obstacle.RoomIndex ==
                ownRoom)
                continue;

            if (obstacle.RoomIndex ==
                counterpartRoom)
            {
                // Adjacent rooms can have overlapping inflated clearance margins. Permit a terminal
                // stub to enter the counterpart margin, but never the actual room body.
                if (SegmentIntersectsRect(
                        a,
                        b,
                        counterpartRawMin,
                        counterpartRawMax))
                    return true;

                continue;
            }

            // Routing obstacles include a generous visual margin. Terminal stubs may cross that
            // margin when rooms sit close together, but they must never cross the actual foreign
            // room body. Deflate back to the source room bounds for this terminal-only check.
            Num.Vector2 rawMin =
                obstacle.Min +
                new Num.Vector2(
                    ObstacleMargin,
                    ObstacleMargin);
            Num.Vector2 rawMax =
                obstacle.Max -
                new Num.Vector2(
                    ObstacleMargin,
                    ObstacleMargin);

            if (SegmentIntersectsRect(
                    a,
                    b,
                    rawMin,
                    rawMax))
                return true;
        }

        return false;
    }

    private static bool CorridorRouteClear(
        Num.Vector2[] points,
        int startRoom,
        int endRoom,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (points == null ||
            points.Length < 2)
            return false;

        for (int i = 0;
             i < points.Length - 1;
             i++)
        {
            if (SegmentBlocked(
                    points[i],
                    points[i + 1],
                    startRoom,
                    endRoom,
                    obstacles))
                return false;
        }

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

        // Include every obstacle that intersects the original search envelope, then expand once
        // around that fixed set. This is order-independent without recursively chaining across the
        // whole map and accidentally coarsening the A* grid.
        Num.Vector2 selectionMin =
            min;
        Num.Vector2 selectionMax =
            max;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];
            if (!obstacle.IntersectsBounds(
                    selectionMin,
                    selectionMax,
                    40f))
                continue;

            min =
                Num.Vector2.Min(
                    min,
                    obstacle.Min -
                    new Num.Vector2(
                        36f,
                        36f));
            max =
                Num.Vector2.Max(
                    max,
                    obstacle.Max +
                    new Num.Vector2(
                        36f,
                        36f));
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

        bool[] blocked =
            BuildBlockedGrid(
                min,
                cell,
                width,
                height,
                obstacles,
                startRoom,
                endRoom);
        blocked[sy * width + sx] = false;
        blocked[ey * width + ex] = false;

        HashSet<long> stableCells =
            preferAlternativeCorridor
                ? new HashSet<long>()
                : BuildStableCells(
                    previous,
                    min,
                    cell,
                    width,
                    height);
        List<SearchNode> nodes = new(width * height * 2);
        Dictionary<int, int> bestByState = new();
        MinHeap open = new(nodes);

        SearchNode seed = new()
        {
            X = sx,
            Y = sy,
            Direction = 4,
            G = 0f,
            F = Manhattan(
                    sx,
                    sy,
                    ex,
                    ey) *
                cell
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
            if (current.Closed)
                continue;

            int currentStateKey =
                StateKey(
                    current.X,
                    current.Y,
                    current.Direction,
                    width,
                    height);
            if (bestByState.TryGetValue(
                    currentStateKey,
                    out int bestCurrentIndex) &&
                bestCurrentIndex != currentIndex)
            {
                // A better copy of this same state was queued after this node. Do not expand the
                // stale higher-cost copy; doing so used to waste a large part of the bounded grid
                // budget on dense maps.
                continue;
            }

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

                float step =
                    cell;
                if (current.Direction < 4)
                {
                    if (current.Direction != direction)
                    {
                        step +=
                            BendPenalty *
                            SearchCostReferenceCell;
                    }

                    if (((current.Direction + 2) & 3) == direction)
                    {
                        step +=
                            BacktrackPenalty *
                            SearchCostReferenceCell;
                    }
                }

                if (IsNearBlockedCell(
                        nx,
                        ny,
                        blocked,
                        width,
                        height))
                {
                    step +=
                        ProximityPenalty *
                        SearchCostReferenceCell;
                }

                Num.Vector2 worldNeighbor = new(min.X + nx * cell, min.Y + ny * cell);
                int occupancyX =
                    (int)Math.Round(
                        worldNeighbor.X / 18f);
                int occupancyY =
                    (int)Math.Round(
                        worldNeighbor.Y / 18f);
                long occupancyKey =
                    GridKey(
                        occupancyX,
                        occupancyY);
                bool turning =
                    current.Direction < 4 &&
                    current.Direction != direction;

                if (occupancy.TryGetValue(
                        occupancyKey,
                        out Occupancy occupied))
                {
                    step +=
                        OccupancyPenalty(
                            occupied,
                            direction,
                            turning) *
                        SearchCostReferenceCell;
                }

                step +=
                    NearbyBendPenalty(
                        occupancy,
                        occupancyX,
                        occupancyY,
                        turning) *
                    SearchCostReferenceCell;

                if (stableCells.Contains(
                        GridKey(
                            nx,
                            ny)))
                {
                    step =
                        Math.Max(
                            cell * 0.20f,
                            step -
                            StabilityBonus *
                            SearchCostReferenceCell);
                }

                float g =
                    current.G +
                    step;
                int stateKey = StateKey(nx, ny, direction, width, height);
                if (bestByState.TryGetValue(stateKey, out int existingIndex) && nodes[existingIndex].G <= g)
                    continue;

                SearchNode next = new()
                {
                    X = nx,
                    Y = ny,
                    Direction = direction,
                    G = g,
                    F =
                        g +
                        Manhattan(
                            nx,
                            ny,
                            ex,
                            ey) *
                        cell,
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

        return BuildSnappedSearchRoute(
            start,
            end,
            startRoom,
            endRoom,
            reversed,
            obstacles);
    }

    private static Num.Vector2[] BuildSnappedSearchRoute(
        Num.Vector2 start,
        Num.Vector2 end,
        int startRoom,
        int endRoom,
        List<Num.Vector2> reversed,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (reversed == null ||
            reversed.Count == 0)
            return Array.Empty<Num.Vector2>();

        Num.Vector2 first =
            reversed[0];
        Num.Vector2 last =
            reversed[reversed.Count - 1];

        Num.Vector2[] startCorners =
        {
            new Num.Vector2(
                first.X,
                start.Y),
            new Num.Vector2(
                start.X,
                first.Y)
        };
        Num.Vector2[] endCorners =
        {
            new Num.Vector2(
                end.X,
                last.Y),
            new Num.Vector2(
                last.X,
                end.Y)
        };

        Num.Vector2[] best =
            null;
        float bestLength =
            float.MaxValue;

        for (int startMode = 0;
             startMode < 2;
             startMode++)
        {
            for (int endMode = 0;
                 endMode < 2;
                 endMode++)
            {
                List<Num.Vector2> route =
                    new(
                        reversed.Count +
                        6)
                    {
                        start
                    };

                if (Math.Abs(
                        start.X -
                        first.X) > 0.5f &&
                    Math.Abs(
                        start.Y -
                        first.Y) > 0.5f)
                {
                    route.Add(
                        startCorners[startMode]);
                }

                route.AddRange(
                    reversed);

                if (Math.Abs(
                        end.X -
                        last.X) > 0.5f &&
                    Math.Abs(
                        end.Y -
                        last.Y) > 0.5f)
                {
                    route.Add(
                        endCorners[endMode]);
                }

                route.Add(
                    end);

                Num.Vector2[] candidate =
                    Simplify(
                        route.ToArray());

                if (!CorridorRouteClear(
                        candidate,
                        startRoom,
                        endRoom,
                        obstacles))
                    continue;

                float length =
                    PathLength(
                        candidate);

                if (length >= bestLength)
                    continue;

                bestLength =
                    length;
                best =
                    candidate;
            }
        }

        return best ??
               Array.Empty<Num.Vector2>();
    }

    private static bool[] BuildBlockedGrid(
        Num.Vector2 origin,
        float cell,
        int width,
        int height,
        List<Obstacle> obstacles,
        int startRoom,
        int endRoom)
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
                    Obstacle obstacle =
                        obstacles[i];

                    Num.Vector2 obstacleMin =
                        obstacle.Min;
                    Num.Vector2 obstacleMax =
                        obstacle.Max;

                    if (obstacle.RoomIndex ==
                            startRoom ||
                        obstacle.RoomIndex ==
                            endRoom)
                    {
                        obstacleMin +=
                            new Num.Vector2(
                                ObstacleMargin,
                                ObstacleMargin);
                        obstacleMax -=
                            new Num.Vector2(
                                ObstacleMargin,
                                ObstacleMargin);
                    }

                    // Cover a small fraction of the cell footprint so a grid edge cannot skim
                    // through a room between two free centres, without effectively inflating every
                    // room by another half-cell and closing narrow but legitimate corridors.
                    float halfCell =
                        cell *
                        0.20f;
                    if (point.X + halfCell <= obstacleMin.X ||
                        point.X - halfCell >= obstacleMax.X ||
                        point.Y + halfCell <= obstacleMin.Y ||
                        point.Y - halfCell >= obstacleMax.Y)
                    {
                        continue;
                    }

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
        Request request,
        Num.Vector2 startBaseEscape,
        Num.Vector2 startEscape,
        Num.Vector2 endEscape,
        Num.Vector2 endBaseEscape,
        List<Obstacle> obstacles,
        Dictionary<long, Occupancy> occupancy,
        bool preferAlternativeCorridor)
    {
        Num.Vector2 start =
            request.Start;
        Num.Vector2 end =
            request.End;
        int startRoom =
            request.StartRoom;
        int endRoom =
            request.EndRoom;

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
                ? new[] { 42f, 72f, 108f, 156f, 220f }
                : new[] { 34f, 58f, 92f, 138f, 196f };

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

                if (!FullRouteClear(
                        request,
                        candidate,
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

        // Obstacles are a hard constraint. An unroutable connection is preferable to drawing a
        // false line through a room thumbnail; callers can keep the route pending/degraded until the
        // layout changes instead of inventing geometry that contradicts the map.
        return Array.Empty<Num.Vector2>();
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

            if (occupancy.TryGetValue(
                    key,
                    out Occupancy occupied) &&
                occupied.BendCount > 0)
            {
                penalty +=
                    Math.Max(
                        0f,
                        JunctionHotspotTurnPenalty -
                        JunctionHotspotPassPenalty) *
                    occupied.BendCount;
            }

            penalty +=
                NearbyBendPenalty(
                    occupancy,
                    (int)Math.Round(
                        points[i].X / 18f),
                    (int)Math.Round(
                        points[i].Y / 18f),
                    turning: true);
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

    private static float NearbyBendPenalty(
        Dictionary<long, Occupancy> occupancy,
        int gridX,
        int gridY,
        bool turning)
    {
        if (occupancy == null ||
            occupancy.Count == 0)
            return 0f;

        float penalty = 0f;

        for (int oy = -1;
             oy <= 1;
             oy++)
        {
            for (int ox = -1;
                 ox <= 1;
                 ox++)
            {
                if (ox == 0 &&
                    oy == 0)
                    continue;

                if (!occupancy.TryGetValue(
                        GridKey(
                            gridX + ox,
                            gridY + oy),
                        out Occupancy neighbor) ||
                    neighbor.BendCount == 0)
                    continue;

                float distanceWeight =
                    ox != 0 &&
                    oy != 0
                        ? 0.72f
                        : 1f;

                penalty +=
                    (turning
                        ? JunctionNeighborTurnPenalty
                        : JunctionNeighborPassPenalty) *
                    neighbor.BendCount *
                    distanceWeight;
            }
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
        if (points == null ||
            points.Length < 2)
            return false;

        int lastSegment =
            points.Length - 2;

        if (lastSegment == 0)
        {
            return
                !TerminalSegmentBlockedDerived(
                    points[0],
                    points[1],
                    startRoom,
                    obstacles) &&
                !TerminalSegmentBlockedDerived(
                    points[0],
                    points[1],
                    endRoom,
                    obstacles);
        }

        for (int i = 0;
             i <= lastSegment;
             i++)
        {
            Num.Vector2 a =
                points[i];
            Num.Vector2 b =
                points[i + 1];

            if (i == 0)
            {
                if (TerminalSegmentBlockedDerived(
                        a,
                        b,
                        startRoom,
                        obstacles))
                    return false;

                continue;
            }

            if (i == lastSegment)
            {
                if (TerminalSegmentBlockedDerived(
                        a,
                        b,
                        endRoom,
                        obstacles))
                    return false;

                continue;
            }

            if (SegmentBlocked(
                    a,
                    b,
                    startRoom,
                    endRoom,
                    obstacles))
                return false;
        }

        return true;
    }

    private static bool TerminalSegmentBlockedDerived(
        Num.Vector2 a,
        Num.Vector2 b,
        int ownRoom,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (obstacles == null)
            return false;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];

            if (obstacle.RoomIndex ==
                ownRoom)
                continue;

            Num.Vector2 rawMin =
                obstacle.Min +
                new Num.Vector2(
                    ObstacleMargin,
                    ObstacleMargin);
            Num.Vector2 rawMax =
                obstacle.Max -
                new Num.Vector2(
                    ObstacleMargin,
                    ObstacleMargin);

            if (SegmentIntersectsRect(
                    a,
                    b,
                    rawMin,
                    rawMax))
                return true;
        }

        return false;
    }

    private static bool SegmentBlocked(
        Num.Vector2 a,
        Num.Vector2 b,
        int startRoom,
        int endRoom,
        IReadOnlyList<Obstacle> obstacles)
    {
        if (obstacles == null)
            return false;

        for (int i = 0;
             i < obstacles.Count;
             i++)
        {
            Obstacle obstacle =
                obstacles[i];
            Num.Vector2 min =
                obstacle.Min;
            Num.Vector2 max =
                obstacle.Max;

            if (obstacle.RoomIndex ==
                    startRoom ||
                obstacle.RoomIndex ==
                    endRoom)
            {
                // Endpoint-room clearance margins are soft for their own connection. The physical
                // room body remains hard, which lets close rooms connect without forcing a huge
                // detour solely because the 24px visual margins overlap.
                min +=
                    new Num.Vector2(
                        ObstacleMargin,
                        ObstacleMargin);
                max -=
                    new Num.Vector2(
                        ObstacleMargin,
                        ObstacleMargin);
            }

            if (SegmentIntersectsRect(
                    a,
                    b,
                    min,
                    max))
                return true;
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

    private static Num.Vector2[] CollapseImmediateBacktracks(
        Num.Vector2[] source)
    {
        if (source == null ||
            source.Length <= 2)
        {
            return source == null
                ? Array.Empty<Num.Vector2>()
                : (Num.Vector2[])source.Clone();
        }

        List<Num.Vector2> result =
            new(source.Length);

        for (int i = 0;
             i < source.Length;
             i++)
        {
            Num.Vector2 point =
                source[i];

            if (result.Count > 0 &&
                Num.Vector2.DistanceSquared(
                    result[result.Count - 1],
                    point) < 0.0001f)
            {
                continue;
            }

            result.Add(point);

            bool reduced = true;
            while (reduced &&
                   result.Count >= 3)
            {
                reduced = false;

                int count =
                    result.Count;
                Num.Vector2 a =
                    result[count - 3];
                Num.Vector2 b =
                    result[count - 2];
                Num.Vector2 d =
                    result[count - 1];

                Num.Vector2 ab =
                    b - a;
                Num.Vector2 bd =
                    d - b;

                // A->B->D on one axis with a negative dot product is a literal U-turn. B is an
                // overshoot: replacing both segments with A->D can only shorten the path and the
                // replacement lies entirely inside the already validated segment union. Terminal
                // fanout/stub anchors are not exempt from this rule; visual ownership must never
                // create "go right, then immediately go left" geometry.
                if (Math.Abs(
                        Cross(
                            ab,
                            bd)) <= 0.01f &&
                    Num.Vector2.Dot(
                        ab,
                        bd) < -0.01f)
                {
                    result.RemoveAt(
                        count - 2);

                    if (result.Count >= 2 &&
                        Num.Vector2.DistanceSquared(
                            result[result.Count - 2],
                            result[result.Count - 1]) < 0.0001f)
                    {
                        result.RemoveAt(
                            result.Count - 1);
                    }

                    reduced = true;
                }
            }
        }

        return result.ToArray();
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
