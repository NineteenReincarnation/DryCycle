using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class SoundLibraryGroupsView
{
    private sealed class SampleRow
    {
        internal EditorSoundSampleSnapshot Sample;
        internal DevToolSourceMark Source;
        internal string SelectableLabel;
    }

    private sealed class SoundEntryRow
    {
        internal SoundGroupEntrySnapshot Entry;
        internal DevToolSourceMark Source;
        internal string StatusLabel;
        internal string TypeLabel;
    }

    private sealed class GroupPresentation
    {
        internal SoundGroupSnapshot Group;
        internal string Preview;
        internal string ComboLabel;
        internal string WorkingSummary;
        internal string Line;
        internal string ActiveHeader;
        internal string InactiveHeader;
        internal string ResourceSummary;
        internal string SetWorkingId;
        internal string ApplyId;
        internal string DeleteId;
        internal SoundEntryRow[] SoundRows = Array.Empty<SoundEntryRow>();
    }

    private static int createType;
    private static string search = string.Empty;
    private static string groupPathEdit = string.Empty;
    private static string observedGroupPath = string.Empty;
    private static string quickGroupName = string.Empty;
    private static string quickGroupId = string.Empty;
    private static bool quickGroupIdManual;
    private const float BrowserBodyFontScale = 1.22f;

    private static EditorSoundSampleSnapshot[] projectedSampleSource;
    private static string projectedSampleSearch = string.Empty;
    private static bool projectedSampleChinese;
    private static readonly List<SampleRow> projectedSamples = new();

    private static SoundGroupSnapshot[] projectedGroupSource;
    private static bool projectedGroupChinese;
    private static GroupPresentation[] projectedGroups = Array.Empty<GroupPresentation>();
    private static readonly Dictionary<string, GroupPresentation> groupPresentationById =
        new(StringComparer.OrdinalIgnoreCase);

    private static SoundGroupSnapshot destinationGroup;
    private static bool destinationChinese;
    private static string destinationScene = string.Empty;
    private static string destinationWorking = string.Empty;
    private static string destinationSceneAndWorking = string.Empty;
    private static string destinationWorkingOption = string.Empty;
    private static string destinationSceneAndWorkingOption = string.Empty;

    private static string libraryPathSource = string.Empty;
    private static bool libraryPathChinese;
    private static string libraryPathDisplay = string.Empty;

    private static SoundGroupSnapshot selectionActionGroup;
    private static int selectionActionCount = -1;
    private static bool selectionActionChinese;
    private static string selectionActionLabel = string.Empty;

    internal static void DrawWorkingGroupBar()
    {
        SoundWorkspaceState.SynchronizeGroups();
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        EnsureGroupPresentations(groups);
        bool hasActive = SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot active);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("工作音效组", "WORKING GROUP"), BrowserBodyFontScale);
        if (hasActive)
        {
            GroupPresentation activePresentation = FindGroupPresentation(active);
            string preview = activePresentation?.Preview ?? active.Name ?? active.Id ?? string.Empty;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##SoundWorkingGroup", preview))
            {
                for (int i = 0; i < projectedGroups.Length; i++)
                {
                    GroupPresentation presentation = projectedGroups[i];
                    SoundGroupSnapshot group = presentation.Group;
                    if (!group.IsLocal) continue;
                    bool selected = string.Equals(group.Id, active.Id, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(presentation.ComboLabel, selected))
                        SoundWorkspaceState.SetActiveGroup(group.Id);
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.TextDisabled(activePresentation?.WorkingSummary ?? BuildWorkingSummary(active));
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
        EnsureSampleProjection(samples);
        DevToolSourceMark lastSource = default;
        bool hasLastSource = false;
        for (int i = 0; i < projectedSamples.Count; i++)
        {
            SampleRow row = projectedSamples[i];
            EditorSoundSampleSnapshot sample = row.Sample;
            DevToolSourceMark source = row.Source;
            if (!hasLastSource || !DevToolSourcePresentation.SameSource(lastSource, source))
            {
                hasLastSource = true;
                lastSource = source;
                DevToolWidgets.SourceHeader(source, 1.52f, BrowserBodyFontScale);
            }

            if (ImGui.Selectable(row.SelectableLabel, false))
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

        if (projectedSamples.Count == 0)
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
        EnsureGroupPresentations(groups);
        if (groups.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("还没有音效组。", "No sound groups loaded."), true);
        }
        else
        {
            for (int i = 0; i < projectedGroups.Length; i++)
                DrawGroupCard(projectedGroups[i]);
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

        ImGui.TextWrapped(FindGroupPresentation(group)?.Line ?? BuildGroupLine(group));
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
        ImGui.TextWrapped(FindGroupPresentation(group)?.Line ?? BuildGroupLine(group));
        if (count <= 0) return;
        if (DevToolWidgets.ActionButton(
                GetSelectionActionLabel(group, count),
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
        EnsureDestinationLabels(hasGroup ? group : null);

        DevToolWidgets.MutedText(DevToolUiSettings.T("添加目标", "Destination"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoundLibraryDestination", DestinationName(SoundWorkspaceState.LibraryDestination)))
        {
            DrawDestinationOption(SoundLibraryDestination.Scene, destinationScene, true, "##SoundDestinationScene");
            DrawDestinationOption(
                SoundLibraryDestination.WorkingGroup,
                destinationWorkingOption,
                hasGroup,
                "##SoundDestinationWorking");
            DrawDestinationOption(
                SoundLibraryDestination.SceneAndWorkingGroup,
                destinationSceneAndWorkingOption,
                hasGroup,
                "##SoundDestinationSceneWorking");
            ImGui.EndCombo();
        }

        if (!hasGroup)
            DevToolWidgets.MutedText(DevToolUiSettings.T("创建工作音效组后，可一键直接写入 Group 或同时写入 Scene + Group。", "Create a working group to write directly to it, or to Scene + Group in one click."), true);
    }

    private static void DrawDestinationOption(
        SoundLibraryDestination value,
        string label,
        bool optionEnabled,
        string id)
    {
        if (!optionEnabled) ImGui.BeginDisabled();
        bool selected = SoundWorkspaceState.LibraryDestination == value;
        if (ImGui.Selectable(label + id, selected) && optionEnabled)
            SoundWorkspaceState.LibraryDestination = value;
        if (selected) ImGui.SetItemDefaultFocus();
        if (!optionEnabled) ImGui.EndDisabled();
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

        string localFilePath = library?.LocalFilePath ?? string.Empty;
        bool chinese = DevToolUiSettings.IsChinese;
        if (!string.Equals(libraryPathSource, localFilePath, StringComparison.Ordinal) ||
            libraryPathChinese != chinese)
        {
            libraryPathSource = localFilePath;
            libraryPathChinese = chinese;
            libraryPathDisplay = DevToolUiSettings.T("当前文件：", "Current file: ") + localFilePath;
        }
        DevToolWidgets.MutedText(libraryPathDisplay, true);
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

    private static void DrawGroupCard(GroupPresentation presentation)
    {
        SoundGroupSnapshot group = presentation.Group;
        bool active = group.IsLocal && string.Equals(group.Id, SoundWorkspaceState.ActiveGroupId, StringComparison.OrdinalIgnoreCase);
        if (!ImGui.CollapsingHeader(active ? presentation.ActiveHeader : presentation.InactiveHeader)) return;

        DevToolWidgets.MutedText(DevToolUiSettings.T("来源：", "Source: ") + group.SourceName, true);
        DevToolWidgets.MutedText(group.SourcePath, true);

        ImGui.TextColored(
            group.HasMissingResources ? new Num.Vector4(1f, 0.56f, 0.32f, 1f) : new Num.Vector4(0.52f, 0.86f, 0.60f, 1f),
            presentation.ResourceSummary);

        SoundEntryRow[] rows = presentation.SoundRows;
        for (int i = 0; i < rows.Length; i++)
        {
            SoundEntryRow row = rows[i];
            SoundGroupEntrySnapshot sound = row.Entry;
            Num.Vector4 color = sound.Available
                ? new Num.Vector4(0.82f, 0.90f, 0.84f, 1f)
                : new Num.Vector4(1f, 0.42f, 0.40f, 1f);
            ImGui.TextColored(color, row.StatusLabel);
            ImGui.SameLine();
            ImGui.TextDisabled(row.TypeLabel);
            ImGui.SameLine();
            DevToolSourcePresentation.DrawInline(row.Source);
        }

        if (group.IsLocal && !active)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("设为工作组", "Set Working"),
                    presentation.SetWorkingId,
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
        if (DevToolWidgets.ActionButton(applyLabel, presentation.ApplyId, DevToolButtonTone.Primary))
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
                    presentation.DeleteId,
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

    private static void EnsureSampleProjection(EditorSoundSampleSnapshot[] samples)
    {
        string normalizedSearch = search?.Trim() ?? string.Empty;
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedSampleSource, samples) &&
            string.Equals(projectedSampleSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedSampleChinese == chinese)
            return;

        projectedSamples.Clear();
        for (int i = 0; i < samples.Length; i++)
        {
            EditorSoundSampleSnapshot sample = samples[i];
            if (!MatchesSample(sample, normalizedSearch)) continue;
            DevToolSourceMark source = DevToolSourcePresentation.FromSound(
                sample.SourceKind,
                sample.SourceId,
                sample.SourceName);
            projectedSamples.Add(new SampleRow
            {
                Sample = sample,
                Source = source,
                SelectableLabel = (sample.Sample ?? string.Empty) + "##CreateSound" + i
            });
        }

        projectedSampleSource = samples;
        projectedSampleSearch = normalizedSearch;
        projectedSampleChinese = chinese;
    }

    private static void EnsureGroupPresentations(SoundGroupSnapshot[] groups)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedGroupSource, groups) && projectedGroupChinese == chinese) return;

        GroupPresentation[] next = new GroupPresentation[groups.Length];
        groupPresentationById.Clear();
        for (int i = 0; i < groups.Length; i++)
        {
            SoundGroupSnapshot group = groups[i];
            SoundGroupEntrySnapshot[] sounds = group?.Sounds ?? Array.Empty<SoundGroupEntrySnapshot>();
            int available = 0;
            SoundEntryRow[] soundRows = new SoundEntryRow[sounds.Length];
            for (int s = 0; s < sounds.Length; s++)
            {
                SoundGroupEntrySnapshot sound = sounds[s];
                if (sound.Available) available++;
                soundRows[s] = new SoundEntryRow
                {
                    Entry = sound,
                    Source = DevToolSourcePresentation.FromSound(
                        sound.SourceKind, sound.SourceId, sound.SourceName),
                    StatusLabel = (sound.Available ? "✓ " : "✕ ") + sound.Sample,
                    TypeLabel = "· " + sound.Type
                };
            }

            string line = BuildGroupLine(group);
            string headerBase = (group?.Name ?? string.Empty) + " · " + (group?.Id ?? string.Empty);
            GroupPresentation presentation = new()
            {
                Group = group,
                Preview = line,
                ComboLabel = line + "  (" + sounds.Length + ")##WorkingGroup" + i,
                WorkingSummary = BuildWorkingSummary(group),
                Line = line,
                ActiveHeader = "● " + headerBase + "##SoundGroup" + i,
                InactiveHeader = headerBase + "##SoundGroup" + i,
                ResourceSummary = DevToolUiSettings.T(
                    $"资源状态：{available}/{sounds.Length} 可用",
                    $"Resources: {available}/{sounds.Length} available"),
                SetWorkingId = "SoundGroupSetWorking" + i,
                ApplyId = "SoundGroupApply" + i,
                DeleteId = "SoundGroupDelete" + i,
                SoundRows = soundRows
            };
            next[i] = presentation;
            if (!string.IsNullOrEmpty(group?.Id)) groupPresentationById[group.Id] = presentation;
        }

        projectedGroupSource = groups;
        projectedGroupChinese = chinese;
        projectedGroups = next;
        destinationGroup = null;
        selectionActionGroup = null;
    }

    private static GroupPresentation FindGroupPresentation(SoundGroupSnapshot group)
    {
        if (group == null) return null;
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        EnsureGroupPresentations(groups);
        if (!string.IsNullOrEmpty(group.Id) &&
            groupPresentationById.TryGetValue(group.Id, out GroupPresentation presentation))
            return presentation;
        return null;
    }

    private static string BuildGroupLine(SoundGroupSnapshot group) =>
        (group?.Name ?? string.Empty) + " · " + (group?.Id ?? string.Empty);

    private static string BuildWorkingSummary(SoundGroupSnapshot group)
    {
        int count = group?.Sounds?.Length ?? 0;
        return DevToolUiSettings.T(
            $"{count} 个声音 · Library / Scene / Inspector 共用",
            $"{count} sounds · shared by Library / Scene / Inspector");
    }

    private static void EnsureDestinationLabels(SoundGroupSnapshot group)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(destinationGroup, group) && destinationChinese == chinese &&
            !string.IsNullOrEmpty(destinationScene))
            return;

        destinationGroup = group;
        destinationChinese = chinese;
        destinationScene = DevToolUiSettings.T("场景", "Scene");
        string workingBase = DevToolUiSettings.T("工作音效组", "Working Group");
        string sceneWorkingBase = DevToolUiSettings.T("场景 + 工作音效组", "Scene + Working Group");
        if (group == null)
        {
            destinationWorking = workingBase;
            destinationSceneAndWorking = sceneWorkingBase;
        }
        else
        {
            destinationWorking = workingBase + " · " + group.Name;
            destinationSceneAndWorking = sceneWorkingBase + " · " + group.Name;
        }
        destinationWorkingOption = destinationWorking;
        destinationSceneAndWorkingOption = destinationSceneAndWorking;
    }

    private static string GetSelectionActionLabel(SoundGroupSnapshot group, int count)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(selectionActionGroup, group) && selectionActionCount == count &&
            selectionActionChinese == chinese)
            return selectionActionLabel;

        selectionActionGroup = group;
        selectionActionCount = count;
        selectionActionChinese = chinese;
        selectionActionLabel = DevToolUiSettings.T(
            $"加入选中的 {count} 个声音",
            $"Add {count} Selected Sounds");
        return selectionActionLabel;
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
        EnsureDestinationLabels(hasGroup ? group : null);
        return destination switch
        {
            SoundLibraryDestination.WorkingGroup => destinationWorking,
            SoundLibraryDestination.SceneAndWorkingGroup => destinationSceneAndWorking,
            _ => destinationScene
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

    private static bool MatchesSample(EditorSoundSampleSnapshot value, string normalizedQuery)
    {
        if (string.IsNullOrEmpty(normalizedQuery)) return true;
        return (value?.Sample?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (value?.SourceName?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (value?.SourceId?.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static string TypeName(int type) => type switch
    {
        0 => DevToolUiSettings.T("全向声音", "Omnidirectional"),
        1 => DevToolUiSettings.T("定向声音", "Directional"),
        2 => DevToolUiSettings.T("点声源", "Spot"),
        _ => DevToolUiSettings.T("声音", "Sound")
    };
}
