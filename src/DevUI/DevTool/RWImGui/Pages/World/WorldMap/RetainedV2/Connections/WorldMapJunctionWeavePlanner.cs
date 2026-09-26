using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Phase 5 presentation geometry: carries a bundle lane into/out of an adjacent unique branch for a
/// bounded distance, then performs the lateral lane change inside that branch instead of collapsing
/// every route at the exact shared junction.
///
/// The planner consumes detached BasePoints + Phase 4 lane metadata only. It owns no route state and
/// is invoked only during retained corridor convergence.
/// </summary>
internal static class WorldMapJunctionWeavePlanner
{
    private readonly struct SegmentProfile
    {
        internal SegmentProfile(
            bool vertical,
            float entryOffset,
            float exitOffset,
            bool entryCarried,
            bool exitCarried)
        {
            Vertical = vertical;
            EntryOffset = entryOffset;
            ExitOffset = exitOffset;
            EntryCarried = entryCarried;
            ExitCarried = exitCarried;
        }

        internal bool Vertical { get; }
        internal float EntryOffset { get; }
        internal float ExitOffset { get; }
        internal bool EntryCarried { get; }
        internal bool ExitCarried { get; }
    }

    private readonly struct Junction
    {
        internal Junction(
            Num.Vector2 previousEnd,
            Num.Vector2 nextStart)
        {
            PreviousEnd = previousEnd;
            NextStart = nextStart;
        }

        internal Num.Vector2 PreviousEnd { get; }
        internal Num.Vector2 NextStart { get; }
    }

    private const float AxisEpsilon = 0.01f;
    private const float OffsetEpsilon = 0.05f;
    private const float PointEpsilonSquared = 0.04f;
    private const float WeaveLeadDistance = 36f;
    private const float MinimumAxisRun = 4f;

    internal static bool NeedsTransition(
        float[] offsets,
        bool[] assigned)
    {
        if (offsets == null ||
            assigned == null ||
            offsets.Length != assigned.Length)
            return false;

        return HasVisibleWeaveBoundary(
            offsets,
            assigned,
            offsets.Length);
    }

    internal static Num.Vector2[] Build(
        Num.Vector2[] source,
        float[] offsets,
        bool[] assigned)
    {
        if (source == null ||
            source.Length < 4 ||
            offsets == null ||
            assigned == null)
            return null;

        int segmentCount = source.Length - 1;
        if (offsets.Length != segmentCount ||
            assigned.Length != segmentCount)
            return null;

        if (!HasVisibleWeaveBoundary(
                offsets,
                assigned,
                segmentCount))
            return null;

        SegmentProfile[] profiles =
            BuildProfiles(
                source,
                offsets,
                assigned);

        if (profiles == null)
            return null;

        Junction[] junctions =
            BuildJunctions(
                source,
                profiles);

        if (junctions == null)
            return null;

        List<Num.Vector2> result =
            new(source.Length + 8);

        for (int segment = 0;
             segment < segmentCount;
             segment++)
        {
            Num.Vector2 start =
                segment == 0
                    ? source[0]
                    : junctions[segment - 1].NextStart;
            Num.Vector2 end =
                segment == segmentCount - 1
                    ? source[source.Length - 1]
                    : junctions[segment].PreviousEnd;

            AppendDistinct(result, start);

            SegmentProfile profile =
                profiles[segment];

            float offsetDelta =
                profile.ExitOffset -
                profile.EntryOffset;

            if (Math.Abs(offsetDelta) <= OffsetEpsilon)
            {
                AppendDistinct(result, end);
            }
            else
            {
                Num.Vector2 baseDelta =
                    source[segment + 1] -
                    source[segment];
                Num.Vector2 direction =
                    CardinalDirection(baseDelta);

                if (direction.LengthSquared() < 0.5f)
                    return null;

                float run =
                    Num.Vector2.Dot(
                        end - start,
                        direction);
                if (run < MinimumAxisRun)
                    return null;

                float lead =
                    Math.Min(
                        WeaveLeadDistance,
                        run * 0.45f);

                float transitionDistance;
                if (profile.EntryCarried &&
                    !profile.ExitCarried)
                {
                    // Leaving a bundle: keep the old lane for a while inside the branch, then peel
                    // toward the branch centreline away from the shared junction.
                    transitionDistance = lead;
                }
                else if (!profile.EntryCarried &&
                         profile.ExitCarried)
                {
                    // Entering a bundle: move into the slot before the shared junction so arrival is
                    // already ordered rather than collapsing at the merge point.
                    transitionDistance =
                        run - lead;
                }
                else
                {
                    transitionDistance =
                        run * 0.5f;
                }

                transitionDistance =
                    Math.Max(
                        1f,
                        Math.Min(
                            run - 1f,
                            transitionDistance));

                Num.Vector2 before =
                    start +
                    direction *
                    transitionDistance;

                Num.Vector2 lateral =
                    profile.Vertical
                        ? new Num.Vector2(
                            offsetDelta,
                            0f)
                        : new Num.Vector2(
                            0f,
                            offsetDelta);

                Num.Vector2 after =
                    before + lateral;

                AppendDistinct(result, before);
                AppendDistinct(result, after);
                AppendDistinct(result, end);
            }

            if (segment < junctions.Length)
            {
                // Same-axis offset changes need one explicit perpendicular connector at the vertex.
                // Perpendicular route turns already share one shifted line intersection.
                AppendDistinct(
                    result,
                    junctions[segment].NextStart);
            }
        }

        return Simplify(result);
    }

    private static bool HasVisibleWeaveBoundary(
        float[] offsets,
        bool[] assigned,
        int segmentCount)
    {
        if (offsets == null ||
            assigned == null ||
            segmentCount <= 1)
            return false;

        // Any visible offset discontinuity needs explicit junction geometry, including the boundary
        // between the protected terminal stub and the first corridor lane. The previous test only
        // considered unassigned interior segments, so a lane could begin immediately after the
        // socket with BuildLanePath averaging the two offsets into a diagonal terminal leader.
        for (int i = 0;
             i < segmentCount - 1;
             i++)
        {
            float left =
                assigned[i]
                    ? offsets[i]
                    : 0f;
            float right =
                assigned[i + 1]
                    ? offsets[i + 1]
                    : 0f;

            if (Math.Abs(
                    left -
                    right) >
                OffsetEpsilon)
                return true;
        }

        return false;
    }

    private static SegmentProfile[] BuildProfiles(
        Num.Vector2[] source,
        float[] offsets,
        bool[] assigned)
    {
        int segmentCount =
            source.Length - 1;
        SegmentProfile[] profiles =
            new SegmentProfile[segmentCount];

        for (int i = 0; i < segmentCount; i++)
        {
            Num.Vector2 delta =
                source[i + 1] -
                source[i];

            bool vertical =
                Math.Abs(delta.X) <
                AxisEpsilon;
            bool horizontal =
                Math.Abs(delta.Y) <
                AxisEpsilon;

            if (!vertical && !horizontal)
                return null;

            float entry =
                assigned[i]
                    ? offsets[i]
                    : 0f;
            float exit = entry;
            bool entryCarried = false;
            bool exitCarried = false;

            if (!assigned[i] &&
                IsWeaveEligible(
                    i,
                    segmentCount))
            {
                if (i > 0 &&
                    assigned[i - 1])
                {
                    entry =
                        offsets[i - 1];
                    entryCarried = true;
                }

                if (i + 1 < segmentCount &&
                    assigned[i + 1])
                {
                    exit =
                        offsets[i + 1];
                    exitCarried = true;
                }
            }

            profiles[i] =
                new SegmentProfile(
                    vertical,
                    entry,
                    exit,
                    entryCarried,
                    exitCarried);
        }

        return profiles;
    }

    private static Junction[] BuildJunctions(
        Num.Vector2[] source,
        SegmentProfile[] profiles)
    {
        int junctionCount =
            source.Length - 2;
        Junction[] junctions =
            new Junction[
                Math.Max(
                    0,
                    junctionCount)];

        for (int vertex = 1;
             vertex < source.Length - 1;
             vertex++)
        {
            SegmentProfile previous =
                profiles[vertex - 1];
            SegmentProfile next =
                profiles[vertex];
            Num.Vector2 point =
                source[vertex];

            if (previous.Vertical !=
                next.Vertical)
            {
                float x =
                    previous.Vertical
                        ? point.X +
                          previous.ExitOffset
                        : point.X +
                          next.EntryOffset;
                float y =
                    previous.Vertical
                        ? point.Y +
                          next.EntryOffset
                        : point.Y +
                          previous.ExitOffset;

                Num.Vector2 corner =
                    new(x, y);
                junctions[vertex - 1] =
                    new Junction(
                        corner,
                        corner);
                continue;
            }

            Num.Vector2 previousEnd;
            Num.Vector2 nextStart;

            if (previous.Vertical)
            {
                previousEnd =
                    new Num.Vector2(
                        point.X +
                        previous.ExitOffset,
                        point.Y);
                nextStart =
                    new Num.Vector2(
                        point.X +
                        next.EntryOffset,
                        point.Y);
            }
            else
            {
                previousEnd =
                    new Num.Vector2(
                        point.X,
                        point.Y +
                        previous.ExitOffset);
                nextStart =
                    new Num.Vector2(
                        point.X,
                        point.Y +
                        next.EntryOffset);
            }

            junctions[vertex - 1] =
                new Junction(
                    previousEnd,
                    nextStart);
        }

        return junctions;
    }

    private static bool IsWeaveEligible(
        int segmentIndex,
        int segmentCount)
    {
        // Phase 1 now owns exactly the physical socket stub at each end. Keeping a second
        // protected segment prevented short routes from carrying their lane cleanly away from a
        // bundle and made several independent links visually collapse at the same junction.
        return segmentIndex >= 1 &&
               segmentIndex <=
               segmentCount - 2;
    }

    private static Num.Vector2 CardinalDirection(
        Num.Vector2 delta)
    {
        if (Math.Abs(delta.X) >=
            Math.Abs(delta.Y))
        {
            if (Math.Abs(delta.X) <
                AxisEpsilon)
                return Num.Vector2.Zero;

            return new Num.Vector2(
                delta.X >= 0f ? 1f : -1f,
                0f);
        }

        if (Math.Abs(delta.Y) <
            AxisEpsilon)
            return Num.Vector2.Zero;

        return new Num.Vector2(
            0f,
            delta.Y >= 0f ? 1f : -1f);
    }

    private static void AppendDistinct(
        List<Num.Vector2> points,
        Num.Vector2 point)
    {
        if (points.Count == 0 ||
            Num.Vector2.DistanceSquared(
                points[points.Count - 1],
                point) >
            PointEpsilonSquared)
        {
            points.Add(point);
        }
    }

    private static Num.Vector2[] Simplify(
        List<Num.Vector2> source)
    {
        if (source == null ||
            source.Count < 3)
            return source?.ToArray() ??
                   Array.Empty<Num.Vector2>();

        List<Num.Vector2> result =
            new(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            Num.Vector2 point =
                source[i];

            if (result.Count > 0 &&
                Num.Vector2.DistanceSquared(
                    result[result.Count - 1],
                    point) <
                PointEpsilonSquared)
                continue;

            if (result.Count >= 2)
            {
                Num.Vector2 a =
                    result[result.Count - 2];
                Num.Vector2 b =
                    result[result.Count - 1];

                bool sameX =
                    Math.Abs(a.X - b.X) <
                    AxisEpsilon &&
                    Math.Abs(b.X - point.X) <
                    AxisEpsilon;
                bool sameY =
                    Math.Abs(a.Y - b.Y) <
                    AxisEpsilon &&
                    Math.Abs(b.Y - point.Y) <
                    AxisEpsilon;

                if (sameX || sameY)
                {
                    result[result.Count - 1] =
                        point;
                    continue;
                }
            }

            result.Add(point);
        }

        return result.ToArray();
    }
}
