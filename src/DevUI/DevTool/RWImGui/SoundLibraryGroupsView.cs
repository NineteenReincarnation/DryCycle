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
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.Create,
                    text: sample.Sample,
                    secondaryIndex: createType));
            }
            if (ImGui.IsItemHovered())
            {
                DevToolTooltip.Show(
                    DevToolUiSettings.T("来源：", "Source: ") + source.Label + "\n" +
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

        if (InGameFolderPicker.Draw(
                DevToolUiSettings.T("选择音效组库文件夹", "Select Sound Group Library Folder"),
                out string selectedFolder))
        {
            groupPathEdit = selectedFolder;
            SetGroupDirectory(selectedFolder);
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
        bool nonAsciiLetterId = ContainsNonAsciiLetter(groupIdEdit);
        if (nonAsciiLetterId)
        {
            ImGui.TextColored(
                new Num.Vector4(1f, 0.64f, 0.30f, 1f),
                DevToolUiSettings.T(
                    "检测到非英文字母字符。建议 Group ID 仅使用 A-Z / a-z；中文和其他字符请写在显示名称里。",
                    "Group ID contains non-letter characters. Prefer A-Z / a-z only; use Display name for Unicode text."));
        }

        DrawUnicodeInputText(
            DevToolUiSettings.T("显示名称", "Display name"),
            "SoundGroupNewName",
            ref groupNameEdit,
            512);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "显示名称支持中文和其他 Unicode 字符；Group ID 用于去重和共享，建议只使用英文字母 A-Z / a-z。",
                "Display names support Unicode; Group IDs are used for deduplication and sharing, so A-Z / a-z only is recommended."),
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
        SoundGroupSnapshot firstLocal = null;
        SoundGroupSnapshot current = null;

        // Do not allocate a temporary List every frame. Groups are already a stable snapshot; scan
        // that array directly and skip non-local entries when the combo is actually opened.
        for (int i = 0; i < groups.Length; i++)
        {
            SoundGroupSnapshot group = groups[i];
            if (!group.IsLocal) continue;
            firstLocal ??= group;
            if (string.Equals(group.Id, targetGroupId, StringComparison.OrdinalIgnoreCase))
                current = group;
        }

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("音效组", "SOUND GROUP"));

        if (firstLocal == null)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("没有可写入的本地音效组，请先在“音效组”页新建。", "No writable local group. Create one in the Groups tab first."),
                true);
            return;
        }

        if (current == null)
        {
            current = firstLocal;
            targetGroupId = current.Id;
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##SoundTargetGroup", current.Name + " · " + current.Id))
        {
            for (int i = 0; i < groups.Length; i++)
            {
                SoundGroupSnapshot group = groups[i];
                if (!group.IsLocal) continue;
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
        DevToolSourceMark source = DevToolSourcePresentation.FromSound(
            selected.ResourceSourceKind,
            selected.ResourceSourceId,
            selected.ResourceSourceName);
        DevToolSourcePresentation.DrawInline(source, DevToolUiSettings.T("资源：", "Resource:"));
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
            ImGui.TextDisabled("· " + sound.Type);
            ImGui.SameLine();
            DevToolSourceMark source = DevToolSourcePresentation.FromSound(
                sound.SourceKind,
                sound.SourceId,
                sound.SourceName);
            DevToolSourcePresentation.DrawInline(source);
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

    private static bool DrawUnicodeInputText(string label, string id, ref string value, uint utf8Capacity)
    {
        DevToolWidgets.MutedText(label);
        ImGui.SetNextItemWidth(-1f);
        value ??= string.Empty;
        return ImGui.InputText("##" + id, ref value, utf8Capacity);
    }

    private static bool ContainsNonAsciiLetter(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) continue;
            return true;
        }
        return false;
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
