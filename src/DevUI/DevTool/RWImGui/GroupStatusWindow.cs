using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presentation-only inspector for persistent UI layout groups.
/// Groups never enter room data, saves or Undo/Redo history.
/// </summary>
internal static class GroupStatusWindow
{
    internal static void Draw(Num.Vector2 display)
    {
        float width = Math.Min(420f, Math.Max(300f, display.X * 0.20f));
        float height = Math.Min(360f, Math.Max(220f, display.Y * 0.28f));

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                Math.Max(8f, display.X - width - 8f),
                Math.Max(8f, display.Y - height - 8f)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(280f, 180f),
            new Num.Vector2(Math.Max(280f, display.X - 16f), Math.Max(180f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("编组###DevToolGroups", "Groups###DevToolGroups"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Groups");

        ImGui.TextDisabled(DevToolUiSettings.T(
            "Shift + 左键框选 · Ctrl+G 编组",
            "Shift + drag select · Ctrl+G group"));
        ImGui.TextDisabled(DevToolUiSettings.T(
            $"当前选择：{FloatingWindowSnap.SelectedWindowCount} 个窗口",
            $"Selected: {FloatingWindowSnap.SelectedWindowCount} windows"));
        ImGui.Separator();

        FloatingWindowSnap.WindowGroupSnapshot[] groups = FloatingWindowSnap.GetGroupSnapshots();
        if (groups.Length == 0)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "还没有编组。框选至少两个窗口后按 Ctrl+G。",
                "No groups yet. Marquee-select at least two windows, then press Ctrl+G."));
            ImGui.End();
            return;
        }

        for (int i = 0; i < groups.Length; i++)
        {
            FloatingWindowSnap.WindowGroupSnapshot group = groups[i];
            string title = DevToolUiSettings.T(
                $"组 {group.Id} · {group.Members.Length} 个窗口",
                $"Group {group.Id} · {group.Members.Length} windows");

            if (!ImGui.CollapsingHeader(title + "##DevToolGroup" + group.Id, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (ImGui.SmallButton(DevToolUiSettings.T("选中整组##SelectGroup", "Select Group##SelectGroup") + group.Id))
                FloatingWindowSnap.SelectGroup(group.Id);

            ImGui.SameLine();
            if (ImGui.SmallButton(DevToolUiSettings.T("解散##DissolveGroup", "Dissolve##DissolveGroup") + group.Id))
            {
                FloatingWindowSnap.DissolveGroup(group.Id);
                continue;
            }

            string[] members = group.Members ?? Array.Empty<string>();
            for (int member = 0; member < members.Length; member++)
            {
                ImGui.BulletText(FriendlyWindowName(members[member]));
            }
        }

        ImGui.End();
    }

    private static string FriendlyWindowName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "?";

        return id switch
        {
            "UI" => DevToolUiSettings.T("界面", "UI"),
            "Commands" => DevToolUiSettings.T("命令", "Commands"),
            "Tools" => DevToolUiSettings.T("工具", "Tools"),
            "Browser" => DevToolUiSettings.T("浏览器", "Browser"),
            "Inspector" => DevToolUiSettings.T("检查器", "Inspector"),
            "BrowserInspector" => DevToolUiSettings.T("编辑面板", "Editor panel"),
            "Status" => DevToolUiSettings.T("状态", "Status"),
            "Font" => DevToolUiSettings.T("字体", "Font"),
            "Map" => DevToolUiSettings.T("地图工作区", "Map workspace"),
            "Dialog" => DevToolUiSettings.T("对话工作区", "Dialog workspace"),
            "Relationships" => DevToolUiSettings.T("关系工作区", "Relationships workspace"),
            _ => id
        };
    }
}
