using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    internal static void Draw()
    {
        // Use a first-use default only. From then on ImGui owns the window position/size so
        // developers can move and resize it like the rest of the rebuilt editor.
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(148f, 58f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(126f, 52f), new Num.Vector2(300f, 160f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;

        if (!ImGui.Begin("UI###DevToolUiModeSwitch", flags))
        {
            ImGui.End();
            return;
        }

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

        ImGui.End();
    }
}
