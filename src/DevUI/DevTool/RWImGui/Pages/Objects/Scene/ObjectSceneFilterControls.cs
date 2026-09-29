using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared category chrome for the Objects scene list.
/// Center Scene and Browser Scene use the same collapse behavior.
/// </summary>
internal static class ObjectSceneFilterControls
{
    internal static bool DrawCollapseAllAction(string scope)
    {
        string label = DevToolUiSettings.T("折叠所有", "Collapse All");
        float buttonWidth = DevToolWidgets.ButtonWidth(label);

        ImGui.SameLine();
        float cursorX = ImGui.GetCursorPosX();
        float actionX = cursorX + ImGui.GetContentRegionAvail().X - buttonWidth;
        if (actionX > cursorX)
            ImGui.SetCursorPosX(actionX);

        return DevToolWidgets.ActionButton(
            label,
            "ObjectSceneCollapseAll##" + scope,
            DevToolButtonTone.Subtle);
    }

    internal static bool DrawCategoryHeader(
        string category,
        string scope,
        bool collapseAll)
    {
        category ??= string.Empty;

        if (collapseAll)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);

        return ImGui.CollapsingHeader(
            category + "##ObjectSceneCategory##" + scope + "##" + category,
            ImGuiTreeNodeFlags.DefaultOpen);
    }
}
