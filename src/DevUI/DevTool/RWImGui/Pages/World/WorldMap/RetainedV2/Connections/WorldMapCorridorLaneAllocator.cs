using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal readonly struct WorldMapCorridorLaneApplyResult
{
    internal WorldMapCorridorLaneApplyResult(string[] rerouteRouteIds)
    {
        RerouteRouteIds = rerouteRouteIds ?? Array.Empty<string>();
    }

    internal string[] RerouteRouteIds { get; }
    internal bool HasRerouteCandidates => RerouteRouteIds.Length > 0;

    internal static WorldMapCorridorLaneApplyResult Empty =>
        new(Array.Empty<string>());
}

/// <summary>
/// Derives stable parallel lanes for overlapping middle corridors across the complete retained route
/// set. Phase 4 extends Phase 2 with bundle continuity: corridor components that carry at least two
/// of the same routes share one stable lane-slot plan, so branch-outs do not make the remaining
/// routes jump inward merely because a local component has fewer members.
/// </summary>
internal static class WorldMapCorridorLaneAllocator
{
    private readonly struct BucketKey : IEquatable<BucketKey>
    {
        internal BucketKey(bool vertical, int coordinate)
        {
            Vertical = vertical;
            Coordinate = coordinate;
        }

        internal bool Vertical { get; }
        internal int Coordinate { get; }

        public bool Equals(BucketKey other) =>
            Vertical == other.Vertical &&
            Coordinate == other.Coordinate;

        public override bool Equals(object obj) =>
            obj is BucketKey other &&
            Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Coordinate * 397) ^
                       (Vertical ? 1 : 0);
            }
        }
    }

    private sealed class SegmentRef
    {
        internal string RouteId = string.Empty;
        internal ConnectionRouteResource Route;
        internal int SegmentIndex;
        internal bool Vertical;
        internal float Coordinate;
        internal float Min;
        internal float Max;
    }

    private sealed class CorridorComponent
    {
        internal int Id;
        internal bool Vertical;
        internal float Coordinate;
        internal float Min;
        internal float Max;
        internal readonly List<SegmentRef> Segments = new();
        internal readonly List<string> RouteIds = new();
    }

    private sealed class RouteLanePlan
    {
        internal RouteLanePlan(int segmentCount)
        {
            int count = Math.Max(0, segmentCount);
            Offsets = new float[count];
            Assigned = new bool[count];
            GroupIds = new int[count];

            for (int i = 0; i < GroupIds.Length; i++)
                GroupIds[i] = -1;
        }

        internal float[] Offsets { get; }
        internal bool[] Assigned { get; }
        internal int[] GroupIds { get; }
        internal byte DensityTier;
    }

    private sealed class ContinuityPlanSet
    {
        internal readonly Dictionary<string, RouteLanePlan> Routes =
            new(StringComparer.Ordinal);
        internal readonly Dictionary<int, byte> GroupDensity =
            new();
        internal readonly Dictionary<int, List<string>> GroupRoutes =
            new();

        // A local corridor may ask for the opposite ordering of routes that already have stable
        // global slots in the same continuity bundle. Reordering existing slots would create an
        // internal lane permutation/X-crossing, so keep the established order and ask the routing
        // store for a bounded alternate-corridor retry instead.
        internal readonly HashSet<string> PermutationConflictRoutes =
            new(StringComparer.Ordinal);
    }

    private const float CoordinateBucketSize = 4f;
    private const float CoordinateMergeTolerance = 4f;
    private const float MinimumSharedRun = 16f;
    private const float PreferredLaneSpacing = 10f;
    private const float MinimumLaneSpacing = 5.5f;
    private const float TargetLaneSpan = 72f;
    private const float MinimumReadableGroupScale = 0.55f;
    private const float MaximumAdjacentGroupScaleDelta = 0.16f;
    private const float MinimumReadableParallelClearance = 4.0f;
    private const float MinimumReadableParallelRun = 18f;

    // Dense bundles are visually split into stable banks. The lane order never changes; a wider
    // gutter every eight lanes gives the eye a grouping landmark without endpoint codes/colors.
    private const int DenseBankThreshold = 12;
    private const int DenseBankCapacity = 8;
    private const float DenseLaneSpacing = 6.25f;
    private const float DenseBankGutter = 9f;
    // Do not jump directly from a full lane bundle to one shared centreline when clearance gets
    // tight. Preserve visible separation as far as possible; collapsing to zero is the final safety
    // fallback only when even a narrow bundle would intersect a room obstacle.
    private static readonly float[] DenseGroupScales =
    {
        1f,
        0.86f,
        0.74f,
        0.62f,
        0.50f,
        0.38f,
        0.28f,
        0f
    };
    private static readonly float[] NormalGroupScales =
    {
        1f,
        0.84f,
        0.68f,
        0.54f,
        0.42f,
        0.30f,
        0f
    };

    private const float PointEpsilonSquared = 0.04f;
    private const float BundleCrossingShoulder = 3f;
    private const int MaxBundleCrossingSegmentChecks = 16384;

    internal static WorldMapCorridorLaneApplyResult Apply(
        Dictionary<string, ConnectionRouteResource> routes,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles,
        HashSet<string> changedIds,
        ref long storeRevision)
    {
        if (routes == null || routes.Count == 0)
            return WorldMapCorridorLaneApplyResult.Empty;

        List<string> routeIds = new(routes.Keys);
        routeIds.Sort(StringComparer.Ordinal);

        Dictionary<BucketKey, List<SegmentRef>> buckets =
            BuildBuckets(routes, routeIds);
        List<CorridorComponent> components =
            BuildComponents(buckets);

        ContinuityPlanSet planSet =
            BuildContinuityPlans(components);
        Dictionary<int, float> groupScales =
            ResolveGroupScales(
                routes,
                planSet,
                obstacles);

        // Keep spacing changes gradual across neighbouring corridor components. A route should not
        // leave a 100% lane bank and enter a 54% bank at the very next junction; that visual
        // "pinch" is almost as hard to follow as an actual crossing.
        SmoothAdjacentGroupScales(
            planSet,
            groupScales);

        HashSet<string> reroute =
            new(StringComparer.Ordinal);

        for (int i = 0; i < routeIds.Count; i++)
        {
            string routeId = routeIds[i];
            ConnectionRouteResource route = routes[routeId];
            Num.Vector2[] basePoints = BasePoints(route);
            if (basePoints == null || basePoints.Length < 2)
                continue;

            Num.Vector2[] candidate = basePoints;
            byte densityTier =
                route.BaseDensityTier;

            if (planSet.Routes.TryGetValue(
                    routeId,
                    out RouteLanePlan lanePlan))
            {
                if (lanePlan.DensityTier > densityTier)
                    densityTier = lanePlan.DensityTier;

                byte compressionTier =
                    CompressionDensityTier(
                        lanePlan,
                        groupScales);
                if (compressionTier > densityTier)
                    densityTier = compressionTier;

                float[] effectiveOffsets =
                    BuildEffectiveOffsets(
                        lanePlan,
                        groupScales);

                Num.Vector2[] continuityCandidate =
                    BuildLanePath(
                        basePoints,
                        effectiveOffsets);

                bool continuityClear =
                    IsDerivedRouteClear(
                        continuityCandidate,
                        route,
                        obstacles);

                if (continuityClear)
                {
                    candidate = continuityCandidate;

                    bool needsTransition =
                        WorldMapJunctionWeavePlanner.NeedsTransition(
                            effectiveOffsets,
                            lanePlan.Assigned);
                    Num.Vector2[] weaveCandidate =
                        WorldMapJunctionWeavePlanner.Build(
                            basePoints,
                            effectiveOffsets,
                            lanePlan.Assigned);

                    if (weaveCandidate != null &&
                        IsDerivedRouteClear(
                            weaveCandidate,
                            route,
                            obstacles))
                    {
                        candidate = weaveCandidate;
                    }
                    else if (needsTransition)
                    {
                        // BuildLanePath averages adjacent offsets and can create a diagonal leader at
                        // a lane boundary. If the explicit orthogonal dogleg/weave cannot be built,
                        // prefer the original orthogonal base route while the bundle gets its one
                        // reroute attempt; never display a fake diagonal shortcut as a fallback.
                        candidate = basePoints;
                        if (densityTier < 1)
                            densityTier = 1;

                        AddTouchedGroupRoutes(
                            lanePlan,
                            planSet,
                            reroute);
                    }
                }
            }

            bool pathChanged =
                !PathsEquivalent(
                    route.Points,
                    candidate);
            bool densityChanged =
                route.DensityTier != densityTier;

            if (!pathChanged && !densityChanged)
                continue;

            if (pathChanged)
            {
                route.Points =
                    ReferenceEquals(candidate, basePoints)
                        ? (Num.Vector2[])basePoints.Clone()
                        : candidate;
            }

            route.DensityTier = densityTier;
            route.Revision =
                Math.Max(
                    1L,
                    route.Revision + 1L);
            changedIds?.Add(routeId);
            unchecked { storeRevision++; }
        }

        // A contradictory local permutation is also a readability failure: the global slot order
        // deliberately stays stable, while the involved routes get the same bounded alternate-
        // corridor reroute treatment as an over-compressed bundle.
        foreach (string routeId in planSet.PermutationConflictRoutes)
            reroute.Add(routeId);

        // Validate the final woven geometry as well. Two members of the same continuity bundle
        // should never cross each other and rely on the generic bridge renderer to explain it; that
        // would defeat the lane model. Any residual perpendicular crossing gets a bounded reroute.
        CollectBundleCrossingConflicts(
            routes,
            planSet,
            reroute);

        // Parallel members that remain closer than the readable lane floor for a meaningful run
        // are a local-clearance failure even if they never literally intersect. Ask the bounded
        // rerouter for another corridor instead of accepting a visually merged pair.
        CollectBundleClearanceConflicts(
            routes,
            planSet,
            reroute);

        // Severe compression is already a readability failure before it reaches a literal
        // centreline collapse. If a bundle drops below the minimum readable scale, surface those
        // routes to the resource store for one congestion-aware reroute pass. If no alternative
        // corridor exists, the store keeps the safe compressed fallback after that bounded retry.
        foreach (KeyValuePair<int, float> pair in groupScales)
        {
            if (pair.Value >= MinimumReadableGroupScale ||
                !planSet.GroupRoutes.TryGetValue(
                    pair.Key,
                    out List<string> groupRoutes) ||
                groupRoutes == null ||
                groupRoutes.Count < 2)
                continue;

            for (int i = 0; i < groupRoutes.Count; i++)
                reroute.Add(groupRoutes[i]);
        }

        if (reroute.Count == 0)
            return WorldMapCorridorLaneApplyResult.Empty;

        string[] rerouteIds = new string[reroute.Count];
        reroute.CopyTo(rerouteIds);
        Array.Sort(rerouteIds, StringComparer.Ordinal);
        return new WorldMapCorridorLaneApplyResult(rerouteIds);
    }

    private static void SmoothAdjacentGroupScales(
        ContinuityPlanSet planSet,
        Dictionary<int, float> scales)
    {
        if (planSet == null ||
            scales == null ||
            scales.Count < 2)
            return;

        Dictionary<int, HashSet<int>> adjacency =
            new();

        foreach (KeyValuePair<string, RouteLanePlan> pair
                 in planSet.Routes)
        {
            RouteLanePlan plan =
                pair.Value;
            if (plan == null ||
                plan.GroupIds.Length == 0)
                continue;

            int previousGroup = -1;
            int previousIndex = -100;

            for (int i = 0;
                 i < plan.GroupIds.Length;
                 i++)
            {
                int groupId =
                    plan.GroupIds[i];
                if (groupId < 0)
                    continue;

                if (previousGroup >= 0 &&
                    groupId != previousGroup &&
                    i - previousIndex <= 2)
                {
                    AddGroupNeighbor(
                        adjacency,
                        previousGroup,
                        groupId);
                    AddGroupNeighbor(
                        adjacency,
                        groupId,
                        previousGroup);
                }

                previousGroup =
                    groupId;
                previousIndex =
                    i;
            }
        }

        if (adjacency.Count == 0)
            return;

        // Only reduce the roomier neighbour. Never inflate the constrained corridor because its
        // scale has already been proven against room obstacles.
        for (int pass = 0;
             pass < scales.Count;
             pass++)
        {
            bool changed = false;

            foreach (KeyValuePair<int, HashSet<int>> pair
                     in adjacency)
            {
                if (!scales.TryGetValue(
                        pair.Key,
                        out float sourceScale))
                    continue;

                foreach (int neighbor in pair.Value)
                {
                    if (!scales.TryGetValue(
                            neighbor,
                            out float neighborScale))
                        continue;

                    float maximum =
                        sourceScale +
                        MaximumAdjacentGroupScaleDelta;

                    if (neighborScale <=
                        maximum +
                        0.0001f)
                        continue;

                    scales[neighbor] =
                        Math.Max(
                            0f,
                            Math.Min(
                                1f,
                                maximum));
                    changed = true;
                }
            }

            if (!changed)
                break;
        }
    }

    private static void AddGroupNeighbor(
        Dictionary<int, HashSet<int>> adjacency,
        int groupId,
        int neighborId)
    {
        if (groupId < 0 ||
            neighborId < 0 ||
            groupId == neighborId)
            return;

        if (!adjacency.TryGetValue(
                groupId,
                out HashSet<int> neighbors))
        {
            neighbors =
                new HashSet<int>();
            adjacency.Add(
                groupId,
                neighbors);
        }

        neighbors.Add(
            neighborId);
    }

    private static void CollectBundleClearanceConflicts(
        Dictionary<string, ConnectionRouteResource> routes,
        ContinuityPlanSet planSet,
        HashSet<string> reroute)
    {
        if (routes == null ||
            planSet == null ||
            reroute == null ||
            planSet.GroupRoutes.Count == 0)
            return;

        List<int> groupIds =
            new(planSet.GroupRoutes.Keys);
        groupIds.Sort();

        HashSet<string> checkedPairs =
            new(StringComparer.Ordinal);
        int checks = 0;

        for (int g = 0;
             g < groupIds.Count &&
             checks < MaxBundleCrossingSegmentChecks;
             g++)
        {
            if (!planSet.GroupRoutes.TryGetValue(
                    groupIds[g],
                    out List<string> ids) ||
                ids == null ||
                ids.Count < 2)
                continue;

            for (int i = 0;
                 i < ids.Count - 1 &&
                 checks < MaxBundleCrossingSegmentChecks;
                 i++)
            {
                string aId =
                    ids[i];

                if (!routes.TryGetValue(
                        aId,
                        out ConnectionRouteResource aRoute))
                    continue;

                for (int j = i + 1;
                     j < ids.Count &&
                     checks < MaxBundleCrossingSegmentChecks;
                     j++)
                {
                    string bId =
                        ids[j];
                    string pairKey =
                        string.CompareOrdinal(
                            aId,
                            bId) <= 0
                            ? aId + "\n" + bId
                            : bId + "\n" + aId;

                    if (!checkedPairs.Add(
                            pairKey) ||
                        !routes.TryGetValue(
                            bId,
                            out ConnectionRouteResource bRoute))
                        continue;

                    if (!RoutesRunTooClose(
                            aRoute?.Points,
                            bRoute?.Points,
                            ref checks))
                        continue;

                    reroute.Add(aId);
                    reroute.Add(bId);
                }
            }
        }
    }

    private static bool RoutesRunTooClose(
        Num.Vector2[] a,
        Num.Vector2[] b,
        ref int checks)
    {
        if (a == null ||
            b == null ||
            a.Length < 4 ||
            b.Length < 4)
            return false;

        int aFirst = 1;
        int aLast = a.Length - 3;
        int bFirst = 1;
        int bLast = b.Length - 3;

        for (int ai = aFirst;
             ai <= aLast &&
             checks < MaxBundleCrossingSegmentChecks;
             ai++)
        {
            Num.Vector2 a0 = a[ai];
            Num.Vector2 a1 = a[ai + 1];
            bool aVertical =
                Math.Abs(
                    a0.X -
                    a1.X) < 0.01f;
            bool aHorizontal =
                Math.Abs(
                    a0.Y -
                    a1.Y) < 0.01f;

            if (!aVertical &&
                !aHorizontal)
                continue;

            for (int bi = bFirst;
                 bi <= bLast &&
                 checks < MaxBundleCrossingSegmentChecks;
                 bi++)
            {
                Num.Vector2 b0 = b[bi];
                Num.Vector2 b1 = b[bi + 1];
                bool bVertical =
                    Math.Abs(
                        b0.X -
                        b1.X) < 0.01f;
                bool bHorizontal =
                    Math.Abs(
                        b0.Y -
                        b1.Y) < 0.01f;

                if ((!bVertical &&
                     !bHorizontal) ||
                    aVertical != bVertical)
                    continue;

                checks++;

                float separation;
                float overlap;

                if (aVertical)
                {
                    separation =
                        Math.Abs(
                            a0.X -
                            b0.X);
                    overlap =
                        IntervalOverlap(
                            a0.Y,
                            a1.Y,
                            b0.Y,
                            b1.Y);
                }
                else
                {
                    separation =
                        Math.Abs(
                            a0.Y -
                            b0.Y);
                    overlap =
                        IntervalOverlap(
                            a0.X,
                            a1.X,
                            b0.X,
                            b1.X);
                }

                if (overlap >=
                        MinimumReadableParallelRun &&
                    separation <
                        MinimumReadableParallelClearance)
                    return true;
            }
        }

        return false;
    }

    private static float IntervalOverlap(
        float a0,
        float a1,
        float b0,
        float b1)
    {
        float aMin =
            Math.Min(
                a0,
                a1);
        float aMax =
            Math.Max(
                a0,
                a1);
        float bMin =
            Math.Min(
                b0,
                b1);
        float bMax =
            Math.Max(
                b0,
                b1);

        return Math.Max(
            0f,
            Math.Min(
                aMax,
                bMax) -
            Math.Max(
                aMin,
                bMin));
    }

    private static void CollectBundleCrossingConflicts(
        Dictionary<string, ConnectionRouteResource> routes,
        ContinuityPlanSet planSet,
        HashSet<string> reroute)
    {
        if (routes == null ||
            planSet == null ||
            reroute == null ||
            planSet.GroupRoutes.Count == 0)
            return;

        List<int> groupIds =
            new(planSet.GroupRoutes.Keys);
        groupIds.Sort();

        HashSet<string> checkedPairs =
            new(StringComparer.Ordinal);
        int segmentChecks = 0;

        for (int g = 0;
             g < groupIds.Count &&
             segmentChecks < MaxBundleCrossingSegmentChecks;
             g++)
        {
            if (!planSet.GroupRoutes.TryGetValue(
                    groupIds[g],
                    out List<string> ids) ||
                ids == null ||
                ids.Count < 2)
                continue;

            for (int i = 0;
                 i < ids.Count - 1 &&
                 segmentChecks < MaxBundleCrossingSegmentChecks;
                 i++)
            {
                string aId =
                    ids[i];

                if (!routes.TryGetValue(
                        aId,
                        out ConnectionRouteResource aRoute))
                    continue;

                for (int j = i + 1;
                     j < ids.Count &&
                     segmentChecks < MaxBundleCrossingSegmentChecks;
                     j++)
                {
                    string bId =
                        ids[j];

                    string pairKey =
                        string.CompareOrdinal(
                            aId,
                            bId) <= 0
                            ? aId + "\n" + bId
                            : bId + "\n" + aId;

                    if (!checkedPairs.Add(
                            pairKey) ||
                        !routes.TryGetValue(
                            bId,
                            out ConnectionRouteResource bRoute))
                        continue;

                    if (!RoutesCrossInsideBundle(
                            aRoute?.Points,
                            bRoute?.Points,
                            ref segmentChecks))
                        continue;

                    reroute.Add(aId);
                    reroute.Add(bId);
                }
            }
        }
    }

    private static bool RoutesCrossInsideBundle(
        Num.Vector2[] a,
        Num.Vector2[] b,
        ref int checks)
    {
        if (a == null ||
            b == null ||
            a.Length < 4 ||
            b.Length < 4)
            return false;

        int aFirst = 1;
        int aLast = a.Length - 3;
        int bFirst = 1;
        int bLast = b.Length - 3;

        for (int ai = aFirst;
             ai <= aLast &&
             checks < MaxBundleCrossingSegmentChecks;
             ai++)
        {
            Num.Vector2 a0 = a[ai];
            Num.Vector2 a1 = a[ai + 1];
            bool aVertical =
                Math.Abs(
                    a0.X -
                    a1.X) < 0.01f;
            bool aHorizontal =
                Math.Abs(
                    a0.Y -
                    a1.Y) < 0.01f;

            if (!aVertical &&
                !aHorizontal)
                continue;

            for (int bi = bFirst;
                 bi <= bLast &&
                 checks < MaxBundleCrossingSegmentChecks;
                 bi++)
            {
                Num.Vector2 b0 = b[bi];
                Num.Vector2 b1 = b[bi + 1];
                bool bVertical =
                    Math.Abs(
                        b0.X -
                        b1.X) < 0.01f;
                bool bHorizontal =
                    Math.Abs(
                        b0.Y -
                        b1.Y) < 0.01f;

                if (!bVertical &&
                    !bHorizontal ||
                    aVertical == bVertical)
                    continue;

                checks++;

                Num.Vector2 vertical0 =
                    aVertical ? a0 : b0;
                Num.Vector2 vertical1 =
                    aVertical ? a1 : b1;
                Num.Vector2 horizontal0 =
                    aVertical ? b0 : a0;
                Num.Vector2 horizontal1 =
                    aVertical ? b1 : a1;

                float x =
                    vertical0.X;
                float y =
                    horizontal0.Y;
                float verticalMin =
                    Math.Min(
                        vertical0.Y,
                        vertical1.Y);
                float verticalMax =
                    Math.Max(
                        vertical0.Y,
                        vertical1.Y);
                float horizontalMin =
                    Math.Min(
                        horizontal0.X,
                        horizontal1.X);
                float horizontalMax =
                    Math.Max(
                        horizontal0.X,
                        horizontal1.X);

                if (y <=
                        verticalMin +
                        BundleCrossingShoulder ||
                    y >=
                        verticalMax -
                        BundleCrossingShoulder ||
                    x <=
                        horizontalMin +
                        BundleCrossingShoulder ||
                    x >=
                        horizontalMax -
                        BundleCrossingShoulder)
                    continue;

                return true;
            }
        }

        return false;
    }

    private static Dictionary<BucketKey, List<SegmentRef>> BuildBuckets(
        Dictionary<string, ConnectionRouteResource> routes,
        List<string> routeIds)
    {
        Dictionary<BucketKey, List<SegmentRef>> buckets =
            new();

        for (int r = 0; r < routeIds.Count; r++)
        {
            string routeId = routeIds[r];
            ConnectionRouteResource route = routes[routeId];
            Num.Vector2[] points = BasePoints(route);

            // Compact routes now carry the same terminal-stub grammar as longer routes. A four-point
            // route already has one true middle segment, so do not exclude it from lane allocation.
            if (points == null ||
                points.Length < 4)
                continue;

            // Protect only the physical socket stub (first/last segment). Everything after that is
            // corridor space and may be lane-separated. Protecting two segments at each end caused
            // short links to stay on the centreline and form false T/X junctions.
            int firstEligible = 1;
            int lastEligible = points.Length - 3;
            if (lastEligible < firstEligible)
                continue;

            for (int segment = firstEligible;
                 segment <= lastEligible;
                 segment++)
            {
                Num.Vector2 a = points[segment];
                Num.Vector2 b = points[segment + 1];

                bool vertical =
                    Math.Abs(a.X - b.X) < 0.01f;
                bool horizontal =
                    Math.Abs(a.Y - b.Y) < 0.01f;
                if (!vertical && !horizontal)
                    continue;

                float min =
                    vertical
                        ? Math.Min(a.Y, b.Y)
                        : Math.Min(a.X, b.X);
                float max =
                    vertical
                        ? Math.Max(a.Y, b.Y)
                        : Math.Max(a.X, b.X);
                if (max - min < MinimumSharedRun)
                    continue;

                float coordinate =
                    vertical
                        ? (a.X + b.X) * 0.5f
                        : (a.Y + b.Y) * 0.5f;
                int quantized =
                    (int)Math.Round(
                        coordinate / CoordinateBucketSize,
                        MidpointRounding.AwayFromZero);
                BucketKey key =
                    new(vertical, quantized);

                if (!buckets.TryGetValue(
                        key,
                        out List<SegmentRef> bucket))
                {
                    bucket = new List<SegmentRef>();
                    buckets.Add(key, bucket);
                }

                bucket.Add(
                    new SegmentRef
                    {
                        RouteId = routeId,
                        Route = route,
                        SegmentIndex = segment,
                        Vertical = vertical,
                        Coordinate = coordinate,
                        Min = min,
                        Max = max
                    });
            }
        }

        return buckets;
    }

    private static List<CorridorComponent> BuildComponents(
        Dictionary<BucketKey, List<SegmentRef>> buckets)
    {
        List<CorridorComponent> components =
            new();

        if (buckets == null ||
            buckets.Count == 0)
            return components;

        // Quantized dictionary buckets are useful for ingestion, but they must not define visual
        // topology. Two almost-coincident lines can fall on opposite sides of a rounding boundary
        // (for example 1.9 and 2.1 with a 4px bucket) and previously escaped lane separation even
        // though they rendered on top of each other. Flatten once, sort by actual coordinate, then
        // build bounded coordinate bands from geometry rather than hash-bucket identity.
        List<SegmentRef> segments =
            new();

        foreach (List<SegmentRef> bucket
                 in buckets.Values)
        {
            if (bucket == null)
                continue;

            for (int i = 0; i < bucket.Count; i++)
            {
                SegmentRef segment =
                    bucket[i];

                if (segment != null)
                    segments.Add(segment);
            }
        }

        if (segments.Count < 2)
            return components;

        segments.Sort(
            (a, b) =>
            {
                int axis =
                    a.Vertical.CompareTo(
                        b.Vertical);
                if (axis != 0)
                    return axis;

                int coordinate =
                    a.Coordinate.CompareTo(
                        b.Coordinate);
                if (coordinate != 0)
                    return coordinate;

                return CompareSegments(
                    a,
                    b);
            });

        int cursor = 0;
        while (cursor < segments.Count)
        {
            SegmentRef first =
                segments[cursor];
            bool vertical =
                first.Vertical;
            float bandStart =
                first.Coordinate;

            int endCursor =
                cursor + 1;

            while (endCursor < segments.Count)
            {
                SegmentRef next =
                    segments[endCursor];

                if (next.Vertical != vertical ||
                    next.Coordinate -
                    bandStart >
                    CoordinateMergeTolerance)
                    break;

                endCursor++;
            }

            int bandCount =
                endCursor -
                cursor;

            if (bandCount >= 2)
            {
                List<SegmentRef> band =
                    new(bandCount);

                for (int i = cursor;
                     i < endCursor;
                     i++)
                {
                    band.Add(
                        segments[i]);
                }

                BuildBucketComponents(
                    band,
                    components);
            }

            cursor =
                endCursor;
        }

        components.Sort(
            CompareComponents);

        for (int i = 0;
             i < components.Count;
             i++)
        {
            components[i].Id =
                i;
        }

        return components;
    }

    private static void BuildBucketComponents(
        List<SegmentRef> bucket,
        List<CorridorComponent> output)
    {
        if (bucket == null || bucket.Count < 2)
            return;

        bucket.Sort(CompareSegments);

        int count = bucket.Count;
        int[] parent = new int[count];
        for (int i = 0; i < count; i++)
            parent[i] = i;

        // Same-axis buckets remain intentionally local. Transitive overlap is useful here: if A
        // overlaps B and B overlaps C, all three belong to one visible corridor even if A and C do
        // not share the complete run.
        for (int i = 0; i < count; i++)
        {
            SegmentRef a = bucket[i];
            for (int j = i + 1; j < count; j++)
            {
                SegmentRef b = bucket[j];
                if (string.Equals(
                        a.RouteId,
                        b.RouteId,
                        StringComparison.Ordinal))
                    continue;

                float overlap =
                    Math.Min(a.Max, b.Max) -
                    Math.Max(a.Min, b.Min);
                if (overlap < MinimumSharedRun)
                    continue;

                Union(parent, i, j);
            }
        }

        Dictionary<int, List<SegmentRef>> grouped =
            new();
        for (int i = 0; i < count; i++)
        {
            int root = Find(parent, i);
            if (!grouped.TryGetValue(
                    root,
                    out List<SegmentRef> group))
            {
                group = new List<SegmentRef>();
                grouped.Add(root, group);
            }
            group.Add(bucket[i]);
        }

        foreach (List<SegmentRef> group in grouped.Values)
        {
            HashSet<string> unique =
                new(StringComparer.Ordinal);
            for (int i = 0; i < group.Count; i++)
                unique.Add(group[i].RouteId);

            if (unique.Count < 2)
                continue;

            CorridorComponent component =
                new()
                {
                    Vertical = group[0].Vertical,
                    Coordinate = AverageCoordinate(group),
                    Min = float.MaxValue,
                    Max = float.MinValue
                };

            for (int i = 0; i < group.Count; i++)
            {
                SegmentRef segment = group[i];
                component.Segments.Add(segment);
                component.Min =
                    Math.Min(
                        component.Min,
                        segment.Min);
                component.Max =
                    Math.Max(
                        component.Max,
                        segment.Max);
            }

            component.RouteIds.AddRange(unique);
            component.RouteIds.Sort(StringComparer.Ordinal);
            output.Add(component);
        }
    }

    private static ContinuityPlanSet BuildContinuityPlans(
        List<CorridorComponent> components)
    {
        ContinuityPlanSet planSet =
            new();

        if (components == null || components.Count == 0)
            return planSet;

        int[] parent =
            new int[components.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        // Build component adjacency through routes instead of O(component^2) comparison. A single
        // shared route does not define a bundle: requiring two shared routes prevents one long
        // connection from merging unrelated corridor systems into a huge sparse lane group.
        Dictionary<string, List<int>> componentsByRoute =
            new(StringComparer.Ordinal);
        for (int c = 0; c < components.Count; c++)
        {
            List<string> ids =
                components[c].RouteIds;
            for (int r = 0; r < ids.Count; r++)
            {
                string id = ids[r];
                if (!componentsByRoute.TryGetValue(
                        id,
                        out List<int> members))
                {
                    members = new List<int>();
                    componentsByRoute.Add(
                        id,
                        members);
                }
                members.Add(c);
            }
        }

        Dictionary<long, int> sharedRouteCounts =
            new();
        foreach (List<int> members
                 in componentsByRoute.Values)
        {
            if (members.Count < 2)
                continue;

            members.Sort();
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = i + 1; j < members.Count; j++)
                {
                    long key =
                        PairKey(
                            members[i],
                            members[j]);
                    sharedRouteCounts.TryGetValue(
                        key,
                        out int shared);
                    sharedRouteCounts[key] =
                        shared + 1;
                }
            }
        }

        foreach (KeyValuePair<long, int> pair
                 in sharedRouteCounts)
        {
            if (pair.Value < 2)
                continue;

            DecodePairKey(
                pair.Key,
                out int a,
                out int b);
            Union(parent, a, b);
        }

        Dictionary<int, List<CorridorComponent>> continuityGroups =
            new();
        for (int i = 0; i < components.Count; i++)
        {
            int root = Find(parent, i);
            if (!continuityGroups.TryGetValue(
                    root,
                    out List<CorridorComponent> group))
            {
                group = new List<CorridorComponent>();
                continuityGroups.Add(root, group);
            }
            group.Add(components[i]);
        }

        List<List<CorridorComponent>> orderedGroups =
            new(continuityGroups.Values);
        orderedGroups.Sort(CompareContinuityGroups);

        for (int g = 0; g < orderedGroups.Count; g++)
            AssignContinuityGroup(
                orderedGroups[g],
                g,
                planSet);

        return planSet;
    }

    private static void AssignContinuityGroup(
        List<CorridorComponent> components,
        int groupId,
        ContinuityPlanSet planSet)
    {
        if (components == null || components.Count == 0)
            return;

        components.Sort(CompareComponents);

        HashSet<string> unique =
            new(StringComparer.Ordinal);
        for (int c = 0; c < components.Count; c++)
        {
            List<string> ids =
                components[c].RouteIds;
            for (int r = 0; r < ids.Count; r++)
                unique.Add(ids[r]);
        }

        if (unique.Count < 2)
            return;

        // One slot table is authoritative for the whole bundle. Build it incrementally from the
        // strongest shared corridor and then insert branch-in routes around already-established
        // neighbours. Existing routes are never permuted when another corridor is visited.
        //
        // This is the key invariant for metro-style readability:
        //   branch-out => vacated slots remain empty;
        //   branch-in  => new routes are inserted, existing routes never swap sides.
        List<string> slotIds =
            BuildContinuitySlotOrder(
                components,
                unique,
                planSet.PermutationConflictRoutes);

        float[] slotOffsets =
            BuildStableSlotOffsets(
                slotIds.Count);
        byte densityTier =
            DensityTierForCount(
                slotIds.Count);

        planSet.GroupDensity[groupId] =
            densityTier;
        planSet.GroupRoutes[groupId] =
            new List<string>(slotIds);

        Dictionary<string, float> slots =
            new(StringComparer.Ordinal);
        for (int i = 0; i < slotIds.Count; i++)
            slots[slotIds[i]] = slotOffsets[i];

        Dictionary<string, RouteLanePlan> lanePlans =
            planSet.Routes;

        for (int c = 0; c < components.Count; c++)
        {
            CorridorComponent component =
                components[c];

            // A corridor can be the mirrored continuation of the same bundle after a 90-degree
            // turn. Resolve that orientation once per component; doing it per segment needlessly
            // repeated the local-order/inversion scan on dense bundles.
            int orientation =
                ComponentOrientation(
                    component,
                    slotIds);

            for (int s = 0;
                 s < component.Segments.Count;
                 s++)
            {
                SegmentRef segment =
                    component.Segments[s];

                if (!slots.TryGetValue(
                        segment.RouteId,
                        out float offset))
                    continue;

                offset *= orientation;

                if (!lanePlans.TryGetValue(
                        segment.RouteId,
                        out RouteLanePlan plan))
                {
                    Num.Vector2[] points =
                        BasePoints(segment.Route);
                    plan =
                        new RouteLanePlan(
                            Math.Max(
                                0,
                                (points?.Length ?? 0) - 1));
                    lanePlans.Add(
                        segment.RouteId,
                        plan);
                }

                if (densityTier > plan.DensityTier)
                    plan.DensityTier = densityTier;

                if ((uint)segment.SegmentIndex <
                    (uint)plan.Offsets.Length)
                {
                    plan.Offsets[segment.SegmentIndex] =
                        offset;
                    plan.Assigned[segment.SegmentIndex] =
                        true;
                    plan.GroupIds[segment.SegmentIndex] =
                        groupId;
                }
            }
        }
    }

    private static List<string> BuildContinuitySlotOrder(
        List<CorridorComponent> components,
        HashSet<string> unique,
        HashSet<string> conflictRoutes)
    {
        List<string> slots =
            new();

        if (components == null ||
            components.Count == 0 ||
            unique == null ||
            unique.Count == 0)
            return slots;

        List<CorridorComponent> remaining =
            new(components);

        CorridorComponent root =
            ChooseContinuityRoot(
                remaining);

        if (root != null)
        {
            List<string> rootOrder =
                LocalComponentOrder(
                    root);

            for (int i = 0; i < rootOrder.Count; i++)
            {
                if (unique.Contains(rootOrder[i]) &&
                    !slots.Contains(rootOrder[i]))
                {
                    slots.Add(rootOrder[i]);
                }
            }

            remaining.Remove(root);
        }

        while (remaining.Count > 0)
        {
            int nextIndex =
                ChooseNextContinuityComponent(
                    remaining,
                    slots);

            CorridorComponent component =
                remaining[nextIndex];
            remaining.RemoveAt(nextIndex);

            MergeComponentOrder(
                slots,
                LocalComponentOrder(component),
                conflictRoutes);
        }

        // Defensive completion for routes that were members of the continuity group but happened
        // not to survive a local component's segment filtering. Deterministic append is preferable
        // to silently losing a slot identity.
        List<string> missing =
            new();

        foreach (string id in unique)
        {
            if (!slots.Contains(id))
                missing.Add(id);
        }

        missing.Sort(StringComparer.Ordinal);
        slots.AddRange(missing);
        return slots;
    }

    private static CorridorComponent ChooseContinuityRoot(
        List<CorridorComponent> components)
    {
        CorridorComponent best =
            null;
        int bestRouteCount =
            -1;
        int bestConflictScore =
            int.MaxValue;
        float bestSpan =
            -1f;

        for (int i = 0; i < components.Count; i++)
        {
            CorridorComponent candidate =
                components[i];

            if (candidate == null)
                continue;

            int routeCount =
                candidate.RouteIds.Count;
            int conflictScore =
                ContinuityRootConflictScore(
                    candidate,
                    components);
            float span =
                Math.Max(
                    0f,
                    candidate.Max -
                    candidate.Min);

            // Coverage remains the primary criterion: the root should establish as many stable
            // slots as possible. Among equally strong roots, prefer the one whose geometric order
            // already agrees with the largest number of neighbouring corridors (allowing mirrors).
            bool better =
                best == null ||
                routeCount > bestRouteCount ||
                routeCount == bestRouteCount &&
                conflictScore < bestConflictScore ||
                routeCount == bestRouteCount &&
                conflictScore == bestConflictScore &&
                span > bestSpan ||
                routeCount == bestRouteCount &&
                conflictScore == bestConflictScore &&
                Math.Abs(span - bestSpan) < 0.001f &&
                CompareComponents(
                    candidate,
                    best) < 0;

            if (!better)
                continue;

            best =
                candidate;
            bestRouteCount =
                routeCount;
            bestConflictScore =
                conflictScore;
            bestSpan =
                span;
        }

        return best;
    }

    private static int ContinuityRootConflictScore(
        CorridorComponent root,
        List<CorridorComponent> components)
    {
        if (root == null ||
            components == null)
            return int.MaxValue;

        List<string> rootOrder =
            LocalComponentOrder(
                root);
        if (rootOrder.Count < 2)
            return 0;

        Dictionary<string, int> positions =
            new(StringComparer.Ordinal);
        for (int i = 0; i < rootOrder.Count; i++)
            positions[rootOrder[i]] = i;

        int score = 0;

        for (int c = 0; c < components.Count; c++)
        {
            CorridorComponent component =
                components[c];

            if (component == null ||
                ReferenceEquals(
                    component,
                    root))
                continue;

            List<string> local =
                LocalComponentOrder(
                    component);

            int known = 0;
            for (int i = 0; i < local.Count; i++)
            {
                if (positions.ContainsKey(
                        local[i]))
                    known++;
            }

            if (known < 2)
                continue;

            int forward =
                CountKnownInversions(
                    local,
                    positions,
                    reverse: false);
            int reversed =
                CountKnownInversions(
                    local,
                    positions,
                    reverse: true);

            score +=
                Math.Min(
                    forward,
                    reversed);
        }

        return score;
    }

    private static int ChooseNextContinuityComponent(
        List<CorridorComponent> remaining,
        List<string> slots)
    {
        HashSet<string> known =
            new(
                slots,
                StringComparer.Ordinal);

        int bestIndex = 0;
        int bestOverlap = -1;
        int bestConflictScore = int.MaxValue;
        int bestRoutes = -1;
        float bestSpan = -1f;

        for (int i = 0; i < remaining.Count; i++)
        {
            CorridorComponent component =
                remaining[i];

            int overlap = 0;
            for (int r = 0; r < component.RouteIds.Count; r++)
            {
                if (known.Contains(component.RouteIds[r]))
                    overlap++;
            }

            int conflictScore =
                ComponentMergeConflictScore(
                    component,
                    slots);

            int routeCount =
                component.RouteIds.Count;
            float span =
                Math.Max(
                    0f,
                    component.Max -
                    component.Min);

            // Grow the slot table from the most strongly connected corridor first. For equal
            // overlap, prefer the component that needs the fewest true permutations after allowing
            // a whole-corridor mirror; this avoids creating a conflict merely because merge order
            // happened to visit a harder branch first.
            bool better =
                overlap > bestOverlap ||
                overlap == bestOverlap &&
                conflictScore < bestConflictScore ||
                overlap == bestOverlap &&
                conflictScore == bestConflictScore &&
                routeCount > bestRoutes ||
                overlap == bestOverlap &&
                conflictScore == bestConflictScore &&
                routeCount == bestRoutes &&
                span > bestSpan ||
                overlap == bestOverlap &&
                conflictScore == bestConflictScore &&
                routeCount == bestRoutes &&
                Math.Abs(span - bestSpan) < 0.001f &&
                CompareComponents(
                    component,
                    remaining[bestIndex]) < 0;

            if (!better)
                continue;

            bestIndex = i;
            bestOverlap = overlap;
            bestConflictScore =
                conflictScore;
            bestRoutes = routeCount;
            bestSpan = span;
        }

        return bestIndex;
    }

    private static int ComponentMergeConflictScore(
        CorridorComponent component,
        List<string> slots)
    {
        if (component == null ||
            slots == null ||
            slots.Count < 2)
            return 0;

        Dictionary<string, int> positions =
            new(StringComparer.Ordinal);

        for (int i = 0; i < slots.Count; i++)
            positions[slots[i]] = i;

        List<string> local =
            LocalComponentOrder(
                component);

        int forward =
            CountKnownInversions(
                local,
                positions,
                reverse: false);
        int reversed =
            CountKnownInversions(
                local,
                positions,
                reverse: true);

        return Math.Min(
            forward,
            reversed);
    }

    private static void MergeComponentOrder(
        List<string> slots,
        List<string> localOrder,
        HashSet<string> conflictRoutes)
    {
        if (slots == null ||
            localOrder == null ||
            localOrder.Count == 0)
            return;

        // A 90-degree turn can mirror the perpendicular world axis. Treat a complete reversal as a
        // valid orientation change, not as a lane permutation. Choose the orientation that produces
        // the fewest inversions against already-established slots before inserting branch routes.
        OrientLocalOrderToSlots(
            localOrder,
            slots);

        Dictionary<string, int> existingPositions =
            new(StringComparer.Ordinal);

        for (int i = 0; i < slots.Count; i++)
            existingPositions[slots[i]] = i;

        // Any inversion that remains after the optional mirror is a real permutation conflict.
        // Mark every participant in an inverted pair so the bounded alternate-corridor pass can
        // move the smallest affected set instead of destabilising the whole bundle.
        MarkPermutationConflicts(
            localOrder,
            existingPositions,
            conflictRoutes);

        // Insert only new routes. Processing in local geometric order lets a newly inserted route
        // become the predecessor of the next one in the same branch-in block, preserving that block
        // without moving any route that was already assigned a slot.
        for (int i = 0; i < localOrder.Count; i++)
        {
            string id =
                localOrder[i];

            if (slots.Contains(id))
                continue;

            string previous =
                FindPreviousPresent(
                    localOrder,
                    i,
                    slots);
            string next =
                FindNextPresent(
                    localOrder,
                    i,
                    slots);

            int insertIndex;

            if (!string.IsNullOrEmpty(previous) &&
                !string.IsNullOrEmpty(next))
            {
                int previousIndex =
                    slots.IndexOf(previous);
                int nextIndex =
                    slots.IndexOf(next);

                if (previousIndex < nextIndex)
                {
                    insertIndex =
                        previousIndex + 1;
                }
                else
                {
                    // A true local permutation remains after mirror normalisation. Preserve global
                    // continuity and place the new route beside its preceding anchor while the
                    // involved routes are queued for a congestion-aware alternate corridor.
                    conflictRoutes?.Add(id);
                    conflictRoutes?.Add(previous);
                    conflictRoutes?.Add(next);
                    insertIndex =
                        Math.Min(
                            slots.Count,
                            previousIndex + 1);
                }
            }
            else if (!string.IsNullOrEmpty(previous))
            {
                insertIndex =
                    slots.IndexOf(previous) + 1;
            }
            else if (!string.IsNullOrEmpty(next))
            {
                insertIndex =
                    slots.IndexOf(next);
            }
            else
            {
                insertIndex =
                    slots.Count;
            }

            insertIndex =
                Math.Max(
                    0,
                    Math.Min(
                        slots.Count,
                        insertIndex));

            slots.Insert(
                insertIndex,
                id);
        }
    }

    private static void OrientLocalOrderToSlots(
        List<string> localOrder,
        List<string> slots)
    {
        if (localOrder == null ||
            localOrder.Count < 2 ||
            slots == null ||
            slots.Count < 2)
            return;

        Dictionary<string, int> positions =
            new(StringComparer.Ordinal);

        for (int i = 0; i < slots.Count; i++)
            positions[slots[i]] = i;

        int forward =
            CountKnownInversions(
                localOrder,
                positions,
                reverse: false);
        int reversed =
            CountKnownInversions(
                localOrder,
                positions,
                reverse: true);

        if (reversed < forward)
            localOrder.Reverse();
    }

    private static int CountKnownInversions(
        List<string> order,
        Dictionary<string, int> positions,
        bool reverse)
    {
        if (order == null ||
            positions == null)
            return 0;

        List<int> known =
            new();

        if (!reverse)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (positions.TryGetValue(
                        order[i],
                        out int position))
                    known.Add(position);
            }
        }
        else
        {
            for (int i = order.Count - 1; i >= 0; i--)
            {
                if (positions.TryGetValue(
                        order[i],
                        out int position))
                    known.Add(position);
            }
        }

        int inversions = 0;
        for (int i = 0; i < known.Count; i++)
        {
            for (int j = i + 1; j < known.Count; j++)
            {
                if (known[i] > known[j])
                    inversions++;
            }
        }

        return inversions;
    }

    private static void MarkPermutationConflicts(
        List<string> localOrder,
        Dictionary<string, int> positions,
        HashSet<string> conflictRoutes)
    {
        if (localOrder == null ||
            positions == null ||
            conflictRoutes == null)
            return;

        for (int i = 0; i < localOrder.Count; i++)
        {
            string left =
                localOrder[i];

            if (!positions.TryGetValue(
                    left,
                    out int leftPosition))
                continue;

            for (int j = i + 1; j < localOrder.Count; j++)
            {
                string right =
                    localOrder[j];

                if (!positions.TryGetValue(
                        right,
                        out int rightPosition) ||
                    leftPosition <= rightPosition)
                    continue;

                conflictRoutes.Add(left);
                conflictRoutes.Add(right);
            }
        }
    }

    private static int ComponentOrientation(
        CorridorComponent component,
        List<string> globalSlots)
    {
        if (component == null ||
            globalSlots == null ||
            globalSlots.Count < 2)
            return 1;

        List<string> local =
            LocalComponentOrder(
                component);

        Dictionary<string, int> positions =
            new(StringComparer.Ordinal);

        for (int i = 0; i < globalSlots.Count; i++)
            positions[globalSlots[i]] = i;

        int forward =
            CountKnownInversions(
                local,
                positions,
                reverse: false);
        int reversed =
            CountKnownInversions(
                local,
                positions,
                reverse: true);

        return reversed < forward
            ? -1
            : 1;
    }

    private static string FindPreviousPresent(
        List<string> localOrder,
        int index,
        List<string> slots)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (slots.Contains(localOrder[i]))
                return localOrder[i];
        }

        return null;
    }

    private static string FindNextPresent(
        List<string> localOrder,
        int index,
        List<string> slots)
    {
        for (int i = index + 1; i < localOrder.Count; i++)
        {
            if (slots.Contains(localOrder[i]))
                return localOrder[i];
        }

        return null;
    }

    private static List<string> LocalComponentOrder(
        CorridorComponent component)
    {
        List<string> order =
            component == null
                ? new List<string>()
                : new List<string>(
                    component.RouteIds);

        order.Sort(
            (left, right) =>
            {
                float leftOrder =
                    ComponentLaneOrder(
                        left,
                        component);
                float rightOrder =
                    ComponentLaneOrder(
                        right,
                        component);

                int geometry =
                    leftOrder.CompareTo(
                        rightOrder);

                if (geometry != 0)
                    return geometry;

                return string.CompareOrdinal(
                    left,
                    right);
            });

        return order;
    }

    private static float ComponentLaneOrder(
        string routeId,
        CorridorComponent component)
    {
        if (string.IsNullOrEmpty(routeId) ||
            component == null)
            return 0f;

        float total = 0f;
        int count = 0;

        for (int s = 0; s < component.Segments.Count; s++)
        {
            SegmentRef segment =
                component.Segments[s];

            if (!string.Equals(
                    segment.RouteId,
                    routeId,
                    StringComparison.Ordinal))
                continue;

            Num.Vector2[] points =
                BasePoints(
                    segment.Route);
            int i =
                segment.SegmentIndex;

            if (points == null ||
                i < 0 ||
                i + 1 >= points.Length)
                continue;

            float corridorCoordinate =
                segment.Coordinate;
            bool foundBranch =
                false;

            if (i > 0)
            {
                float value =
                    segment.Vertical
                        ? points[i - 1].X
                        : points[i - 1].Y;

                if (Math.Abs(
                        value -
                        corridorCoordinate) > 0.01f)
                {
                    total += value;
                    count++;
                    foundBranch = true;
                }
            }

            if (i + 2 < points.Length)
            {
                float value =
                    segment.Vertical
                        ? points[i + 2].X
                        : points[i + 2].Y;

                if (Math.Abs(
                        value -
                        corridorCoordinate) > 0.01f)
                {
                    total += value;
                    count++;
                    foundBranch = true;
                }
            }

            if (!foundBranch)
            {
                total +=
                    corridorCoordinate;
                count++;
            }
        }

        return count > 0
            ? total / count
            : component.Coordinate;
    }

    private static Dictionary<int, float> ResolveGroupScales(
        Dictionary<string, ConnectionRouteResource> routes,
        ContinuityPlanSet planSet,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles)
    {
        Dictionary<int, float> scales =
            new();

        if (planSet == null ||
            planSet.GroupRoutes.Count == 0)
            return scales;

        List<int> groupIds =
            new(planSet.GroupRoutes.Keys);
        groupIds.Sort();

        for (int i = 0; i < groupIds.Count; i++)
            scales[groupIds[i]] = 1f;

        // Resolve one scale for the entire bundle. Two neighbouring routes can never choose
        // different 82%/68% factors and therefore cannot reverse their lane order.
        for (int i = 0; i < groupIds.Count; i++)
        {
            int groupId =
                groupIds[i];

            planSet.GroupDensity.TryGetValue(
                groupId,
                out byte densityTier);

            float[] candidates =
                densityTier > 0
                    ? DenseGroupScales
                    : NormalGroupScales;

            float chosen = 0f;
            bool found = false;

            for (int c = 0;
                 c < candidates.Length;
                 c++)
            {
                float candidateScale =
                    candidates[c];

                if (!GroupRoutesClear(
                        groupId,
                        candidateScale,
                        routes,
                        planSet,
                        scales,
                        obstacles))
                    continue;

                chosen =
                    candidateScale;
                found = true;
                break;
            }

            scales[groupId] =
                found ? chosen : 0f;
        }

        // Cross-bundle transition geometry can make a route invalid only after several independent
        // bundle scales have been chosen. Collapse every bundle touched by such a route together,
        // then recheck until no new group changes are required. This is conservative but preserves
        // the "same bundle = same scale" invariant.
        for (int pass = 0;
             pass <= groupIds.Count;
             pass++)
        {
            bool changed = false;

            foreach (KeyValuePair<string, RouteLanePlan> pair
                     in planSet.Routes)
            {
                if (!routes.TryGetValue(
                        pair.Key,
                        out ConnectionRouteResource route))
                    continue;

                Num.Vector2[] basePoints =
                    BasePoints(route);
                if (basePoints == null ||
                    basePoints.Length < 2)
                    continue;

                float[] effective =
                    BuildEffectiveOffsets(
                        pair.Value,
                        scales);
                Num.Vector2[] candidate =
                    BuildLanePath(
                        basePoints,
                        effective);

                if (IsDerivedRouteClear(
                        candidate,
                        route,
                        obstacles))
                    continue;

                HashSet<int> touched =
                    CollectGroups(
                        pair.Value);

                foreach (int groupId in touched)
                {
                    if (scales.TryGetValue(
                            groupId,
                            out float current) &&
                        current > 0f)
                    {
                        scales[groupId] = 0f;
                        changed = true;
                    }
                }
            }

            if (!changed)
                break;
        }

        return scales;
    }

    private static bool GroupRoutesClear(
        int groupId,
        float candidateScale,
        Dictionary<string, ConnectionRouteResource> routes,
        ContinuityPlanSet planSet,
        Dictionary<int, float> scales,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles)
    {
        if (!planSet.GroupRoutes.TryGetValue(
                groupId,
                out List<string> routeIds))
            return true;

        for (int i = 0;
             i < routeIds.Count;
             i++)
        {
            string routeId =
                routeIds[i];

            if (!routes.TryGetValue(
                    routeId,
                    out ConnectionRouteResource route) ||
                !planSet.Routes.TryGetValue(
                    routeId,
                    out RouteLanePlan plan))
                continue;

            Num.Vector2[] basePoints =
                BasePoints(route);
            if (basePoints == null ||
                basePoints.Length < 2)
                continue;

            float[] effective =
                BuildEffectiveOffsets(
                    plan,
                    scales,
                    groupId,
                    candidateScale);

            Num.Vector2[] candidate =
                BuildLanePath(
                    basePoints,
                    effective);

            if (!IsDerivedRouteClear(
                    candidate,
                    route,
                    obstacles))
                return false;
        }

        return true;
    }

    private static void AddTouchedGroupRoutes(
        RouteLanePlan plan,
        ContinuityPlanSet planSet,
        HashSet<string> output)
    {
        if (plan == null ||
            planSet == null ||
            output == null)
            return;

        HashSet<int> groups =
            new();

        for (int i = 0; i < plan.GroupIds.Length; i++)
        {
            int groupId =
                plan.GroupIds[i];
            if (groupId >= 0)
                groups.Add(groupId);
        }

        foreach (int groupId in groups)
        {
            if (!planSet.GroupRoutes.TryGetValue(
                    groupId,
                    out List<string> routeIds) ||
                routeIds == null)
                continue;

            for (int i = 0; i < routeIds.Count; i++)
                output.Add(routeIds[i]);
        }
    }

    private static byte CompressionDensityTier(
        RouteLanePlan plan,
        Dictionary<int, float> scales)
    {
        if (plan == null ||
            scales == null ||
            plan.GroupIds.Length == 0)
            return 0;

        float minimumScale =
            1f;
        bool found =
            false;

        for (int i = 0; i < plan.GroupIds.Length; i++)
        {
            int groupId =
                plan.GroupIds[i];
            if (groupId < 0 ||
                !scales.TryGetValue(
                    groupId,
                    out float scale))
                continue;

            found = true;
            minimumScale =
                Math.Min(
                    minimumScale,
                    scale);
        }

        if (!found)
            return 0;

        if (minimumScale < 0.36f)
            return 2;

        if (minimumScale < 0.66f)
            return 1;

        return 0;
    }

    private static float[] BuildEffectiveOffsets(
        RouteLanePlan plan,
        Dictionary<int, float> scales,
        int overrideGroupId = -1,
        float overrideScale = 1f)
    {
        if (plan == null ||
            plan.Offsets.Length == 0)
            return Array.Empty<float>();

        bool needsCopy = false;

        for (int i = 0;
             i < plan.GroupIds.Length;
             i++)
        {
            int groupId =
                plan.GroupIds[i];
            if (groupId < 0)
                continue;

            float scale =
                groupId == overrideGroupId
                    ? overrideScale
                    : scales != null &&
                      scales.TryGetValue(
                          groupId,
                          out float resolved)
                        ? resolved
                        : 1f;

            if (Math.Abs(scale - 1f) > 0.0001f)
            {
                needsCopy = true;
                break;
            }
        }

        if (!needsCopy)
            return plan.Offsets;

        float[] effective =
            (float[])plan.Offsets.Clone();

        for (int i = 0;
             i < effective.Length;
             i++)
        {
            int groupId =
                plan.GroupIds[i];
            if (groupId < 0)
                continue;

            float scale =
                groupId == overrideGroupId
                    ? overrideScale
                    : scales != null &&
                      scales.TryGetValue(
                          groupId,
                          out float resolved)
                        ? resolved
                        : 1f;

            effective[i] *= scale;
        }

        return effective;
    }

    private static HashSet<int> CollectGroups(
        RouteLanePlan plan)
    {
        HashSet<int> groups =
            new();

        if (plan == null)
            return groups;

        for (int i = 0;
             i < plan.GroupIds.Length;
             i++)
        {
            int groupId =
                plan.GroupIds[i];
            if (groupId >= 0)
                groups.Add(groupId);
        }

        return groups;
    }

    private static bool IsDerivedRouteClear(
        Num.Vector2[] points,
        ConnectionRouteResource route,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles)
    {
        return points != null &&
               points.Length >= 2 &&
               route != null &&
               WorldMapOrthogonalRouter.IsDerivedRouteClear(
                   points,
                   route.FromRoomIndex,
                   route.ToRoomIndex,
                   obstacles);
    }

    private static Num.Vector2[] BuildLanePath(
        Num.Vector2[] source,
        float[] offsets)
    {
        if (source == null ||
            source.Length < 2 ||
            offsets == null ||
            offsets.Length != source.Length - 1)
            return source == null
                ? Array.Empty<Num.Vector2>()
                : (Num.Vector2[])source.Clone();

        Num.Vector2[] result =
            new Num.Vector2[source.Length];
        result[0] = source[0];
        result[result.Length - 1] =
            source[source.Length - 1];

        for (int point = 1;
             point < source.Length - 1;
             point++)
        {
            Num.Vector2 previousA =
                source[point - 1];
            Num.Vector2 previousB =
                source[point];
            Num.Vector2 nextA =
                source[point];
            Num.Vector2 nextB =
                source[point + 1];

            bool previousVertical =
                Math.Abs(
                    previousA.X -
                    previousB.X) < 0.01f;
            bool nextVertical =
                Math.Abs(
                    nextA.X -
                    nextB.X) < 0.01f;

            float previousOffset =
                offsets[point - 1];
            float nextOffset =
                offsets[point];

            if (previousVertical != nextVertical)
            {
                // At a 90 deg bundle turn, the same global slot maps from horizontal Y offset to
                // vertical X offset. Their intersection is the explicit continuous lane corner.
                float x =
                    previousVertical
                        ? previousB.X + previousOffset
                        : nextA.X + nextOffset;
                float y =
                    previousVertical
                        ? nextA.Y + nextOffset
                        : previousB.Y + previousOffset;

                result[point] =
                    new Num.Vector2(x, y);
            }
            else if (previousVertical)
            {
                result[point] =
                    new Num.Vector2(
                        source[point].X +
                        (previousOffset + nextOffset) *
                        0.5f,
                        source[point].Y);
            }
            else
            {
                result[point] =
                    new Num.Vector2(
                        source[point].X,
                        source[point].Y +
                        (previousOffset + nextOffset) *
                        0.5f);
            }
        }

        return Simplify(result);
    }

    private static Num.Vector2[] Simplify(
        Num.Vector2[] source)
    {
        if (source == null || source.Length < 3)
            return source ??
                   Array.Empty<Num.Vector2>();

        List<Num.Vector2> points =
            new(source.Length);

        for (int i = 0; i < source.Length; i++)
        {
            Num.Vector2 point = source[i];
            if (points.Count > 0 &&
                Num.Vector2.DistanceSquared(
                    points[points.Count - 1],
                    point) < PointEpsilonSquared)
                continue;

            if (points.Count >= 2)
            {
                Num.Vector2 a =
                    points[points.Count - 2];
                Num.Vector2 b =
                    points[points.Count - 1];

                bool sameX =
                    Math.Abs(a.X - b.X) < 0.01f &&
                    Math.Abs(b.X - point.X) < 0.01f;
                bool sameY =
                    Math.Abs(a.Y - b.Y) < 0.01f &&
                    Math.Abs(b.Y - point.Y) < 0.01f;

                if (sameX || sameY)
                {
                    points[points.Count - 1] =
                        point;
                    continue;
                }
            }

            points.Add(point);
        }

        return points.ToArray();
    }

    private static bool PathsEquivalent(
        Num.Vector2[] a,
        Num.Vector2[] b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null ||
            a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (Num.Vector2.DistanceSquared(
                    a[i],
                    b[i]) >
                PointEpsilonSquared)
                return false;
        }

        return true;
    }

    private static Num.Vector2[] BasePoints(
        ConnectionRouteResource route)
    {
        if (route?.BasePoints != null &&
            route.BasePoints.Length >= 2)
            return route.BasePoints;

        return route?.Points;
    }

    private static int CompareSegments(
        SegmentRef a,
        SegmentRef b)
    {
        int route =
            string.CompareOrdinal(
                a?.RouteId,
                b?.RouteId);
        if (route != 0) return route;

        int segment =
            (a?.SegmentIndex ?? -1)
                .CompareTo(
                    b?.SegmentIndex ?? -1);
        if (segment != 0) return segment;

        return (a?.Coordinate ?? 0f)
            .CompareTo(
                b?.Coordinate ?? 0f);
    }

    private static int CompareComponents(
        CorridorComponent a,
        CorridorComponent b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int axis =
            a.Vertical.CompareTo(b.Vertical);
        if (axis != 0) return axis;

        int coordinate =
            a.Coordinate.CompareTo(
                b.Coordinate);
        if (coordinate != 0) return coordinate;

        int min =
            a.Min.CompareTo(b.Min);
        if (min != 0) return min;

        int max =
            a.Max.CompareTo(b.Max);
        if (max != 0) return max;

        string aRoute =
            a.RouteIds.Count > 0
                ? a.RouteIds[0]
                : string.Empty;
        string bRoute =
            b.RouteIds.Count > 0
                ? b.RouteIds[0]
                : string.Empty;
        return string.CompareOrdinal(
            aRoute,
            bRoute);
    }

    private static int CompareContinuityGroups(
        List<CorridorComponent> a,
        List<CorridorComponent> b)
    {
        CorridorComponent aa =
            a != null && a.Count > 0
                ? a[0]
                : null;
        CorridorComponent bb =
            b != null && b.Count > 0
                ? b[0]
                : null;
        return CompareComponents(aa, bb);
    }

    private static float AverageCoordinate(
        List<SegmentRef> segments)
    {
        if (segments == null || segments.Count == 0)
            return 0f;

        double total = 0d;
        for (int i = 0; i < segments.Count; i++)
            total += segments[i].Coordinate;

        return (float)(
            total /
            segments.Count);
    }

    private static float[] BuildStableSlotOffsets(int count)
    {
        float[] offsets =
            new float[Math.Max(0, count)];
        if (count <= 1)
            return offsets;

        if (count <= DenseBankThreshold)
        {
            float spacing =
                AdaptiveSpacing(count);
            float center =
                (count - 1) * 0.5f;

            for (int i = 0; i < count; i++)
                offsets[i] = (i - center) * spacing;

            return offsets;
        }

        // Do not compress an arbitrary number of routes into one visually uniform wall. Stable
        // ordinal slots are divided into banks of eight with a small additional gutter between
        // banks. The gap is presentation structure, not a new route/topology relationship.
        float cursor = 0f;
        offsets[0] = 0f;

        for (int i = 1; i < count; i++)
        {
            cursor += DenseLaneSpacing;
            if (i % DenseBankCapacity == 0)
                cursor += DenseBankGutter;

            offsets[i] = cursor;
        }

        float midpoint =
            (offsets[0] +
             offsets[count - 1]) *
            0.5f;
        for (int i = 0; i < count; i++)
            offsets[i] -= midpoint;

        return offsets;
    }

    private static byte DensityTierForCount(int count)
    {
        if (count >= 24)
            return 2;
        if (count > DenseBankThreshold)
            return 1;
        return 0;
    }

    private static float AdaptiveSpacing(int count)
    {
        if (count <= 1)
            return 0f;

        float bounded =
            TargetLaneSpan /
            Math.Max(
                1,
                count - 1);
        return Math.Max(
            MinimumLaneSpacing,
            Math.Min(
                PreferredLaneSpacing,
                bounded));
    }

    private static int Find(
        int[] parent,
        int value)
    {
        int root = value;
        while (parent[root] != root)
            root = parent[root];

        while (parent[value] != value)
        {
            int next = parent[value];
            parent[value] = root;
            value = next;
        }

        return root;
    }

    private static void Union(
        int[] parent,
        int a,
        int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
            parent[rootB] = rootA;
    }

    private static long PairKey(int a, int b)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        return ((long)(uint)min << 32) |
               (uint)max;
    }

    private static void DecodePairKey(
        long key,
        out int a,
        out int b)
    {
        a =
            unchecked(
                (int)(uint)(key >> 32));
        b =
            unchecked(
                (int)(uint)key);
    }
}
