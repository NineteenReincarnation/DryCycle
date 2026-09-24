using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small non-interactive confirmation surface for editor actions. Shortcut discovery itself lives
/// exclusively in ShortcutWindow; this overlay only acknowledges an action after it has fired.
/// </summary>
internal static class ActionToastOverlay
{
    private static readonly Num.Vector4 SuccessText = new(0.62f, 0.84f, 1.00f, 1f);
    private static readonly Num.Vector4 WarningText = new(1.00f, 0.74f, 0.36f, 1f);
    private static readonly Num.Vector4 Border = new(0.28f, 0.52f, 0.76f, 0.92f);
    private static readonly Num.Vector4 WarningBorder = new(0.72f, 0.48f, 0.20f, 0.95f);
    private static readonly Num.Vector4 Background = new(0.025f, 0.040f, 0.060f, 0.94f);

    private const double VisibleSeconds = 1.65;
    private const double FadeSeconds = 0.32;

    private static string message = string.Empty;
    private static string shortcutKeys = string.Empty;
    private static string renderedMessage = string.Empty;
    private static bool warning;
    private static bool toastActive;
    private static double shownAt = -1000d;

    internal static void Draw(EditorPresentationSnapshot snapshot, DevToolUiFrameContext frameContext)
    {
        Num.Vector2 display = frameContext.DisplaySize;

        // ActionToastOverlay is already part of the guaranteed per-frame frontend render chain.
        // Pump the standalone universal DevUI mirror here so every active DevInterface page is
        // testable through the same generic renderer without adding another frontend callback.
        UniversalDevUiMirrorWindow.Draw(display);

        // Keyboard shortcuts use the global feedback channel. This legacy toast remains available
        // for explicit non-shortcut action notifications only.

        // Most stable frames have no toast. Keep the common path free of ImGui time queries and
        // fade/layout work until an action actually activates the overlay.
        if (!toastActive) return;

        double age = ImGui.GetTime() - shownAt;
        if (age < 0d || age >= VisibleSeconds)
        {
            toastActive = false;
            return;
        }

        string visibleMessage = renderedMessage;
        if (string.IsNullOrEmpty(visibleMessage))
            visibleMessage = DevToolUiSettings.T("已执行", "Done");

        float alpha = 1f;
        double fadeStart = VisibleSeconds - FadeSeconds;
        if (age > fadeStart)
            alpha = (float)Math.Max(0d, Math.Min(1d, (VisibleSeconds - age) / FadeSeconds));

        float messageWidth = ImGui.CalcTextSize(visibleMessage).X;
        float width = Math.Max(168f, messageWidth + 34f);
        float height = Math.Max(34f, ImGui.GetFrameHeight() + 12f);
        float x = Math.Max(8f, (display.X - width) * 0.5f);
        float y = Math.Max(36f, Math.Min(64f, display.Y * 0.045f));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(Background.W * alpha);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Num.Vector2(12f, 7f));
        Num.Vector4 border = warning ? WarningBorder : Border;
        border.W *= alpha;
        ImGui.PushStyleColor(ImGuiCol.Border, border);

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                 ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.NoInputs |
                                 ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin("##DevToolActionToast", flags))
        {
            Num.Vector4 main = warning ? WarningText : SuccessText;
            main.W *= alpha;
            ImGui.TextColored(main, visibleMessage);

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            Num.Vector2 min = ImGui.GetWindowPos();
            Num.Vector2 max = min + ImGui.GetWindowSize();
            float remaining = (float)Math.Max(0d, Math.Min(1d, 1d - age / VisibleSeconds));
            Num.Vector4 bar = warning ? WarningText : SuccessText;
            bar.W = 0.88f * alpha;
            draw.AddLine(
                new Num.Vector2(min.X + 2f, max.Y - 2f),
                new Num.Vector2(min.X + 2f + (max.X - min.X - 4f) * remaining, max.Y - 2f),
                ImGui.GetColorU32(bar),
                2f);
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    internal static void Notify(string chinese, string english, string keys = null, bool isWarning = false)
    {
        message = DevToolUiSettings.T(chinese, english);
        shortcutKeys = keys ?? string.Empty;
        renderedMessage = string.IsNullOrWhiteSpace(shortcutKeys)
            ? message
            : message + "  |  " + shortcutKeys;
        warning = isWarning;
        shownAt = ImGui.GetTime();
        toastActive = true;
    }


}