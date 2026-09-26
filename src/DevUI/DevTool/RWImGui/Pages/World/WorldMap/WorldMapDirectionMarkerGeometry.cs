using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Pure screen-space direction-marker placement.
///
/// The retained GPU route, immediate fallback route and hover/focus overlays all feed their exact
/// visible polyline into this helper. Keeping placement independent from ImGui makes the short-link
/// and room-occlusion rules executable in hosted CI instead of relying only on the Unity GPU suite.
/// </summary>
internal static class WorldMapDirectionMarkerGeometry
{
    private const float MinimumVisibleRun = 3f;
    private static readonly List<Num.Vector2> occludedIntervals = new();

    internal static bool TryResolve(
        IReadOnlyList<Num.Vector2> path,
        Num.Vector2 clipMin,
        Num.Vector2 clipMax,
        IReadOnlyList<Num.Vector4> rooms,
        out Num.Vector2 point,
        out Num.Vector2 tangent,
        out float scale)
    {
        point = tangent = default;
        scale = 1f;
        float best = 0f;
        if (path == null)
            return false;

        for (int i = 0; i + 1 < path.Count; i++)
        {
            Num.Vector2 a = path[i];
            Num.Vector2 delta = path[i + 1] - a;
            float length = delta.Length();
            if (length < 4f)
                continue;

            float startInset =
                i == 0
                    ? Math.Min(10f, Math.Max(1.5f, length * 0.16f))
                    : Math.Min(4f, Math.Max(1f, length * 0.10f));
            float endInset =
                i == path.Count - 2
                    ? Math.Min(10f, Math.Max(1.5f, length * 0.16f))
                    : Math.Min(4f, Math.Max(1f, length * 0.10f));

            float insetBudget =
                Math.Max(0f, length - MinimumVisibleRun);
            float insetTotal =
                startInset + endInset;
            if (insetTotal > insetBudget &&
                insetTotal > 0.001f)
            {
                float shrink =
                    insetBudget / insetTotal;
                startInset *= shrink;
                endInset *= shrink;
            }

            float lo = startInset / length;
            float hi = 1f - endInset / length;
            if (!Clip(
                    a,
                    delta,
                    clipMin + new Num.Vector2(4f),
                    clipMax - new Num.Vector2(4f),
                    ref lo,
                    ref hi))
                continue;

            occludedIntervals.Clear();
            if (rooms != null)
            {
                for (int r = 0; r < rooms.Count; r++)
                {
                    Num.Vector4 room = rooms[r];
                    float from = lo;
                    float to = hi;

                    // The marker may approach a socket closely on a tiny gap, but it must not sit
                    // on top of the room silhouette itself.
                    if (Clip(
                            a,
                            delta,
                            new Num.Vector2(room.X - 2f, room.Y - 2f),
                            new Num.Vector2(room.Z + 2f, room.W + 2f),
                            ref from,
                            ref to))
                    {
                        occludedIntervals.Add(
                            new Num.Vector2(from, to));
                    }
                }
            }

            occludedIntervals.Sort(CompareIntervals);

            float cursor = lo;
            for (int r = 0; r <= occludedIntervals.Count; r++)
            {
                float end =
                    r < occludedIntervals.Count
                        ? occludedIntervals[r].X
                        : hi;
                float available =
                    (end - cursor) * length;

                if (available >= MinimumVisibleRun &&
                    available > best)
                {
                    best = available;
                    point =
                        a + delta * ((cursor + end) * 0.5f);
                    tangent =
                        delta / length;
                    scale =
                        Math.Max(
                            0.32f,
                            Math.Min(1f, available / 14f));
                }

                if (r < occludedIntervals.Count)
                {
                    cursor =
                        Math.Max(
                            cursor,
                            occludedIntervals[r].Y);
                }
            }
        }

        return best > 0f;
    }

    private static int CompareIntervals(
        Num.Vector2 a,
        Num.Vector2 b) =>
        a.X.CompareTo(b.X);

    private static bool Clip(
        Num.Vector2 a,
        Num.Vector2 delta,
        Num.Vector2 min,
        Num.Vector2 max,
        ref float lo,
        ref float hi) =>
        Slab(a.X, delta.X, min.X, max.X, ref lo, ref hi) &&
        Slab(a.Y, delta.Y, min.Y, max.Y, ref lo, ref hi) &&
        hi > lo;

    private static bool Slab(
        float origin,
        float delta,
        float min,
        float max,
        ref float lo,
        ref float hi)
    {
        if (Math.Abs(delta) < 0.0001f)
            return origin >= min && origin <= max;

        float a =
            (min - origin) / delta;
        float b =
            (max - origin) / delta;

        lo =
            Math.Max(
                lo,
                Math.Min(a, b));
        hi =
            Math.Min(
                hi,
                Math.Max(a, b));

        return hi > lo;
    }
}
