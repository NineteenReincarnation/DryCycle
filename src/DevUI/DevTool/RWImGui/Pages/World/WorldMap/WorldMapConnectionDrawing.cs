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

    // Called on the UI thread. Marker placement itself is pure geometry so the same short-link and
    // room-occlusion contract is exercised in hosted CI.
    internal static bool TryDirectionMarker(
        IReadOnlyList<Num.Vector2> path,
        Num.Vector2 clipMin,
        Num.Vector2 clipMax,
        IReadOnlyList<Num.Vector4> rooms,
        out Num.Vector2 point,
        out Num.Vector2 tangent,
        out float scale) =>
        WorldMapDirectionMarkerGeometry.TryResolve(
            path,
            clipMin,
            clipMax,
            rooms,
            out point,
            out tangent,
            out scale);

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
