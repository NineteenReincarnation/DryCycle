using System;
using System.Collections.Generic;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Floating-window layout service. It owns magnetic alignment, live alignment guides and the
/// presentation-only multi-selection used to reorganize several editor panels at once.
/// </summary>
internal static class FloatingWindowSnap
{
    private const float SnapDistance = 14f;
    private const float ScreenMargin = 8f;
    private const float MinPerpendicularOverlap = 24f;
    private const float AlignmentReach = 240f;
    private const float MarqueeDragThreshold = 5f;
    private const float GeometryEpsilon = 0.25f;

    private enum GuideAxis
    {
        Vertical,
        Horizontal
    }

    private readonly struct GuideLine
    {
        internal GuideLine(GuideAxis axis, float coordinate)
        {
            Axis = axis;
            Coordinate = coordinate;
        }

        internal GuideAxis Axis { get; }
        internal float Coordinate { get; }
    }

    private sealed class WindowState
    {
        internal Num.Vector2 Position;
        internal Num.Vector2 Size;
        internal int LastSeenFrame;
        internal bool ManipulatedDuringDrag;
        internal bool MouseWasDown;
        internal bool Initialized;

        internal bool WasMoving;
        internal bool WasResizing;
        internal bool ResizeLeft;
        internal bool ResizeRight;
        internal bool ResizeTop;
        internal bool ResizeBottom;
    }

    private static readonly Dictionary<string, WindowState> Windows = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Selected = new(StringComparer.Ordinal);
    private static readonly List<string> StaleKeys = new();
    private static readonly Dictionary<string, Num.Vector2> GroupDragOrigins = new(StringComparer.Ordinal);
    private static readonly List<GuideLine> Guides = new();

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

        Guides.Clear();
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
                groupDragDelta = SnapGroupDelta(groupDragDelta);
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
    /// Call immediately after ImGui.Begin for a movable/resizable editor window.
    /// Moving and resizing both use the same alignment engine and guide lines.
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
            ResetManipulationMode(state);
        }
        else
        {
            bool moved = DistanceSquared(position, state.Position) > GeometryEpsilon;
            bool resized = DistanceSquared(size, state.Size) > GeometryEpsilon;

            if (mouseDown && (moved || resized))
            {
                state.ManipulatedDuringDrag = true;

                if (resized)
                {
                    DetectResizeEdges(state, position, size);
                    state.WasResizing = true;
                    state.WasMoving = false;
                    ApplyResizeSnap(id, ref position, ref size, state);
                }
                else if (!state.WasResizing && moved)
                {
                    state.WasMoving = true;
                    position = FindBestPosition(id, position, size, ignore: null, addGuides: true);
                    ImGui.SetWindowPos(position);
                }
            }

            // Final correction on release guarantees exact integer/floating-point equality even
            // if ImGui's own resize code moved the edge again after the previous live snap.
            if (!mouseDown && state.MouseWasDown && state.ManipulatedDuringDrag)
            {
                if (state.WasResizing)
                {
                    ApplyResizeSnap(id, ref position, ref size, state);
                }
                else
                {
                    Num.Vector2 snapped = FindBestPosition(id, position, size, ignore: null, addGuides: true);
                    if (DistanceSquared(snapped, position) > 0.01f)
                    {
                        ImGui.SetWindowPos(snapped);
                        position = snapped;
                    }
                }

                state.ManipulatedDuringDrag = false;
                ResetManipulationMode(state);
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

        DrawAlignmentGuides();

        if (!marqueeActive) return;

        Num.Vector2 min = Num.Vector2.Min(marqueeStart, marqueeCurrent);
        Num.Vector2 max = Num.Vector2.Max(marqueeStart, marqueeCurrent);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        uint fill = ImGui.GetColorU32(new Num.Vector4(0.18f, 0.48f, 1f, 0.14f));
        uint border = ImGui.GetColorU32(new Num.Vector4(0.35f, 0.68f, 1f, 0.95f));
        draw.AddRectFilled(min, max, fill);
        draw.AddRect(min, max, border, 0f, ImDrawFlags.None, 2f);
    }

    private static void DetectResizeEdges(WindowState state, Num.Vector2 position, Num.Vector2 size)
    {
        float oldLeft = state.Position.X;
        float oldRight = state.Position.X + state.Size.X;
        float oldTop = state.Position.Y;
        float oldBottom = state.Position.Y + state.Size.Y;

        float leftDelta = Math.Abs(position.X - oldLeft);
        float rightDelta = Math.Abs(position.X + size.X - oldRight);
        float topDelta = Math.Abs(position.Y - oldTop);
        float bottomDelta = Math.Abs(position.Y + size.Y - oldBottom);

        bool left = leftDelta > GeometryEpsilon;
        bool right = rightDelta > GeometryEpsilon;
        bool top = topDelta > GeometryEpsilon;
        bool bottom = bottomDelta > GeometryEpsilon;

        // Standard ImGui resizing changes one horizontal and/or one vertical edge. If layout
        // effects make both edges appear to move, keep the edge with the larger user delta.
        if (left && right)
        {
            left = leftDelta >= rightDelta;
            right = !left;
        }
        if (top && bottom)
        {
            top = topDelta >= bottomDelta;
            bottom = !top;
        }

        if (left || right)
        {
            state.ResizeLeft = left;
            state.ResizeRight = right;
        }
        if (top || bottom)
        {
            state.ResizeTop = top;
            state.ResizeBottom = bottom;
        }
    }

    private static void ApplyResizeSnap(
        string id,
        ref Num.Vector2 position,
        ref Num.Vector2 size,
        WindowState state)
    {
        float left = position.X;
        float right = position.X + size.X;
        float top = position.Y;
        float bottom = position.Y + size.Y;

        if (state.ResizeLeft)
        {
            float target = FindBestEdgeTarget(
                id, verticalAxis: true, currentEdge: left,
                perpendicularMin: top, perpendicularMax: bottom,
                fixedOppositeEdge: right, matchingSizeFromLeftOrTop: true,
                addGuides: true);
            if (!float.IsNaN(target) && right - target >= 40f)
                left = target;
        }
        else if (state.ResizeRight)
        {
            float target = FindBestEdgeTarget(
                id, verticalAxis: true, currentEdge: right,
                perpendicularMin: top, perpendicularMax: bottom,
                fixedOppositeEdge: left, matchingSizeFromLeftOrTop: false,
                addGuides: true);
            if (!float.IsNaN(target) && target - left >= 40f)
                right = target;
        }

        if (state.ResizeTop)
        {
            float target = FindBestEdgeTarget(
                id, verticalAxis: false, currentEdge: top,
                perpendicularMin: left, perpendicularMax: right,
                fixedOppositeEdge: bottom, matchingSizeFromLeftOrTop: true,
                addGuides: true);
            if (!float.IsNaN(target) && bottom - target >= 28f)
                top = target;
        }
        else if (state.ResizeBottom)
        {
            float target = FindBestEdgeTarget(
                id, verticalAxis: false, currentEdge: bottom,
                perpendicularMin: left, perpendicularMax: right,
                fixedOppositeEdge: top, matchingSizeFromLeftOrTop: false,
                addGuides: true);
            if (!float.IsNaN(target) && target - top >= 28f)
                bottom = target;
        }

        Num.Vector2 snappedPosition = new(left, top);
        Num.Vector2 snappedSize = new(Math.Max(40f, right - left), Math.Max(28f, bottom - top));

        if (DistanceSquared(snappedPosition, position) > 0.01f)
            ImGui.SetWindowPos(snappedPosition);
        if (DistanceSquared(snappedSize, size) > 0.01f)
            ImGui.SetWindowSize(snappedSize);

        position = snappedPosition;
        size = snappedSize;
    }

    /// <summary>
    /// Finds the closest coordinate for one actively-resized edge. Targets include screen edges,
    /// screen centre, every neighbouring window edge/centre, adjacent edges and equal dimensions.
    /// </summary>
    private static float FindBestEdgeTarget(
        string id,
        bool verticalAxis,
        float currentEdge,
        float perpendicularMin,
        float perpendicularMax,
        float fixedOppositeEdge,
        bool matchingSizeFromLeftOrTop,
        bool addGuides)
    {
        float bestTarget = float.NaN;
        float bestDistance = SnapDistance + 0.001f;

        float screenStart = ScreenMargin;
        float screenCenter = verticalAxis ? displaySize.X * 0.5f : displaySize.Y * 0.5f;
        float screenEnd = (verticalAxis ? displaySize.X : displaySize.Y) - ScreenMargin;
        ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, screenStart);
        ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, screenCenter);
        ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, screenEnd);

        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            if (string.Equals(pair.Key, id, StringComparison.Ordinal)) continue;
            WindowState other = pair.Value;
            if (!other.Initialized || other.LastSeenFrame < frame - 1) continue;

            float otherPerpendicularMin = verticalAxis ? other.Position.Y : other.Position.X;
            float otherPerpendicularMax = verticalAxis
                ? other.Position.Y + other.Size.Y
                : other.Position.X + other.Size.X;
            if (!RangesNear(
                    perpendicularMin, perpendicularMax,
                    otherPerpendicularMin, otherPerpendicularMax,
                    AlignmentReach))
                continue;

            float otherStart = verticalAxis ? other.Position.X : other.Position.Y;
            float otherSize = verticalAxis ? other.Size.X : other.Size.Y;
            float otherEnd = otherStart + otherSize;
            float otherCenter = (otherStart + otherEnd) * 0.5f;

            // Edge-to-edge, edge-to-opposite-edge and edge-to-centre alignment.
            ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, otherStart);
            ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, otherCenter);
            ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, otherEnd);

            // Equal width/height while resizing. The actively dragged edge moves while the
            // opposite edge remains fixed.
            float equalSizeTarget = matchingSizeFromLeftOrTop
                ? fixedOppositeEdge - otherSize
                : fixedOppositeEdge + otherSize;
            ConsiderEdge(ref bestTarget, ref bestDistance, currentEdge, equalSizeTarget);
        }

        if (!float.IsNaN(bestTarget) && addGuides)
            AddGuide(verticalAxis ? GuideAxis.Vertical : GuideAxis.Horizontal, bestTarget);
        return bestTarget;
    }

    private static Num.Vector2 SnapGroupDelta(Num.Vector2 rawDelta)
    {
        if (GroupDragOrigins.Count == 0) return rawDelta;

        Num.Vector2 min = new(float.MaxValue, float.MaxValue);
        Num.Vector2 max = new(float.MinValue, float.MinValue);
        foreach (KeyValuePair<string, Num.Vector2> pair in GroupDragOrigins)
        {
            if (!Windows.TryGetValue(pair.Key, out WindowState state) || !state.Initialized) continue;
            Num.Vector2 p = pair.Value + rawDelta;
            min = Num.Vector2.Min(min, p);
            max = Num.Vector2.Max(max, p + state.Size);
        }

        if (min.X == float.MaxValue) return rawDelta;
        Num.Vector2 size = max - min;
        Num.Vector2 snapped = FindBestPosition(null, min, size, Selected, addGuides: true);
        return rawDelta + (snapped - min);
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

    private static void DrawAlignmentGuides()
    {
        if (Guides.Count == 0) return;

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        uint guideColor = ImGui.GetColorU32(new Num.Vector4(0.20f, 0.72f, 1f, 0.92f));

        for (int i = 0; i < Guides.Count; i++)
        {
            GuideLine guide = Guides[i];
            if (guide.Axis == GuideAxis.Vertical)
            {
                draw.AddLine(
                    new Num.Vector2(guide.Coordinate, 0f),
                    new Num.Vector2(guide.Coordinate, displaySize.Y),
                    guideColor,
                    1.5f);
            }
            else
            {
                draw.AddLine(
                    new Num.Vector2(0f, guide.Coordinate),
                    new Num.Vector2(displaySize.X, guide.Coordinate),
                    guideColor,
                    1.5f);
            }
        }
    }

    private static void AddGuide(GuideAxis axis, float coordinate)
    {
        for (int i = 0; i < Guides.Count; i++)
        {
            if (Guides[i].Axis == axis && Math.Abs(Guides[i].Coordinate - coordinate) < 0.5f)
                return;
        }
        Guides.Add(new GuideLine(axis, coordinate));
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

    /// <summary>
    /// Aligns a moving rectangle. It supports screen edges/centre and every standard relation
    /// between window left/centre/right and top/centre/bottom, including adjacent-edge docking.
    /// </summary>
    private static Num.Vector2 FindBestPosition(
        string id,
        Num.Vector2 position,
        Num.Vector2 size,
        HashSet<string> ignore,
        bool addGuides)
    {
        float bestX = position.X;
        float bestY = position.Y;
        float bestXDistance = SnapDistance + 0.001f;
        float bestYDistance = SnapDistance + 0.001f;
        float bestXGuide = float.NaN;
        float bestYGuide = float.NaN;

        float left = position.X;
        float right = position.X + size.X;
        float centerX = (left + right) * 0.5f;
        float top = position.Y;
        float bottom = position.Y + size.Y;
        float centerY = (top + bottom) * 0.5f;

        ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, left, ScreenMargin, 0f);
        ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, centerX, displaySize.X * 0.5f, -size.X * 0.5f);
        ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, right, displaySize.X - ScreenMargin, -size.X);

        ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, top, ScreenMargin, 0f);
        ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, centerY, displaySize.Y * 0.5f, -size.Y * 0.5f);
        ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, bottom, displaySize.Y - ScreenMargin, -size.Y);

        foreach (KeyValuePair<string, WindowState> pair in Windows)
        {
            if (id != null && string.Equals(pair.Key, id, StringComparison.Ordinal)) continue;
            if (ignore != null && ignore.Contains(pair.Key)) continue;

            WindowState other = pair.Value;
            if (!other.Initialized || other.LastSeenFrame < frame - 1) continue;

            float otherLeft = other.Position.X;
            float otherRight = other.Position.X + other.Size.X;
            float otherCenterX = (otherLeft + otherRight) * 0.5f;
            float otherTop = other.Position.Y;
            float otherBottom = other.Position.Y + other.Size.Y;
            float otherCenterY = (otherTop + otherBottom) * 0.5f;

            if (RangesNear(top, bottom, otherTop, otherBottom, AlignmentReach))
            {
                // Left / centre / right can align to any left / centre / right guide.
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, left, otherLeft, 0f);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, left, otherCenterX, 0f);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, left, otherRight, 0f);

                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, centerX, otherLeft, -size.X * 0.5f);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, centerX, otherCenterX, -size.X * 0.5f);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, centerX, otherRight, -size.X * 0.5f);

                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, right, otherLeft, -size.X);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, right, otherCenterX, -size.X);
                ConsiderMoveX(ref bestX, ref bestXDistance, ref bestXGuide, right, otherRight, -size.X);
            }

            if (RangesNear(left, right, otherLeft, otherRight, AlignmentReach))
            {
                // Top / centre / bottom can align to any top / centre / bottom guide.
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, top, otherTop, 0f);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, top, otherCenterY, 0f);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, top, otherBottom, 0f);

                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, centerY, otherTop, -size.Y * 0.5f);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, centerY, otherCenterY, -size.Y * 0.5f);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, centerY, otherBottom, -size.Y * 0.5f);

                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, bottom, otherTop, -size.Y);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, bottom, otherCenterY, -size.Y);
                ConsiderMoveY(ref bestY, ref bestYDistance, ref bestYGuide, bottom, otherBottom, -size.Y);
            }
        }

        if (addGuides)
        {
            if (!float.IsNaN(bestXGuide)) AddGuide(GuideAxis.Vertical, bestXGuide);
            if (!float.IsNaN(bestYGuide)) AddGuide(GuideAxis.Horizontal, bestYGuide);
        }

        return ClampVisible(new Num.Vector2(bestX, bestY), size);
    }

    private static Num.Vector2 ClampVisible(Num.Vector2 position, Num.Vector2 size)
    {
        float x = Math.Max(0f, Math.Min(position.X, Math.Max(0f, displaySize.X - 40f)));
        float y = Math.Max(0f, Math.Min(position.Y, Math.Max(0f, displaySize.Y - 24f)));
        return new Num.Vector2(x, y);
    }

    private static void ConsiderEdge(
        ref float bestTarget,
        ref float bestDistance,
        float currentEdge,
        float targetEdge)
    {
        float distance = Math.Abs(currentEdge - targetEdge);
        if (distance > SnapDistance || distance >= bestDistance) return;
        bestDistance = distance;
        bestTarget = targetEdge;
    }

    private static void ConsiderMoveX(
        ref float bestPosition,
        ref float bestDistance,
        ref float bestGuide,
        float currentAnchor,
        float targetAnchor,
        float positionOffset)
    {
        float distance = Math.Abs(currentAnchor - targetAnchor);
        if (distance > SnapDistance || distance >= bestDistance) return;
        bestDistance = distance;
        bestPosition = targetAnchor + positionOffset;
        bestGuide = targetAnchor;
    }

    private static void ConsiderMoveY(
        ref float bestPosition,
        ref float bestDistance,
        ref float bestGuide,
        float currentAnchor,
        float targetAnchor,
        float positionOffset)
    {
        float distance = Math.Abs(currentAnchor - targetAnchor);
        if (distance > SnapDistance || distance >= bestDistance) return;
        bestDistance = distance;
        bestPosition = targetAnchor + positionOffset;
        bestGuide = targetAnchor;
    }

    private static bool RangesNear(float aMin, float aMax, float bMin, float bMax, float reach)
    {
        float overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
        if (overlap >= MinPerpendicularOverlap) return true;

        float gap = aMax < bMin ? bMin - aMax : bMax < aMin ? aMin - bMax : 0f;
        return gap <= reach;
    }

    private static void ResetManipulationMode(WindowState state)
    {
        state.WasMoving = false;
        state.WasResizing = false;
        state.ResizeLeft = false;
        state.ResizeRight = false;
        state.ResizeTop = false;
        state.ResizeBottom = false;
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
