using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

// Presentation only: routing/hit testing keep the original orthogonal path and its socket anchors.
internal static class WorldMapConnectionDrawing
{
    internal const float CornerRadius = 6f;
    private static readonly List<Num.Vector2> occludedIntervals = new();

    internal static void RoundCorners(IReadOnlyList<Num.Vector2> path, List<Num.Vector2> output, float radius)
    {
        output.Clear();
        if (path == null || path.Count == 0) return;
        output.Add(path[0]);
        for (int i = 1; i + 1 < path.Count; i++)
        {
            Num.Vector2 corner = path[i], before = corner - path[i - 1], after = path[i + 1] - corner;
            float inLength = before.Length(), outLength = after.Length();
            if (inLength < 0.01f || outLength < 0.01f) continue;
            before /= inLength;
            after /= outLength;
            if (Math.Abs(Num.Vector2.Dot(before, after)) > 0.99f)
            { output.Add(corner); continue; }
            float r = Math.Min(radius, Math.Min(inLength, outLength) * 0.35f);
            Num.Vector2 a = corner - before * r, b = corner + after * r;
            output.Add(a);
            for (int step = 1; step <= 4; step++)
            {
                float t = step * 0.25f, s = 1f - t;
                output.Add(s * s * a + 2f * s * t * corner + t * t * b);
            }
        }
        if (path.Count > 1) output.Add(path[path.Count - 1]);
    }

    // Called on the UI thread. Search all visible straight runs, including terminal runs, then
    // subtract room silhouettes. Terminal padding shrinks with the actual run length so a genuine
    // 10-20 px pipe gap still gets a compact marker instead of silently losing its direction.
    internal static bool TryDirectionMarker(
        IReadOnlyList<Num.Vector2> path, Num.Vector2 clipMin, Num.Vector2 clipMax,
        IReadOnlyList<Num.Vector4> rooms, out Num.Vector2 point, out Num.Vector2 tangent, out float scale)
    {
        const float MinimumVisibleRun = 3f;
        point = tangent = default;
        scale = 1f;
        float best = 0f;
        if (path == null) return false;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            Num.Vector2 a = path[i], delta = path[i + 1] - a;
            float length = delta.Length();
            if (length < 4f) continue;

            float startInset = i == 0
                ? Math.Min(10f, Math.Max(1.5f, length * 0.16f))
                : Math.Min(4f, Math.Max(1f, length * 0.10f));
            float endInset = i == path.Count - 2
                ? Math.Min(10f, Math.Max(1.5f, length * 0.16f))
                : Math.Min(4f, Math.Max(1f, length * 0.10f));
            float insetBudget = Math.Max(0f, length - MinimumVisibleRun);
            float insetTotal = startInset + endInset;
            if (insetTotal > insetBudget && insetTotal > 0.001f)
            {
                float shrink = insetBudget / insetTotal;
                startInset *= shrink;
                endInset *= shrink;
            }

            float lo = startInset / length;
            float hi = 1f - endInset / length;
            if (!Clip(a, delta, clipMin + new Num.Vector2(4), clipMax - new Num.Vector2(4), ref lo, ref hi))
                continue;

            occludedIntervals.Clear();
            if (rooms != null)
                for (int r = 0; r < rooms.Count; r++)
                {
                    Num.Vector4 room = rooms[r];
                    float from = lo, to = hi;
                    // Keep the glyph outside room silhouettes, but do not reserve the old 5 px on
                    // both sides: that alone consumed an entire short inter-room corridor.
                    if (Clip(a, delta, new Num.Vector2(room.X - 2, room.Y - 2),
                            new Num.Vector2(room.Z + 2, room.W + 2), ref from, ref to))
                        occludedIntervals.Add(new Num.Vector2(from, to));
                }

            occludedIntervals.Sort(CompareIntervals);
            float cursor = lo;
            for (int r = 0; r <= occludedIntervals.Count; r++)
            {
                float end = r < occludedIntervals.Count ? occludedIntervals[r].X : hi;
                float available = (end - cursor) * length;
                if (available >= MinimumVisibleRun && available > best)
                {
                    best = available;
                    point = a + delta * ((cursor + end) * 0.5f);
                    tangent = delta / length;
                    scale = Math.Max(0.32f, Math.Min(1f, available / 14f));
                }
                if (r < occludedIntervals.Count)
                    cursor = Math.Max(cursor, occludedIntervals[r].Y);
            }
        }
        return best > 0f;
    }

    private static int CompareIntervals(Num.Vector2 a, Num.Vector2 b) => a.X.CompareTo(b.X);

    private static bool Clip(Num.Vector2 a, Num.Vector2 d, Num.Vector2 min, Num.Vector2 max, ref float lo, ref float hi) =>
        Slab(a.X, d.X, min.X, max.X, ref lo, ref hi) &&
        Slab(a.Y, d.Y, min.Y, max.Y, ref lo, ref hi) && hi > lo;

    private static bool Slab(float origin, float delta, float min, float max, ref float lo, ref float hi)
    {
        if (Math.Abs(delta) < 0.0001f) return origin >= min && origin <= max;
        float a = (min - origin) / delta, b = (max - origin) / delta;
        lo = Math.Max(lo, Math.Min(a, b));
        hi = Math.Min(hi, Math.Max(a, b));
        return hi > lo;
    }

    internal static void DrawDirectionMarker(ImDrawListPtr draw, IReadOnlyList<Num.Vector2> path,
        Num.Vector2 clipMin, Num.Vector2 clipMax, IReadOnlyList<Num.Vector4> rooms,
        WorldConnectionDirection direction, uint color)
    {
        if (!TryDirectionMarker(path, clipMin, clipMax, rooms, out Num.Vector2 p, out Num.Vector2 t, out float scale)) return;
        const uint shadow = 0xF20B0906;
        Num.Vector2 n = new(-t.Y, t.X);
        if (direction == WorldConnectionDirection.Bidirectional)
        {
            // A single compact <-> glyph; its 14px footprint is independent of map zoom.
            draw.AddLine(p - t * (4 * scale), p + t * (4 * scale), shadow, 4f * scale);
            draw.AddLine(p - t * (4 * scale), p + t * (4 * scale), color, 1.2f * scale);
            Head(draw, p - t * (6 * scale), -t, n, scale, shadow, color);
            Head(draw, p + t * (6 * scale), t, n, scale, shadow, color);
        }
        else
        {
            if (direction == WorldConnectionDirection.BToA) t = -t;
            Head(draw, p + t * (2 * scale), t, n, scale, shadow, color);
        }
    }

    private static void Head(ImDrawListPtr draw, Num.Vector2 tip, Num.Vector2 t, Num.Vector2 n,
        float scale, uint shadow, uint color)
    {
        draw.AddTriangleFilled(tip + t * scale, tip - t * (5 * scale) + n * (4 * scale),
            tip - t * (5 * scale) - n * (4 * scale), shadow);
        draw.AddTriangleFilled(tip, tip - t * (4 * scale) + n * (2.5f * scale),
            tip - t * (4 * scale) - n * (2.5f * scale), color);
    }
}
