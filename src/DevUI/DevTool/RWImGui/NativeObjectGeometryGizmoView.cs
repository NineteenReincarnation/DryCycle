using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Gizmos;
using DryCycle.DevUI.DevTool.Objects;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Native secondary handles derived from detached object Inspector properties. The first migration
/// pattern is PlacedObject.Data.handlePos: Rain World stores this as an offset from owner.pos across
/// its resizable/radius/rect/path-style builtin objects. The frontend never reflects or dereferences
/// PlacedObject.Data; it only consumes the detached key/value emitted by the backend inspector.
/// </summary>
internal static class NativeObjectGeometryGizmoView
{
    private const float PointRadius = 5f;
    private const float HitRadius = 11f;

    private struct DragState
    {
        internal bool Active;
        internal int ObjectIndex;
        internal string PropertyKey;
        internal float CenterWorldX;
        internal float CenterWorldY;
    }

    private sealed class Candidate
    {
        internal string Key;
        internal Num.Vector2 Screen;
    }

    private static DragState drag;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        EditorInspectorSnapshot inspector = snapshot?.Inspector;
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (!Ready(viewport, display) || inspector?.HasSelection != true || inspector.SelectionCount != 1)
        {
            CancelIfOrphaned();
            return;
        }

        if (drag.Active && drag.ObjectIndex != inspector.ObjectIndex)
        {
            CancelDrag();
            return;
        }

        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Num.Vector2 center = WorldToScreen(viewport, display, inspector.X, inspector.Y);
        Candidate nearest = null;
        float nearestDistance = float.MaxValue;

        EditorPropertySnapshot[] properties = inspector.Properties ?? Array.Empty<EditorPropertySnapshot>();
        for (int i = 0; i < properties.Length; i++)
        {
            EditorPropertySnapshot property = properties[i];
            if (!IsNativeOffsetHandle(property)) continue;

            Num.Vector2 endpoint = WorldToScreen(
                viewport,
                display,
                inspector.X + property.X,
                inspector.Y + property.Y);
            DrawHandle(draw, center, endpoint);

            float distance = DistanceSquared(mouse, endpoint);
            if (distance <= HitRadius * HitRadius && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = new Candidate { Key = property.Key, Screen = endpoint };
            }
        }

        if (drag.Active)
        {
            ContinueDrag(viewport, display, inspector);
            return;
        }

        if (nearest == null || ImGui.GetIO().WantCaptureMouse ||
            !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        drag = new DragState
        {
            Active = true,
            ObjectIndex = inspector.ObjectIndex,
            PropertyKey = nearest.Key,
            CenterWorldX = inspector.X,
            CenterWorldY = inspector.Y
        };
        NativeObjectGeometryGizmoCommandQueue.Enqueue(new NativeObjectGeometryGizmoCommand(
            NativeGizmoCommandKind.Begin,
            drag.ObjectIndex,
            drag.PropertyKey));
    }

    internal static void ResetRetainedState()
    {
        CancelIfOrphaned();
        drag = default;
    }

    private static void ContinueDrag(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorInspectorSnapshot inspector)
    {
        if (!drag.Active) return;

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CancelDrag();
            return;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Num.Vector2 world = ScreenToWorld(viewport, display, ImGui.GetIO().MousePos);
            float offsetX = world.X - drag.CenterWorldX;
            float offsetY = world.Y - drag.CenterWorldY;
            NativeObjectGeometryGizmoCommandQueue.Enqueue(new NativeObjectGeometryGizmoCommand(
                NativeGizmoCommandKind.Update,
                drag.ObjectIndex,
                drag.PropertyKey,
                offsetX,
                offsetY));
        }

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        NativeObjectGeometryGizmoCommandQueue.Enqueue(new NativeObjectGeometryGizmoCommand(
            NativeGizmoCommandKind.Commit,
            drag.ObjectIndex,
            drag.PropertyKey));
        drag = default;
    }

    private static void CancelIfOrphaned()
    {
        if (drag.Active)
            CancelDrag();
    }

    private static void CancelDrag()
    {
        if (drag.Active)
        {
            NativeObjectGeometryGizmoCommandQueue.Enqueue(new NativeObjectGeometryGizmoCommand(
                NativeGizmoCommandKind.Cancel,
                drag.ObjectIndex,
                drag.PropertyKey));
        }
        drag = default;
    }

    private static bool IsNativeOffsetHandle(EditorPropertySnapshot property)
    {
        if (property == null || property.Kind != EditorPropertyKind.Vector2) return false;
        if (!string.Equals(property.Source, "Rain World model", StringComparison.Ordinal)) return false;
        string key = property.Key ?? string.Empty;
        return key.EndsWith(".handlePos", StringComparison.Ordinal);
    }

    private static bool Ready(EditorViewportSnapshot viewport, Num.Vector2 display) =>
        viewport?.Available == true && viewport.Width > 0f && viewport.Height > 0f &&
        display.X > 0f && display.Y > 0f;

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

    private static void DrawHandle(ImDrawListPtr draw, Num.Vector2 center, Num.Vector2 endpoint)
    {
        uint line = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(1f, 0.72f, 0.24f, 0.85f));
        uint outer = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.06f, 0.08f, 0.10f, 0.96f));
        uint inner = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(1f, 0.72f, 0.24f, 1f));
        draw.AddLine(center, endpoint, line, 1.5f);
        draw.AddCircleFilled(endpoint, PointRadius + 2f, outer, 16);
        draw.AddCircleFilled(endpoint, PointRadius, inner, 16);
    }
}