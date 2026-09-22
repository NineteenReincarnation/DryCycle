using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Derives stable parallel lanes for overlapping middle corridors across the complete retained route
/// set. It never owns authoring state and never runs for pan/zoom; ConnectionResourceStore invokes it
/// only after route membership/base geometry changes.
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
        internal float Min;
        internal float Max;
    }

    private const float CoordinateBucketSize = 4f;
    private const float MinimumSharedRun = 28f;
    private const float PreferredLaneSpacing = 10f;
    private const float MinimumLaneSpacing = 5.5f;
    private const float TargetLaneSpan = 72f;
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

        Dictionary<BucketKey, List<SegmentRef>> buckets = new();
        Dictionary<string, float[]> offsetsByRoute =
            new(StringComparer.Ordinal);

        for (int r = 0; r < routeIds.Count; r++)
        {
            string routeId = routeIds[r];
            ConnectionRouteResource route = routes[routeId];
            Num.Vector2[] points = BasePoints(route);
            if (route?.Kind == WorldMapOrthogonalRouter.RouteKind.Compact ||
                points == null ||
                points.Length < 6)
                continue;

            // Keep two terminal segments untouched at each end. Phase 1 owns those segments and
            // guarantees that room sockets remain visually traceable into the shared routing field.
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
                        Min = min,
                        Max = max
                    });
            }
        }

        foreach (List<SegmentRef> bucket in buckets.Values)
            AssignBucket(bucket, offsetsByRoute);

        for (int i = 0; i < routeIds.Count; i++)
        {
            string routeId = routeIds[i];
            ConnectionRouteResource route = routes[routeId];
            Num.Vector2[] basePoints = BasePoints(route);
            if (basePoints == null || basePoints.Length < 2)
                continue;

            Num.Vector2[] candidate;
            if (offsetsByRoute.TryGetValue(
                    routeId,
                    out float[] offsets))
            {
                candidate =
                    BuildLanePath(
                        basePoints,
                        offsets);
                if (candidate.Length < 2 ||
                    !WorldMapOrthogonalRouter.IsDerivedRouteClear(
                        candidate,
                        route.FromRoomIndex,
                        route.ToRoomIndex,
                        obstacles))
                {
                    if (PathsEquivalent(
                            route.Points,
                            basePoints))
                        continue;

                    candidate =
                        (Num.Vector2[])basePoints.Clone();
                }
            }
            else
            {
                if (PathsEquivalent(
                        route.Points,
                        basePoints))
                    continue;

                candidate =
                    (Num.Vector2[])basePoints.Clone();
            }

            if (PathsEquivalent(route.Points, candidate))
                continue;

            route.Points = candidate;
            route.Revision =
                Math.Max(
                    1L,
                    route.Revision + 1L);
            changedIds?.Add(routeId);
            unchecked { storeRevision++; }
        }
    }

    private static void AssignBucket(
        List<SegmentRef> bucket,
        Dictionary<string, float[]> offsetsByRoute)
    {
        if (bucket == null || bucket.Count < 2)
            return;

        int count = bucket.Count;
        int[] parent = new int[count];
        for (int i = 0; i < count; i++)
            parent[i] = i;

        // Buckets are deliberately tiny (same axis + ~4 world-pixel coordinate). Pairwise union is
        // cheaper than maintaining a global interval tree and runs only when route geometry changes.
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

        Dictionary<int, List<SegmentRef>> components =
            new();
        for (int i = 0; i < count; i++)
        {
            int root = Find(parent, i);
            if (!components.TryGetValue(
                    root,
                    out List<SegmentRef> component))
            {
                component = new List<SegmentRef>();
                components.Add(root, component);
            }
            component.Add(bucket[i]);
        }

        foreach (List<SegmentRef> component
                 in components.Values)
        {
            HashSet<string> unique =
                new(StringComparer.Ordinal);
            for (int i = 0; i < component.Count; i++)
                unique.Add(component[i].RouteId);

            if (unique.Count < 2)
                continue;

            List<string> ids = new(unique);
            ids.Sort(StringComparer.Ordinal);

            float spacing =
                AdaptiveSpacing(ids.Count);
            float center =
                (ids.Count - 1) * 0.5f;

            Dictionary<string, float> routeOffsets =
                new(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                routeOffsets[ids[i]] =
                    (i - center) * spacing;
            }

            for (int i = 0; i < component.Count; i++)
            {
                SegmentRef segment =
                    component[i];
                float offset =
                    routeOffsets[segment.RouteId];

                if (!offsetsByRoute.TryGetValue(
                        segment.RouteId,
                        out float[] offsets))
                {
                    Num.Vector2[] points =
                        BasePoints(segment.Route);
                    offsets =
                        new float[
                            Math.Max(
                                0,
                                (points?.Length ?? 0) - 1)];
                    offsetsByRoute.Add(
                        segment.RouteId,
                        offsets);
                }

                if ((uint)segment.SegmentIndex <
                    (uint)offsets.Length)
                {
                    offsets[segment.SegmentIndex] =
                        offset;
                }
            }
        }
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
}
