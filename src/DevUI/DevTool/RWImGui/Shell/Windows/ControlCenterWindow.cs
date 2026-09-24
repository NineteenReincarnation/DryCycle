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

        float maxWidth = Math.Max(300f, display.X - 16f);
        float preferredWidth = 430f * Math.Min(1.18f, scale);
        float width = Math.Min(maxWidth, Math.Max(340f, preferredWidth));
        float defaultX = 8f;

        ImGui.SetNextWindowPos(new Num.Vector2(defaultX, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 190f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(320f, maxWidth), 120f),
            new Num.Vector2(maxWidth, Math.Max(140f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("界面###DevToolControlCenter", "Interface###DevToolControlCenter"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            DrawPerformanceDiagnostics(display);
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("ControlCenter");
        DrawInterfacePanel();

        FitWindowHeightToContents(display);
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
        if (DevToolWidgets.ActionButton(
                "中文",
                "ControlCenterChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
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

    private static float BodyScale() =>
        DevToolUiSettings.IsChinese ? BodyScaleChinese : BodyScaleEnglish;

    private static float KeyColumn() =>
        DevToolUiSettings.IsChinese ? 116f : 108f;

    private static void FitWindowHeightToContents(Num.Vector2 display)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float minimum = 126f;
        float maximum = Math.Max(minimum, display.Y - 16f);
        float desired = ImGui.GetCursorPosY() + style.WindowPadding.Y;
        desired = Math.Max(minimum, Math.Min(maximum, desired));

        Num.Vector2 current = ImGui.GetWindowSize();
        if (Math.Abs(current.Y - desired) > 0.5f)
            ImGui.SetWindowSize(new Num.Vector2(current.X, desired), ImGuiCond.Always);
    }

    private static void DrawPerformanceDiagnostics(Num.Vector2 display)
    {
        if (DevToolUserFacingCopyCleanup.HideNormalDiagnostics)
            return;
        if (DevToolPerformanceMonitor.Enabled)
            DevToolPerformanceWindow.Draw(display);
    }
}
