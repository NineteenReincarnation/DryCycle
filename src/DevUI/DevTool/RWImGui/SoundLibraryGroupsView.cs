using System;
using System.Collections.Generic;
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
    private static string groupIdEdit = string.Empty;
    private static string groupNameEdit = string.Empty;
    private static string targetGroupId = string.Empty;
    private const float BrowserBodyFontScale = 1.22f;

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
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "SoundLibrarySearch", ref search, 128);
        ImGui.Spacing();

        EditorSoundSampleSnapshot[] samples = snapshot.SampleEntries ?? Array.Empty<EditorSoundSampleSnapshot>();
        EditorSoundSourceKind? lastKind = null;
        string lastSource = null;
        int matches = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            EditorSoundSampleSnapshot sample = samples[i];
            if (!MatchesSample(sample, search)) continue;
            matches++;

            if (lastKind != sample.SourceKind)
            {
                lastKind = sample.SourceKind;
                lastSource = null;
                DevToolWidgets.SourceHeader(
                    SourceKindLabel(sample.SourceKind),
                    SourceColor(sample.SourceKind),
                    1.52f,
                    BrowserBodyFontScale);
            }

            if ((sample.SourceKind == EditorSoundSourceKind.Dlc || sample.SourceKind == EditorSoundSourceKind.Mod) &&
                !string.Equals(lastSource, sample.SourceName, StringComparison.Ordinal))
            {
                lastSource = sample.SourceName;
                DevToolWidgets.SectionHeader(sample.SourceName, BrowserBodyFontScale);
            }

            if (ImGui.Selectable(sample.Sample + "##CreateSound" + i, false))
            {
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.Create,
                    text: sample.Sample,
                    secondaryIndex: createType));
            }
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    DevToolUiSettings.T("来源：", "Source: ") + sample.SourceName + "\n" +
                    DevToolUiSettings.T("添加为 ", "Add as ") + TypeName(createType));
            }
        }

        if (matches == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的环境音频。", "No matching ambient samples."), true);
    }

    internal static void DrawGroups()
    {
        SoundGroupLibrarySnapshot library = SoundGroupLibrary.Current;
        SynchronizeGroupPath(library);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("本地音效组库", "LOCAL SOUND GROUP LIBRARY"), BrowserBodyFontScale);
        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T("保存目录", "Library folder"),
            "SoundGroupLibraryFolder",
            ref groupPathEdit,
            512);

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("使用此目录", "Use Folder"),
                "SoundGroupUseFolder",
                DevToolButtonTone.Primary))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetGroupDirectory,
                text: groupPathEdit));
        }
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("恢复默认", "Default")));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("恢复默认", "Default"),
                "SoundGroupDefaultFolder",
                DevToolButtonTone.Subtle))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.ResetGroupDirectory));
        }
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("重新读取", "Reload")));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重新读取", "Reload"),
                "SoundGroupReload",
                DevToolButtonTone.Subtle))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.ReloadGroups));
        }

        DevToolWidgets.MutedText(
            DevToolUiSettings.T("当前文件：", "Current file: ") + library.LocalFilePath,
            true);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "需要让其他地图开发者自动读取编组时，请在 Mod 中创建：mods\\ModName\\music\\sound-groups.xml",
                "For portable groups, create: mods\\ModName\\music\\sound-groups.xml"),
            true);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("新建本地音效组", "NEW LOCAL SOUND GROUP"), BrowserBodyFontScale);
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("编组 ID", "Group ID"), "SoundGroupNewId", ref groupIdEdit, 128);
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("显示名称", "Display name"), "SoundGroupNewName", ref groupNameEdit, 128);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("ID 用于去重和共享，建议使用 ModID.GroupName。", "IDs are used for deduplication and sharing; ModID.GroupName is recommended."),
            true);

        bool canCreate = !string.IsNullOrWhiteSpace(groupIdEdit) && !string.IsNullOrWhiteSpace(groupNameEdit);
        if (!canCreate) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("创建音效组", "Create Group"),
                "SoundGroupCreate",
                DevToolButtonTone.Primary,
                true))
        {
            string nextId = groupIdEdit.Trim();
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.CreateGroup,
                key: nextId,
                text: groupNameEdit.Trim()));
            targetGroupId = nextId;
            groupIdEdit = string.Empty;
            groupNameEdit = string.Empty;
        }
        if (!canCreate) ImGui.EndDisabled();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("已加载音效组", "LOADED SOUND GROUPS"), BrowserBodyFontScale);
        SoundGroupSnapshot[] groups = library.Groups ?? Array.Empty<SoundGroupSnapshot>();
        if (groups.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("还没有音效组。", "No sound groups loaded."), true);
            return;
        }

        for (int i = 0; i < groups.Length; i++)
            DrawGroupCard(groups[i], i);
    }

    internal static void DrawAddToGroup(EditorSoundSnapshot selected)
    {
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        List<SoundGroupSnapshot> local = new();
        for (int i = 0; i < groups.Length; i++)
            if (groups[i].IsLocal) local.Add(groups[i]);

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("音效组", "SOUND GROUP"));

        if (local.Count == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("没有可写入的本地音效组，请先在“音效组”页新建。", "No writable local group. Create one in the Groups tab first."),
                true);
            return;
        }

        bool targetExists = false;
        for (int i = 0; i < local.Count; i++)
            targetExists |= string.Equals(local[i].Id, targetGroupId, StringComparison.OrdinalIgnoreCase);
        if (!targetExists) targetGroupId = local[0].Id;

        SoundGroupSnapshot current = local[0];
        for (int i = 0; i < local.Count; i++)
            if (string.Equals(local[i].Id, targetGroupId, StringComparison.OrdinalIgnoreCase)) current = local[i];

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoundTargetGroup", current.Name + " · " + current.Id))
        {
            for (int i = 0; i < local.Count; i++)
            {
                SoundGroupSnapshot group = local[i];
                bool chosen = string.Equals(group.Id, targetGroupId, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(group.Name + " · " + group.Id + "##TargetGroup" + i, chosen))
                    targetGroupId = group.Id;
                if (chosen) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("加入当前声音", "Add Current Sound"),
                "SoundAddToGroup",
                DevToolButtonTone.Primary,
                true))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.AddSoundToGroup,
                index: selected.Index,
                key: targetGroupId));
        }
    }

    internal static void DrawSelectedResourceStatus(EditorSoundPresentationSnapshot snapshot, EditorSoundSnapshot selected)
    {
        EditorSoundSampleSnapshot[] samples = snapshot.SampleEntries ?? Array.Empty<EditorSoundSampleSnapshot>();
        for (int i = 0; i < samples.Length; i++)
        {
            if (!string.Equals(samples[i].Sample, selected.Sample, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.TextDisabled(
                DevToolUiSettings.T("资源：", "Resource: ") +
                SourceKindLabel(samples[i].SourceKind) + " · " + samples[i].SourceName);
            return;
        }

        EditorSoundSampleSnapshot resolved = SoundSampleCatalog.Resolve(selected.Sample);
        ImGui.TextDisabled(
            DevToolUiSettings.T("资源：", "Resource: ") +
            SourceKindLabel(resolved.SourceKind) + " · " + resolved.SourceName);
    }

    internal static void DrawProblemsOnce() => SoundGroupProblemsWindow.DrawOnce();

    private static void DrawGroupCard(SoundGroupSnapshot group, int index)
    {
        string header = group.Name + " · " + group.Id + "##SoundGroup" + index;
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
            ImGui.TextDisabled("· " + sound.Type + " · " + sound.SourceName);
        }

        string applyLabel = group.HasMissingResources
            ? DevToolUiSettings.T("应用可用项", "Apply Available")
            : DevToolUiSettings.T("应用到当前房间", "Apply to Room");
        if (DevToolWidgets.ActionButton(applyLabel, "SoundGroupApply" + index, DevToolButtonTone.Primary))
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.ApplyGroup,
                key: group.Id));
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
                if (string.Equals(targetGroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                    targetGroupId = string.Empty;
            }
        }

        ImGui.Spacing();
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
               (value?.SourceName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static string TypeName(int type) => type switch
    {
        0 => DevToolUiSettings.T("全向声音", "Omnidirectional"),
        1 => DevToolUiSettings.T("定向声音", "Directional"),
        2 => DevToolUiSettings.T("点声源", "Spot"),
        _ => DevToolUiSettings.T("声音", "Sound")
    };

    private static string SourceKindLabel(EditorSoundSourceKind kind) => kind switch
    {
        EditorSoundSourceKind.Vanilla => DevToolUiSettings.T("原版", "VANILLA"),
        EditorSoundSourceKind.Dlc => DevToolUiSettings.T("DLC 添加", "DLC"),
        EditorSoundSourceKind.Mod => DevToolUiSettings.T("模组添加", "MODS"),
        _ => DevToolUiSettings.T("缺失", "MISSING")
    };

    private static Num.Vector4 SourceColor(EditorSoundSourceKind kind) => kind switch
    {
        EditorSoundSourceKind.Vanilla => new Num.Vector4(0.88f, 0.88f, 0.88f, 1f),
        EditorSoundSourceKind.Dlc => new Num.Vector4(1f, 0.70f, 0.34f, 1f),
        EditorSoundSourceKind.Mod => new Num.Vector4(0.78f, 0.72f, 1f, 1f),
        _ => new Num.Vector4(1f, 0.42f, 0.40f, 1f)
    };
}
