using System;
using System.Collections.Generic;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Lightweight magnetic snapping for the rebuilt floating editor windows.
/// Snapping happens when the user releases a moved window, so windows never become
/// permanently docked and can always be pulled away again on the next drag.
/// </summary>
internal static class FloatingWindowSnap
{
    private const float SnapDistance = 14f;
    private const float ScreenMargin = 8f;
    private const float MinPerpendicularOverlap = 24f;

    private sealed class WindowState
    {
        internal Num.Vector2 Position;
        internal Num.Vector2 Size;
        internal int LastSeenFrame;
        internal bool ManipulatedDuringDrag;
        internal bool MouseWasDown;
        internal bool Initialized;
    }

    private static readonly Dictionary<string, WindowState> Windows = new(StringComparer.Ordinal);
    private static readonly List<string> StaleKeys = new();
    private static Num.Vector2 displaySize;
    private static int frame;

    internal static void BeginFrame(Num.Vector2 currentDisplaySize)
    {
        frame++;
        displaySize = currentDisplaySize;
        if (displaySize.X < 1f) displaySize.X = 1366f;
        if (displaySize.Y < 1f) displaySize.Y = 768f;

        StaleKeys.Clear();
        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            if (pair.Value.LastSeenFrame < frame - 2)
                StaleKeys.Add(pair.Key);
        }

        for (int i = 0; i < StaleKeys.Count; i++)
            Windows.Remove(StaleKeys[i]);
    }

    /// <summary>
    /// Call immediately after ImGui.Begin for a movable editor window.
    /// </summary>
    internal static void TrackCurrentWindow(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        Num.Vector2 position = ImGui.GetWindowPos();
        Num.Vector2 size = ImGui.GetWindowSize();
        bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);

        if (!Windows.TryGetValue(id, out WindowState state))
        {
            state = new WindowState();
            Windows[id] = state;
        }

        if (!state.Initialized)
        {
            state.Position = position;
            state.Size = size;
            state.MouseWasDown = mouseDown;
            state.LastSeenFrame = frame;
            state.Initialized = true;
            return;
        }

        bool moved = DistanceSquared(position, state.Position) > 0.25f;
        bool resized = DistanceSquared(size, state.Size) > 0.25f;
        if (mouseDown && (moved || resized))
            state.ManipulatedDuringDrag = true;

        // Snap once when the drag/resize button is released. Applying the correction only at
        // release avoids the "sticky wall" problem where a continuously snapped window cannot
        // be pulled away from its neighbour.
        if (!mouseDown && state.MouseWasDown && state.ManipulatedDuringDrag)
        {
            Num.Vector2 snapped = FindBestPosition(id, position, size);
            if (DistanceSquared(snapped, position) > 0.01f)
            {
                ImGui.SetWindowPos(snapped);
                position = snapped;
            }

            state.ManipulatedDuringDrag = false;
        }

        state.Position = position;
        state.Size = size;
        state.MouseWasDown = mouseDown;
        state.LastSeenFrame = frame;
    }

    private static Num.Vector2 FindBestPosition(string id, Num.Vector2 position, Num.Vector2 size)
    {
        float bestX = position.X;
        float bestY = position.Y;
        float bestXDistance = SnapDistance + 0.001f;
        float bestYDistance = SnapDistance + 0.001f;

        Consider(ref bestX, ref bestXDistance, position.X, ScreenMargin);
        Consider(ref bestX, ref bestXDistance, position.X, displaySize.X - ScreenMargin - size.X);
        Consider(ref bestY, ref bestYDistance, position.Y, ScreenMargin);
        Consider(ref bestY, ref bestYDistance, position.Y, displaySize.Y - ScreenMargin - size.Y);

        float left = position.X;
        float right = position.X + size.X;
        float top = position.Y;
        float bottom = position.Y + size.Y;

        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            if (string.Equals(pair.Key, id, StringComparison.Ordinal)) continue;
            WindowState other = pair.Value;
            if (!other.Initialized || other.LastSeenFrame < frame - 1) continue;

            float otherLeft = other.Position.X;
            float otherRight = other.Position.X + other.Size.X;
            float otherTop = other.Position.Y;
            float otherBottom = other.Position.Y + other.Size.Y;

            float verticalOverlap = Math.Min(bottom, otherBottom) - Math.Max(top, otherTop);
            if (verticalOverlap >= MinPerpendicularOverlap)
            {
                // Adjacent edge snapping.
                Consider(ref bestX, ref bestXDistance, left, otherRight);
                Consider(ref bestX, ref bestXDistance, right, otherLeft, -size.X);

                // Same-edge alignment makes stacked panels line up cleanly.
                Consider(ref bestX, ref bestXDistance, left, otherLeft);
                Consider(ref bestX, ref bestXDistance, right, otherRight, -size.X);
            }

            float horizontalOverlap = Math.Min(right, otherRight) - Math.Max(left, otherLeft);
            if (horizontalOverlap >= MinPerpendicularOverlap)
            {
                Consider(ref bestY, ref bestYDistance, top, otherBottom);
                Consider(ref bestY, ref bestYDistance, bottom, otherTop, -size.Y);
                Consider(ref bestY, ref bestYDistance, top, otherTop);
                Consider(ref bestY, ref bestYDistance, bottom, otherBottom, -size.Y);
            }
        }

        // Never let magnetic correction push the whole title bar beyond the visible display.
        bestX = Math.Max(0f, Math.Min(bestX, Math.Max(0f, displaySize.X - 40f)));
        bestY = Math.Max(0f, Math.Min(bestY, Math.Max(0f, displaySize.Y - 24f)));
        return new Num.Vector2(bestX, bestY);
    }

    private static void Consider(
        ref float best,
        ref float bestDistance,
        float currentEdge,
        float targetEdge,
        float positionOffset = 0f)
    {
        float distance = Math.Abs(currentEdge - targetEdge);
        if (distance > SnapDistance || distance >= bestDistance) return;
        bestDistance = distance;
        best = targetEdge + positionOffset;
    }

    private static float DistanceSquared(Num.Vector2 a, Num.Vector2 b)
    {
        float x = a.X - b.X;
        float y = a.Y - b.Y;
        return x * x + y * y;
    }
}
