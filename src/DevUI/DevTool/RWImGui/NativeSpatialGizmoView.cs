using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Gizmos;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Native scene-space interaction layer. It consumes only detached presentation snapshots and emits
/// NativeGizmoCommand values; no Rain World/DevInterface runtime object is dereferenced here.
/// </summary>
internal static class NativeSpatialGizmoView
{
    private const float PointRadius = 5.5f;
    private const float HitRadius = 11f;
    private const float DirectionLength = 92f;

    private struct DragState
    {
        internal bool Active;
        internal NativeGizmoTargetKind Target;
        internal int Index;
        internal float CenterWorldX;
        internal float CenterWorldY;
    }

    private static DragState drag;

    internal static void DrawObjects(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (!Ready(viewport, display) || snapshot?.SceneObjects == null) return;

        EditorObjectSnapshot nearest = null;
        float nearestDistance = float.MaxValue;
        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        for (int i = 0; i < snapshot.SceneObjects.Length; i++)
        {
            EditorObjectSnapshot item = snapshot.SceneObjects[i];
            if (item == null) continue;
            Num.Vector2 p = WorldToScreen(viewport, display, item.X, item.Y);
            DrawPoint(draw, p, item.Selected);
            float distance = DistanceSquared(mouse, p);
            if (distance < nearestDistance && distance <= HitRadius * HitRadius)
            {
                nearest = item;
                nearestDistance = distance;
            }
        }

        HandlePointInteraction(
            nearest?.Index ?? -1,
            NativeGizmoTargetKind.ObjectPosition,
            nearest?.X ?? 0f,
            nearest?.Y ?? 0f,
            viewport,
            display);
    }

    internal static void DrawSound(EditorSoundPresentationSnapshot snapshot, Num.Vector2 display)
    {
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (!Ready(viewport, display) || snapshot?.Sounds == null) return;

        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        EditorSoundSnapshot nearest = null;
        float nearestDistance = float.MaxValue;

        for (int i = 0; i < snapshot.Sounds.Length; i++)
        {
            EditorSoundSnapshot sound = snapshot.Sounds[i];
            if (sound == null || !string.Equals(sound.Type, "Spot", StringComparison.Ordinal)) continue;

            Num.Vector2 center = WorldToScreen(viewport, display, sound.X, sound.Y);
            DrawPoint(draw, center, sound.Selected);
            if (sound.Selected)
                DrawRadius(draw, center, WorldRadiusToScreen(viewport, display, sound.Radius));

            float distance = DistanceSquared(mouse, center);
            if (distance < nearestDistance && distance <= HitRadius * HitRadius)
            {
                nearest = sound;
                nearestDistance = distance;
            }
        }

        EditorSoundSnapshot selected = SelectedSound(snapshot);
        if (selected != null && !selected.Inherited && string.Equals(selected.Type, "Spot", StringComparison.Ordinal))
        {
            Num.Vector2 center = WorldToScreen(viewport, display, selected.X, selected.Y);
            float radius = WorldRadiusToScreen(viewport, display, selected.Radius);
            Num.Vector2 radiusHandle = new(center.X + radius, center.Y);
            DrawSecondaryHandle(draw, radiusHandle);
            if (!drag.Active && CanStartInteraction() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                DistanceSquared(mouse, radiusHandle) <= HitRadius * HitRadius)
            {
                BeginDrag(NativeGizmoTargetKind.SoundRadius, selected.Index, selected.X, selected.Y);
                return;
            }
        }
        else if (selected != null && !selected.Inherited && string.Equals(selected.Type, "Directional", StringComparison.Ordinal))
        {
            Num.Vector2 center = new(display.X * 0.5f, display.Y * 0.5f);
            Num.Vector2 direction = DirectionToScreen(viewport, display, selected.DirectionX, selected.DirectionY);
            Num.Vector2 end = center + direction * DirectionLength;
            DrawDirection(draw, center, end);
            if (!drag.Active && CanStartInteraction() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                DistanceSquared(mouse, end) <= HitRadius * HitRadius)
            {
                BeginDrag(NativeGizmoTargetKind.SoundDirection, selected.Index, 0f, 0f);
                return;
            }
        }

        HandlePointInteraction(
            nearest?.Index ?? -1,
            NativeGizmoTargetKind.SoundPosition,
            nearest?.X ?? 0f,
            nearest?.Y ?? 0f,
            viewport,
            display);

        ContinueSpecialSoundDrag(viewport, display, selected);
    }

    internal static void DrawTriggers(EditorTriggerPresentationSnapshot snapshot, Num.Vector2 display)
    {
        EditorViewportSnapshot viewport = EditorViewportPresentationHub.Current;
        if (!Ready(viewport, display) || snapshot?.Triggers == null) return;

        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        EditorTriggerSnapshot nearest = null;
        float nearestDistance = float.MaxValue;

        for (int i = 0; i < snapshot.Triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger = snapshot.Triggers[i];
            if (trigger == null || !trigger.IsSpot) continue;

            Num.Vector2 center = WorldToScreen(viewport, display, trigger.X, trigger.Y);
            DrawPoint(draw, center, trigger.Selected);
            if (trigger.Selected)
                DrawRadius(draw, center, WorldRadiusToScreen(viewport, display, trigger.Radius));

            float distance = DistanceSquared(mouse, center);
            if (distance < nearestDistance && distance <= HitRadius * HitRadius)
            {
                nearest = trigger;
                nearestDistance = distance;
            }
        }

        EditorTriggerSnapshot selected = SelectedTrigger(snapshot);
        if (selected != null && selected.IsSpot)
        {
            Num.Vector2 center = WorldToScreen(viewport, display, selected.X, selected.Y);
            float radius = WorldRadiusToScreen(viewport, display, selected.Radius);
            Num.Vector2 radiusHandle = new(center.X + radius, center.Y);
            DrawSecondaryHandle(draw, radiusHandle);
            if (!drag.Active && CanStartInteraction() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                DistanceSquared(mouse, radiusHandle) <= HitRadius * HitRadius)
            {
                BeginDrag(NativeGizmoTargetKind.TriggerRadius, selected.Index, selected.X, selected.Y);
                return;
            }
        }

        HandlePointInteraction(
            nearest?.Index ?? -1,
            NativeGizmoTargetKind.TriggerPosition,
            nearest?.X ?? 0f,
            nearest?.Y ?? 0f,
            viewport,
            display);

        ContinueTriggerRadiusDrag(viewport, display, selected);
    }

    internal static void ResetRetainedState()
    {
        if (drag.Active)
            NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
                NativeGizmoCommandKind.Cancel,
                drag.Target,
                drag.Index));
        drag = default;
    }

    private static void HandlePointInteraction(
        int index,
        NativeGizmoTargetKind target,
        float worldX,
        float worldY,
        EditorViewportSnapshot viewport,
        Num.Vector2 display)
    {
        if (drag.Active)
        {
            if (drag.Target != target) return;
            ContinuePositionDrag(viewport, display);
            return;
        }

        if (index < 0 || !CanStartInteraction() || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
            NativeGizmoCommandKind.Select,
            target,
            index));
        BeginDrag(target, index, worldX, worldY);
    }

    private static void BeginDrag(
        NativeGizmoTargetKind target,
        int index,
        float centerWorldX,
        float centerWorldY)
    {
        drag = new DragState
        {
            Active = true,
            Target = target,
            Index = index,
            CenterWorldX = centerWorldX,
            CenterWorldY = centerWorldY
        };
        NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
            NativeGizmoCommandKind.Begin,
            target,
            index));
    }

    private static void ContinuePositionDrag(EditorViewportSnapshot viewport, Num.Vector2 display)
    {
        if (!drag.Active) return;
        if (CancelRequested()) return;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Num.Vector2 world = ScreenToWorld(viewport, display, ImGui.GetIO().MousePos);
            NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
                NativeGizmoCommandKind.Update,
                drag.Target,
                drag.Index,
                world.X,
                world.Y));
        }

        CommitIfReleased();
    }

    private static void ContinueSpecialSoundDrag(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorSoundSnapshot selected)
    {
        if (!drag.Active || selected == null || drag.Index != selected.Index) return;
        if (drag.Target != NativeGizmoTargetKind.SoundRadius &&
            drag.Target != NativeGizmoTargetKind.SoundDirection)
            return;
        if (CancelRequested()) return;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (drag.Target == NativeGizmoTargetKind.SoundRadius)
            {
                Num.Vector2 world = ScreenToWorld(viewport, display, ImGui.GetIO().MousePos);
                float dx = world.X - drag.CenterWorldX;
                float dy = world.Y - drag.CenterWorldY;
                float radius = (float)Math.Sqrt(dx * dx + dy * dy);
                NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
                    NativeGizmoCommandKind.Update,
                    drag.Target,
                    drag.Index,
                    radius,
                    0f));
            }
            else
            {
                Num.Vector2 center = new(display.X * 0.5f, display.Y * 0.5f);
                Num.Vector2 mouse = ImGui.GetIO().MousePos;
                float scaleX = display.X / viewport.Width;
                float scaleY = display.Y / viewport.Height;
                float x = (mouse.X - center.X) / Math.Max(0.0001f, scaleX);
                float y = -(mouse.Y - center.Y) / Math.Max(0.0001f, scaleY);
                NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
                    NativeGizmoCommandKind.Update,
                    drag.Target,
                    drag.Index,
                    x,
                    y));
            }
        }

        CommitIfReleased();
    }

    private static void ContinueTriggerRadiusDrag(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        EditorTriggerSnapshot selected)
    {
        if (!drag.Active || drag.Target != NativeGizmoTargetKind.TriggerRadius ||
            selected == null || drag.Index != selected.Index)
            return;
        if (CancelRequested()) return;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Num.Vector2 world = ScreenToWorld(viewport, display, ImGui.GetIO().MousePos);
            float dx = world.X - drag.CenterWorldX;
            float dy = world.Y - drag.CenterWorldY;
            float radius = (float)Math.Sqrt(dx * dx + dy * dy);
            NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
                NativeGizmoCommandKind.Update,
                drag.Target,
                drag.Index,
                radius,
                0f));
        }

        CommitIfReleased();
    }

    private static bool CancelRequested()
    {
        if (!ImGui.IsKeyPressed(ImGuiKey.Escape)) return false;
        NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
            NativeGizmoCommandKind.Cancel,
            drag.Target,
            drag.Index));
        drag = default;
        return true;
    }

    private static void CommitIfReleased()
    {
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        NativeGizmoCommandQueue.Enqueue(new NativeGizmoCommand(
            NativeGizmoCommandKind.Commit,
            drag.Target,
            drag.Index));
        drag = default;
    }

    private static bool CanStartInteraction() =>
        !drag.Active && !ImGui.GetIO().WantCaptureMouse;

    private static bool Ready(EditorViewportSnapshot viewport, Num.Vector2 display) =>
        viewport?.Available == true && viewport.Width > 0f && viewport.Height > 0f && display.X > 0f && display.Y > 0f;

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

    private static float WorldRadiusToScreen(EditorViewportSnapshot viewport, Num.Vector2 display, float radius)
    {
        float scaleX = display.X / viewport.Width;
        float scaleY = display.Y / viewport.Height;
        return Math.Max(1f, radius * (scaleX + scaleY) * 0.5f);
    }

    private static Num.Vector2 DirectionToScreen(
        EditorViewportSnapshot viewport,
        Num.Vector2 display,
        float x,
        float y)
    {
        Num.Vector2 value = new(
            x * display.X / viewport.Width,
            -y * display.Y / viewport.Height);
        float length = value.Length();
        return length > 0.0001f ? value / length : new Num.Vector2(0f, 1f);
    }

    private static void DrawPoint(ImDrawListPtr draw, Num.Vector2 point, bool selected)
    {
        uint outer = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.06f, 0.08f, 0.10f, 0.95f));
        uint inner = ImGui.ColorConvertFloat4ToU32(selected
            ? new Num.Vector4(0.28f, 0.78f, 1f, 1f)
            : new Num.Vector4(0.78f, 0.82f, 0.86f, 0.9f));
        draw.AddCircleFilled(point, PointRadius + 2f, outer, 16);
        draw.AddCircleFilled(point, PointRadius, inner, 16);
    }

    private static void DrawSecondaryHandle(ImDrawListPtr draw, Num.Vector2 point)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(1f, 0.72f, 0.24f, 1f));
        draw.AddCircleFilled(point, PointRadius, color, 16);
    }

    private static void DrawRadius(ImDrawListPtr draw, Num.Vector2 center, float radius)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.28f, 0.78f, 1f, 0.58f));
        draw.AddCircle(center, radius, color, 64, 1.5f);
        draw.AddLine(center, new Num.Vector2(center.X + radius, center.Y), color, 1.2f);
    }

    private static void DrawDirection(ImDrawListPtr draw, Num.Vector2 center, Num.Vector2 end)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new Num.Vector4(1f, 0.72f, 0.24f, 0.92f));
        draw.AddLine(center, end, color, 2f);
        draw.AddCircleFilled(end, PointRadius, color, 16);
    }

    private static float DistanceSquared(Num.Vector2 a, Num.Vector2 b)
    {
        float x = a.X - b.X;
        float y = a.Y - b.Y;
        return x * x + y * y;
    }

    private static EditorSoundSnapshot SelectedSound(EditorSoundPresentationSnapshot snapshot)
    {
        if (snapshot?.Sounds == null || snapshot.SelectedIndex < 0 || snapshot.SelectedIndex >= snapshot.Sounds.Length)
            return null;
        return snapshot.Sounds[snapshot.SelectedIndex];
    }

    private static EditorTriggerSnapshot SelectedTrigger(EditorTriggerPresentationSnapshot snapshot)
    {
        if (snapshot?.Triggers == null || snapshot.SelectedIndex < 0 || snapshot.SelectedIndex >= snapshot.Triggers.Length)
            return null;
        return snapshot.Triggers[snapshot.SelectedIndex];
    }
}
