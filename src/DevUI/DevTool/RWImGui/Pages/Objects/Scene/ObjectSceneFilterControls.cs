using System;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared chrome for the Objects scene visibility model.
///
/// Scene can be hosted either in the center workspace or in the Browser. Both surfaces must expose
/// the same category visibility/focus controls because they mutate one shared scene policy.
/// </summary>
internal static class ObjectSceneFilterControls
{
    internal static void DrawFocusSummary(string scope)
    {
        if (string.IsNullOrEmpty(ObjectSceneVisibilityState.FocusedCategory))
            return;

        ImGui.Spacing();
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("聚焦: ", "Focus: ") + ObjectSceneVisibilityState.FocusedCategory);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("清除", "Clear"),
                "ObjectSceneClearFocus##" + scope,
                DevToolButtonTone.Subtle))
            ObjectSceneVisibilityState.ClearFocus();
    }

    internal static void DrawCategoryHeader(string category, string scope)
    {
        ObjectCategoryVisibility mode = ObjectSceneVisibilityState.GetCategoryMode(category);
        string state = mode switch
        {
            ObjectCategoryVisibility.Ghost => DevToolUiSettings.T("弱显", "Ghost"),
            ObjectCategoryVisibility.Hidden => DevToolUiSettings.T("隐藏", "Hidden"),
            _ => DevToolUiSettings.T("正常", "Normal")
        };

        DevToolWidgets.MutedText(category);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                state,
                "ObjectCategoryVisibility##" + scope + "##" + category,
                mode == ObjectCategoryVisibility.Normal
                    ? DevToolButtonTone.Subtle
                    : DevToolButtonTone.Normal))
            ObjectSceneVisibilityState.CycleCategoryMode(category);

        ImGui.SameLine();
        bool focused = string.Equals(
            ObjectSceneVisibilityState.FocusedCategory,
            category,
            StringComparison.OrdinalIgnoreCase);
        if (DevToolWidgets.ActionButton(
                focused
                    ? DevToolUiSettings.T("取消聚焦", "Unfocus")
                    : DevToolUiSettings.T("聚焦", "Focus"),
                "ObjectCategoryFocus##" + scope + "##" + category,
                focused ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            ObjectSceneVisibilityState.SetFocusedCategory(category);
    }
}
