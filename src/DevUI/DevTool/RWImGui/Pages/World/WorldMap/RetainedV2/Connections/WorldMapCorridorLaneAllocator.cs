using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

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
    }

    private const float CoordinateBucketSize = 4f;
    private const float MinimumSharedRun = 28f;
    private const float PreferredLaneSpacing = 10f;
    private const float MinimumLaneSpacing = 5.5f;
    private const float TargetLaneSpan = 72f;

    // Dense bundles are visually split into stable banks. The lane order never changes; a wider
    // gutter every eight lanes gives the eye a grouping landmark without endpoint codes/colors.
    private const int DenseBankThreshold = 12;
    private const int DenseBankCapacity = 8;
    private const float DenseLaneSpacing = 6.25f;
    private const float DenseBankGutter = 9f;
    private static readonly float[] DenseGroupScales =
    {
        1f,
        0.82f,
        0.68f,
        0f
    };
    private static readonly float[] NormalGroupScales =
    {
        1f,
        0f
    };

    private const float PointEpsilonSquared = 0.04f;

    internal static void Apply(
        Dictionary<string, ConnectionRouteResource> routes,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles,
        HashSet<string> changedIds,
        ref long storeRevision)
    {
        if (routes == null || routes.Count == 0)
            return;

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

            if (route?.Kind == WorldMapOrthogonalRouter.RouteKind.Compact ||
                points == null ||
                points.Length < 6)
                continue;

            // Phase 1 owns the terminal fan-out. Keep two segments untouched at both ends so
            // corridor continuity can never pull a socket back into the shared bundle.
            int firstEligible = 2;
            int lastEligible = points.Length - 4;
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
        List<CorridorComponent> components = new();
        if (buckets == null || buckets.Count == 0)
            return components;

        List<BucketKey> keys = new(buckets.Keys);
        keys.Sort(
            (a, b) =>
            {
                int axis = a.Vertical.CompareTo(b.Vertical);
                if (axis != 0) return axis;
                return a.Coordinate.CompareTo(b.Coordinate);
            });

        for (int k = 0; k < keys.Count; k++)
        {
            List<SegmentRef> bucket =
                buckets[keys[k]];
            BuildBucketComponents(
                bucket,
                components);
        }

        components.Sort(CompareComponents);
        for (int i = 0; i < components.Count; i++)
            components[i].Id = i;

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

        // Build component adjacency through routes instead of O(component²) comparison. A single
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

        // One slot table is authoritative for the whole bundle. If B/C peel off after a four-lane
        // corridor, A/D keep the outer slots instead of snapping inward to a newly centred two-lane
        // corridor. Empty slots are deliberate visual memory, not wasted state.
        List<string> slotIds = new(unique);
        slotIds.Sort(StringComparer.Ordinal);

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
                // At a 90° bundle turn, the same global slot maps from horizontal Y offset to
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
