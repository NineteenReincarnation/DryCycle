using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    internal static void Draw()
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                 ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##DevToolUiModeSwitch", flags))
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
