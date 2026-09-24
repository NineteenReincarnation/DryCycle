using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class SoundEditorView
{
    private enum BrowserTab
    {
        Library,
        Groups,
        Scene
    }

    private readonly struct SoundEditKey : IEquatable<SoundEditKey>
    {
        internal SoundEditKey(int index, string key)
        {
            Index = index;
            Key = key ?? string.Empty;
        }

        private int Index { get; }
        private string Key { get; }

        public bool Equals(SoundEditKey other) =>
            Index == other.Index && string.Equals(Key, other.Key, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is SoundEditKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return Index * 397 ^ StringComparer.Ordinal.GetHashCode(Key);
            }
        }
    }

    private sealed class SoundSceneRow
    {
        internal EditorSoundSnapshot Sound;
        internal string SelectedLabel = string.Empty;
        internal string NormalLabel = string.Empty;
    }

    private static readonly Dictionary<SoundEditKey, Num.Vector2> SoundVectorEdits = new();
    private static readonly List<SoundSceneRow> SceneRows = new();

    private static BrowserTab browserTab;
    private static string sceneSearch = string.Empty;
    private static string selectionGroupName = string.Empty;
    private static string selectionGroupId = string.Empty;
    private static bool selectionGroupIdManual;
    private const float BrowserBodyFontScale = 1.22f;

    private static EditorSoundSnapshot[] projectedSceneSounds;
    private static string projectedSceneSearch = string.Empty;
    private static bool projectedSceneChinese;
    private static string observedSceneSearch;
    private static string normalizedSceneSearch = string.Empty;

    private static int sceneCountValue = -1;
    private static bool sceneCountChinese;
    private static string sceneCountText = string.Empty;

    private static int inspectorSelectionCount = -1;
    private static bool inspectorSelectionChinese;
    private static string inspectorSelectionText = string.Empty;

    private static int sceneSelectionCount = -1;
    private static bool sceneSelectionChinese;
    private static string sceneSelectionText = string.Empty;

    private static EditorSoundSnapshot stateSound;
    private static bool stateChinese;
    private static string stateText = string.Empty;

    private static string activeGroupButtonId = string.Empty;
    private static string activeGroupButtonName = string.Empty;
    private static bool activeGroupButtonChinese;
    private static string activeGroupButtonText = string.Empty;

    internal static void DrawBrowser(EditorSoundPresentationSnapshot snapshot)
    {
        SoundLibraryGroupsView.DrawProblemsOnce();
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音编辑器不可用。", "Sound editor unavailable."), true);
            return;
        }

        SoundWorkspaceState.SynchronizeScene(snapshot);

        bool sceneInBrowser = !DevToolUiSettings.SceneInCenter;
        if (!sceneInBrowser && browserTab == BrowserTab.Scene)
            browserTab = BrowserTab.Library;

        DevToolWidgets.PaneTitle(DevToolUiSettings.T("声音", "SOUNDS"), BrowserBodyFontScale);
        DrawTabButton(BrowserTab.Library, DevToolUiSettings.T("资源库", "Library"), "SoundLibraryTab");
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("音效组", "Groups")));
        DrawTabButton(BrowserTab.Groups, DevToolUiSettings.T("音效组", "Groups"), "SoundGroupsTab");
        if (sceneInBrowser)
        {
            DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("场景", "Scene")));
            DrawTabButton(BrowserTab.Scene, DevToolUiSettings.T("场景", "Scene"), "SoundSceneTab");
        }
        ImGui.Separator();

        SoundLibraryGroupsView.DrawWorkingGroupBar();

        switch (browserTab)
        {
            case BrowserTab.Groups:
                SoundLibraryGroupsView.DrawGroups();
                break;
            case BrowserTab.Scene when sceneInBrowser:
                DrawSceneWorkspace(snapshot);
                break;
            default:
                SoundLibraryGroupsView.DrawLibrary(snapshot);
                break;
        }
    }

    internal static void DrawInspector(EditorSoundPresentationSnapshot snapshot)
    {
        SoundLibraryGroupsView.DrawProblemsOnce();
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音编辑器不可用。", "Sound editor unavailable."), true);
            return;
        }

        SoundWorkspaceState.SynchronizeScene(snapshot);

        bool collapseAll = DevToolWidgets.PaneTitleWithAction(
            DevToolUiSettings.T("声音", "Sound"),
            DevToolUiSettings.T("折叠所有", "Collapse All"),
            "SoundInspectorCollapseAll");

        if (collapseAll) ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(
                DevToolUiSettings.T("房间音频##SoundRoomAudio", "Room Audio##SoundRoomAudio"),
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRoomFloat(SoundEditorKeys.BackgroundDroneVolume, DevToolUiSettings.T("背景低鸣", "Bkg Drone"), snapshot.BackgroundDroneVolume, 0f, 1f);
            DrawRoomFloat(SoundEditorKeys.NoThreatDroneVolume, DevToolUiSettings.T("无威胁低鸣", "No Threat Drone"), snapshot.NoThreatDroneVolume, 0f, 1f);
        }

        int[] selectedIndices = SoundWorkspaceState.SelectedIndices();
        if (selectedIndices.Length > 1)
        {
            ImGui.Separator();
            ImGui.TextColored(
                new Num.Vector4(0.62f, 0.84f, 1f, 1f),
                GetInspectorSelectionText(selectedIndices.Length));
            SoundLibraryGroupsView.DrawAddSelectionToGroup(selectedIndices);
        }

        EditorSoundSnapshot selected = FindSelected(snapshot);
        if (selected == null)
        {
            ImGui.Separator();
            ImGui.TextWrapped(DevToolUiSettings.T("从场景列表或世界 Gizmo 中选择一个声音进行编辑。", "Select a sound from Scene or its world gizmo to edit it."));
            return;
        }

        ImGui.Separator();
        ImGui.TextWrapped(selected.Sample);
        ImGui.TextDisabled(GetSoundState(selected));
        SoundLibraryGroupsView.DrawSelectedResourceStatus(snapshot, selected);

        if (collapseAll) ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(
                DevToolUiSettings.T("声音内容##SoundSelectedDetails", "Sound Details##SoundSelectedDetails"),
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (selected.Inherited) ImGui.BeginDisabled();
            DrawSoundFloat(selected, SoundEditorKeys.Volume, DevToolUiSettings.T("音量", "Volume"), selected.Volume, 0f, 1f);
            DrawSoundFloat(selected, SoundEditorKeys.Pitch, DevToolUiSettings.T("音高", "Pitch"), selected.Pitch, 0.1f, 1.9f);

            if (string.Equals(selected.Type, "Directional", StringComparison.Ordinal) ||
                string.Equals(selected.Type, "Spot", StringComparison.Ordinal))
                DrawSoundFloat(selected, SoundEditorKeys.Doppler, DevToolUiSettings.T("多普勒", "Doppler"), selected.Doppler, 0f, 1f);

            if (string.Equals(selected.Type, "Spot", StringComparison.Ordinal))
            {
                ImGui.Separator();
                ImGui.TextDisabled(DevToolUiSettings.T("空间", "SPATIAL"));
                DrawSoundVector(selected, SoundEditorKeys.Position, DevToolUiSettings.T("位置", "Position"), selected.X, selected.Y);
                DrawSoundFloat(selected, SoundEditorKeys.Radius, DevToolUiSettings.T("半径", "Radius"), selected.Radius, 0f, 4000f);
                DrawSoundFloat(selected, SoundEditorKeys.Taper, DevToolUiSettings.T("衰减", "Taper"), selected.Taper, 0f, 1f);
            }
            else if (string.Equals(selected.Type, "Directional", StringComparison.Ordinal))
            {
                ImGui.Separator();
                ImGui.TextDisabled(DevToolUiSettings.T("方向", "DIRECTION"));
                DrawSoundVector(selected, SoundEditorKeys.Direction, DevToolUiSettings.T("方向", "Direction"), selected.DirectionX, selected.DirectionY);
            }

            if (selected.Inherited) ImGui.EndDisabled();
            if (selected.Inherited)
                ImGui.TextWrapped(DevToolUiSettings.T("继承声音 | 请修改来源模板，或添加本地覆盖。", "Inherited sound | edit its source template or add a local override."));
        }

        if (selectedIndices.Length <= 1)
            SoundLibraryGroupsView.DrawAddToGroup(selected);

        if (!selected.Inherited)
        {
            ImGui.Separator();
            if (DevToolWidgets.ActionButton(DevToolUiSettings.T("删除声音", "Delete Sound"), "DeleteSound", DevToolButtonTone.Danger))
            {
                SoundWorkspaceState.ClearSelection();
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.Delete, selected.Index));
            }
        }
    }

    internal static void ResetRetainedState()
    {
        SoundVectorEdits.Clear();
        SceneRows.Clear();
        projectedSceneSounds = null;
        projectedSceneSearch = string.Empty;
        projectedSceneChinese = false;
        observedSceneSearch = null;
        normalizedSceneSearch = string.Empty;
        stateSound = null;
        stateChinese = false;
        stateText = string.Empty;
        sceneCountValue = -1;
        sceneCountText = string.Empty;
        inspectorSelectionCount = -1;
        inspectorSelectionText = string.Empty;
        sceneSelectionCount = -1;
        sceneSelectionText = string.Empty;
        activeGroupButtonId = string.Empty;
        activeGroupButtonName = string.Empty;
        activeGroupButtonText = string.Empty;
        selectionGroupName = string.Empty;
        selectionGroupId = string.Empty;
        selectionGroupIdManual = false;
    }

    private static void DrawTabButton(BrowserTab tab, string label, string id)
    {
        bool active = browserTab == tab;
        if (DevToolWidgets.ActionButton(label, id, active ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            browserTab = tab;
    }

    internal static void DrawSceneWorkspace(EditorSoundPresentationSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音场景不可用。", "Sound scene unavailable."), true);
            return;
        }

        EditorSoundSnapshot[] sounds = snapshot.Sounds ?? Array.Empty<EditorSoundSnapshot>();
        SoundWorkspaceState.SynchronizeScene(snapshot);

        ImGui.TextDisabled(GetSceneCountText(sounds.Length));
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "SoundSceneSearch", ref sceneSearch, 128);

        bool ctrl = global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftControl) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightControl) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftCommand) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightCommand);
        bool shift = global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftShift) ||
                     global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightShift);

        if (!ImGui.GetIO().WantTextInput && ctrl && global::UnityEngine.Input.GetKeyDown(global::UnityEngine.KeyCode.A))
        {
            SoundWorkspaceState.SelectAll(sounds.Length);
            EditorShortcutFeedback.PublishCustom(
                sounds.Length > 0 ? "已全选声音" : "当前没有声音",
                sounds.Length > 0 ? "All sounds selected" : "No sounds to select",
                "Ctrl+A",
                sounds.Length > 0,
                sounds.Length > 0 ? EditorShortcutFeedbackVisual.Select : EditorShortcutFeedbackVisual.Warning);
        }

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("全选", "Select All"),
                "SoundSceneSelectAll",
                DevToolButtonTone.Subtle))
        {
            SoundWorkspaceState.SelectAll(sounds.Length);
        }
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("清除选择", "Clear")));
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("清除选择", "Clear"),
                "SoundSceneClearSelection",
                DevToolButtonTone.Subtle))
        {
            SoundWorkspaceState.ClearSelection();
        }

        DevToolWidgets.MutedText(
            DevToolUiSettings.T("单击单选 | Ctrl 追加/取消 | Shift 范围选择 | Ctrl+A 全选", "Click selects | Ctrl toggles | Shift selects a range | Ctrl+A selects all"),
            true);
        ImGui.Separator();

        EnsureSceneProjection(sounds);
        {
            using DevToolListClipper clipper = new(SceneRows.Count);
            while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
            {
                for (int i = firstVisible; i < lastVisibleExclusive; i++)
                {
                    SoundSceneRow row = SceneRows[i];
                    EditorSoundSnapshot sound = row.Sound;
                    bool selected = SoundWorkspaceState.IsSceneSelected(sound.Index);

                    ImGui.PushID(sound.Index);
                    bool clicked = ImGui.Selectable(selected ? row.SelectedLabel : row.NormalLabel, selected);
                    ImGui.PopID();
                    if (clicked)
                    {
                        SoundWorkspaceState.HandleSceneClick(sound.Index, ctrl, shift);
                        SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.Select, sound.Index));
                    }
                }
            }
        }

        if (SceneRows.Count == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景声音。", "No matching scene sounds."), true);

        int[] selectedIndices = SoundWorkspaceState.SelectedIndices();
        if (selectedIndices.Length == 0) return;

        ImGui.Separator();
        ImGui.TextColored(
            new Num.Vector4(0.62f, 0.84f, 1f, 1f),
            GetSceneSelectionText(selectedIndices.Length));

        if (SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group))
        {
            if (DevToolWidgets.ActionButton(
                    GetActiveGroupButtonText(group),
                    "SoundSceneAddSelectionToGroup",
                    DevToolButtonTone.Primary,
                    true))
            {
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.AddSoundsToGroup,
                    key: group.Id,
                    indices: selectedIndices));
                ActionToastOverlay.Notify(
                    $"已向 {group.Name} 加入 {selectedIndices.Length} 个声音",
                    $"Added {selectedIndices.Length} sounds to {group.Name}");
            }
        }
        else
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("先创建工作音效组即可批量加入。", "Create a working group to add the selection in one click."), true);
        }

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("从选择新建音效组", "New Group From Selection"),
                "SoundSceneCreateGroupFromSelection",
                DevToolButtonTone.Subtle,
                true))
        {
            selectionGroupName = string.Empty;
            selectionGroupId = SoundWorkspaceState.SuggestUniqueGroupId(string.Empty);
            selectionGroupIdManual = false;
            ImGui.OpenPopup("##SoundCreateGroupFromSelectionPopup");
        }

        DrawCreateGroupFromSelectionPopup(selectedIndices);
    }

    private static void EnsureSceneProjection(EditorSoundSnapshot[] sounds)
    {
        string normalizedSearch = SceneSearchQuery();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedSceneSounds, sounds) &&
            string.Equals(projectedSceneSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedSceneChinese == chinese)
            return;

        SceneRows.Clear();
        for (int i = 0; i < sounds.Length; i++)
        {
            EditorSoundSnapshot sound = sounds[i];
            if (!MatchesScene(sound, normalizedSearch)) continue;

            string prefix = sound.Type switch
            {
                "Omnidirectional" => "O",
                "Directional" => "D",
                "Spot" => "S",
                _ => "?"
            };
            string suffix = sound.Inherited
                ? DevToolUiSettings.T("  [继承]", "  [Inherited]")
                : sound.OverWrite
                    ? DevToolUiSettings.T("  [覆盖]", "  [Override]")
                    : string.Empty;
            string body = "[" + prefix + "] " + sound.Sample + suffix;
            SceneRows.Add(new SoundSceneRow
            {
                Sound = sound,
                SelectedLabel = DevToolGlyphs.CheckedBox + " " + body,
                NormalLabel = DevToolGlyphs.EmptyBox + " " + body
            });
        }

        projectedSceneSounds = sounds;
        projectedSceneSearch = normalizedSearch;
        projectedSceneChinese = chinese;
    }

    private static string SceneSearchQuery()
    {
        if (string.Equals(observedSceneSearch, sceneSearch, StringComparison.Ordinal))
            return normalizedSceneSearch;
        observedSceneSearch = sceneSearch;
        normalizedSceneSearch = sceneSearch?.Trim() ?? string.Empty;
        return normalizedSceneSearch;
    }

    private static string GetSceneCountText(int count)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (sceneCountValue == count && sceneCountChinese == chinese && sceneCountText.Length > 0)
            return sceneCountText;
        sceneCountValue = count;
        sceneCountChinese = chinese;
        sceneCountText = chinese ? $"{count} 个环境声音" : $"{count} ambient sounds";
        return sceneCountText;
    }

    private static string GetInspectorSelectionText(int count)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (inspectorSelectionCount == count && inspectorSelectionChinese == chinese && inspectorSelectionText.Length > 0)
            return inspectorSelectionText;
        inspectorSelectionCount = count;
        inspectorSelectionChinese = chinese;
        inspectorSelectionText = chinese ? $"已选择 {count} 个声音" : $"{count} SOUNDS SELECTED";
        return inspectorSelectionText;
    }

    private static string GetSceneSelectionText(int count)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (sceneSelectionCount == count && sceneSelectionChinese == chinese && sceneSelectionText.Length > 0)
            return sceneSelectionText;
        sceneSelectionCount = count;
        sceneSelectionChinese = chinese;
        sceneSelectionText = chinese ? $"已选择 {count} 个声音" : $"{count} selected";
        return sceneSelectionText;
    }

    private static string GetSoundState(EditorSoundSnapshot sound)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(stateSound, sound) && stateChinese == chinese)
            return stateText;

        stateText = sound.Type;
        if (sound.Inherited) stateText += chinese ? " | 继承" : " | Inherited";
        else if (sound.OverWrite) stateText += chinese ? " | 覆盖模板" : " | Overrides template";
        stateSound = sound;
        stateChinese = chinese;
        return stateText;
    }

    private static string GetActiveGroupButtonText(SoundGroupSnapshot group)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        string id = group?.Id ?? string.Empty;
        string name = group?.Name ?? string.Empty;
        if (string.Equals(activeGroupButtonId, id, StringComparison.Ordinal) &&
            string.Equals(activeGroupButtonName, name, StringComparison.Ordinal) &&
            activeGroupButtonChinese == chinese && activeGroupButtonText.Length > 0)
            return activeGroupButtonText;

        activeGroupButtonId = id;
        activeGroupButtonName = name;
        activeGroupButtonChinese = chinese;
        activeGroupButtonText = (chinese ? "加入工作组 | " : "Add to Working Group | ") + name;
        return activeGroupButtonText;
    }

    private static void DrawCreateGroupFromSelectionPopup(int[] selectedIndices)
    {
        if (!ImGui.BeginPopup("##SoundCreateGroupFromSelectionPopup")) return;

        int count = selectedIndices?.Length ?? 0;
        ImGui.TextUnformatted(DevToolUiSettings.T("从场景选择创建音效组", "Create Group From Scene Selection"));
        ImGui.TextDisabled(DevToolUiSettings.T($"将保存 {count} 个声音的当前参数快照", $"Capture current parameters from {count} sounds"));
        ImGui.Separator();

        DevToolWidgets.MutedText(DevToolUiSettings.T("显示名称", "Display name"));
        ImGui.SetNextItemWidth(360f);
        bool nameChanged = ImGui.InputText("##SceneSelectionGroupName", ref selectionGroupName, 512);
        if (nameChanged && !selectionGroupIdManual)
            selectionGroupId = SoundWorkspaceState.SuggestUniqueGroupId(selectionGroupName);

        DevToolWidgets.MutedText(DevToolUiSettings.T("编组 ID", "Group ID"));
        ImGui.SetNextItemWidth(360f);
        ImGui.InputText("##SceneSelectionGroupId", ref selectionGroupId, 128);
        if (ImGui.IsItemEdited()) selectionGroupIdManual = true;

        bool validId = SoundWorkspaceState.IsValidGroupId(selectionGroupId);
        bool duplicate = SoundWorkspaceState.GroupIdExists(selectionGroupId);
        if (!validId && !string.IsNullOrWhiteSpace(selectionGroupId))
            ImGui.TextColored(new Num.Vector4(1f, 0.64f, 0.30f, 1f), DevToolUiSettings.T("ID 仅使用 A-Z / a-z / 0-9 / _ / -。", "Use only A-Z / a-z / 0-9 / _ / - in Group IDs."));
        else if (duplicate)
            ImGui.TextColored(new Num.Vector4(1f, 0.64f, 0.30f, 1f), DevToolUiSettings.T("这个 Group ID 已存在。", "This Group ID already exists."));

        bool canCreate = count > 0 && !string.IsNullOrWhiteSpace(selectionGroupName) && validId && !duplicate;
        if (!canCreate) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("创建并设为工作组", "Create & Use"),
                "SoundSceneCreateGroupConfirm",
                DevToolButtonTone.Primary))
        {
            string id = selectionGroupId.Trim();
            string name = selectionGroupName.Trim();
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.CreateGroupFromSounds,
                key: id,
                text: name,
                indices: selectedIndices));
            SoundWorkspaceState.SetActiveGroup(id);
            ActionToastOverlay.Notify(
                $"已创建 {name}，包含 {count} 个声音",
                $"Created {name} with {count} sounds");
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.EndPopup();
    }

    private static bool MatchesScene(EditorSoundSnapshot sound, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return (sound?.Sample?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (sound?.Type?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (sound?.ResourceSourceName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static void DrawRoomFloat(string key, string label, float current, float min, float max)
    {
        ImGui.PushID("SoundRoom");
        ImGui.PushID(key);
        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(
            DevToolNumericScope.SoundRoom, key, label, current, min, max);
        ImGui.PopID();
        ImGui.PopID();
        if (edit.Committed)
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetRoomValue,
                key: key,
                value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));
        }
    }

    private static void DrawSoundFloat(EditorSoundSnapshot sound, string key, string label, float current, float min, float max)
    {
        if (sound.Inherited)
            DevToolNumericWidgets.Discard(DevToolNumericScope.SoundItem, key, sound.Index);

        ImGui.PushID(sound.Index);
        ImGui.PushID(key);
        DevToolNumericEditResult<float> edit = DevToolNumericWidgets.SliderFloat(
            DevToolNumericScope.SoundItem, key, label, current, min, max, instance: sound.Index);
        ImGui.PopID();
        ImGui.PopID();
        if (!sound.Inherited && edit.Committed)
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetSoundValue,
                index: sound.Index,
                key: key,
                value: new EditorPropertyValue(EditorPropertyKind.Float, x: edit.Value)));
        }
    }

    private static void DrawSoundVector(EditorSoundSnapshot sound, string key, string label, float x, float y)
    {
        SoundEditKey stateKey = new(sound.Index, key);
        Num.Vector2 value = Get(SoundVectorEdits, stateKey, new Num.Vector2(x, y));
        ImGui.PushID(sound.Index);
        ImGui.PushID(key);
        bool changed = ImGui.InputFloat2(label, ref value, "%.2f");
        ImGui.PopID();
        ImGui.PopID();
        SoundVectorEdits[stateKey] = value;
        if (!sound.Inherited && ImGui.IsItemDeactivatedAfterEdit())
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetSoundValue,
                index: sound.Index,
                key: key,
                value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: value.X, y: value.Y)));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            SoundVectorEdits[stateKey] = new Num.Vector2(x, y);
        }
    }

    private static EditorSoundSnapshot FindSelected(EditorSoundPresentationSnapshot snapshot)
    {
        EditorSoundSnapshot[] sounds = snapshot.Sounds ?? Array.Empty<EditorSoundSnapshot>();
        int index = snapshot.SelectedIndex;
        if (index >= 0 && index < sounds.Length) return sounds[index];

        int[] selection = SoundWorkspaceState.SelectedIndices();
        if (selection.Length == 0) return null;
        index = selection[0];
        return index >= 0 && index < sounds.Length ? sounds[index] : null;
    }

    private static TValue Get<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
