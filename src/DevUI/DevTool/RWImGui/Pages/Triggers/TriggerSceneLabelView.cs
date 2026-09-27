using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Clickable world labels for named spatial triggers. The visual language mirrors Object labels:
/// compact dark cards, a short leader to the authored world point, and direct selection on click.
/// Only triggers with a real world position (currently SpotTrigger) are projected into the room.
/// </summary>
internal static class TriggerSceneLabelView
{
    private const float PaddingX = 6f;
    private const float PaddingY = 3f;
    private const float Gap = 4f;
    private const float HitPadding = 4f;

    private sealed class Label
    {
        internal EditorTriggerSnapshot Trigger;
        internal Num.Vector2 Anchor;
        internal Num.Vector2 Min;
        internal Num.Vector2 Max;
    }

    private static readonly List<Label> placed = new();

    internal static bool OwnsMouse { get; private set; }

    internal static void Draw(
        EditorTriggerPresentationSnapshot snapshot,
        Num.Vector2 display)
    {
        OwnsMouse = false;
        EditorViewportSnapshot viewport =
            EditorViewportPresentationHub.Current;

        if (snapshot?.Available != true ||
            snapshot.Triggers == null ||
            viewport?.Available != true ||
            viewport.Width <= 0f ||
            viewport.Height <= 0f ||
            display.X <= 1f ||
            display.Y <= 1f)
        {
            placed.Clear();
            return;
        }

        Build(snapshot.Triggers, viewport, display);

        ImGuiIOPtr io =
            ImGui.GetIO();
        bool pointerBlocked =
            io.WantCaptureMouse ||
            FloatingWindowSnap.OwnsMouse ||
            NativeSpatialGizmoView.OwnsMouse;

        Label hit =
            pointerBlocked
                ? null
                : HitTest(io.MousePos);

        ImDrawListPtr draw =
            ImGui.GetBackgroundDrawList();

        for (int i = 0; i < placed.Count; i++)
        {
            Label label =
                placed[i];
            DrawLabel(
                draw,
                label,
                ReferenceEquals(label, hit));
        }

        if (hit == null ||
            pointerBlocked ||
            !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        OwnsMouse = true;
        TriggerEditorCommandQueue.Enqueue(
            new TriggerEditorCommand(
                TriggerEditorCommandKind.Select,
                hit.Trigger.Index));
    }

    internal static void ResetRetainedState()
    {
        placed.Clear();
        OwnsMouse = false;
    }

    private static void Build(
        EditorTriggerSnapshot[] triggers,
        EditorViewportSnapshot viewport,
        Num.Vector2 display)
    {
        placed.Clear();

        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger =
                triggers[i];

            if (trigger == null ||
                !trigger.IsSpot ||
                string.IsNullOrWhiteSpace(trigger.Name))
                continue;

            Num.Vector2 anchor =
                WorldToScreen(
                    trigger.X,
                    trigger.Y,
                    viewport,
                    display);

            if (anchor.X < -160f ||
                anchor.X > display.X + 160f ||
                anchor.Y < -80f ||
                anchor.Y > display.Y + 80f)
                continue;

            Num.Vector2 size =
                ImGui.CalcTextSize(trigger.Name) +
                new Num.Vector2(
                    PaddingX * 2f,
                    PaddingY * 2f);

            Label label =
                new()
                {
                    Trigger = trigger,
                    Anchor = anchor
                };

            Place(
                label,
                size,
                display);

            placed.Add(label);
        }
    }

    private static void Place(
        Label label,
        Num.Vector2 size,
        Num.Vector2 display)
    {
        for (int i = 0; i < 8; i++)
        {
            Num.Vector2 min =
                label.Anchor +
                PlacementOffset(i, size);
            Num.Vector2 max =
                min + size;

            Clamp(
                ref min,
                ref max,
                display);

            if (!Overlaps(min, max))
            {
                label.Min = min;
                label.Max = max;
                return;
            }
        }

        Num.Vector2 fallbackMin =
            label.Anchor +
            new Num.Vector2(
                8f,
                -size.Y - 8f);
        Num.Vector2 fallbackMax =
            fallbackMin + size;

        Clamp(
            ref fallbackMin,
            ref fallbackMax,
            display);

        label.Min =
            fallbackMin;
        label.Max =
            fallbackMax;
    }

    private static Num.Vector2 PlacementOffset(
        int index,
        Num.Vector2 size) =>
        index switch
        {
            0 => new Num.Vector2(8f, -size.Y * 0.5f),
            1 => new Num.Vector2(8f, 8f),
            2 => new Num.Vector2(8f, -size.Y - 8f),
            3 => new Num.Vector2(-size.X - 8f, -size.Y * 0.5f),
            4 => new Num.Vector2(-size.X - 8f, 8f),
            5 => new Num.Vector2(-size.X - 8f, -size.Y - 8f),
            6 => new Num.Vector2(-size.X * 0.5f, 10f),
            _ => new Num.Vector2(-size.X * 0.5f, -size.Y - 10f)
        };

    private static bool Overlaps(
        Num.Vector2 min,
        Num.Vector2 max)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            Label other =
                placed[i];

            if (max.X + Gap < other.Min.X ||
                min.X - Gap > other.Max.X ||
                max.Y + Gap < other.Min.Y ||
                min.Y - Gap > other.Max.Y)
                continue;

            return true;
        }

        return false;
    }

    private static Label HitTest(
        Num.Vector2 point)
    {
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            Label label =
                placed[i];

            if (point.X >= label.Min.X - HitPadding &&
                point.X <= label.Max.X + HitPadding &&
                point.Y >= label.Min.Y - HitPadding &&
                point.Y <= label.Max.Y + HitPadding)
                return label;
        }

        return null;
    }

    private static void DrawLabel(
        ImDrawListPtr draw,
        Label label,
        bool hovered)
    {
        bool selected =
            label.Trigger.Selected;
        bool strong =
            selected || hovered;

        uint background =
            ImGui.ColorConvertFloat4ToU32(
                new Num.Vector4(
                    0.035f,
                    0.045f,
                    0.055f,
                    strong ? 0.86f : 0.68f));
        uint border =
            ImGui.ColorConvertFloat4ToU32(
                selected
                    ? new Num.Vector4(0.28f, 0.78f, 1f, 0.96f)
                    : hovered
                        ? new Num.Vector4(0.70f, 0.86f, 0.98f, 0.90f)
                        : new Num.Vector4(0.55f, 0.76f, 0.92f, 0.22f));
        uint text =
            ImGui.ColorConvertFloat4ToU32(
                new Num.Vector4(
                    0.94f,
                    0.97f,
                    1f,
                    1f));
        uint leader =
            ImGui.ColorConvertFloat4ToU32(
                new Num.Vector4(
                    0.68f,
                    0.78f,
                    0.88f,
                    strong ? 0.66f : 0.30f));

        Num.Vector2 nearest =
            new(
                Math.Max(
                    label.Min.X,
                    Math.Min(
                        label.Anchor.X,
                        label.Max.X)),
                Math.Max(
                    label.Min.Y,
                    Math.Min(
                        label.Anchor.Y,
                        label.Max.Y)));

        draw.AddLine(
            label.Anchor,
            nearest,
            leader,
            strong ? 1.2f : 1f);
        draw.AddRectFilled(
            label.Min,
            label.Max,
            background,
            4f);
        draw.AddRect(
            label.Min,
            label.Max,
            border,
            4f,
            ImDrawFlags.None,
            strong ? 1.2f : 1f);
        draw.AddText(
            label.Min +
            new Num.Vector2(
                PaddingX,
                PaddingY),
            text,
            label.Trigger.Name);
    }

    private static void Clamp(
        ref Num.Vector2 min,
        ref Num.Vector2 max,
        Num.Vector2 display)
    {
        Num.Vector2 size =
            max - min;

        min.X =
            Math.Max(
                2f,
                Math.Min(
                    Math.Max(
                        2f,
                        display.X - size.X - 2f),
                    min.X));
        min.Y =
            Math.Max(
                2f,
                Math.Min(
                    Math.Max(
                        2f,
                        display.Y - size.Y - 2f),
                    min.Y));

        max =
            min + size;
    }

    private static Num.Vector2 WorldToScreen(
        float x,
        float y,
        EditorViewportSnapshot viewport,
        Num.Vector2 display)
    {
        float nx =
            (x - viewport.CameraX) /
            Math.Max(
                1f,
                viewport.Width);
        float ny =
            (y - viewport.CameraY) /
            Math.Max(
                1f,
                viewport.Height);

        return new Num.Vector2(
            nx * display.X,
            display.Y - ny * display.Y);
    }
}
