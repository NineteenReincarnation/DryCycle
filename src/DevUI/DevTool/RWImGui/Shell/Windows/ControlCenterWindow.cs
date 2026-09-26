using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compact presentation/settings surface for the rebuilt DevTool.
///
/// Command controls, room/session status and the EDITING/FOCUS badge intentionally do not live
/// here. Save/undo/redo and other keyboard actions already have global shortcut feedback, while
/// page-specific state belongs to the active workspace. Keeping only interface controls avoids
/// duplicating information and leaves more of the room visible.
/// </summary>
internal static class ControlCenterWindow
{
    private static readonly Num.Vector4 AccentText = new(0.63f, 0.82f, 1.00f, 1f);

    private const float BodyScaleEnglish = 1.18f;
    private const float BodyScaleChinese = 1.24f;
    private const float TitleBoost = 1.10f;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));

        // Keep the compact DevTool scrollbar treatment even though this window normally does not
        // need to scroll. It matters on very small displays and at extreme font scales.
        DevToolScrollChrome.Apply(ImGui.GetIO(), scale);

        float maxWidth = Math.Max(280f, display.X - 16f);
        float compactMaxWidth = Math.Min(maxWidth, 520f * Math.Min(1.12f, scale));
        float defaultX = 8f;

        ImGui.SetNextWindowPos(new Num.Vector2(defaultX, 8f), ImGuiCond.FirstUseEver);
        // This surface contains only two short control rows. Let ImGui derive both axes from the
        // actual localized button/text extents instead of preserving an old oversized window.
        // AlwaysAutoResize also corrects already-saved ImGui sizes from previous builds.
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(260f, compactMaxWidth), 0f),
            new Num.Vector2(compactMaxWidth, Math.Max(140f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("界面###DevToolControlCenter", "Interface###DevToolControlCenter"),
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            DrawPerformanceDiagnostics(display);
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("ControlCenter");
        DrawInterfacePanel();

        ImGui.End();
        DrawPerformanceDiagnostics(display);
    }

    private static void DrawInterfacePanel()
    {
        float bodyScale = BodyScale();
        ImGui.SetWindowFontScale(bodyScale * TitleBoost);
        ImGui.TextColored(AccentText, DevToolUiSettings.T("界面", "INTERFACE"));
        ImGui.SetWindowFontScale(bodyScale);

        ImGui.Spacing();

        float keyColumn = KeyColumn();

        DevToolWidgets.MutedText(DevToolUiSettings.T("模式", "Mode"));
        ImGui.SameLine(keyColumn);
        bool vanilla = EditorUiModeState.UseVanilla;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新 UI", "New UI"),
                "ControlCenterNewUi",
                vanilla ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            EditorUiModeState.SetVanilla(false);

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("原版", "Vanilla"),
                "ControlCenterVanillaUi",
                vanilla ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            EditorUiModeState.SetVanilla(true);

        ImGui.Spacing();

        DevToolWidgets.MutedText(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine(keyColumn);
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;

        // English mode normally uses RWImGUI's default Latin font, so a literal "中文" label would
        // render as "??" even though the HarmonyOS CJK face is already present in the shared atlas.
        // Push only that registered CJK face for this one language button and normalize its base
        // font size to the current English face so the two buttons keep matching geometry.
        bool pushedChineseLabelFont =
            TryPushChineseLanguageLabelFont(
                bodyScale);

        bool chooseChinese =
            DevToolWidgets.ActionButton(
                "中文",
                "ControlCenterChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle);

        if (pushedChineseLabelFont)
        {
            ImGui.SetWindowFontScale(
                bodyScale);
            ImGui.PopFont();
        }

        if (chooseChinese)
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "English",
                "ControlCenterEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);

        if (!DevToolUserFacingCopyCleanup.HideNormalDiagnostics)
        {
            ImGui.Spacing();

            DevToolWidgets.MutedText(DevToolUiSettings.T("性能", "Profiling"));
            ImGui.SameLine(keyColumn);
            bool profiling = DevToolPerformanceMonitor.Enabled;
            if (DevToolWidgets.ActionButton(
                    profiling
                        ? DevToolUiSettings.T("监控中", "Monitoring")
                        : DevToolUiSettings.T("开启", "Enable"),
                    "ControlCenterPerformance",
                    profiling ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            {
                bool enable = !profiling;
                DevToolPerformanceMonitor.SetEnabled(enable);
                DevToolFrontendPerformanceMonitor.SetEnabled(enable);
            }
        }
    }

    private static unsafe bool TryPushChineseLanguageLabelFont(
        float bodyScale)
    {
        // Chinese mode already has the HarmonyOS face pushed for the whole frame.
        if (DevToolUiSettings.IsChinese)
            return false;

        if (!DevToolFontCatalog.TryResolveRegisteredFace(
                DevToolFontCatalog.DefaultChineseFamily,
                DevToolUiSettings.DefaultChineseFontWeight,
                requireChinese: true,
                out ImFontPtr chineseFont,
                out _,
                out _,
                out _))
            return false;

        ImFontPtr currentFont =
            ImGui.GetFont();

        if (chineseFont.NativePtr == null ||
            currentFont.NativePtr == null ||
            chineseFont.FontSize <= 0.01f ||
            currentFont.FontSize <= 0.01f ||
            !DevToolFrontend.TryPushRegisteredFont(
                chineseFont,
                "Control Center Chinese language label"))
            return false;

        ImGui.SetWindowFontScale(
            bodyScale *
            currentFont.FontSize /
            chineseFont.FontSize);
        return true;
    }

    private static float BodyScale() =>
        DevToolUiSettings.IsChinese ? BodyScaleChinese : BodyScaleEnglish;

    private static float KeyColumn() =>
        DevToolUiSettings.IsChinese ? 116f : 108f;

    private static void DrawPerformanceDiagnostics(Num.Vector2 display)
    {
        if (DevToolUserFacingCopyCleanup.HideNormalDiagnostics)
            return;
        if (DevToolPerformanceMonitor.Enabled)
            DevToolPerformanceWindow.Draw(display);
    }
}
