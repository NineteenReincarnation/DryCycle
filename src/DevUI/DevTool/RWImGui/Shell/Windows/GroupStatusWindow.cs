using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Presentation-only inspector for persistent UI layout groups.
/// Groups never enter room data, saves or Undo/Redo history.
/// Shortcut instructions live exclusively in ShortcutWindow.
/// </summary>
internal static class GroupStatusWindow
{
    private sealed class GroupPresentationBinding
    {
        internal int Id;
        internal string Header = string.Empty;
        internal string SelectLabel = string.Empty;
        internal string DissolveLabel = string.Empty;
        internal string[] Members = Array.Empty<string>();
    }

    private const int SnapshotRefreshFrames = 8;
    private static FloatingWindowSnap.WindowGroupSnapshot[] cachedGroups = Array.Empty<FloatingWindowSnap.WindowGroupSnapshot>();
    private static int nextSnapshotRefreshFrame;

    private static FloatingWindowSnap.WindowGroupSnapshot[] projectedGroups;
    private static bool projectedChinese;
    private static GroupPresentationBinding[] groupBindings = Array.Empty<GroupPresentationBinding>();

    private static int projectedSelectedCount = -1;
    private static bool projectedSelectedCountChinese;
    private static string selectedCountLabel = string.Empty;

    internal static void Draw(Num.Vector2 display)
    {
        float width = Math.Min(460f, Math.Max(330f, display.X * 0.22f));
        float height = Math.Min(400f, Math.Max(250f, display.Y * 0.31f));

        ImGui.SetNextWindowPos(
            new Num.Vector2(
                Math.Max(8f, display.X - width - 8f),
                Math.Max(8f, display.Y - height - 8f)),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(310f, 210f),
            new Num.Vector2(Math.Max(310f, display.X - 16f), Math.Max(210f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("编组###DevToolGroups", "Groups###DevToolGroups"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Groups");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.20f : 1.14f);

        ImGui.TextDisabled(GetSelectedCountLabel());
        ImGui.Separator();

        FloatingWindowSnap.WindowGroupSnapshot[] groups = GetCachedGroups();
        if (groups.Length == 0)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "当前还没有窗口编组。编组操作统一显示在左下角快捷键窗口中。",
                "No window groups yet. Group operations are listed in the Shortcuts window at bottom-left."));
            ImGui.End();
            return;
        }

        GroupPresentationBinding[] bindings = GetGroupBindings(groups);
        for (int i = 0; i < bindings.Length; i++)
        {
            GroupPresentationBinding binding = bindings[i];
            if (!ImGui.CollapsingHeader(binding.Header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (ImGui.SmallButton(binding.SelectLabel))
                FloatingWindowSnap.SelectGroup(binding.Id);

            ImGui.SameLine();
            if (ImGui.SmallButton(binding.DissolveLabel))
            {
                FloatingWindowSnap.DissolveGroup(binding.Id);
                InvalidateGroupCache();
                continue;
            }

            for (int member = 0; member < binding.Members.Length; member++)
                ImGui.BulletText(binding.Members[member]);
        }

        ImGui.End();
    }

    private static FloatingWindowSnap.WindowGroupSnapshot[] GetCachedGroups()
    {
        int frame = ImGui.GetFrameCount();
        if (frame < nextSnapshotRefreshFrame) return cachedGroups;

        cachedGroups = FloatingWindowSnap.GetGroupSnapshots();
        nextSnapshotRefreshFrame = frame + SnapshotRefreshFrames;
        return cachedGroups;
    }

    private static GroupPresentationBinding[] GetGroupBindings(FloatingWindowSnap.WindowGroupSnapshot[] groups)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedGroups, groups) && projectedChinese == chinese)
            return groupBindings;

        GroupPresentationBinding[] next = new GroupPresentationBinding[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            FloatingWindowSnap.WindowGroupSnapshot group = groups[i];
            string[] members = group.Members ?? Array.Empty<string>();
            string[] friendlyMembers = new string[members.Length];
            for (int member = 0; member < members.Length; member++)
                friendlyMembers[member] = FriendlyWindowName(members[member]);

            next[i] = new GroupPresentationBinding
            {
                Id = group.Id,
                Header = DevToolUiSettings.T(
                             $"组 {group.Id} · {members.Length} 个窗口",
                             $"Group {group.Id} · {members.Length} windows") +
                         "##DevToolGroup" + group.Id,
                SelectLabel = DevToolUiSettings.T("选中整组##SelectGroup", "Select Group##SelectGroup") + group.Id,
                DissolveLabel = DevToolUiSettings.T("解散##DissolveGroup", "Dissolve##DissolveGroup") + group.Id,
                Members = friendlyMembers
            };
        }

        projectedGroups = groups;
        projectedChinese = chinese;
        groupBindings = next;
        return groupBindings;
    }

    private static string GetSelectedCountLabel()
    {
        int count = FloatingWindowSnap.SelectedWindowCount;
        bool chinese = DevToolUiSettings.IsChinese;
        if (projectedSelectedCount == count && projectedSelectedCountChinese == chinese)
            return selectedCountLabel;

        projectedSelectedCount = count;
        projectedSelectedCountChinese = chinese;
        selectedCountLabel = DevToolUiSettings.T(
            $"当前选择：{count} 个窗口",
            $"Selected: {count} windows");
        return selectedCountLabel;
    }

    private static void InvalidateGroupCache()
    {
        nextSnapshotRefreshFrame = 0;
        projectedGroups = null;
    }

    private static string FriendlyWindowName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "?";

        return id switch
        {
            "ControlCenter" => DevToolUiSettings.T("总控", "Control center"),
            "UI" => DevToolUiSettings.T("界面", "UI"),
            "Commands" => DevToolUiSettings.T("命令", "Commands"),
            "Tools" => DevToolUiSettings.T("工具", "Tools"),
            "Browser" => DevToolUiSettings.T("浏览器", "Browser"),
            "Inspector" => DevToolUiSettings.T("检查器", "Inspector"),
            "BrowserInspector" => DevToolUiSettings.T("编辑面板", "Editor panel"),
            "Status" => DevToolUiSettings.T("状态", "Status"),
            "Font" => DevToolUiSettings.T("字体", "Font"),
            "Shortcuts" => DevToolUiSettings.T("快捷键", "Shortcuts"),
            "Map" => DevToolUiSettings.T("地图工作区", "Map workspace"),
            "Dialog" => DevToolUiSettings.T("对话工作区", "Dialog workspace"),
            "Relationships" => DevToolUiSettings.T("关系工作区", "Relationships workspace"),
            _ => id
        };
    }
}
