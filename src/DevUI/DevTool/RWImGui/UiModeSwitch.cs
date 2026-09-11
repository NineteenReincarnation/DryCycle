using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    internal static void Draw()
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(250f, 104f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(210f, 96f), new Num.Vector2(420f, 220f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;

        if (!ImGui.Begin(DevToolUiSettings.T("界面###DevToolUiModeSwitch", "UI###DevToolUiModeSwitch"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("UI");

        // Keep the original two-way mode switch visible in both modes. Vanilla hides the rebuilt
        // editor panels, but never hides the control that lets the developer return to New UI.
        bool vanilla = EditorUiModeState.UseVanilla;
        if (!vanilla) ImGui.BeginDisabled();
        if (ImGui.SmallButton("New UI##DevToolUseNewUi"))
            EditorUiModeState.SetVanilla(false);
        if (!vanilla) ImGui.EndDisabled();

        ImGui.SameLine();
        if (vanilla) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Vanilla##DevToolUseVanillaUi"))
            EditorUiModeState.SetVanilla(true);
        if (vanilla) ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("语言", "Language"));
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        if (chinese) ImGui.BeginDisabled();
        if (ImGui.SmallButton("中文##DevToolChinese"))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.Chinese);
        if (chinese) ImGui.EndDisabled();

        ImGui.SameLine();
        bool english = DevToolUiSettings.Language == DevToolUiLanguage.English;
        if (english) ImGui.BeginDisabled();
        if (ImGui.SmallButton("English##DevToolEnglish"))
            DevToolUiSettings.SetLanguage(DevToolUiLanguage.English);
        if (english) ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("Esc 隐藏/恢复面板", "Esc hides/restores panels"));
        ImGui.End();
    }
}
