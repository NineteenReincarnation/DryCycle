using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small non-interactive confirmation surface for editor shortcuts. It intentionally lives at the
/// top-center of the room instead of inside a movable editor window, so keyboard actions have an
/// immediate visual acknowledgement without stealing room space or input focus.
/// </summary>
internal static class ActionToastOverlay
{
    private static readonly Num.Vector4 SuccessText = new(0.62f, 0.84f, 1.00f, 1f);
    private static readonly Num.Vector4 WarningText = new(1.00f, 0.74f, 0.36f, 1f);
    private static readonly Num.Vector4 ShortcutText = new(0.68f, 0.75f, 0.84f, 1f);
    private static readonly Num.Vector4 Border = new(0.28f, 0.52f, 0.76f, 0.92f);
    private static readonly Num.Vector4 WarningBorder = new(0.72f, 0.48f, 0.20f, 0.95f);
    private static readonly Num.Vector4 Background = new(0.025f, 0.040f, 0.060f, 0.94f);

    private const double VisibleSeconds = 1.65;
    private const double FadeSeconds = 0.32;

    private static string message = string.Empty;
    private static string shortcut = string.Empty;
    private static bool warning;
    private static double shownAt = -1000d;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ObserveShortcuts(snapshot);

        double age = ImGui.GetTime() - shownAt;
        if (age < 0d || age >= VisibleSeconds) return;

        float alpha = 1f;
        double fadeStart = VisibleSeconds - FadeSeconds;
        if (age > fadeStart)
            alpha = (float)Math.Max(0d, Math.Min(1d, (VisibleSeconds - age) / FadeSeconds));

        string visibleMessage = string.IsNullOrEmpty(message)
            ? DevToolUiSettings.T("已执行", "Done")
            : message;
        float messageWidth = ImGui.CalcTextSize(visibleMessage).X;
        float shortcutWidth = string.IsNullOrEmpty(shortcut) ? 0f : ImGui.CalcTextSize(shortcut).X;
        float gap = shortcutWidth > 0f ? 18f : 0f;
        float width = Math.Max(168f, messageWidth + shortcutWidth + gap + 34f);
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

            if (!string.IsNullOrEmpty(shortcut))
            {
                ImGui.SameLine(0f, gap);
                Num.Vector4 key = ShortcutText;
                key.W *= alpha;
                ImGui.TextColored(key, shortcut);
            }

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
        shortcut = keys ?? string.Empty;
        warning = isWarning;
        shownAt = ImGui.GetTime();
    }

    private static void ObserveShortcuts(EditorPresentationSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput || EditorInputRouter.WantsTextInput) return;

        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                    Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (ctrl && Input.GetKeyDown(KeyCode.S))
        {
            Notify("保存", "Save", "Ctrl+S");
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.Z))
        {
            if (shift)
            {
                bool canRedo = snapshot?.CanRedo == true;
                Notify(canRedo ? "重做" : "没有可重做内容", canRedo ? "Redo" : "Nothing to redo", "Ctrl+Shift+Z", !canRedo);
            }
            else
            {
                bool canUndo = snapshot?.CanUndo == true;
                Notify(canUndo ? "撤销" : "没有可撤销内容", canUndo ? "Undo" : "Nothing to undo", "Ctrl+Z", !canUndo);
            }
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.Y))
        {
            bool canRedo = snapshot?.CanRedo == true;
            Notify(canRedo ? "重做" : "没有可重做内容", canRedo ? "Redo" : "Nothing to redo", "Ctrl+Y", !canRedo);
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.D) && snapshot?.ToolMode == EditorToolMode.Objects)
        {
            int selected = snapshot.Inspector?.SelectionCount ?? 0;
            Notify(selected > 0 ? "复制所选物件" : "没有选中物件", selected > 0 ? "Duplicate selection" : "Nothing selected", "Ctrl+D", selected <= 0);
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.B))
        {
            Notify("切换浏览器", "Toggle Browser", "Ctrl+B");
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.I))
        {
            Notify("切换检查器", "Toggle Inspector", "Ctrl+I");
            return;
        }

        if (!ctrl && Input.GetKeyDown(KeyCode.Tab))
            Notify(snapshot?.FocusMode == true ? "退出专注" : "进入专注", snapshot?.FocusMode == true ? "Exit Focus" : "Enter Focus", "Tab");
    }
}
