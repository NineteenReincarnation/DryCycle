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
    private static bool fontStatusProjectionValid;
    private static bool projectedFontStatusChinese;
    private static string projectedFontName = string.Empty;
    private static int projectedActualWeight = int.MinValue;
    private static int projectedWeightVariants = int.MinValue;
    private static string projectedFontStatus = string.Empty;
    private static string projectedResolvedWeightStatus = string.Empty;
    private static string projectedFamilyWeightsStatus = string.Empty;

    internal static void Draw(Num.Vector2 display)
    {
        // Typography inspection touches the font catalog and local font diagnostics. It is useful
        // once the editor is interactive, but it is not part of the minimum first-visible frame.
        // Progressive hydration keeps this secondary window out of O/H activation and page-restore
        // frames, then resumes the exact same UI on the next fully hydrated frame.
        if (!EditorPresentationHub.Current.Hydrated)
            return;

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

        if (DevToolUiSettings.IsChinese)
        {
            ImGui.TextDisabled("字重：Medium (500)");
        }
        else
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

        string fontName = DevToolFrontend.ResolvedFontName;
        int actualWeight = DevToolFrontend.ResolvedFontWeight;
        int weightVariants = DevToolFrontend.ResolvedFontWeightVariantCount;
        EnsureFontStatusProjection(fontName, actualWeight, weightVariants);
        ImGui.TextDisabled(projectedFontStatus);
        ImGui.TextDisabled(projectedResolvedWeightStatus);
        ImGui.TextDisabled(DevToolUiSettings.T(
            "默认字号：中文 21 px / 英文 18 px",
            "Default size: Chinese 21 px / English 18 px"));

        if (DevToolUiSettings.IsChinese && weightVariants <= 1)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "当前所选中文字体只有一个可识别字重。字重偏好会保留；同一字体族存在其他字重时会自动选择最接近的版本。",
                "The selected CJK family exposes one identifiable weight. The preference is retained and the closest family variant is used when available."));
        }
        else
        {
            ImGui.TextDisabled(projectedFamilyWeightsStatus);
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
        ImGui.Text(DevToolUiSettings.T("雨世界开发工具 | 字体预览 123 ABC", "Rain World DevTool | Font preview 123 ABC"));
        ImGui.TextDisabled(DevToolUiSettings.T("弱化文字预览 | 参数说明", "Muted text preview | parameter hint"));
        ImGui.TextDisabled(DevToolUiSettings.T("窗口描边：黑色 2 px", "Window outline: black 2 px"));

        if (ImGui.Button(DevToolUiSettings.T("恢复默认", "Reset Defaults")))
            DevToolUiSettings.ResetFontAppearance();

        ImGui.End();
    }

    private static void EnsureFontStatusProjection(string fontName, int actualWeight, int weightVariants)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        string stableFontName = fontName ?? string.Empty;
        if (fontStatusProjectionValid &&
            projectedFontStatusChinese == chinese &&
            projectedActualWeight == actualWeight &&
            projectedWeightVariants == weightVariants &&
            string.Equals(projectedFontName, stableFontName, StringComparison.Ordinal))
            return;

        projectedFontStatusChinese = chinese;
        projectedFontName = stableFontName;
        projectedActualWeight = actualWeight;
        projectedWeightVariants = weightVariants;

        string friendlyFace = DevToolFontCatalog.FriendlyFaceName(stableFontName);
        projectedFontStatus = DevToolUiSettings.T("当前字体：", "Font: ") +
                              (string.IsNullOrEmpty(friendlyFace)
                                  ? DevToolUiSettings.T("默认", "Default")
                                  : friendlyFace);
        projectedResolvedWeightStatus = DevToolUiSettings.T("实际字重：", "Resolved weight: ") + actualWeight;
        projectedFamilyWeightsStatus = DevToolUiSettings.T("当前字体族字重：", "Family weights: ") + Math.Max(1, weightVariants);
        fontStatusProjectionValid = true;
    }

    private static void DrawChineseFontSelector()
    {
        int registeredLocalFaces = DevToolFontCatalog.RegisteredLocalFaceCount;

        DevToolWidgets.MutedText("中文字体");
        ImGui.TextDisabled("HarmonyOS Sans SC Medium");
        ImGui.TextDisabled(DevToolFontCatalog.ChineseFontFileName);

        DevToolWidgets.MutedText("字体目录");
        ImGui.TextWrapped(DevToolFontCatalog.FontDirectory);

        if (!DevToolFontCatalog.RegistrationSucceeded)
        {
            ImGui.TextWrapped("注册状态：" + DevToolFontCatalog.RegistrationMessage);
        }
        else
        {
            ImGui.TextDisabled(
                "固定中文字体已加载 | DevTool Context 已注册 " +
                registeredLocalFaces +
                " 个字体面");
        }

        ImGui.Spacing();
    }

}