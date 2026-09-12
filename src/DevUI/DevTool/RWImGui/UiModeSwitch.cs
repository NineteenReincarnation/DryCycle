using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    internal static void Draw()
    {
        // Apply one shared visual language before any rebuilt editor window is drawn this frame.
        DevToolUiTheme.Apply();

        Num.Vector2 display = ImGui.GetIO().DisplaySize;

        // In New UI mode the switch, language controls, commands and session status all live in
        // ControlCenterWindow. Vanilla keeps only this deliberately small return surface so the
        // original DevUI remains readable and the developer can always switch back.
        if (EditorUiModeState.UseVanilla)
        {
            DrawVanillaReturnPanel();
        }
        else
        {
            // Shortcut discovery has one permanent, shared surface instead of leaking temporary
            // key hints into every editor panel. The window is itself part of the floating layout.
            ShortcutWindow.Draw(EditorPresentationHub.Current, display);

            // Migration coverage is compact by default and expands only on demand. It audits the
            // real live DevInterface tree, including RegionKit/DryCycle nodes injected at runtime.
            MigrationCoverageWindow.Draw(display);

            // The group inspector is contextual rather than permanent chrome. Keeping it hidden while
            // no selection/group exists prevents an empty fourth panel from competing with the room.
            if (FloatingWindowSnap.SelectedWindowCount > 0 || FloatingWindowSnap.GetGroupSnapshots().Length > 0)
                GroupStatusWindow.Draw(display);
        }

        // Draw last so shortcut feedback stays above normal editor windows in the fixed top-center
        // acknowledgement area requested by the editor workflow.
        ActionToastOverlay.Draw(EditorPresentationHub.Current, display);
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
