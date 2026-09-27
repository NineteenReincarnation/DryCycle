using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Session-local font controls for the rebuilt developer UI. The window is deliberately kept
/// separate from editor data so presentation changes never enter room saves or Undo/Redo.
/// </summary>
internal static class FontSettingsWindow
{
    internal static void Draw(Num.Vector2 display)
    {
        // Typography controls are presentation-only and do not belong to the minimum first-visible
        // frame. Progressive hydration keeps this secondary window out of O/H activation and
        // page-restore frames.
        if (!EditorPresentationHub.Current.Hydrated)
            return;

        // The Map workspace needs uninterrupted horizontal/vertical space. Typography settings are
        // presentation-only and do not need to cover the graph while Map is the active tool.
        if (EditorPresentationHub.Current.ToolMode == EditorToolMode.Map ||
            EditorPresentationHub.Current.ToolMode == EditorToolMode.Triggers)
            return;

        // Only editable controls are shown in this window, so keep its footprint compact.
        const float preferredWidth = 468f;
        float preferredHeight = DevToolUiSettings.IsChinese ? 238f : 274f;
        float width = Math.Min(preferredWidth, Math.Max(320f, display.X - 16f));
        float height = Math.Min(preferredHeight, Math.Max(190f, display.Y - 16f));

        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(8f, display.X - width - 8f), 8f),
            ImGuiCond.FirstUseEver);
        // Once intentionally overrides stale ImGui.ini dimensions from older builds, while still
        // allowing the developer to resize the window afterwards during the current session.
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Once);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(420f, Math.Max(320f, display.X - 16f)), 190f),
            new Num.Vector2(Math.Max(420f, display.X - 16f), Math.Max(190f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("字体###DevToolFontSettings", "Font###DevToolFontSettings"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Font");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.12f);

        ImGui.TextDisabled(DevToolUiSettings.T("排版", "TYPOGRAPHY"));

        float size = DevToolUiSettings.FontSize;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("字号##DevToolFontSize", "Size##DevToolFontSize"),
                ref size,
                12f,
                72f,
                "%.1f px"))
        {
            DevToolUiSettings.FontSize = Math.Max(12f, Math.Min(72f, size));

            // Font size changes affect glyphs and controls, not the authored floating-window
            // footprint. Keep the requested top-right window size stable.
            ImGui.SetWindowSize(new Num.Vector2(
                Math.Min(preferredWidth, Math.Max(320f, display.X - 16f)),
                Math.Min(preferredHeight, Math.Max(190f, display.Y - 16f))));
        }

        if (!DevToolUiSettings.IsChinese)
        {
            int weight = DevToolUiSettings.FontWeight;
            if (ImGui.SliderInt(
                    "Weight##DevToolFontWeight",
                    ref weight,
                    100,
                    900))
            {
                weight = Math.Max(100, Math.Min(900, ((weight + 50) / 100) * 100));
                DevToolUiSettings.FontWeight = weight;
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("颜色", "COLORS"));

        Num.Vector4 text = DevToolUiSettings.TextColor;
        if (ImGui.ColorEdit4(DevToolUiSettings.T("正文##DevToolTextColor", "Text##DevToolTextColor"), ref text))
            DevToolUiSettings.TextColor = text;

        Num.Vector4 disabled = DevToolUiSettings.DisabledTextColor;
        if (ImGui.ColorEdit4(DevToolUiSettings.T("弱化文字##DevToolDisabledTextColor", "Muted text##DevToolDisabledTextColor"), ref disabled))
            DevToolUiSettings.DisabledTextColor = disabled;

        ImGui.Separator();

        if (ImGui.Button(DevToolUiSettings.T("恢复默认", "Reset Defaults")))
            DevToolUiSettings.ResetFontAppearance();

        ImGui.End();
    }
}
