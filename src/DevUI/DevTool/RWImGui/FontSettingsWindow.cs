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
        // The Map workspace needs uninterrupted horizontal/vertical space. Typography settings are
        // presentation-only and do not need to cover the graph while Map is the active tool.
        if (EditorPresentationHub.Current.ToolMode == EditorToolMode.Map)
            return;

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
        int localFontFiles = DevToolFontCatalog.CountLocalFontFiles();
        int registeredLocalFaces = DevToolFontCatalog.RegisteredLocalFaceCount;
        int localChineseFaces = DevToolFontCatalog.CountSelectableLocalChineseFaces();

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
                "当前 Atlas 中没有可用于简体中文界面的字体。DryCycle 会在 RWImGui 初始化完成、第一帧开始前尝试加入本地字体；如果安全窗口已关闭，则不会强行重建 Atlas。"
            );
        }
        else
        {
            ImGui.TextDisabled($"可选 {families.Length} 个字体族 · 本地中文字体面 {localChineseFaces} 个 · 默认 HarmonyOS Sans SC Medium");
        }

        DevToolWidgets.MutedText("字体目录");
        ImGui.TextWrapped(DevToolFontCatalog.FontDirectory);
        ImGui.TextDisabled($"目录字体 {localFontFiles} 个 · 启动阶段已注册 {registeredLocalFaces} 个 · 可用于中文 {localChineseFaces} 个");

        if (DevToolFontCatalog.RegistrationAttempted && !DevToolFontCatalog.RegistrationSucceeded)
        {
            ImGui.TextWrapped("注册状态：" + DevToolFontCatalog.RegistrationMessage);
        }
        else if (registeredLocalFaces > 0 && localChineseFaces == 0)
        {
            ImGui.TextWrapped(
                "本地字体已经加入 Atlas，但没有一个包含所需的简体中文字形。HarmonyOS Sans 请使用 HarmonyOS_Sans_SC_*.ttf；HarmonyOS_Sans_*.ttf 是通用西文字体，不是简中字体。"
            );
        }
        else if (localChineseFaces > 0)
        {
            ImGui.TextDisabled(
                "同一字体族的 Regular / Medium / Bold 等会合并为一个字体族条目；使用下面的字重滑块切换具体字体面。选择字体族或字重后下一帧立即生效。"
            );
        }

        ImGui.Spacing();
    }
}
