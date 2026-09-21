using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Gizmos;
using DryCycle.DevUI.DevTool.Objects;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Generic detached object gizmo renderer. It understands only points, anchor lines and cubic Bezier
/// segments. Rain World model semantics stay behind NativeObjectGizmoEditCommandQueue.
/// </summary>
internal static class NativeObjectGizmoView
{
    private const float PointRadius = 5f;
    private const float HitRadius = 11f;
    private const float CurveHitRadius = 9f;
    private const int CurveSamples = 28;

    private struct DragState
    {
        internal bool Active;
        internal int ObjectIndex;
        internal string HandleId;
    }

    private sealed class HandleCandidate
    {
        internal string Id;
        internal bool Removable;
        internal float DistanceSquared;
    }

    private sealed class CurveCandidate
    {
        internal int SegmentIndex;
        internal float T;
        internal float DistanceSquared;
    }

    private static DragState drag;
    private static bool claimedMouseThisFrame;

    internal static bool OwnsMouse => drag.Active || claimedMouseThisFrame;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        claimedMouseThisFrame = false;
        EditorInspectorSnapshot inspector = snapshot?.Inspector;
        EditorObjectGizmoSnapshot gizmo = inspector?.ObjectGizmo;
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;

        if (!Ready(viewport, display) ||
            inspector?.HasSelection != true ||
            inspector.SelectionCount != 1 ||
            gizmo == null ||
            gizmo.ObjectIndex != inspector.ObjectIndex)
        {
            CancelIfOrphaned();
            return;
        }

        if (drag.Active && drag.ObjectIndex != gizmo.ObjectIndex)
        {
            CancelDrag();
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 mouse = io.MousePos;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        DrawLines(draw, viewport, display, gizmo.Lines);
        CurveCandidate nearestCurve = DrawCurves(draw, viewport, display, gizmo.BezierSegments, mouse);
        HandleCandidate nearestHandle = DrawHandles(draw, viewport, display, gizmo.Handles, mouse);

        if (drag.Active)
        {
            ContinueDrag(viewport, display, io);
            return;
        }

        if (io.WantCaptureMouse || NativeSpatialGizmoView.OwnsMouse ||
            !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        // Match vanilla BezierSplineControl semantics: Shift-click near a curve splits it, while
        // Ctrl/Command-click on a midpoint joins adjacent segments.
        if (io.KeyShift && nearestCurve != null)
        {
            claimedMouseThisFrame = true;
            NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
                NativeObjectGizmoEditKind.InsertCurvePoint,
                gizmo.ObjectIndex,
                segmentIndex: nearestCurve.SegmentIndex,
                curveT: nearestCurve.T));
            return;
        }

        if (io.KeyCtrl && nearestHandle?.Removable == true)
        {
            claimedMouseThisFrame = true;
            NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
                NativeObjectGizmoEditKind.RemoveHandle,
                gizmo.ObjectIndex,
                handleId: nearestHandle.Id));
            return;
        }

        if (nearestHandle == null)
            return;

        claimedMouseThisFrame = true;
        drag = new DragState
        {
            Active = true,
            ObjectIndex = gizmo.ObjectIndex,
            HandleId = nearestHandle.Id
        };

        NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
            NativeObjectGizmoEditKind.Begin,
            drag.ObjectIndex,
            drag.HandleId));
    }

    internal static void ResetRetainedState()
    {
        CancelIfOrphaned();
        drag = default;
        claimedMouseThisFrame = false;
    }

    private static void ContinueDrag(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        ImGuiIOPtr io)
    {
        if (!drag.Active) return;

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CancelDrag();
            return;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Num.Vector2 world = ScreenToWorld(viewport, display, io.MousePos);
            NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
                NativeObjectGizmoEditKind.Update,
                drag.ObjectIndex,
                drag.HandleId,
                world.X,
                world.Y,
                snap: io.KeyShift));
        }

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;

        NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
            NativeObjectGizmoEditKind.Commit,
            drag.ObjectIndex,
            drag.HandleId));
        drag = default;
    }

    private static void DrawLines(
        ImDrawListPtr draw,
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorObjectLineSegmentSnapshot[] lines)
    {
        if (lines == null) return;

        for (int i = 0; i < lines.Length; i++)
        {
            EditorObjectLineSegmentSnapshot line = lines[i];
            if (line == null) continue;
            Num.Vector2 a = WorldToScreen(viewport, display, line.X0, line.Y0);
            Num.Vector2 b = WorldToScreen(viewport, display, line.X1, line.Y1);
            draw.AddLine(a, b, RegionColor(), 1.2f);
        }
    }

    private static HandleCandidate DrawHandles(
        ImDrawListPtr draw,
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorObjectGizmoHandleSnapshot[] handles,
        Num.Vector2 mouse)
    {
        HandleCandidate nearest = null;
        float nearestDistance = float.MaxValue;
        if (handles == null) return null;

        for (int i = 0; i < handles.Length; i++)
        {
            EditorObjectGizmoHandleSnapshot handle = handles[i];
            if (handle == null || string.IsNullOrEmpty(handle.Id)) continue;

            Num.Vector2 point = WorldToScreen(viewport, display, handle.X, handle.Y);
            if (handle.DrawAnchorLine)
            {
                Num.Vector2 anchor = WorldToScreen(
                    viewport,
                    display,
                    handle.AnchorX,
                    handle.AnchorY);
                draw.AddLine(anchor, point, LineColor(), 1.5f);
            }

            DrawPoint(draw, point, handle.Removable);

            float distance = DistanceSquared(mouse, point);
            if (distance <= HitRadius * HitRadius && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = new HandleCandidate
                {
                    Id = handle.Id,
                    Removable = handle.Removable,
                    DistanceSquared = distance
                };
            }
        }

        return nearest;
    }

    private static CurveCandidate DrawCurves(
        ImDrawListPtr draw,
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorObjectBezierSegmentSnapshot[] segments,
        Num.Vector2 mouse)
    {
        CurveCandidate nearest = null;
        float nearestDistance = float.MaxValue;
        if (segments == null) return null;

        for (int i = 0; i < segments.Length; i++)
        {
            EditorObjectBezierSegmentSnapshot segment = segments[i];
            if (segment == null) continue;

            Num.Vector2 p0 = WorldToScreen(viewport, display, segment.X0, segment.Y0);
            Num.Vector2 c0 = WorldToScreen(viewport, display, segment.C0X, segment.C0Y);
            Num.Vector2 p1 = WorldToScreen(viewport, display, segment.X1, segment.Y1);
            Num.Vector2 c1 = WorldToScreen(viewport, display, segment.C1X, segment.C1Y);

            Num.Vector2 previous = p0;
            for (int sample = 1; sample <= CurveSamples; sample++)
            {
                float t = sample / (float)CurveSamples;
                Num.Vector2 current = Cubic(p0, c0, c1, p1, t);
                draw.AddLine(previous, current, CurveColor(), 1.35f);

                float distance = DistanceToSegmentSquared(mouse, previous, current, out float along);
                if (distance <= CurveHitRadius * CurveHitRadius && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = new CurveCandidate
                    {
                        SegmentIndex = segment.SegmentIndex,
                        T = ((sample - 1) + along) / CurveSamples,
                        DistanceSquared = distance
                    };
                }

                previous = current;
            }
        }

        return nearest;
    }

    private static Num.Vector2 Cubic(
        Num.Vector2 p0,
        Num.Vector2 c0,
        Num.Vector2 c1,
        Num.Vector2 p1,
        float t)
    {
        float u = 1f - t;
        float uu = u * u;
        float tt = t * t;
        return p0 * (uu * u) +
               c0 * (3f * uu * t) +
               c1 * (3f * u * tt) +
               p1 * (tt * t);
    }

    private static float DistanceToSegmentSquared(
        Num.Vector2 point,
        Num.Vector2 a,
        Num.Vector2 b,
        out float along)
    {
        Num.Vector2 ab = b - a;
        float lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
        if (lengthSquared <= 0.0001f)
        {
            along = 0f;
            return DistanceSquared(point, a);
        }

        Num.Vector2 ap = point - a;
        along = (ap.X * ab.X + ap.Y * ab.Y) / lengthSquared;
        along = Math.Max(0f, Math.Min(1f, along));
        Num.Vector2 nearest = a + ab * along;
        return DistanceSquared(point, nearest);
    }

    private static void DrawPoint(ImDrawListPtr draw, Num.Vector2 point, bool removable)
    {
        uint outer = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.06f, 0.08f, 0.10f, 0.96f));
        uint inner = ImGui.ColorConvertFloat4ToU32(
            removable
                ? new Num.Vector4(0.95f, 0.52f, 0.22f, 1f)
                : new Num.Vector4(1f, 0.72f, 0.24f, 1f));
        draw.AddCircleFilled(point, PointRadius + 2f, outer, 16);
        draw.AddCircleFilled(point, PointRadius, inner, 16);
    }

    private static uint LineColor() =>
        ImGui.ColorConvertFloat4ToU32(new Num.Vector4(1f, 0.72f, 0.24f, 0.78f));

    private static uint CurveColor() =>
        ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.40f, 0.83f, 0.94f, 0.78f));

    private static uint RegionColor() =>
        ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.32f, 0.68f, 0.96f, 0.62f));

    private static void CancelIfOrphaned()
    {
        if (drag.Active)
            CancelDrag();
    }

    private static void CancelDrag()
    {
        if (drag.Active)
        {
            NativeObjectGizmoEditCommandQueue.Enqueue(new NativeObjectGizmoEditCommand(
                NativeObjectGizmoEditKind.Cancel,
                drag.ObjectIndex,
                drag.HandleId));
        }
        drag = default;
    }

    private static bool Ready(EditorViewportSnapshot viewport, Num.Vector2 display) =>
        viewport?.Available == true &&
        viewport.Width > 0f &&
        viewport.Height > 0f &&
        display.X > 0f &&
        display.Y > 0f;

    private static Num.Vector2 WorldToScreen(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        float worldX,
        float worldY)
    {
        float scaleX = display.X / viewport.Width;
        float scaleY = display.Y / viewport.Height;
        return new Num.Vector2(
            (worldX - viewport.CameraX) * scaleX,
            display.Y - (worldY - viewport.CameraY) * scaleY);
    }

    private static Num.Vector2 ScreenToWorld(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        Num.Vector2 screen)
    {
        float scaleX = display.X / viewport.Width;
        float scaleY = display.Y / viewport.Height;
        return new Num.Vector2(
            viewport.CameraX + screen.X / Math.Max(0.0001f, scaleX),
            viewport.CameraY + (display.Y - screen.Y) / Math.Max(0.0001f, scaleY));
    }

    private static float DistanceSquared(Num.Vector2 a, Num.Vector2 b)
    {
        float x = a.X - b.X;
        float y = a.Y - b.Y;
        return x * x + y * y;
    }
}
