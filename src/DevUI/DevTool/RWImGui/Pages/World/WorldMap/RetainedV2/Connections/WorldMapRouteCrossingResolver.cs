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
        Num.Vector2 tangent,
        bool dense)
    {
        OverRouteId = overRouteId ?? string.Empty;
        UnderRouteId = underRouteId ?? string.Empty;
        Point = point;
        Tangent = tangent;
        Dense = dense;
    }

    internal string OverRouteId { get; }
    internal string UnderRouteId { get; }
    internal Num.Vector2 Point { get; }
    internal Num.Vector2 Tangent { get; }
    internal bool Dense { get; }
}

internal readonly struct WorldMapCrossingResolveResult
{
    internal WorldMapCrossingResolveResult(
        WorldMapCrossingMark[] marks,
        bool budgetLimited,
        int candidateChecks)
    {
        Marks = marks ?? Array.Empty<WorldMapCrossingMark>();
        BudgetLimited = budgetLimited;
        CandidateChecks = Math.Max(0, candidateChecks);
    }

    internal WorldMapCrossingMark[] Marks { get; }
    internal bool BudgetLimited { get; }
    internal int CandidateChecks { get; }

    internal static WorldMapCrossingResolveResult Empty =>
        new(
            Array.Empty<WorldMapCrossingMark>(),
            false,
            0);
}

/// <summary>
/// Detects semantic route crossings from final retained route geometry. Phase 7 keeps dense maps
/// bounded: spatial cells own deterministic candidate budgets and dense bridge marks use a cheaper
/// presentation LOD. Stable frames/pan/zoom never execute this resolver.
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

    private const int DenseCellPairThreshold = 96;
    private const int MaxUniquePairChecksPerCell = 4096;
    private const int MaxTotalCandidateChecks = 32768;
    private const int MaxMarksPerCell = 128;
    private const int MaxDenseMarksPerOverRoutePerCell = 8;
    private const int MaxTotalMarks = 1024;

    internal static WorldMapCrossingResolveResult Build(
        IReadOnlyDictionary<string, ConnectionRouteResource> routes)
    {
        if (routes == null || routes.Count < 2)
            return WorldMapCrossingResolveResult.Empty;

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

            int firstEligible = 2;
            int lastEligible = points.Length - 4;
            if (lastEligible < firstEligible)
                continue;

            for (int i = firstEligible; i <= lastEligible; i++)
            {
                Num.Vector2 a = points[i];
                Num.Vector2 b = points[i + 1];

                bool vertical =
                    Math.Abs(a.X - b.X) < AxisEpsilon;
                bool horizontal =
                    Math.Abs(a.Y - b.Y) < AxisEpsilon;
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
            return WorldMapCrossingResolveResult.Empty;

        HashSet<long> checkedPairs = new();
        List<WorldMapCrossingMark> marks = new();
        List<WorldMapCrossingMark> localMarks = new();
        bool budgetLimited = false;
        int candidateChecks = 0;

        List<long> cellKeys = new(cells.Keys);
        cellKeys.Sort();

        for (int c = 0; c < cellKeys.Count; c++)
        {
            if (marks.Count >= MaxTotalMarks ||
                candidateChecks >= MaxTotalCandidateChecks)
            {
                budgetLimited = true;
                break;
            }

            CellBucket cell = cells[cellKeys[c]];
            long potentialPairs =
                (long)cell.Verticals.Count *
                cell.Horizontals.Count;
            if (potentialPairs <= 0)
                continue;

            bool dense =
                potentialPairs >=
                DenseCellPairThreshold;

            localMarks.Clear();
            int cellChecks = 0;
            bool stopCell = false;

            for (int v = 0;
                 v < cell.Verticals.Count &&
                 !stopCell;
                 v++)
            {
                SegmentRef vertical =
                    cell.Verticals[v];

                for (int h = 0;
                     h < cell.Horizontals.Count;
                     h++)
                {
                    SegmentRef horizontal =
                        cell.Horizontals[h];

                    if (string.Equals(
                            vertical.RouteId,
                            horizontal.RouteId,
                            StringComparison.Ordinal))
                        continue;

                    if (cellChecks >=
                            MaxUniquePairChecksPerCell ||
                        candidateChecks >=
                            MaxTotalCandidateChecks)
                    {
                        budgetLimited = true;
                        stopCell = true;
                        break;
                    }

                    long pairKey =
                        PairKey(
                            vertical.Id,
                            horizontal.Id);
                    if (!checkedPairs.Add(pairKey))
                        continue;

                    cellChecks++;
                    candidateChecks++;

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

                    Num.Vector2 tangent =
                        over.Vertical
                            ? new Num.Vector2(0f, 1f)
                            : new Num.Vector2(1f, 0f);

                    localMarks.Add(
                        new WorldMapCrossingMark(
                            over.RouteId,
                            under.RouteId,
                            point,
                            tangent,
                            dense));
                }
            }

            if (localMarks.Count == 0)
                continue;

            int remaining =
                MaxTotalMarks -
                marks.Count;
            int cellLimit =
                Math.Min(
                    MaxMarksPerCell,
                    remaining);

            if (localMarks.Count <= cellLimit)
            {
                marks.AddRange(localMarks);
                continue;
            }

            budgetLimited = true;
            AppendBudgetedCellMarks(
                localMarks,
                marks,
                cellLimit);
        }

        if (marks.Count == 0)
        {
            return new WorldMapCrossingResolveResult(
                Array.Empty<WorldMapCrossingMark>(),
                budgetLimited,
                candidateChecks);
        }

        marks.Sort(CompareMarks);

        return new WorldMapCrossingResolveResult(
            marks.ToArray(),
            budgetLimited,
            candidateChecks);
    }

    private static void AppendBudgetedCellMarks(
        List<WorldMapCrossingMark> candidates,
        List<WorldMapCrossingMark> output,
        int limit)
    {
        if (candidates == null ||
            candidates.Count == 0 ||
            output == null ||
            limit <= 0)
            return;

        candidates.Sort(CompareBudgetPriority);

        bool[] selected =
            new bool[candidates.Count];
        Dictionary<string, int> overRouteCounts =
            new(StringComparer.Ordinal);
        HashSet<string> representedOverRoutes =
            new(StringComparer.Ordinal);
        int added = 0;

        // First pass preserves route diversity: one bridge per over-route before any route consumes
        // several slots in this dense cell.
        for (int i = 0;
             i < candidates.Count &&
             added < limit;
             i++)
        {
            WorldMapCrossingMark mark =
                candidates[i];

            if (!representedOverRoutes.Add(
                    mark.OverRouteId))
                continue;

            output.Add(mark);
            selected[i] = true;
            overRouteCounts[mark.OverRouteId] = 1;
            added++;
        }

        // Second pass fills the local budget but prevents one route from owning the whole crossing
        // mesh in a dense grid.
        for (int i = 0;
             i < candidates.Count &&
             added < limit;
             i++)
        {
            if (selected[i])
                continue;

            WorldMapCrossingMark mark =
                candidates[i];
            overRouteCounts.TryGetValue(
                mark.OverRouteId,
                out int count);

            if (count >=
                MaxDenseMarksPerOverRoutePerCell)
                continue;

            output.Add(mark);
            overRouteCounts[mark.OverRouteId] =
                count + 1;
            added++;
        }
    }

    private static void AddToCells(
        Dictionary<long, CellBucket> cells,
        SegmentRef segment)
    {
        int fixedCell =
            FloorCell(segment.Fixed);
        int minCell =
            FloorCell(segment.Min);
        int maxCell =
            FloorCell(segment.Max);

        for (int axisCell = minCell;
             axisCell <= maxCell;
             axisCell++)
        {
            int x =
                segment.Vertical
                    ? fixedCell
                    : axisCell;
            int y =
                segment.Vertical
                    ? axisCell
                    : fixedCell;
            long key =
                CellKey(x, y);

            if (!cells.TryGetValue(
                    key,
                    out CellBucket cell))
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

        if (point.Y <=
                vertical.Min + BridgeShoulder ||
            point.Y >=
                vertical.Max - BridgeShoulder ||
            point.X <=
                horizontal.Min + BridgeShoulder ||
            point.X >=
                horizontal.Max - BridgeShoulder)
            return false;

        float endpointClearanceSquared =
            RouteEndpointClearance *
            RouteEndpointClearance;

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
                   points[0]) <
               clearanceSquared ||
               Num.Vector2.DistanceSquared(
                   point,
                   points[points.Length - 1]) <
               clearanceSquared;
    }

    private static int CompareMarks(
        WorldMapCrossingMark a,
        WorldMapCrossingMark b)
    {
        int route =
            string.CompareOrdinal(
                a.OverRouteId,
                b.OverRouteId);
        if (route != 0)
            return route;

        int x =
            a.Point.X.CompareTo(b.Point.X);
        if (x != 0)
            return x;

        int y =
            a.Point.Y.CompareTo(b.Point.Y);
        if (y != 0)
            return y;

        return string.CompareOrdinal(
            a.UnderRouteId,
            b.UnderRouteId);
    }

    private static int CompareBudgetPriority(
        WorldMapCrossingMark a,
        WorldMapCrossingMark b)
    {
        uint ah = StablePriority(a);
        uint bh = StablePriority(b);

        int priority = ah.CompareTo(bh);
        if (priority != 0)
            return priority;

        return CompareMarks(a, b);
    }

    private static uint StablePriority(
        WorldMapCrossingMark mark)
    {
        unchecked
        {
            uint hash = 2166136261u;
            HashString(
                ref hash,
                mark.OverRouteId);
            HashString(
                ref hash,
                mark.UnderRouteId);

            hash =
                (hash ^
                 (uint)(int)Math.Round(
                     mark.Point.X * 4f)) *
                16777619u;
            hash =
                (hash ^
                 (uint)(int)Math.Round(
                     mark.Point.Y * 4f)) *
                16777619u;

            return hash;
        }
    }

    private static void HashString(
        ref uint hash,
        string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        unchecked
        {
            for (int i = 0; i < value.Length; i++)
            {
                hash =
                    (hash ^ (uint)value[i]) *
                    16777619u;
            }
        }
    }

    private static int FloorCell(float value) =>
        (int)Math.Floor(
            value / CellSize);

    private static long CellKey(int x, int y) =>
        ((long)(uint)x << 32) |
        (uint)y;

    private static long PairKey(int a, int b)
    {
        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        return ((long)(uint)min << 32) |
               (uint)max;
    }
}
