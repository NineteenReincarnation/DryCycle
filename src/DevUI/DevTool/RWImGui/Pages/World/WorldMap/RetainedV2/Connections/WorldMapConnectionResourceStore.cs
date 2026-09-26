using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained connection-route owner. Route invalidation follows topology/room dependencies, never
/// pan/zoom. Only affected routes are rebuilt after room movement; topology changes rebuild the
/// dependency/lane index and route set.
/// </summary>
internal sealed class WorldMapConnectionResourceStore
{
    private const int IdleRoutesPerFrame = 24;
    private const int InteractiveRoutesPerFrame = 8;
    // Use one spacing grammar from the room edge into the global corridor. Pair lanes used to
    // begin at 8/5px while terminal/corridor lanes targeted roughly 10/6px, forcing an immediate
    // expansion after the stub even when there was plenty of space.
    private const float PreferredPairLaneSpacing = 10f;
    private const float MinimumPairLaneSpacing = 6f;
    private const float PreferredTerminalLaneSpacing = 10f;
    private const float MinimumTerminalLaneSpacing = 6f;
    private const float PairLaneTargetSpan = 72f;
    private const float TerminalLaneTargetSpan = 72f;
    private const int DenseLaneBankThreshold = 12;
    private const int DenseLaneBankCapacity = 8;
    private const float DensePairLaneSpacing = 6.25f;
    private const float DensePairBankGutter = 9f;
    private const float DenseTerminalDepthGap = 10f;

    private readonly Dictionary<string, ConnectionRouteResource> routes =
        new(StringComparer.Ordinal);
    private readonly ConnectionDependencyIndex dependencies = new();
    private readonly Dictionary<string, float> laneOffsets =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldMapWorldSpaceRouter.TerminalFanout> terminalFanouts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte> endpointDensityTiers =
        new(StringComparer.Ordinal);
    private readonly Queue<string> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly HashSet<string> routeChanged = new(StringComparer.Ordinal);
    private readonly List<WorldMapScene.ConnectionNode> buildBatch = new();
    private readonly List<System.Numerics.Vector2[]> routeOccupancySeeds = new();
    private readonly List<System.Numerics.Vector2[]> routeAvoidanceSeeds = new();
    private const byte MaxCorridorRerouteAttempts = 2;

    private readonly Dictionary<string, byte> corridorRerouteAttempts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<int, WorldMapOrthogonalRouter.Obstacle> routingObstacles =
        new();
    private readonly List<WorldMapOrthogonalRouter.Obstacle> routingObstacleSnapshot =
        new();
    private bool routingObstacleSnapshotDirty = true;
    private bool corridorLayoutDirty = true;
    private bool crossingLayoutDirty = true;
    private WorldMapCrossingMark[] crossings = Array.Empty<WorldMapCrossingMark>();
    private bool crossingBudgetLimited;
    private int crossingCandidateChecks;
    private long crossingRevision;
    private long revision;

    internal IReadOnlyDictionary<string, ConnectionRouteResource> Routes => routes;
    internal IReadOnlyList<WorldMapCrossingMark> Crossings => crossings;
    internal long CrossingRevision => crossingRevision;
    internal bool CrossingsCurrent => !crossingLayoutDirty;
    internal bool CrossingBudgetLimited => crossingBudgetLimited;
    internal int CrossingCandidateChecks => crossingCandidateChecks;
    internal int Count => routes.Count;
    internal int PendingCount => queue.Count;
    internal long Revision => revision;


    internal bool TryGet(string id, out ConnectionRouteResource route)
    {
        route = null;
        return !string.IsNullOrEmpty(id) && routes.TryGetValue(id, out route);
    }

    internal bool HasCompleteRoutes(WorldMapScene scene)
    {
        if (scene == null) return false;
        if (scene.Connections.Count == 0) return true;
        if (routes.Count < scene.Connections.Count) return false;

        foreach (string id in scene.Connections.Keys)
        {
            if (!routes.TryGetValue(id, out ConnectionRouteResource route) ||
                route?.Points == null ||
                route.Points.Length < 2)
                return false;
        }

        return true;
    }

    internal void DrainRouteChanges(List<string> output)
    {
        if (output == null) return;
        output.Clear();
        if (routeChanged.Count == 0) return;
        output.AddRange(routeChanged);
        routeChanged.Clear();
    }

    internal void ApplyDirty(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        WorldMapDirtySet dirty)
    {
        if (scene == null || dirty == null) return;

        foreach (string id in dirty.RemovedConnections)
        {
            corridorRerouteAttempts.Remove(id);

            if (routes.Remove(id))
            {
                corridorLayoutDirty = true;
                crossingLayoutDirty = true;
                routeChanged.Add(id);
                unchecked { revision++; }
                MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
            }
            queued.Remove(id);
        }

        foreach (int roomIndex in dirty.RemovedRooms)
        {
            if (routingObstacles.Remove(roomIndex))
                routingObstacleSnapshotDirty = true;
        }

        if (dirty.FullRebuild || dirty.TopologyChanged)
        {
            corridorRerouteAttempts.Clear();
            dependencies.Rebuild(scene);
            RebuildLanePlan(scene, roomResources);
            RebuildRoutingObstacles(scene, roomResources);
            corridorLayoutDirty = true;
            crossingLayoutDirty = true;
            EnqueueAll(scene);
            return;
        }

        if (dirty.RoomPorts.Count > 0)
        {
            corridorRerouteAttempts.Clear();
            RebuildTerminalFanouts(scene, roomResources);
        }

        if (dirty.RoomTransforms.Count > 0)
            corridorRerouteAttempts.Clear();

        foreach (string id in dirty.Connections)
        {
            corridorRerouteAttempts.Remove(id);
            Enqueue(id);
        }

        InvalidateRooms(
            scene,
            roomResources,
            dirty.RoomTransforms,
            refreshObstacle: true);
        InvalidateRooms(
            scene,
            roomResources,
            dirty.RoomPorts,
            refreshObstacle: false);
        InvalidateRooms(
            scene,
            roomResources,
            dirty.RemovedRooms,
            refreshObstacle: false);
    }

    internal void InvalidateRooms(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        IEnumerable<int> roomIndices,
        bool refreshObstacle = true)
    {
        if (roomIndices == null) return;
        foreach (int roomIndex in roomIndices)
        {
            if (refreshObstacle)
                UpdateRoutingObstacle(scene, roomResources, roomIndex);

            foreach (string id in dependencies.GetForRoom(roomIndex))
                Enqueue(id);
        }
    }

    internal void Update(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        if (scene == null ||
            (queue.Count == 0 &&
             !corridorLayoutDirty &&
             !crossingLayoutDirty))
            return;

        int budget = WorldMapBackgroundBudget.RouteBuildBudget(
            IdleRoutesPerFrame,
            InteractiveRoutesPerFrame);
        if (budget <= 0)
            return;

        if (queue.Count == 0)
        {
            if (WorldMapBackgroundBudget.RoomDragInteractionActive)
                return;

            if (corridorLayoutDirty)
                ApplyCorridorLanes();
            else if (crossingLayoutDirty)
                RebuildCrossings();

            return;
        }

        bool routeSetChanged = false;
        buildBatch.Clear();
        int scanCount = queue.Count;
        int scanned = 0;
        while (budget > 0 &&
               queue.Count > 0 &&
               scanned < scanCount)
        {
            string id = queue.Dequeue();
            scanned++;

            WorldMapPersistentRouteRestoreResult restore =
                WorldMapPersistentRetainedCache.TryRestoreRoute(
                    scene,
                    roomResources,
                    id,
                    out ConnectionRouteResource restoredRoute);

            if (restore == WorldMapPersistentRouteRestoreResult.Pending)
            {
                queue.Enqueue(id);
                continue;
            }

            if (!queued.Remove(id))
                continue;

            if (restore == WorldMapPersistentRouteRestoreResult.Restored)
            {
                restoredRoute.Revision =
                    routes.TryGetValue(id, out ConnectionRouteResource oldRoute)
                        ? oldRoute.Revision + 1L
                        : 1L;
                routes[id] = restoredRoute;
                corridorLayoutDirty = true;
                crossingLayoutDirty = true;
                routeSetChanged = true;
                routeChanged.Add(id);
                unchecked { revision++; }
                budget--;
                continue;
            }

            if (!scene.TryGetConnection(id, out WorldMapScene.ConnectionNode connection))
            {
                if (routes.Remove(id))
                {
                    corridorLayoutDirty = true;
                    crossingLayoutDirty = true;
                    routeChanged.Add(id);
                    unchecked { revision++; }
                    MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
                }
                continue;
            }

            buildBatch.Add(connection);
            budget--;
        }

        Dictionary<string, ConnectionRouteResource> rebuilt =
            null;

        if (buildBatch.Count > 0)
        {
            // Occupancy is incremental inside one router batch, so solve the same set in a stable
            // order regardless of which dirty notification enqueued the connections first.
            buildBatch.Sort(
                (left, right) =>
                    string.CompareOrdinal(
                        left?.Id,
                        right?.Id));

            BuildRouteOccupancySeeds();
            rebuilt =
                WorldMapWorldSpaceRouter.Build(
                    scene,
                    roomResources,
                    buildBatch,
                    laneOffsets,
                    terminalFanouts,
                    GetRoutingObstacleSnapshot(),
                    routeOccupancySeeds,
                    routeAvoidanceSeeds);
        }

        for (int i = 0; i < buildBatch.Count; i++)
        {
            WorldMapScene.ConnectionNode connection = buildBatch[i];
            if (!rebuilt.TryGetValue(connection.Id, out ConnectionRouteResource route))
                continue;

            if (routes.TryGetValue(connection.Id, out ConnectionRouteResource previous))
                route.Revision = previous.Revision + 1L;
            else
                route.Revision = 1L;

            routes[connection.Id] = route;
            corridorLayoutDirty = true;
            crossingLayoutDirty = true;
            routeSetChanged = true;
            routeChanged.Add(connection.Id);
            unchecked { revision++; }
            MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
        }

        if (routeSetChanged || corridorLayoutDirty)
        {
            // Room dragging keeps only incident base-route rebuilds on the hot path. Global corridor
            // reflow is deferred until the interaction cooldown expires, then converges once.
            if (!WorldMapBackgroundBudget.RoomDragInteractionActive)
                ApplyCorridorLanes();
        }
    }

    internal void Reset()
    {
        routes.Clear();
        dependencies.Reset();
        laneOffsets.Clear();
        terminalFanouts.Clear();
        endpointDensityTiers.Clear();
        queue.Clear();
        queued.Clear();
        routeChanged.Clear();
        buildBatch.Clear();
        routeOccupancySeeds.Clear();
        routeAvoidanceSeeds.Clear();
        corridorRerouteAttempts.Clear();
        routingObstacles.Clear();
        routingObstacleSnapshot.Clear();
        routingObstacleSnapshotDirty = true;
        corridorLayoutDirty = true;
        crossingLayoutDirty = true;
        crossings = Array.Empty<WorldMapCrossingMark>();
        crossingBudgetLimited = false;
        crossingCandidateChecks = 0;
        crossingRevision = 0L;
        WorldMapOrthogonalRouter.Clear();
        revision = 0L;
    }

    private void EnqueueAll(WorldMapScene scene)
    {
        List<string> ids =
            new(scene.Connections.Keys);
        ids.Sort(StringComparer.Ordinal);

        for (int i = 0; i < ids.Count; i++)
            Enqueue(ids[i]);

        List<string> stale = null;
        foreach (string id in routes.Keys)
        {
            if (scene.Connections.ContainsKey(id)) continue;
            stale ??= new List<string>();
            stale.Add(id);
        }

        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++)
        {
            if (routes.Remove(stale[i]))
            {
                corridorLayoutDirty = true;
                crossingLayoutDirty = true;
                routeChanged.Add(stale[i]);
                unchecked { revision++; }
            }
        }
    }

    private void Enqueue(string id)
    {
        if (string.IsNullOrEmpty(id) || !queued.Add(id)) return;
        queue.Enqueue(id);
    }

    private void RebuildRoutingObstacles(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        routingObstacles.Clear();
        if (scene != null)
        {
            foreach (WorldMapScene.RoomNode room in scene.Rooms.Values)
                UpdateRoutingObstacle(scene, roomResources, room.RoomIndex);
        }
        routingObstacleSnapshotDirty = true;
    }

    private void UpdateRoutingObstacle(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        int roomIndex)
    {
        if (scene == null ||
            !scene.TryGetRoom(roomIndex, out WorldMapScene.RoomNode room))
        {
            if (routingObstacles.Remove(roomIndex))
                routingObstacleSnapshotDirty = true;
            return;
        }

        WorldMapWorldSpaceRouter.GetRoomBounds(
            room,
            roomResources,
            out System.Numerics.Vector2 min,
            out System.Numerics.Vector2 max);
        routingObstacles[roomIndex] =
            WorldMapOrthogonalRouter.CreateRoutingObstacle(
                roomIndex,
                min,
                max);
        routingObstacleSnapshotDirty = true;
    }

    private IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> GetRoutingObstacleSnapshot()
    {
        if (!routingObstacleSnapshotDirty)
            return routingObstacleSnapshot;

        routingObstacleSnapshot.Clear();
        foreach (WorldMapOrthogonalRouter.Obstacle obstacle in routingObstacles.Values)
            routingObstacleSnapshot.Add(obstacle);
        routingObstacleSnapshot.Sort((a, b) => a.RoomIndex.CompareTo(b.RoomIndex));
        routingObstacleSnapshotDirty = false;
        return routingObstacleSnapshot;
    }

    private void ApplyCorridorLanes()
    {
        if (!corridorLayoutDirty)
            return;

        corridorLayoutDirty = false;
        ApplyEndpointDensityTiers();

        WorldMapCorridorLaneApplyResult laneResult =
            WorldMapCorridorLaneAllocator.Apply(
                routes,
                GetRoutingObstacleSnapshot(),
                routeChanged,
                ref revision);

        crossingLayoutDirty = true;

        // Only reroute after the current route queue has converged. Doing this in the middle of a
        // progressive cold start would react to an incomplete map and cause avoidable route churn.
        if (queue.Count == 0 &&
            laneResult.HasRerouteCandidates)
        {
            int scheduled =
                ScheduleCorridorReroutes(
                    laneResult.RerouteRouteIds);

            if (scheduled > 0)
                return;
        }

        // During progressive cold-start batches, crossings are presentation sugar and may wait until
        // all currently queued base routes exist. This avoids rebuilding the crossing index 24 routes
        // at a time across a large region.
        if (queue.Count == 0)
            RebuildCrossings();
    }

    private int ScheduleCorridorReroutes(
        IReadOnlyList<string> routeIds)
    {
        if (routeIds == null ||
            routeIds.Count == 0)
            return 0;

        int scheduled = 0;

        for (int i = 0; i < routeIds.Count; i++)
        {
            string id =
                routeIds[i];

            if (string.IsNullOrEmpty(id) ||
                !routes.ContainsKey(id))
                continue;

            corridorRerouteAttempts.TryGetValue(
                id,
                out byte attempts);

            if (attempts >=
                MaxCorridorRerouteAttempts)
                continue;

            corridorRerouteAttempts[id] =
                (byte)(attempts + 1);

            Enqueue(id);
            scheduled++;
        }

        return scheduled;
    }

    private void BuildRouteOccupancySeeds()
    {
        routeOccupancySeeds.Clear();
        routeAvoidanceSeeds.Clear();

        if (routes.Count == 0)
            return;

        HashSet<string> rebuilding =
            new(StringComparer.Ordinal);

        for (int i = 0; i < buildBatch.Count; i++)
        {
            string id =
                buildBatch[i]?.Id;

            if (!string.IsNullOrEmpty(id))
                rebuilding.Add(id);
        }

        foreach (KeyValuePair<string, ConnectionRouteResource> pair
                 in routes)
        {
            ConnectionRouteResource route =
                pair.Value;
            System.Numerics.Vector2[] points =
                route?.BasePoints != null &&
                route.BasePoints.Length >= 2
                    ? route.BasePoints
                    : route?.Points;

            if (points == null ||
                points.Length < 2)
                continue;

            if (rebuilding.Contains(pair.Key))
            {
                // A route that collapsed its lane bank gets one bounded retry. Its previous base
                // corridor is supplied as strong soft avoidance rather than as a hard obstacle, so
                // the router actively searches another corridor but can still fall back here if no
                // valid alternative exists.
                if (corridorRerouteAttempts.TryGetValue(
                        pair.Key,
                        out byte attempts) &&
                    attempts > 0)
                {
                    // A second retry applies stronger soft pressure to the corridor that already
                    // failed once. Repeating the seed is intentional: the router's occupancy model
                    // accumulates weight without turning the old path into a hard obstacle.
                    int copies =
                        Math.Max(
                            1,
                            (int)attempts);

                    for (int copy = 0;
                         copy < copies;
                         copy++)
                    {
                        routeAvoidanceSeeds.Add(
                            points);
                    }
                }

                continue;
            }

            routeOccupancySeeds.Add(
                points);
        }
    }

    private void RebuildCrossings()
    {
        if (!crossingLayoutDirty)
            return;

        WorldMapCrossingResolveResult resolved =
            WorldMapRouteCrossingResolver.Build(routes);

        crossings = resolved.Marks;
        crossingBudgetLimited =
            resolved.BudgetLimited;
        crossingCandidateChecks =
            resolved.CandidateChecks;
        crossingLayoutDirty = false;

        unchecked
        {
            crossingRevision++;
            revision++;
        }
    }

    private void ApplyEndpointDensityTiers()
    {
        foreach (KeyValuePair<string, ConnectionRouteResource> pair
                 in routes)
        {
            byte tier = 0;
            endpointDensityTiers.TryGetValue(
                pair.Key,
                out tier);

            if (pair.Value != null)
                pair.Value.BaseDensityTier = tier;
        }
    }

    private readonly struct TerminalEndpoint
    {
        internal TerminalEndpoint(
            string connectionId,
            bool start,
            int roomIndex,
            int nodeIndex,
            int side,
            float along)
        {
            ConnectionId = connectionId;
            Start = start;
            RoomIndex = roomIndex;
            NodeIndex = nodeIndex;
            Side = side;
            Along = along;
        }

        internal string ConnectionId { get; }
        internal bool Start { get; }
        internal int RoomIndex { get; }
        internal int NodeIndex { get; }
        internal int Side { get; }
        internal float Along { get; }
    }

    private void RebuildLanePlan(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        RebuildPairLaneOffsets(scene, roomResources);
        RebuildTerminalFanouts(scene, roomResources);
    }

    private void RebuildPairLaneOffsets(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        laneOffsets.Clear();

        // Room-pair identity alone is not enough to define a parallel bundle. Two connections can
        // link the same rooms while leaving through completely different sides (for example right-
        // to-left versus top-to-bottom). Offsetting those as if they shared one corridor bends them
        // toward each other and creates unnecessary crossings. Split each room pair by canonical
        // endpoint-side combination first, then allocate lanes only inside that visual family.
        Dictionary<long, Dictionary<int, List<WorldMapScene.ConnectionNode>>> groups =
            new();

        foreach (WorldMapScene.ConnectionNode connection
                 in scene.Connections.Values)
        {
            int roomA =
                Math.Min(
                    connection.FromRoomIndex,
                    connection.ToRoomIndex);
            int roomB =
                Math.Max(
                    connection.FromRoomIndex,
                    connection.ToRoomIndex);
            long pairKey =
                ((long)(uint)roomA << 32) |
                (uint)roomB;

            int sideA =
                PairEndpointSide(
                    connection,
                    roomA,
                    scene,
                    roomResources);
            int sideB =
                PairEndpointSide(
                    connection,
                    roomB,
                    scene,
                    roomResources);
            int sideKey =
                (sideA << 2) |
                sideB;

            if (!groups.TryGetValue(
                    pairKey,
                    out Dictionary<int, List<WorldMapScene.ConnectionNode>> sideGroups))
            {
                sideGroups =
                    new Dictionary<int, List<WorldMapScene.ConnectionNode>>();
                groups.Add(
                    pairKey,
                    sideGroups);
            }

            if (!sideGroups.TryGetValue(
                    sideKey,
                    out List<WorldMapScene.ConnectionNode> members))
            {
                members =
                    new List<WorldMapScene.ConnectionNode>();
                sideGroups.Add(
                    sideKey,
                    members);
            }

            members.Add(
                connection);
        }

        foreach (KeyValuePair<long, Dictionary<int, List<WorldMapScene.ConnectionNode>>> pair
                 in groups)
        {
            int roomA =
                unchecked(
                    (int)(uint)(pair.Key >> 32));
            int roomB =
                unchecked(
                    (int)(uint)pair.Key);

            bool horizontalPair = true;
            if (scene.TryGetRoom(
                    roomA,
                    out WorldMapScene.RoomNode aRoom) &&
                scene.TryGetRoom(
                    roomB,
                    out WorldMapScene.RoomNode bRoom))
            {
                WorldMapWorldSpaceRouter.GetRoomBounds(
                    aRoom,
                    roomResources,
                    out System.Numerics.Vector2 aMin,
                    out System.Numerics.Vector2 aMax);
                WorldMapWorldSpaceRouter.GetRoomBounds(
                    bRoom,
                    roomResources,
                    out System.Numerics.Vector2 bMin,
                    out System.Numerics.Vector2 bMax);

                System.Numerics.Vector2 delta =
                    (bMin + bMax) * 0.5f -
                    (aMin + aMax) * 0.5f;
                horizontalPair =
                    Math.Abs(delta.X) >=
                    Math.Abs(delta.Y);
            }

            List<int> sideKeys =
                new(pair.Value.Keys);
            sideKeys.Sort();

            for (int groupIndex = 0;
                 groupIndex < sideKeys.Count;
                 groupIndex++)
            {
                List<WorldMapScene.ConnectionNode> members =
                    pair.Value[sideKeys[groupIndex]];

                if (members == null ||
                    members.Count == 0)
                    continue;

                members.Sort(
                    (left, right) =>
                        ComparePairLaneMembers(
                            left,
                            right,
                            roomA,
                            roomB,
                            horizontalPair,
                            scene,
                            roomResources));

                float[] offsets =
                    BuildPairLaneOffsets(
                        members.Count);

                for (int i = 0;
                     i < members.Count;
                     i++)
                {
                    laneOffsets[members[i].Id] =
                        offsets[i];
                }
            }
        }
    }

    private static int PairEndpointSide(
        WorldMapScene.ConnectionNode connection,
        int roomIndex,
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        if (connection == null ||
            scene == null ||
            !scene.TryGetRoom(
                roomIndex,
                out WorldMapScene.RoomNode room))
            return 0;

        System.Numerics.Vector2 point =
            ResolvePairEndpoint(
                connection,
                roomIndex,
                scene,
                roomResources);

        WorldMapWorldSpaceRouter.GetRoomBounds(
            room,
            roomResources,
            out System.Numerics.Vector2 min,
            out System.Numerics.Vector2 max);

        System.Numerics.Vector2 direction =
            WorldMapOrthogonalRouter.InferPortDirection(
                point,
                min,
                max);

        return SideCode(
            direction);
    }

    private static int ComparePairLaneMembers(
        WorldMapScene.ConnectionNode left,
        WorldMapScene.ConnectionNode right,
        int roomA,
        int roomB,
        bool horizontalPair,
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        GetCanonicalPairEndpoints(
            left,
            roomA,
            roomB,
            scene,
            roomResources,
            out System.Numerics.Vector2 leftA,
            out System.Numerics.Vector2 leftB);
        GetCanonicalPairEndpoints(
            right,
            roomA,
            roomB,
            scene,
            roomResources,
            out System.Numerics.Vector2 rightA,
            out System.Numerics.Vector2 rightB);

        float leftAlongA =
            horizontalPair
                ? leftA.Y
                : leftA.X;
        float leftAlongB =
            horizontalPair
                ? leftB.Y
                : leftB.X;
        float rightAlongA =
            horizontalPair
                ? rightA.Y
                : rightA.X;
        float rightAlongB =
            horizontalPair
                ? rightB.Y
                : rightB.X;

        // Average endpoint order is the primary lane order. Endpoint-specific tie breakers stop
        // sibling links from crossing merely because their connection IDs sort differently.
        float leftAverage =
            (leftAlongA + leftAlongB) *
            0.5f;
        float rightAverage =
            (rightAlongA + rightAlongB) *
            0.5f;

        int average =
            leftAverage.CompareTo(
                rightAverage);
        if (average != 0)
            return average;

        int aOrder =
            leftAlongA.CompareTo(
                rightAlongA);
        if (aOrder != 0)
            return aOrder;

        int bOrder =
            leftAlongB.CompareTo(
                rightAlongB);
        if (bOrder != 0)
            return bOrder;

        int leftNodeA =
            left.FromRoomIndex == roomA
                ? left.FromNodeIndex
                : left.ToNodeIndex;
        int rightNodeA =
            right.FromRoomIndex == roomA
                ? right.FromNodeIndex
                : right.ToNodeIndex;
        int node =
            leftNodeA.CompareTo(
                rightNodeA);
        if (node != 0)
            return node;

        return string.CompareOrdinal(
            left.Id,
            right.Id);
    }

    private static void GetCanonicalPairEndpoints(
        WorldMapScene.ConnectionNode connection,
        int roomA,
        int roomB,
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        out System.Numerics.Vector2 pointA,
        out System.Numerics.Vector2 pointB)
    {
        pointA =
            ResolvePairEndpoint(
                connection,
                roomA,
                scene,
                roomResources);
        pointB =
            ResolvePairEndpoint(
                connection,
                roomB,
                scene,
                roomResources);
    }

    private static System.Numerics.Vector2 ResolvePairEndpoint(
        WorldMapScene.ConnectionNode connection,
        int roomIndex,
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        if (connection == null ||
            scene == null ||
            !scene.TryGetRoom(
                roomIndex,
                out WorldMapScene.RoomNode room))
            return System.Numerics.Vector2.Zero;

        int nodeIndex =
            connection.FromRoomIndex == roomIndex
                ? connection.FromNodeIndex
                : connection.ToNodeIndex;

        if (nodeIndex >= 0)
        {
            return WorldMapWorldSpaceRouter.EndpointPosition(
                room,
                nodeIndex,
                roomResources);
        }

        WorldMapWorldSpaceRouter.GetRoomBounds(
            room,
            roomResources,
            out System.Numerics.Vector2 min,
            out System.Numerics.Vector2 max);
        return (min + max) * 0.5f;
    }

    private void RebuildTerminalFanouts(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        terminalFanouts.Clear();
        endpointDensityTiers.Clear();
        if (scene == null ||
            scene.Connections.Count == 0)
            return;

        Dictionary<long, List<TerminalEndpoint>> groups =
            new();

        foreach (WorldMapScene.ConnectionNode connection
                 in scene.Connections.Values)
        {
            if (connection == null ||
                !scene.TryGetRoom(
                    connection.FromRoomIndex,
                    out WorldMapScene.RoomNode fromRoom) ||
                !scene.TryGetRoom(
                    connection.ToRoomIndex,
                    out WorldMapScene.RoomNode toRoom))
                continue;

            WorldMapWorldSpaceRouter.GetRoomBounds(
                fromRoom,
                roomResources,
                out System.Numerics.Vector2 fromMin,
                out System.Numerics.Vector2 fromMax);
            WorldMapWorldSpaceRouter.GetRoomBounds(
                toRoom,
                roomResources,
                out System.Numerics.Vector2 toMin,
                out System.Numerics.Vector2 toMax);

            System.Numerics.Vector2 start =
                WorldMapWorldSpaceRouter.EndpointPosition(
                    fromRoom,
                    connection.FromNodeIndex,
                    roomResources);
            System.Numerics.Vector2 end =
                connection.ToNodeIndex >= 0
                    ? WorldMapWorldSpaceRouter.EndpointPosition(
                        toRoom,
                        connection.ToNodeIndex,
                        roomResources)
                    : BoundaryToward(
                        toMin,
                        toMax,
                        start);

            AddTerminalEndpoint(
                groups,
                connection.Id,
                start: true,
                fromRoom.RoomIndex,
                connection.FromNodeIndex,
                start,
                fromMin,
                fromMax);
            AddTerminalEndpoint(
                groups,
                connection.Id,
                start: false,
                toRoom.RoomIndex,
                connection.ToNodeIndex,
                end,
                toMin,
                toMax);
        }

        foreach (List<TerminalEndpoint> endpoints
                 in groups.Values)
        {
            endpoints.Sort(CompareTerminalEndpoints);
            float spacing =
                AdaptiveLaneSpacing(
                    endpoints.Count,
                    PreferredTerminalLaneSpacing,
                    MinimumTerminalLaneSpacing,
                    TerminalLaneTargetSpan);
            byte endpointDensityTier =
                DensityTierForCount(
                    endpoints.Count);

            for (int i = 0; i < endpoints.Count; i++)
            {
                TerminalEndpoint endpoint =
                    endpoints[i];

                if (endpointDensityTier > 0)
                {
                    endpointDensityTiers.TryGetValue(
                        endpoint.ConnectionId,
                        out byte currentTier);
                    if (endpointDensityTier > currentTier)
                    {
                        endpointDensityTiers[endpoint.ConnectionId] =
                            endpointDensityTier;
                    }
                }

                // Fan terminal stubs from the visual centre of the side instead of making
                // every later endpoint progressively longer. The old one-sided staircase could push
                // the final few routes dozens of pixels farther out and create a comb-like shape.
                // Centre-out depth keeps ownership clear while leaving more corridor space.
                float center =
                    (endpoints.Count - 1) *
                    0.5f;
                float rankFromCenter =
                    Math.Abs(i - center);
                float extraDepth =
                    rankFromCenter *
                    spacing;

                if (endpoints.Count > DenseLaneBankThreshold)
                {
                    extraDepth +=
                        ((int)(rankFromCenter /
                               DenseLaneBankCapacity)) *
                        DenseTerminalDepthGap;
                }

                terminalFanouts.TryGetValue(
                    endpoint.ConnectionId,
                    out WorldMapWorldSpaceRouter.TerminalFanout current);

                terminalFanouts[endpoint.ConnectionId] =
                    endpoint.Start
                        ? current.WithStart(
                            i,
                            endpoints.Count,
                            extraDepth)
                        : current.WithEnd(
                            i,
                            endpoints.Count,
                            extraDepth);
            }
        }
    }

    private static void AddTerminalEndpoint(
        Dictionary<long, List<TerminalEndpoint>> groups,
        string connectionId,
        bool start,
        int roomIndex,
        int nodeIndex,
        System.Numerics.Vector2 point,
        System.Numerics.Vector2 roomMin,
        System.Numerics.Vector2 roomMax)
    {
        System.Numerics.Vector2 direction =
            WorldMapOrthogonalRouter.InferPortDirection(
                point,
                roomMin,
                roomMax);
        int side =
            SideCode(direction);
        float along =
            direction.X != 0f
                ? point.Y
                : point.X;
        long key =
            ((long)(uint)roomIndex << 3) |
            (uint)side;

        if (!groups.TryGetValue(
                key,
                out List<TerminalEndpoint> endpoints))
        {
            endpoints = new List<TerminalEndpoint>();
            groups.Add(key, endpoints);
        }

        endpoints.Add(
            new TerminalEndpoint(
                connectionId,
                start,
                roomIndex,
                nodeIndex,
                side,
                along));
    }

    private static int CompareTerminalEndpoints(
        TerminalEndpoint a,
        TerminalEndpoint b)
    {
        int byAlong =
            a.Along.CompareTo(b.Along);
        if (byAlong != 0)
            return byAlong;

        int byNode =
            a.NodeIndex.CompareTo(b.NodeIndex);
        if (byNode != 0)
            return byNode;

        int byId =
            string.CompareOrdinal(
                a.ConnectionId,
                b.ConnectionId);
        if (byId != 0)
            return byId;

        return a.Start.CompareTo(b.Start);
    }

    private static int SideCode(
        System.Numerics.Vector2 direction)
    {
        if (direction.X < -0.5f) return 0;
        if (direction.X > 0.5f) return 1;
        if (direction.Y < -0.5f) return 2;
        return 3;
    }

    private static byte DensityTierForCount(int count)
    {
        if (count >= 24)
            return 2;
        if (count > DenseLaneBankThreshold)
            return 1;
        return 0;
    }

    private static float[] BuildPairLaneOffsets(int count)
    {
        float[] offsets =
            new float[Math.Max(0, count)];
        if (count <= 1)
            return offsets;

        if (count <= DenseLaneBankThreshold)
        {
            float spacing =
                AdaptiveLaneSpacing(
                    count,
                    PreferredPairLaneSpacing,
                    MinimumPairLaneSpacing,
                    PairLaneTargetSpan);
            float center =
                (count - 1) * 0.5f;

            for (int i = 0; i < count; i++)
                offsets[i] = (i - center) * spacing;

            return offsets;
        }

        float cursor = 0f;
        for (int i = 1; i < count; i++)
        {
            cursor += DensePairLaneSpacing;
            if (i % DenseLaneBankCapacity == 0)
                cursor += DensePairBankGutter;
            offsets[i] = cursor;
        }

        float midpoint =
            offsets[count - 1] * 0.5f;
        for (int i = 0; i < count; i++)
            offsets[i] -= midpoint;

        return offsets;
    }

    private static float AdaptiveLaneSpacing(
        int count,
        float preferred,
        float minimum,
        float targetSpan)
    {
        if (count <= 1)
            return 0f;

        float bounded =
            targetSpan /
            Math.Max(
                1,
                count - 1);
        return Math.Max(
            minimum,
            Math.Min(
                preferred,
                bounded));
    }

    private static System.Numerics.Vector2 BoundaryToward(
        System.Numerics.Vector2 min,
        System.Numerics.Vector2 max,
        System.Numerics.Vector2 target)
    {
        System.Numerics.Vector2 center =
            (min + max) * 0.5f;
        System.Numerics.Vector2 delta =
            target - center;

        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
        {
            return new System.Numerics.Vector2(
                delta.X >= 0f ? max.X : min.X,
                center.Y);
        }

        return new System.Numerics.Vector2(
            center.X,
            delta.Y >= 0f ? max.Y : min.Y);
    }
}
