using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Room-background marquee selection for the Objects workspace.
///
/// The frontend consumes only detached object/viewport snapshots and emits ordinary selection
/// commands. It never dereferences RoomCamera, RoomSettings or PlacedObject from the render thread.
/// </summary>
internal static class ObjectMarqueeSelectionView
{
    private const float StartThresholdPixels = 5f;
    private const float ObjectHitPaddingPixels = 13f;

    private static bool armed;
    private static bool dragging;
    private static bool gridVisible = true;
    private static float gridStep = 20f;
    private static Num.Vector2 start;
    private static Num.Vector2 current;

    internal static bool OwnsMouse => armed || dragging;
    internal static bool GridVisible
    {
        get => gridVisible;
        set => gridVisible = value;
    }

    internal static float GridStep
    {
        get => gridStep;
        set => gridStep = Math.Max(1f, value);
    }

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || snapshot.ToolMode != EditorToolMode.Objects || snapshot.PlacementActive)
        {
            Reset();
            return;
        }

        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (viewport?.Available != true || display.X <= 1f || display.Y <= 1f)
        {
            Reset();
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();

        if (gridVisible)
            DrawGrid(viewport, display, gridStep);

        if (!armed && !dragging)
        {
            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
                ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) ||
                FloatingWindowSnap.OwnsMouse ||
                NativeObjectGizmoView.OwnsMouse ||
                NativeSpatialGizmoView.OwnsMouse)
                return;

            Num.Vector2 mouse = io.MousePos;
            if (IsNearObject(snapshot.SceneObjects, viewport, display, mouse))
                return;

            armed = true;
            start = mouse;
            current = mouse;
        }

        if (armed || dragging)
        {
            current = io.MousePos;

            if (!dragging)
            {
                Num.Vector2 delta = current - start;
                if (delta.LengthSquared() >= StartThresholdPixels * StartThresholdPixels)
                {
                    dragging = true;
                    armed = false;
                }
            }

            if (dragging)
                DrawMarquee(start, current);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (dragging)
                    Commit(snapshot.SceneObjects, viewport, display, start, current, io.KeyShift, io.KeyCtrl);
                Reset();
            }
        }
    }

    internal static void Reset()
    {
        armed = false;
        dragging = false;
        start = default;
        current = default;
    }

    internal static void ResetRetainedState()
    {
        Reset();
        gridVisible = true;
        gridStep = 20f;
    }

    private static void Commit(
        EditorObjectSnapshot[] objects,
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        Num.Vector2 a,
        Num.Vector2 b,
        bool shift,
        bool ctrl)
    {
        objects ??= Array.Empty<EditorObjectSnapshot>();

        float minX = Math.Min(a.X, b.X);
        float maxX = Math.Max(a.X, b.X);
        float minY = Math.Min(a.Y, b.Y);
        float maxY = Math.Max(a.Y, b.Y);

        // Default marquee replaces the selection. Shift adds hits. Ctrl toggles hits.
        if (!shift && !ctrl)
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, index: -1));

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;

            Num.Vector2 point = WorldToScreen(item.X, item.Y, viewport, display);
            if (point.X < minX || point.X > maxX || point.Y < minY || point.Y > maxY)
                continue;

            if (ctrl)
            {
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.ToggleObjectSelection,
                    index: item.Index));
                continue;
            }

            // Shift is additive: already-selected members stay selected rather than being toggled off.
            if (shift && item.Selected)
                continue;

            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.ToggleObjectSelection,
                index: item.Index));
        }
    }

    private static bool IsNearObject(
        EditorObjectSnapshot[] objects,
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        Num.Vector2 mouse)
    {
        if (objects == null) return false;
        float limitSq = ObjectHitPaddingPixels * ObjectHitPaddingPixels;
        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;
            Num.Vector2 point = WorldToScreen(item.X, item.Y, viewport, display);
            if ((point - mouse).LengthSquared() <= limitSq)
                return true;
        }
        return false;
    }

    private static Num.Vector2 WorldToScreen(
        float worldX,
        float worldY,
        EditorViewportSnapshot viewport,
        Num.Vector2 display)
    {
        float nx = (worldX - viewport.CameraX) / Math.Max(1f, viewport.Width);
        float ny = (worldY - viewport.CameraY) / Math.Max(1f, viewport.Height);
        return new Num.Vector2(nx * display.X, display.Y - ny * display.Y);
    }

    private static void DrawGrid(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        float step)
    {
        step = Math.Max(1f, step);
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        uint minor = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.72f, 0.82f, 0.92f, 0.10f));
        uint major = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.82f, 0.90f, 1.00f, 0.18f));

        float leftWorld = viewport.CameraX;
        float rightWorld = viewport.CameraX + viewport.Width;
        float bottomWorld = viewport.CameraY;
        float topWorld = viewport.CameraY + viewport.Height;

        int firstX = (int)Math.Floor(leftWorld / step);
        int lastX = (int)Math.Ceiling(rightWorld / step);
        for (int gx = firstX; gx <= lastX; gx++)
        {
            float worldX = gx * step;
            Num.Vector2 a = WorldToScreen(worldX, bottomWorld, viewport, display);
            Num.Vector2 b = WorldToScreen(worldX, topWorld, viewport, display);
            draw.AddLine(a, b, gx % 5 == 0 ? major : minor, gx % 5 == 0 ? 1.25f : 1f);
        }

        int firstY = (int)Math.Floor(bottomWorld / step);
        int lastY = (int)Math.Ceiling(topWorld / step);
        for (int gy = firstY; gy <= lastY; gy++)
        {
            float worldY = gy * step;
            Num.Vector2 a = WorldToScreen(leftWorld, worldY, viewport, display);
            Num.Vector2 b = WorldToScreen(rightWorld, worldY, viewport, display);
            draw.AddLine(a, b, gy % 5 == 0 ? major : minor, gy % 5 == 0 ? 1.25f : 1f);
        }
    }

    private static void DrawMarquee(Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 min = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        Num.Vector2 max = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.30f, 0.62f, 1.00f, 0.12f));
        uint border = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.55f, 0.80f, 1.00f, 0.95f));
        draw.AddRectFilled(min, max, fill);
        draw.AddRect(min, max, border, 0f, ImDrawFlags.None, 1.5f);
    }
}
