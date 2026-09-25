using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    private static bool migrationDiagnosticsFaulted;
    private static bool groupStatusFaulted;

    internal static void Draw()
    {
        // Apply one shared visual language before any rebuilt editor window is drawn this frame.
        DevToolUiTheme.Apply();

        Num.Vector2 display = ImGui.GetIO().DisplaySize;
        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;

        // In New UI mode the switch, language controls, commands and session status all live in
        // ControlCenterWindow. Vanilla keeps only this deliberately small return surface so the
        // original DevUI remains readable and the developer can always switch back.
        if (EditorUiModeState.UseVanilla)
        {
            DrawVanillaReturnPanel();
            return;
        }

        // The renderer/context can be healthy before the core DevTool backend publishes its first
        // immutable snapshot. Keep a visible shell in that state instead of returning an empty frame.
        if (!snapshot.Available)
        {
            DrawBackendWaitingPanel(display);
            return;
        }

        // Map needs the largest uninterrupted workspace. Compatibility diagnostics stay hidden
        // there. The radial shortcut palette is owned once by DevToolOverlay for every tool mode.
        if (snapshot.ToolMode != EditorToolMode.Map && !migrationDiagnosticsFaulted)
        {
            try
            {
                MigrationCoverageWindow.Draw(display);
            }
            catch (Exception error)
            {
                migrationDiagnosticsFaulted = true;
                global::DryCycle.Plugin.Logger?.LogError(
                    "DevTool migration diagnostics failed and were isolated from the core UI shell. " +
                    error);
            }
        }

        // These windows are diagnostics only. They must never stand between a healthy ImGui context
        // and the main Control Center/DevToolOverlay.
        if (!groupStatusFaulted &&
            (FloatingWindowSnap.SelectedWindowCount > 0 ||
             FloatingWindowSnap.GetGroupSnapshots().Length > 0))
        {
            try
            {
                GroupStatusWindow.Draw(display);
            }
            catch (Exception error)
            {
                groupStatusFaulted = true;
                global::DryCycle.Plugin.Logger?.LogError(
                    "DevTool group-status diagnostics failed and were isolated from the core UI shell. " +
                    error);
            }
        }

        // ActionToastOverlay is drawn once, after the main editor windows, by BridgePlugin.
        // Do not draw it here as well; duplicate pumping also duplicated the universal mirror.
    }

    private static void DrawBackendWaitingPanel(Num.Vector2 display)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        float width = Math.Min(Math.Max(360f, 360f * Math.Min(1.25f, scale)), Math.Max(260f, display.X - 16f));

        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, 0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("界面###DevToolBackendWaiting", "UI###DevToolBackendWaiting"),
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("BackendWaiting");
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("新 UI 渲染器已启动", "NEW UI RENDERER ONLINE"));
        ImGui.TextWrapped(
            DevToolUiSettings.T(
                "正在等待 DevTool 后端发布编辑器状态。原版 DevUI 会继续保持可用。",
                "Waiting for the DevTool backend to publish an editor snapshot. Vanilla DevUI remains available."));

        ImGui.Spacing();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("使用原版 DevUI", "Use Vanilla DevUI"),
                "DevToolWaitingUseVanilla",
                DevToolButtonTone.Subtle))
        {
            EditorUiModeState.SetVanilla(true);
        }

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine(92f);
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        if (DevToolWidgets.ActionButton(
                "中文",
                "DevToolWaitingChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "English",
                "DevToolWaitingEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);

        ImGui.End();
    }

    private static void DrawVanillaReturnPanel()
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(300f * Math.Min(1.25f, scale), 106f * Math.Min(1.20f, scale)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(240f, 92f),
            new Num.Vector2(520f, 220f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("界面###DevToolUiModeSwitch", "UI###DevToolUiModeSwitch"),
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("UI");

        DevToolWidgets.MutedText(DevToolUiSettings.T("显示模式", "Display mode"));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("切换到新 UI", "Switch to New UI"),
                "DevToolUseNewUi",
                DevToolButtonTone.Primary))
            EditorUiModeState.SetVanilla(false);

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine(92f);
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        if (DevToolWidgets.ActionButton(
                "中文",
                "DevToolChinese",
                chinese ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "English",
                "DevToolEnglish",
                chinese ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);

        ImGui.End();
    }
}
