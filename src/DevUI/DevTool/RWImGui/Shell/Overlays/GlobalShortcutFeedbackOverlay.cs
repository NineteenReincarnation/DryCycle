using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// One top-center animation surface for every keyboard shortcut in the rebuilt DevTool.
///
/// Shortcut handlers publish immutable feedback payloads through EditorShortcutFeedback. This class
/// is page-agnostic and therefore automatically works for global shortcuts and view-specific ones.
/// </summary>
internal static class GlobalShortcutFeedbackOverlay
{
    private static readonly Num.Vector4 SaveAccent = new(0.35f, 0.86f, 0.58f, 1f);
    private static readonly Num.Vector4 UndoAccent = new(0.33f, 0.68f, 0.96f, 1f);
    private static readonly Num.Vector4 RedoAccent = new(0.68f, 0.52f, 0.96f, 1f);
    private static readonly Num.Vector4 ToggleAccent = new(0.36f, 0.74f, 0.95f, 1f);
    private static readonly Num.Vector4 DuplicateAccent = new(0.52f, 0.72f, 0.96f, 1f);
    private static readonly Num.Vector4 DeleteAccent = new(0.94f, 0.46f, 0.38f, 1f);
    private static readonly Num.Vector4 SelectAccent = new(0.43f, 0.80f, 0.77f, 1f);
    private static readonly Num.Vector4 GroupAccent = new(0.67f, 0.57f, 0.94f, 1f);
    private static readonly Num.Vector4 MoveAccent = new(0.46f, 0.74f, 0.94f, 1f);
    private static readonly Num.Vector4 CopyAccent = new(0.47f, 0.76f, 0.90f, 1f);
    private static readonly Num.Vector4 PasteAccent = new(0.43f, 0.82f, 0.62f, 1f);
    private static readonly Num.Vector4 LayerAccent = new(0.88f, 0.68f, 0.34f, 1f);
    private static readonly Num.Vector4 CancelAccent = new(0.84f, 0.56f, 0.36f, 1f);
    private static readonly Num.Vector4 WarningAccent = new(0.96f, 0.68f, 0.30f, 1f);
    private static readonly Num.Vector4 Background = new(0.022f, 0.032f, 0.046f, 0.965f);
    private static readonly Num.Vector4 MainText = new(0.94f, 0.965f, 1f, 1f);
    private static readonly Num.Vector4 SecondaryText = new(0.62f, 0.68f, 0.76f, 1f);

    private const double VisibleSeconds = 1.32d;
    private const double EnterSeconds = 0.16d;
    private const double FadeSeconds = 0.28d;
    private const double MarkSeconds = 0.30d;

    private static int observedRevision;
    private static EditorShortcutFeedbackSnapshot feedback;
    private static bool active;
    private static double shownAt = -1000d;

    internal static void Draw(Num.Vector2 display)
    {
        ObserveSignal();
        if (!active || feedback == null)
            return;

        double age = ImGui.GetTime() - shownAt;
        if (age < 0d || age >= VisibleSeconds)
        {
            active = false;
            return;
        }

        float enter = Smooth01((float)(age / EnterSeconds));
        float fade = 1f;
        double fadeStart = VisibleSeconds - FadeSeconds;
        if (age > fadeStart)
            fade = Smooth01((float)((VisibleSeconds - age) / FadeSeconds));

        float alpha = enter * fade;
        string title = DevToolUiSettings.IsChinese
            ? feedback.ChineseTitle
            : feedback.EnglishTitle;
        if (string.IsNullOrWhiteSpace(title))
            title = DevToolUiSettings.T("已执行", "Done");

        string detail = feedback.Keys ?? string.Empty;
        Num.Vector4 accent = ResolveAccent(feedback);

        float titleWidth = ImGui.CalcTextSize(title).X;
        float detailWidth = ImGui.CalcTextSize(detail).X;
        float width = Math.Max(218f, Math.Max(titleWidth, detailWidth) + 78f);
        float height = Math.Max(52f, ImGui.GetFrameHeight() * 2f + 12f);
        float x = Math.Max(8f, (display.X - width) * 0.5f);
        float y = 9f - (1f - enter) * 11f;

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(Background.W * alpha);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Num.Vector2(10f, 7f));

        Num.Vector4 border = accent;
        border.W = 0.90f * alpha;
        ImGui.PushStyleColor(ImGuiCol.Border, border);

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (ImGui.Begin("##DevToolGlobalShortcutFeedback", flags))
        {
            DrawAnimatedMark(age, alpha, accent, feedback);

            ImGui.SetCursorPos(new Num.Vector2(48f, 6f));
            Num.Vector4 titleColor = MainText;
            titleColor.W *= alpha;
            ImGui.TextColored(titleColor, title);

            ImGui.SetCursorPos(new Num.Vector2(48f, Math.Max(25f, height - 24f)));
            Num.Vector4 detailColor = SecondaryText;
            detailColor.W *= alpha;
            ImGui.TextColored(detailColor, detail);

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            Num.Vector2 min = ImGui.GetWindowPos();
            Num.Vector2 max = min + ImGui.GetWindowSize();
            float remaining =
                (float)Math.Max(
                    0d,
                    Math.Min(
                        1d,
                        1d - age / VisibleSeconds));

            Num.Vector4 rail = accent;
            rail.W = 0.86f * alpha;
            draw.AddRectFilled(
                new Num.Vector2(min.X + 5f, max.Y - 3f),
                new Num.Vector2(
                    min.X + 5f + (max.X - min.X - 10f) * remaining,
                    max.Y - 1f),
                ImGui.GetColorU32(rail),
                1f);
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    private static void ObserveSignal()
    {
        int revision = EditorShortcutFeedback.Revision;
        if (revision == observedRevision)
            return;

        observedRevision = revision;
        feedback = EditorShortcutFeedback.Current;
        shownAt = ImGui.GetTime();
        active =
            feedback != null &&
            feedback.Kind != EditorShortcutFeedbackKind.None;
    }

    private static Num.Vector4 ResolveAccent(EditorShortcutFeedbackSnapshot item)
    {
        if (item == null)
            return WarningAccent;

        if (!item.Succeeded &&
            item.Visual != EditorShortcutFeedbackVisual.Delete)
            return WarningAccent;

        return item.Visual switch
        {
            EditorShortcutFeedbackVisual.Save => item.Succeeded ? SaveAccent : DeleteAccent,
            EditorShortcutFeedbackVisual.Undo => UndoAccent,
            EditorShortcutFeedbackVisual.Redo => RedoAccent,
            EditorShortcutFeedbackVisual.Toggle => ToggleAccent,
            EditorShortcutFeedbackVisual.Duplicate => DuplicateAccent,
            EditorShortcutFeedbackVisual.Delete => DeleteAccent,
            EditorShortcutFeedbackVisual.Select => SelectAccent,
            EditorShortcutFeedbackVisual.Group => GroupAccent,
            EditorShortcutFeedbackVisual.Move => MoveAccent,
            EditorShortcutFeedbackVisual.Copy => CopyAccent,
            EditorShortcutFeedbackVisual.Paste => PasteAccent,
            EditorShortcutFeedbackVisual.Layer => LayerAccent,
            EditorShortcutFeedbackVisual.Cancel => CancelAccent,
            EditorShortcutFeedbackVisual.Warning => WarningAccent,
            _ => SaveAccent
        };
    }

    private static void DrawAnimatedMark(
        double age,
        float alpha,
        Num.Vector4 accent,
        EditorShortcutFeedbackSnapshot item)
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 window = ImGui.GetWindowPos();

        float pop = Smooth01((float)(age / EnterSeconds));
        float mark = Smooth01((float)(age / MarkSeconds));
        float size = 25f * (0.82f + 0.18f * pop);
        Num.Vector2 center = window + new Num.Vector2(27f, 25f);
        float radius = size * 0.5f;

        Num.Vector4 ring = accent;
        ring.W *= alpha;
        uint color = ImGui.GetColorU32(ring);

        draw.AddCircle(center, radius, color, 28, 2f);

        if (!item.Succeeded)
        {
            DrawWarningMark(draw, center, color, mark);
            return;
        }

        switch (item.Visual)
        {
            case EditorShortcutFeedbackVisual.Undo:
                DrawArrow(draw, center, color, mark, left: true);
                break;
            case EditorShortcutFeedbackVisual.Redo:
                DrawArrow(draw, center, color, mark, left: false);
                break;
            case EditorShortcutFeedbackVisual.Delete:
            case EditorShortcutFeedbackVisual.Cancel:
                DrawCross(draw, center, color, mark);
                break;
            case EditorShortcutFeedbackVisual.Move:
            case EditorShortcutFeedbackVisual.Layer:
                DrawArrow(draw, center, color, mark, left: false);
                break;
            default:
                DrawCheck(draw, center, color, mark);
                break;
        }
    }

    private static void DrawCheck(
        ImDrawListPtr draw,
        Num.Vector2 center,
        uint color,
        float mark)
    {
        Num.Vector2 a = center + new Num.Vector2(-6.0f, 0.5f);
        Num.Vector2 b = center + new Num.Vector2(-1.6f, 5.0f);
        Num.Vector2 c = center + new Num.Vector2(7.0f, -5.3f);

        float first = Math.Min(1f, mark * 2f);
        draw.AddLine(a, a + (b - a) * first, color, 2.3f);

        if (mark <= 0.5f)
            return;

        float second = Math.Min(1f, (mark - 0.5f) * 2f);
        draw.AddLine(b, b + (c - b) * second, color, 2.3f);
    }

    private static void DrawArrow(
        ImDrawListPtr draw,
        Num.Vector2 center,
        uint color,
        float mark,
        bool left)
    {
        float direction = left ? -1f : 1f;
        Num.Vector2 tail = center + new Num.Vector2(-direction * 6.5f, 3.5f);
        Num.Vector2 head = center + new Num.Vector2(direction * 6.0f, -2.5f);

        draw.AddLine(tail, tail + (head - tail) * mark, color, 2.2f);

        if (mark < 0.45f)
            return;

        float headProgress = Math.Min(1f, (mark - 0.45f) / 0.55f);
        Num.Vector2 upper = head + new Num.Vector2(-direction * 5.0f, -4.0f);
        Num.Vector2 lower = head + new Num.Vector2(-direction * 5.0f, 4.0f);
        draw.AddLine(head, head + (upper - head) * headProgress, color, 2.2f);
        draw.AddLine(head, head + (lower - head) * headProgress, color, 2.2f);
    }

    private static void DrawCross(
        ImDrawListPtr draw,
        Num.Vector2 center,
        uint color,
        float mark)
    {
        Num.Vector2 a = center + new Num.Vector2(-5.5f, -5.5f);
        Num.Vector2 b = center + new Num.Vector2(5.5f, 5.5f);
        Num.Vector2 c = center + new Num.Vector2(5.5f, -5.5f);
        Num.Vector2 d = center + new Num.Vector2(-5.5f, 5.5f);
        draw.AddLine(a, a + (b - a) * mark, color, 2.2f);
        draw.AddLine(c, c + (d - c) * mark, color, 2.2f);
    }

    private static void DrawWarningMark(
        ImDrawListPtr draw,
        Num.Vector2 center,
        uint color,
        float mark)
    {
        float line = Math.Min(1f, mark * 1.4f);
        Num.Vector2 top = center + new Num.Vector2(0f, -6f);
        Num.Vector2 bottom = center + new Num.Vector2(0f, 2f);
        draw.AddLine(top, top + (bottom - top) * line, color, 2.3f);

        if (mark > 0.62f)
        {
            draw.AddCircleFilled(
                center + new Num.Vector2(0f, 6f),
                1.45f,
                color,
                10);
        }
    }

    private static float Smooth01(float value)
    {
        value = Math.Max(0f, Math.Min(1f, value));
        return value * value * (3f - 2f * value);
    }
}
