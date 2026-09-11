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
        if (ImGui.Button(DevToolUiSettings.T("切换到原版", "Use Vanilla")))
            EditorUiModeState.SetVanilla(true);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "切换后完全隐藏新 UI。Ctrl+Shift+U 可切回。",
                "Hides the rebuilt UI completely. Ctrl+Shift+U returns to it."));

        ImGui.SameLine();
        ImGui.TextDisabled(DevToolUiSettings.T("Esc 隐藏/恢复面板", "Esc hides/restores panels"));
        ImGui.End();
    }
}
