using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    internal static void Draw()
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(250f * scale, 104f * scale), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(210f * Math.Min(1f, scale), 96f),
            new Num.Vector2(760f, 520f));
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
        ImGui.TextDisabled(DevToolUiSettings.T("模式", "Mode"));
        bool vanilla = EditorUiModeState.UseVanilla;
        if (!vanilla) ImGui.BeginDisabled();
        if (ImGui.SmallButton(DevToolUiSettings.T("新 UI##DevToolUseNewUi", "New UI##DevToolUseNewUi")))
            EditorUiModeState.SetVanilla(false);
        if (!vanilla) ImGui.EndDisabled();

        ImGui.SameLine();
        if (vanilla) ImGui.BeginDisabled();
        if (ImGui.SmallButton(DevToolUiSettings.T("原版##DevToolUseVanillaUi", "Vanilla##DevToolUseVanillaUi")))
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
        ImGui.TextDisabled(DevToolUiSettings.T(
            "Shift + 左键拖框：多选窗口",
            "Shift + left drag: multi-select windows"));
        ImGui.TextDisabled(DevToolUiSettings.T(
            "拖动任一已选标题栏：整组移动",
            "Drag any selected title bar: move group"));
        ImGui.End();
    }
}
