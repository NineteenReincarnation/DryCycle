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
        float width = Math.Min(Math.Max(390f, display.X - 16f), 390f * uiScale);
        float height = Math.Min(Math.Max(430f, display.Y - 16f), 430f * Math.Min(1.35f, uiScale));

        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(8f, display.X - width - 8f), 8f),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(350f * uiScale, Math.Max(350f, display.X - 16f)), 330f),
            new Num.Vector2(Math.Max(350f, display.X - 16f), Math.Max(330f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("字体###DevToolFontSettings", "Font###DevToolFontSettings"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Font");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.12f);

        ImGui.TextDisabled(DevToolUiSettings.T("排版", "TYPOGRAPHY"));

        if (DevToolUiSettings.IsChinese)
            DrawChineseFontSelector();

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
                Math.Min(Math.Max(390f, display.X - 16f), 390f * nextScale),
                Math.Min(Math.Max(430f, display.Y - 16f), 430f * Math.Min(1.35f, nextScale))));
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
        string friendlyFace = DevToolFontCatalog.FriendlyFaceName(fontName);
        ImGui.TextDisabled(DevToolUiSettings.T("当前字体：", "Font: ") +
                           (string.IsNullOrEmpty(friendlyFace) ? DevToolUiSettings.T("默认", "Default") : friendlyFace));
        ImGui.TextDisabled(DevToolUiSettings.T("实际字重：", "Resolved weight: ") + actualWeight);
        ImGui.TextDisabled(DevToolUiSettings.T(
            "默认字号：中文 42 px / 英文 36 px",
            "Default size: Chinese 42 px / English 36 px"));

        if (DevToolUiSettings.IsChinese && weightVariants <= 1)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "当前所选中文字体只有一个可识别字重。字重偏好会保留；同一字体族存在其他字重时会自动选择最接近的版本。",
                "The selected CJK family exposes one identifiable weight. The preference is retained and the closest family variant is used when available."));
        }
        else
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前字体族字重：", "Family weights: ") + Math.Max(1, weightVariants));
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

    private static void DrawChineseFontSelector()
    {
        string[] families = DevToolFontCatalog.GetAvailableChineseFamilies();
        string selectedFamily = DevToolUiSettings.ChineseFontFamily;

        DevToolWidgets.MutedText("中文字体");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##DevToolChineseFontFamily", selectedFamily))
        {
            for (int i = 0; i < families.Length; i++)
            {
                string family = families[i];
                bool selected = string.Equals(family, selectedFamily, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(family + "##DevToolChineseFamily" + i, selected))
                    DevToolUiSettings.ChineseFontFamily = family;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (families.Length == 0)
        {
            ImGui.TextWrapped(
                "未检测到可用的简体中文字体。请把支持中文的 .ttf / .otf / .ttc 放入下面目录，并重新启动游戏。"
            );
        }
        else
        {
            ImGui.TextDisabled($"已检测 {families.Length} 个中文字体族 · 默认 HarmonyOS Sans SC Bold");
        }

        DevToolWidgets.MutedText("字体目录");
        ImGui.TextWrapped(DevToolFontCatalog.FontDirectory);
        ImGui.TextDisabled("新增字体需在启动 RWImGui 前存在于该目录；重启后会自动加入此列表。");
        ImGui.Spacing();
    }
}
