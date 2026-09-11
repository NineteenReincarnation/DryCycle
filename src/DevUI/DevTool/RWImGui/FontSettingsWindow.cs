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
        float width = 320f;
        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(8f, display.X - width - 8f), 8f),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 286f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(280f, 230f), new Num.Vector2(520f, 560f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("字体###DevToolFontSettings", "Font###DevToolFontSettings"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Font");

        ImGui.TextDisabled(DevToolUiSettings.T("排版", "TYPOGRAPHY"));

        float size = DevToolUiSettings.FontSize;
        if (ImGui.SliderFloat(
                DevToolUiSettings.T("字号##DevToolFontSize", "Size##DevToolFontSize"),
                ref size,
                11f,
                32f,
                "%.1f px"))
        {
            DevToolUiSettings.FontSize = Math.Max(11f, Math.Min(32f, size));
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

        if (ImGui.Button(DevToolUiSettings.T("恢复默认", "Reset Defaults")))
            DevToolUiSettings.ResetFontAppearance();

        ImGui.End();
    }
}
