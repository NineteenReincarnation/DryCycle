using System;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class SoundLibraryGroupsView
{
    private static int createType;
    private static string search = string.Empty;
    private static string groupPathEdit = string.Empty;
    private static string observedGroupPath = string.Empty;
    private static string quickGroupName = string.Empty;
    private static string quickGroupId = string.Empty;
    private static bool quickGroupIdManual;
    private const float BrowserBodyFontScale = 1.22f;

    internal static void DrawWorkingGroupBar()
    {
        SoundWorkspaceState.SynchronizeGroups();
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        bool hasActive = SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot active);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("工作音效组", "WORKING GROUP"), BrowserBodyFontScale);
        if (hasActive)
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##SoundWorkingGroup", active.Name + " · " + active.Id))
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    SoundGroupSnapshot group = groups[i];
                    if (!group.IsLocal) continue;
                    bool selected = string.Equals(group.Id, active.Id, StringComparison.OrdinalIgnoreCase);
                    string label = group.Name + " · " + group.Id + "  (" + (group.Sounds?.Length ?? 0) + ")";
                    if (ImGui.Selectable(label + "##WorkingGroup" + i, selected))
                        SoundWorkspaceState.SetActiveGroup(group.Id);
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.TextDisabled(DevToolUiSettings.T(
                $"{active.Sounds?.Length ?? 0} 个声音 · Library / Scene / Inspector 共用",
                $"{active.Sounds?.Length ?? 0} sounds · shared by Library / Scene / Inspector"));
        }
        else
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("还没有可写入的本地音效组。", "No writable local sound group yet."),
                true);
        }

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("+ 新建工作组", "+ New Working Group"),
                "SoundQuickCreateGroup",
                DevToolButtonTone.Subtle,
                true))
        {
            quickGroupName = string.Empty;
            quickGroupId = SoundWorkspaceState.SuggestUniqueGroupId(string.Empty);
            quickGroupIdManual = false;
            ImGui.OpenPopup("##SoundQuickGroupPopup");
        }

        DrawQuickGroupPopup();
        ImGui.Separator();
    }

    internal static void DrawLibrary(EditorSoundPresentationSnapshot snapshot)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("创建声音", "CREATE SOUND"), BrowserBodyFontScale);
        string omni = DevToolUiSettings.T("全向", "Omni");
        string directional = DevToolUiSettings.T("定向", "Directional");
        string spot = DevToolUiSettings.T("点声源", "Spot");

        if (ImGui.RadioButton(omni, createType == 0)) createType = 0;
        DevToolWidgets.SameLineIfFits(DevToolWidgets.RadioWidth(directional));
        if (ImGui.RadioButton(directional, createType == 1)) createType = 1;
        DevToolWidgets.SameLineIfFits(DevToolWidgets.RadioWidth(spot));
        if (ImGui.RadioButton(spot, createType == 2)) createType = 2;

        ImGui.Spacing();
        DrawLibraryDestination();
        ImGui.Spacing();
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "SoundLibrarySearch", ref search, 128);
        ImGui.Spacing();

        EditorSoundSampleSnapshot[] samples = snapshot.SampleEntries ?? Array.Empty<EditorSoundSampleSnapshot>();
        DevToolSourceMark lastSource = default;
        bool hasLastSource = false;
        int matches = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            EditorSoundSampleSnapshot sample = samples[i];
            if (!MatchesSample(sample, search)) continue;
            matches++;

            DevToolSourceMark source = DevToolSourcePresentation.FromSound(
                sample.SourceKind,
                sample.SourceId,
                sample.SourceName);
            if (!hasLastSource || !DevToolSourcePresentation.SameSource(lastSource, source))
            {
                hasLastSource = true;
                lastSource = source;
                DevToolWidgets.SourceHeader(source, 1.52f, BrowserBodyFontScale);
            }

            if (ImGui.Selectable(sample.Sample + "##CreateSound" + i, false))
            {
                SoundLibraryDestination destination = SoundWorkspaceState.LibraryDestination;
                bool hasGroup = SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot activeGroup);
                if (destination != SoundLibraryDestination.Scene && !hasGroup)
                    destination = SoundLibraryDestination.Scene;

                if (destination != SoundLibraryDestination.WorkingGroup)
                    SoundWorkspaceState.ClearSelection();

                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.CreateFromLibrary,
                    index: (int)destination,
                    key: hasGroup ? activeGroup.Id : string.Empty,
                    text: sample.Sample,
                    secondaryIndex: createType));

                NotifyLibraryDestination(sample.Sample, destination, activeGroup);
            }
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    DevToolUiSettings.T("来源：", "Source: ") + source.Label + "\n" +
                    DevToolUiSettings.T("添加为：", "Add as: ") + TypeName(createType) + "\n" +
                    DevToolUiSettings.T("目标：", "Destination: ") + DestinationName(SoundWorkspaceState.LibraryDestination));
            }
        }

        if (matches == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的环境音频。", "No matching ambient samples."), true);
    }

    internal static void DrawGroups()
    {
        SoundGroupLibrarySnapshot library = SoundGroupLibrary.Current;
        SynchronizeGroupPath(library);

        if (ImGui.CollapsingHeader(DevToolUiSettings.T("库设置##SoundGroupLibrarySettings", "Library Settings##SoundGroupLibrarySettings")))
            DrawLibrarySettings(library);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("已加载音效组", "LOADED SOUND GROUPS"), BrowserBodyFontScale);
        SoundGroupSnapshot[] groups = library.Groups ?? Array.Empty<SoundGroupSnapshot>();
        if (groups.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("还没有音效组。", "No sound groups loaded."), true);
        }
        else
        {
            for (int i = 0; i < groups.Length; i++)
                DrawGroupCard(groups[i], i);
        }

        if (InGameFolderPicker.Draw(
                DevToolUiSettings.T("选择音效组库文件夹", "Select Sound Group Library Folder"),
                out string selectedFolder))
        {
            groupPathEdit = selectedFolder;
            SetGroupDirectory(selectedFolder);
        }
    }

    internal static void DrawAddToGroup(EditorSoundSnapshot selected)
    {
        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("工作音效组", "WORKING GROUP"));

        if (!SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group))
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("没有工作音效组；请在左侧创建或选择一个。", "No working group; create or select one in the Browser."),
                true);
            return;
        }

        ImGui.TextWrapped(group.Name + " · " + group.Id);
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("加入当前声音", "Add Current Sound"),
                "SoundAddToWorkingGroup",
                DevToolButtonTone.Primary,
                true))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.AddSoundToGroup,
                index: selected.Index,
                key: group.Id));
            ActionToastOverlay.Notify(
                "已加入工作音效组：" + group.Name,
                "Added to working group: " + group.Name);
        }
    }

    internal static void DrawAddSelectionToGroup(int[] indices)
    {
        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("工作音效组", "WORKING GROUP"));

        if (!SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group))
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("没有工作音效组；请在左侧创建或选择一个。", "No working group; create or select one in the Browser."),
                true);
            return;
        }

        int count = indices?.Length ?? 0;
        ImGui.TextWrapped(group.Name + " · " + group.Id);
        if (count <= 0) return;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T($"加入选中的 {count} 个声音", $"Add {count} Selected Sounds"),
                "SoundAddSelectionToWorkingGroup",
                DevToolButtonTone.Primary,
                true))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.AddSoundsToGroup,
                key: group.Id,
                indices: indices));
            ActionToastOverlay.Notify(
                $"已向 {group.Name} 加入 {count} 个声音",
                $"Added {count} sounds to {group.Name}");
        }
    }

    internal static void DrawSelectedResourceStatus(EditorSoundPresentationSnapshot snapshot, EditorSoundSnapshot selected)
    {
        DevToolSourceMark source = DevToolSourcePresentation.FromSound(
            selected.ResourceSourceKind,
            selected.ResourceSourceId,
            selected.ResourceSourceName);
        DevToolSourcePresentation.DrawInline(source, DevToolUiSettings.T("资源：", "Resource:"));
    }

    internal static void DrawProblemsOnce() => SoundGroupProblemsWindow.DrawOnce();

    private static void DrawLibraryDestination()
    {
        bool hasGroup = SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group);
        if (!hasGroup && SoundWorkspaceState.LibraryDestination != SoundLibraryDestination.Scene)
            SoundWorkspaceState.LibraryDestination = SoundLibraryDestination.Scene;

        DevToolWidgets.MutedText(DevToolUiSettings.T("添加目标", "Destination"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoundLibraryDestination", DestinationName(SoundWorkspaceState.LibraryDestination)))
        {
            DrawDestinationOption(SoundLibraryDestination.Scene, DevToolUiSettings.T("场景", "Scene"), true);
            DrawDestinationOption(
                SoundLibraryDestination.WorkingGroup,
                hasGroup ? DevToolUiSettings.T("工作音效组 · ", "Working Group · ") + group.Name : DevToolUiSettings.T("工作音效组", "Working Group"),
                hasGroup);
            DrawDestinationOption(
                SoundLibraryDestination.SceneAndWorkingGroup,
                hasGroup ? DevToolUiSettings.T("场景 + 工作音效组 · ", "Scene + Working Group · ") + group.Name : DevToolUiSettings.T("场景 + 工作音效组", "Scene + Working Group"),
                hasGroup);
            ImGui.EndCombo();
        }

        if (!hasGroup)
            DevToolWidgets.MutedText(DevToolUiSettings.T("创建工作音效组后，可一键直接写入 Group 或同时写入 Scene + Group。", "Create a working group to write directly to it, or to Scene + Group in one click."), true);
    }

    private static void DrawDestinationOption(SoundLibraryDestination value, string label, bool enabled)
    {
        if (!enabled) ImGui.BeginDisabled();
        bool selected = SoundWorkspaceState.LibraryDestination == value;
        if (ImGui.Selectable(label + "##SoundDestination" + value, selected) && enabled)
            SoundWorkspaceState.LibraryDestination = value;
        if (selected) ImGui.SetItemDefaultFocus();
        if (!enabled) ImGui.EndDisabled();
    }

    private static void DrawLibrarySettings(SoundGroupLibrarySnapshot library)
    {
        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T("保存目录", "Library folder"),
            "SoundGroupLibraryFolder",
            ref groupPathEdit,
            1024);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SetGroupDirectory(groupPathEdit);

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("选择文件夹", "Choose Folder"),
                "SoundGroupChooseFolder",
                DevToolButtonTone.Normal))
        {
            InGameFolderPicker.Open(groupPathEdit);
        }

        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("恢复默认", "Default")));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("恢复默认", "Default"),
                "SoundGroupDefaultFolder",
                DevToolButtonTone.Normal))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.ResetGroupDirectory));
        }

        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("重新读取", "Reload")));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重新读取", "Reload"),
                "SoundGroupReload",
                DevToolButtonTone.Normal))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.ReloadGroups));
        }

        DevToolWidgets.MutedText(DevToolUiSettings.T("当前文件：", "Current file: ") + library.LocalFilePath, true);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "共享给其他地图开发者时，在 Mod 中使用：mods\\ModName\\music\\sound-groups.xml",
                "For portable groups use: mods\\ModName\\music\\sound-groups.xml"),
            true);
    }

    private static void DrawQuickGroupPopup()
    {
        if (!ImGui.BeginPopup("##SoundQuickGroupPopup")) return;

        ImGui.TextUnformatted(DevToolUiSettings.T("新建工作音效组", "New Working Group"));
        ImGui.Separator();

        DevToolWidgets.MutedText(DevToolUiSettings.T("显示名称", "Display name"));
        ImGui.SetNextItemWidth(340f);
        bool nameChanged = ImGui.InputText("##SoundQuickGroupName", ref quickGroupName, 512);
        if (nameChanged && !quickGroupIdManual)
            quickGroupId = SoundWorkspaceState.SuggestUniqueGroupId(quickGroupName);

        DevToolWidgets.MutedText(DevToolUiSettings.T("编组 ID", "Group ID"));
        ImGui.SetNextItemWidth(340f);
        ImGui.InputText("##SoundQuickGroupId", ref quickGroupId, 128);
        if (ImGui.IsItemEdited()) quickGroupIdManual = true;

        bool validId = SoundWorkspaceState.IsValidGroupId(quickGroupId);
        bool duplicate = SoundWorkspaceState.GroupIdExists(quickGroupId);
        if (!validId && !string.IsNullOrWhiteSpace(quickGroupId))
            ImGui.TextColored(new Num.Vector4(1f, 0.64f, 0.30f, 1f), DevToolUiSettings.T("ID 仅使用 A-Z / a-z / 0-9 / _ / -。", "Use only A-Z / a-z / 0-9 / _ / - in Group IDs."));
        else if (duplicate)
            ImGui.TextColored(new Num.Vector4(1f, 0.64f, 0.30f, 1f), DevToolUiSettings.T("这个 Group ID 已存在。", "This Group ID already exists."));

        bool canCreate = !string.IsNullOrWhiteSpace(quickGroupName) && validId && !duplicate;
        if (!canCreate) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("创建并设为工作组", "Create & Use"),
                "SoundQuickGroupConfirm",
                DevToolButtonTone.Primary))
        {
            string id = quickGroupId.Trim();
            string name = quickGroupName.Trim();
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.CreateGroup,
                key: id,
                text: name));
            SoundWorkspaceState.SetActiveGroup(id);
            ActionToastOverlay.Notify("已创建工作音效组：" + name, "Created working group: " + name);
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.EndPopup();
    }

    private static void DrawGroupCard(SoundGroupSnapshot group, int index)
    {
        bool active = group.IsLocal && string.Equals(group.Id, SoundWorkspaceState.ActiveGroupId, StringComparison.OrdinalIgnoreCase);
        string header = (active ? "● " : string.Empty) + group.Name + " · " + group.Id + "##SoundGroup" + index;
        if (!ImGui.CollapsingHeader(header)) return;

        DevToolWidgets.MutedText(DevToolUiSettings.T("来源：", "Source: ") + group.SourceName, true);
        DevToolWidgets.MutedText(group.SourcePath, true);

        SoundGroupEntrySnapshot[] sounds = group.Sounds ?? Array.Empty<SoundGroupEntrySnapshot>();
        int available = 0;
        for (int i = 0; i < sounds.Length; i++)
            if (sounds[i].Available) available++;

        string resourceSummary = DevToolUiSettings.T(
            $"资源状态：{available}/{sounds.Length} 可用",
            $"Resources: {available}/{sounds.Length} available");
        ImGui.TextColored(
            group.HasMissingResources ? new Num.Vector4(1f, 0.56f, 0.32f, 1f) : new Num.Vector4(0.52f, 0.86f, 0.60f, 1f),
            resourceSummary);

        for (int i = 0; i < sounds.Length; i++)
        {
            SoundGroupEntrySnapshot sound = sounds[i];
            string status = sound.Available ? "✓ " : "✕ ";
            Num.Vector4 color = sound.Available
                ? new Num.Vector4(0.82f, 0.90f, 0.84f, 1f)
                : new Num.Vector4(1f, 0.42f, 0.40f, 1f);
            ImGui.TextColored(color, status + sound.Sample);
            ImGui.SameLine();
            ImGui.TextDisabled("· " + sound.Type);
            ImGui.SameLine();
            DevToolSourceMark source = DevToolSourcePresentation.FromSound(
                sound.SourceKind,
                sound.SourceId,
                sound.SourceName);
            DevToolSourcePresentation.DrawInline(source);
        }

        if (group.IsLocal && !active)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("设为工作组", "Set Working"),
                    "SoundGroupSetWorking" + index,
                    DevToolButtonTone.Subtle))
            {
                SoundWorkspaceState.SetActiveGroup(group.Id);
            }
            DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("应用到房间", "Apply to Room")));
        }
        else if (active)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前工作组", "Current Working Group"));
        }

        string applyLabel = group.HasMissingResources
            ? DevToolUiSettings.T("应用可用项", "Apply Available")
            : DevToolUiSettings.T("应用到当前房间", "Apply to Room");
        if (DevToolWidgets.ActionButton(applyLabel, "SoundGroupApply" + index, DevToolButtonTone.Primary))
        {
            SoundWorkspaceState.ClearSelection();
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.ApplyGroup,
                key: group.Id));
            ActionToastOverlay.Notify("已应用音效组：" + group.Name, "Applied sound group: " + group.Name);
        }
        if (group.HasMissingResources && ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T("缺失资源会被跳过，详细信息见预警窗口。", "Missing resources are skipped; see the Problems window."));

        if (group.IsLocal)
        {
            DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("删除", "Delete")));
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除", "Delete"),
                    "SoundGroupDelete" + index,
                    DevToolButtonTone.Danger))
            {
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.DeleteGroup,
                    key: group.Id));
                if (active) SoundWorkspaceState.SetActiveGroup(string.Empty);
            }
        }

        ImGui.Spacing();
    }

    private static void NotifyLibraryDestination(string sample, SoundLibraryDestination destination, SoundGroupSnapshot group)
    {
        switch (destination)
        {
            case SoundLibraryDestination.WorkingGroup:
                ActionToastOverlay.Notify(
                    "已加入工作音效组：" + (group?.Name ?? sample),
                    "Added to working group: " + (group?.Name ?? sample));
                break;
            case SoundLibraryDestination.SceneAndWorkingGroup:
                ActionToastOverlay.Notify(
                    "已加入场景和工作音效组",
                    "Added to Scene and Working Group");
                break;
            default:
                ActionToastOverlay.Notify("已加入场景：" + sample, "Added to Scene: " + sample);
                break;
        }
    }

    private static string DestinationName(SoundLibraryDestination destination)
    {
        bool hasGroup = SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group);
        return destination switch
        {
            SoundLibraryDestination.WorkingGroup when hasGroup => DevToolUiSettings.T("工作音效组 · ", "Working Group · ") + group.Name,
            SoundLibraryDestination.SceneAndWorkingGroup when hasGroup => DevToolUiSettings.T("场景 + 工作音效组 · ", "Scene + Working Group · ") + group.Name,
            SoundLibraryDestination.WorkingGroup => DevToolUiSettings.T("工作音效组", "Working Group"),
            SoundLibraryDestination.SceneAndWorkingGroup => DevToolUiSettings.T("场景 + 工作音效组", "Scene + Working Group"),
            _ => DevToolUiSettings.T("场景", "Scene")
        };
    }

    private static void SetGroupDirectory(string directory)
    {
        SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
            SoundEditorCommandKind.SetGroupDirectory,
            text: directory?.Trim() ?? string.Empty));
    }

    private static void SynchronizeGroupPath(SoundGroupLibrarySnapshot library)
    {
        string path = library?.LocalDirectory ?? string.Empty;
        if (string.Equals(path, observedGroupPath, StringComparison.Ordinal)) return;
        observedGroupPath = path;
        groupPathEdit = path;
    }

    private static bool MatchesSample(EditorSoundSampleSnapshot value, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return (value?.Sample?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (value?.SourceName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (value?.SourceId?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static string TypeName(int type) => type switch
    {
        0 => DevToolUiSettings.T("全向声音", "Omnidirectional"),
        1 => DevToolUiSettings.T("定向声音", "Directional"),
        2 => DevToolUiSettings.T("点声源", "Spot"),
        _ => DevToolUiSettings.T("声音", "Sound")
    };
}
