using System;
using System.Collections.Generic;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Floating-window layout service. Besides magnetic edge snapping it owns the presentation-only
/// multi-selection used to reorganize several editor panels at once.
/// </summary>
internal static class FloatingWindowSnap
{
    private const float SnapDistance = 14f;
    private const float ScreenMargin = 8f;
    private const float MinPerpendicularOverlap = 24f;
    private const float MarqueeDragThreshold = 5f;

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
    private static readonly HashSet<string> Selected = new(StringComparer.Ordinal);
    private static readonly List<string> StaleKeys = new();
    private static readonly Dictionary<string, Num.Vector2> GroupDragOrigins = new(StringComparer.Ordinal);

    private static Num.Vector2 displaySize;
    private static int frame;

    private static bool marqueeActive;
    private static bool marqueeReleasePending;
    private static bool marqueeAdditive;
    private static Num.Vector2 marqueeStart;
    private static Num.Vector2 marqueeCurrent;

    private static bool groupDragging;
    private static Num.Vector2 groupDragStartMouse;
    private static Num.Vector2 groupDragDelta;

    internal static bool OwnsMouse => marqueeActive || marqueeReleasePending || groupDragging;

    internal static void BeginFrame(Num.Vector2 currentDisplaySize)
    {
        frame++;
        displaySize = currentDisplaySize;
        if (displaySize.X < 1f) displaySize.X = 1366f;
        if (displaySize.Y < 1f) displaySize.Y = 768f;

        RemoveStaleWindows();

        ImGuiIOPtr io = ImGui.GetIO();
        bool leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);

        // Shift + left click/drag is reserved for panel selection. Starting on empty space creates
        // a marquee; starting on a panel toggles that panel in the current selection.
        if (io.KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (TryFindWindowAtPoint(io.MousePos, selectedOnly: false, titleOnly: false, out string hit))
            {
                if (!io.KeyCtrl) Selected.Clear();
                if (!Selected.Add(hit)) Selected.Remove(hit);
                marqueeActive = false;
                marqueeReleasePending = false;
            }
            else
            {
                marqueeActive = true;
                marqueeReleasePending = false;
                marqueeAdditive = io.KeyCtrl;
                marqueeStart = io.MousePos;
                marqueeCurrent = io.MousePos;
                if (!marqueeAdditive) Selected.Clear();
            }
        }

        if (marqueeActive)
        {
            marqueeCurrent = io.MousePos;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) || !leftDown)
            {
                marqueeActive = false;
                marqueeReleasePending = true;
            }
        }

        // Once a marquee has selected two or more windows, a normal left drag on the title bar of
        // any selected window moves the entire group. Individual ImGui controls remain untouched.
        if (!io.KeyShift && !groupDragging && Selected.Count > 1 &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            TryFindWindowAtPoint(io.MousePos, selectedOnly: true, titleOnly: true, out _))
        {
            groupDragging = true;
            groupDragStartMouse = io.MousePos;
            groupDragDelta = Num.Vector2.Zero;
            GroupDragOrigins.Clear();
            foreach (string id in Selected)
            {
                if (Windows.TryGetValue(id, out WindowState state) && state.Initialized)
                    GroupDragOrigins[id] = state.Position;
            }
        }

        if (groupDragging)
        {
            if (leftDown)
            {
                groupDragDelta = io.MousePos - groupDragStartMouse;
            }
            else
            {
                groupDragging = false;
                groupDragDelta = Num.Vector2.Zero;
                GroupDragOrigins.Clear();
            }
        }
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
            DrawSelectionOutline(id, position, size);
            return;
        }

        // Group movement overrides ImGui's normal per-window drag for the selected set. This is
        // deliberately presentation-only and never touches editor document/history state.
        if (groupDragging && Selected.Contains(id) && GroupDragOrigins.TryGetValue(id, out Num.Vector2 origin))
        {
            Num.Vector2 target = ClampVisible(origin + groupDragDelta, size);
            ImGui.SetWindowPos(target);
            position = target;
            state.ManipulatedDuringDrag = false;
        }
        else
        {
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
        }

        state.Position = position;
        state.Size = size;
        state.MouseWasDown = mouseDown;
        state.LastSeenFrame = frame;

        DrawSelectionOutline(id, position, size);
    }

    /// <summary>
    /// Call after all editor windows have been submitted for the frame.
    /// </summary>
    internal static void EndFrame()
    {
        if (marqueeReleasePending)
        {
            marqueeReleasePending = false;
            if (DistanceSquared(marqueeStart, marqueeCurrent) >= MarqueeDragThreshold * MarqueeDragThreshold)
                SelectIntersectingWindows(marqueeStart, marqueeCurrent, marqueeAdditive);
        }

        if (!marqueeActive) return;

        Num.Vector2 min = Num.Vector2.Min(marqueeStart, marqueeCurrent);
        Num.Vector2 max = Num.Vector2.Max(marqueeStart, marqueeCurrent);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        uint fill = ImGui.GetColorU32(new Num.Vector4(0.18f, 0.48f, 1f, 0.14f));
        uint border = ImGui.GetColorU32(new Num.Vector4(0.35f, 0.68f, 1f, 0.95f));
        draw.AddRectFilled(min, max, fill);
        draw.AddRect(min, max, border, 0f, ImDrawFlags.None, 2f);
    }

    private static void SelectIntersectingWindows(Num.Vector2 a, Num.Vector2 b, bool additive)
    {
        if (!additive) Selected.Clear();

        Num.Vector2 min = Num.Vector2.Min(a, b);
        Num.Vector2 max = Num.Vector2.Max(a, b);
        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            WindowState state = pair.Value;
            if (!state.Initialized || state.LastSeenFrame < frame - 1) continue;

            Num.Vector2 otherMin = state.Position;
            Num.Vector2 otherMax = state.Position + state.Size;
            if (RectanglesIntersect(min, max, otherMin, otherMax))
                Selected.Add(pair.Key);
        }
    }

    private static bool TryFindWindowAtPoint(
        Num.Vector2 point,
        bool selectedOnly,
        bool titleOnly,
        out string id)
    {
        id = null;
        int bestFrame = -1;

        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            WindowState state = pair.Value;
            if (!state.Initialized || state.LastSeenFrame < frame - 1) continue;
            if (selectedOnly && !Selected.Contains(pair.Key)) continue;

            Num.Vector2 min = state.Position;
            Num.Vector2 max = state.Position + state.Size;
            if (titleOnly)
            {
                float titleHeight = Math.Min(state.Size.Y, Math.Max(28f, 26f * DevToolUiSettings.UiScale));
                max.Y = min.Y + titleHeight;
            }

            if (!Contains(min, max, point)) continue;
            if (state.LastSeenFrame < bestFrame) continue;
            bestFrame = state.LastSeenFrame;
            id = pair.Key;
        }

        return id != null;
    }

    private static void DrawSelectionOutline(string id, Num.Vector2 position, Num.Vector2 size)
    {
        if (!Selected.Contains(id)) return;

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        uint color = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        Num.Vector2 pad = new(2f, 2f);
        draw.AddRect(position - pad, position + size + pad, color, 0f, ImDrawFlags.None, 3f);
    }

    private static void RemoveStaleWindows()
    {
        StaleKeys.Clear();
        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            if (pair.Value.LastSeenFrame < frame - 2)
                StaleKeys.Add(pair.Key);
        }

        for (int i = 0; i < StaleKeys.Count; i++)
        {
            string id = StaleKeys[i];
            Windows.Remove(id);
            Selected.Remove(id);
            GroupDragOrigins.Remove(id);
        }
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
                Consider(ref bestX, ref bestXDistance, left, otherRight);
                Consider(ref bestX, ref bestXDistance, right, otherLeft, -size.X);
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

        return ClampVisible(new Num.Vector2(bestX, bestY), size);
    }

    private static Num.Vector2 ClampVisible(Num.Vector2 position, Num.Vector2 size)
    {
        float x = Math.Max(0f, Math.Min(position.X, Math.Max(0f, displaySize.X - 40f)));
        float y = Math.Max(0f, Math.Min(position.Y, Math.Max(0f, displaySize.Y - 24f)));
        return new Num.Vector2(x, y);
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

    private static bool RectanglesIntersect(
        Num.Vector2 aMin,
        Num.Vector2 aMax,
        Num.Vector2 bMin,
        Num.Vector2 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y;

    private static bool Contains(Num.Vector2 min, Num.Vector2 max, Num.Vector2 point) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private static float DistanceSquared(Num.Vector2 a, Num.Vector2 b)
    {
        float x = a.X - b.X;
        float y = a.Y - b.Y;
        return x * x + y * y;
    }
}
