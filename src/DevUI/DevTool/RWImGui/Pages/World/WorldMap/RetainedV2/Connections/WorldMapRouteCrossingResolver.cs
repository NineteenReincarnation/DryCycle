using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal readonly struct WorldMapCrossingMark
{
    internal WorldMapCrossingMark(
        string overRouteId,
        string underRouteId,
        Num.Vector2 point,
        Num.Vector2 tangent)
    {
        OverRouteId = overRouteId ?? string.Empty;
        UnderRouteId = underRouteId ?? string.Empty;
        Point = point;
        Tangent = tangent;
    }

    internal string OverRouteId { get; }
    internal string UnderRouteId { get; }
    internal Num.Vector2 Point { get; }
    internal Num.Vector2 Tangent { get; }
}

/// <summary>
/// Detects semantic route crossings from the final retained route geometry. Detection is spatially
/// bucketed and runs only after route/corridor layout changes. It never participates in pan/zoom.
/// </summary>
internal static class WorldMapRouteCrossingResolver
{
    private sealed class SegmentRef
    {
        internal int Id;
        internal string RouteId = string.Empty;
        internal Num.Vector2[] RoutePoints;
        internal int SegmentIndex;
        internal bool Vertical;
        internal float Fixed;
        internal float Min;
        internal float Max;
    }

    private sealed class CellBucket
    {
        internal readonly List<SegmentRef> Verticals = new();
        internal readonly List<SegmentRef> Horizontals = new();
    }

    private const float CellSize = 96f;
    private const float AxisEpsilon = 0.01f;
    private const float BridgeShoulder = 13f;
    private const float RouteEndpointClearance = 30f;

    internal static WorldMapCrossingMark[] Build(
        IReadOnlyDictionary<string, ConnectionRouteResource> routes)
    {
        if (routes == null || routes.Count < 2)
            return Array.Empty<WorldMapCrossingMark>();

        List<string> routeIds = new(routes.Keys);
        routeIds.Sort(StringComparer.Ordinal);

        Dictionary<long, CellBucket> cells = new();
        int segmentId = 0;

        for (int r = 0; r < routeIds.Count; r++)
        {
            string routeId = routeIds[r];
            if (!routes.TryGetValue(routeId, out ConnectionRouteResource route))
                continue;

            Num.Vector2[] points = route?.Points;
            if (points == null || points.Length < 6)
                continue;

            // Protect the two terminal segments on each side. Phase 1 owns those and a "bridge"
            // beside a room socket is more confusing than a plain crossing.
            int firstEligible = 2;
            int lastEligible = points.Length - 4;
            if (lastEligible < firstEligible)
                continue;

            for (int i = firstEligible; i <= lastEligible; i++)
            {
                Num.Vector2 a = points[i];
                Num.Vector2 b = points[i + 1];

                bool vertical = Math.Abs(a.X - b.X) < AxisEpsilon;
                bool horizontal = Math.Abs(a.Y - b.Y) < AxisEpsilon;
                if (!vertical && !horizontal)
                    continue;

                float min =
                    vertical ? Math.Min(a.Y, b.Y) : Math.Min(a.X, b.X);
                float max =
                    vertical ? Math.Max(a.Y, b.Y) : Math.Max(a.X, b.X);
                if (max - min <= BridgeShoulder * 2f)
                    continue;

                SegmentRef segment = new()
                {
                    Id = segmentId++,
                    RouteId = routeId,
                    RoutePoints = points,
                    SegmentIndex = i,
                    Vertical = vertical,
                    Fixed = vertical ? a.X : a.Y,
                    Min = min,
                    Max = max
                };

                AddToCells(cells, segment);
            }
        }

        if (cells.Count == 0)
            return Array.Empty<WorldMapCrossingMark>();

        HashSet<long> checkedPairs = new();
        List<WorldMapCrossingMark> marks = new();

        foreach (CellBucket cell in cells.Values)
        {
            for (int v = 0; v < cell.Verticals.Count; v++)
            {
                SegmentRef vertical = cell.Verticals[v];
                for (int h = 0; h < cell.Horizontals.Count; h++)
                {
                    SegmentRef horizontal = cell.Horizontals[h];

                    if (string.Equals(
                            vertical.RouteId,
                            horizontal.RouteId,
                            StringComparison.Ordinal))
                        continue;

                    long pairKey = PairKey(vertical.Id, horizontal.Id);
                    if (!checkedPairs.Add(pairKey))
                        continue;

                    if (!TryCross(
                            vertical,
                            horizontal,
                            out Num.Vector2 point))
                        continue;

                    SegmentRef over;
                    SegmentRef under;
                    if (string.CompareOrdinal(
                            vertical.RouteId,
                            horizontal.RouteId) >= 0)
                    {
                        over = vertical;
                        under = horizontal;
                    }
                    else
                    {
                        over = horizontal;
                        under = vertical;
                    }

                    // Canonical tangent keeps the bridge bow stable even if connection direction is
                    // reversed. Direction is communicated by arrows, not by which side the hump uses.
                    Num.Vector2 tangent =
                        over.Vertical
                            ? new Num.Vector2(0f, 1f)
                            : new Num.Vector2(1f, 0f);

                    marks.Add(
                        new WorldMapCrossingMark(
                            over.RouteId,
                            under.RouteId,
                            point,
                            tangent));
                }
            }
        }

        if (marks.Count == 0)
            return Array.Empty<WorldMapCrossingMark>();

        marks.Sort(CompareMarks);
        return marks.ToArray();
    }

    private static void AddToCells(
        Dictionary<long, CellBucket> cells,
        SegmentRef segment)
    {
        int fixedCell = FloorCell(segment.Fixed);
        int minCell = FloorCell(segment.Min);
        int maxCell = FloorCell(segment.Max);

        for (int axisCell = minCell; axisCell <= maxCell; axisCell++)
        {
            int x = segment.Vertical ? fixedCell : axisCell;
            int y = segment.Vertical ? axisCell : fixedCell;
            long key = CellKey(x, y);

            if (!cells.TryGetValue(key, out CellBucket cell))
            {
                cell = new CellBucket();
                cells.Add(key, cell);
            }

            if (segment.Vertical)
                cell.Verticals.Add(segment);
            else
                cell.Horizontals.Add(segment);
        }
    }

    private static bool TryCross(
        SegmentRef vertical,
        SegmentRef horizontal,
        out Num.Vector2 point)
    {
        point =
            new Num.Vector2(
                vertical.Fixed,
                horizontal.Fixed);

        if (point.Y <= vertical.Min + BridgeShoulder ||
            point.Y >= vertical.Max - BridgeShoulder ||
            point.X <= horizontal.Min + BridgeShoulder ||
            point.X >= horizontal.Max - BridgeShoulder)
            return false;

        float endpointClearanceSquared =
            RouteEndpointClearance * RouteEndpointClearance;

        if (NearRouteEndpoint(
                vertical.RoutePoints,
                point,
                endpointClearanceSquared) ||
            NearRouteEndpoint(
                horizontal.RoutePoints,
                point,
                endpointClearanceSquared))
            return false;

        return true;
    }

    private static bool NearRouteEndpoint(
        Num.Vector2[] points,
        Num.Vector2 point,
        float clearanceSquared)
    {
        if (points == null || points.Length < 2)
            return true;

        return Num.Vector2.DistanceSquared(
                   point,
                   points[0]) < clearanceSquared ||
               Num.Vector2.DistanceSquared(
                   point,
                   points[points.Length - 1]) < clearanceSquared;
    }

    private static int CompareMarks(
        WorldMapCrossingMark a,
        WorldMapCrossingMark b)
    {
        int route =
            string.CompareOrdinal(
                a.OverRouteId,
                b.OverRouteId);
        if (route != 0) return route;

        int x = a.Point.X.CompareTo(b.Point.X);
        if (x != 0) return x;

        int y = a.Point.Y.CompareTo(b.Point.Y);
        if (y != 0) return y;

        return string.CompareOrdinal(
            a.UnderRouteId,
            b.UnderRouteId);
    }

    private static int FloorCell(float value) =>
        (int)Math.Floor(value / CellSize);

    private static long CellKey(int x, int y) =>
        ((long)(uint)x << 32) | (uint)y;

    private static long PairKey(int a, int b)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        return ((long)(uint)min << 32) | (uint)max;
    }
}
