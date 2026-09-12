using System;
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
        float uiScale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        float width = Math.Min(Math.Max(350f, display.X - 16f), 350f * uiScale);
        float height = Math.Min(Math.Max(318f, display.Y - 16f), 318f * uiScale);

        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(8f, display.X - width - 8f), 8f),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(310f * uiScale, Math.Max(310f, display.X - 16f)), 260f),
            new Num.Vector2(Math.Max(310f, display.X - 16f), Math.Max(260f, display.Y - 16f)));
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

            // Keep this control window usable while the font scale changes. Other floating panels
            // keep their developer-authored sizes and can be batch-selected/repositioned.
            float nextScale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
            ImGui.SetWindowSize(new Num.Vector2(
                Math.Min(Math.Max(350f, display.X - 16f), 350f * nextScale),
                Math.Min(Math.Max(318f, display.Y - 16f), 318f * nextScale)));
        }

        int weight = DevToolUiSettings.FontWeight;
        if (ImGui.SliderInt(
                DevToolUiSettings.T("字重##DevToolFontWeight", "Weight##DevToolFontWeight"),
                ref weight,
                100,
                900))
        {
            // Font families normally expose weights in 100-point steps. Snap the requested value
            // so selection is deterministic while dragging the slider.
            weight = Math.Max(100, Math.Min(900, ((weight + 50) / 100) * 100));
            DevToolUiSettings.FontWeight = weight;
        }

        string fontName = DevToolFrontend.ResolvedFontName;
        int actualWeight = DevToolFrontend.ResolvedFontWeight;
        int weightVariants = DevToolFrontend.ResolvedFontWeightVariantCount;
        ImGui.TextDisabled(DevToolUiSettings.T("当前字体：", "Font: ") +
                           (string.IsNullOrEmpty(fontName) ? DevToolUiSettings.T("默认", "Default") : fontName));
        ImGui.TextDisabled(DevToolUiSettings.T("实际字重：", "Resolved weight: ") + actualWeight);
        ImGui.TextDisabled(DevToolUiSettings.T(
            "默认字号：中文 42 px / 英文 36 px",
            "Default size: Chinese 42 px / English 36 px"));

        if (DevToolUiSettings.IsChinese && weightVariants <= 1)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "当前 RWImGui 简中文字库只有一个可识别字重。字重偏好会保留，检测到 Medium/Bold 等字体后自动使用。",
                "The current RWImGui CJK atlas exposes only one identifiable weight. The preference is retained and will use Medium/Bold variants automatically when available."));
        }
        else
        {
            ImGui.TextDisabled(DevToolUiSettings.T("可识别字重：", "Detected weights: ") + Math.Max(1, weightVariants));
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
        ImGui.TextDisabled(DevToolUiSettings.T("预览", "PREVIEW"));
        ImGui.Text(DevToolUiSettings.T("雨世界开发工具 · 字体预览 123 ABC", "Rain World DevTool · Font preview 123 ABC"));
        ImGui.TextDisabled(DevToolUiSettings.T("弱化文字预览 · 参数说明", "Muted text preview · parameter hint"));
        ImGui.TextDisabled(DevToolUiSettings.T("窗口描边：黑色 2 px", "Window outline: black 2 px"));

        if (ImGui.Button(DevToolUiSettings.T("恢复默认", "Reset Defaults")))
            DevToolUiSettings.ResetFontAppearance();

        ImGui.End();
    }
}
