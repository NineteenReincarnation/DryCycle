using System;
using System.Collections.Generic;
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

    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> VectorEdits = new(StringComparer.Ordinal);
    private static BrowserTab browserTab;
    private static string sceneSearch = string.Empty;
    private static string selectionGroupName = string.Empty;
    private static string selectionGroupId = string.Empty;
    private static bool selectionGroupIdManual;
    private const float BrowserBodyFontScale = 1.22f;

    internal static void DrawBrowser(EditorSoundPresentationSnapshot snapshot)
    {
        SoundLibraryGroupsView.DrawProblemsOnce();
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音编辑器不可用。", "Sound editor unavailable."), true);
            return;
        }

        SoundWorkspaceState.SynchronizeScene(snapshot);

        DevToolWidgets.PaneTitle(DevToolUiSettings.T("声音", "SOUNDS"), BrowserBodyFontScale);
        DrawTabButton(BrowserTab.Library, DevToolUiSettings.T("资源库", "Library"), "SoundLibraryTab");
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("音效组", "Groups")));
        DrawTabButton(BrowserTab.Groups, DevToolUiSettings.T("音效组", "Groups"), "SoundGroupsTab");
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(DevToolUiSettings.T("场景", "Scene")));
        DrawTabButton(BrowserTab.Scene, DevToolUiSettings.T("场景", "Scene"), "SoundSceneTab");
        ImGui.Separator();

        SoundLibraryGroupsView.DrawWorkingGroupBar();

        switch (browserTab)
        {
            case BrowserTab.Groups:
                SoundLibraryGroupsView.DrawGroups();
                break;
            case BrowserTab.Scene:
                DrawScene(snapshot);
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
                DevToolUiSettings.T($"已选择 {selectedIndices.Length} 个声音", $"{selectedIndices.Length} SOUNDS SELECTED"));
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
        string state = selected.Type;
        if (selected.Inherited) state += DevToolUiSettings.T(" · 继承", " · Inherited");
        else if (selected.OverWrite) state += DevToolUiSettings.T(" · 覆盖模板", " · Overrides template");
        ImGui.TextDisabled(state);
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
                ImGui.TextWrapped(DevToolUiSettings.T("继承声音 · 请修改来源模板，或添加本地覆盖。", "Inherited sound · edit its source template or add a local override."));
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

    private static void DrawTabButton(BrowserTab tab, string label, string id)
    {
        bool active = browserTab == tab;
        if (DevToolWidgets.ActionButton(label, id, active ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            browserTab = tab;
    }

    private static void DrawScene(EditorSoundPresentationSnapshot snapshot)
    {
        EditorSoundSnapshot[] sounds = snapshot.Sounds ?? Array.Empty<EditorSoundSnapshot>();
        SoundWorkspaceState.SynchronizeScene(snapshot);

        ImGui.TextDisabled(DevToolUiSettings.T($"{sounds.Length} 个环境声音", $"{sounds.Length} ambient sounds"));
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "SoundSceneSearch", ref sceneSearch, 128);

        bool ctrl = global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftControl) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightControl) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftCommand) ||
                    global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightCommand);
        bool shift = global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.LeftShift) ||
                     global::UnityEngine.Input.GetKey(global::UnityEngine.KeyCode.RightShift);

        if (!ImGui.GetIO().WantTextInput && ctrl && global::UnityEngine.Input.GetKeyDown(global::UnityEngine.KeyCode.A))
            SoundWorkspaceState.SelectAll(sounds.Length);

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
            DevToolUiSettings.T("单击单选 · Ctrl 追加/取消 · Shift 范围选择 · Ctrl+A 全选", "Click selects · Ctrl toggles · Shift selects a range · Ctrl+A selects all"),
            true);
        ImGui.Separator();

        int visible = 0;
        for (int i = 0; i < sounds.Length; i++)
        {
            EditorSoundSnapshot sound = sounds[i];
            if (!MatchesScene(sound, sceneSearch)) continue;
            visible++;

            bool selected = SoundWorkspaceState.IsSceneSelected(sound.Index);
            string prefix = sound.Type switch
            {
                "Omnidirectional" => "O",
                "Directional" => "D",
                "Spot" => "S",
                _ => "?"
            };
            string label = (selected ? "☑ " : "☐ ") + "[" + prefix + "] " + sound.Sample;
            if (sound.Inherited) label += DevToolUiSettings.T("  [继承]", "  [Inherited]");
            else if (sound.OverWrite) label += DevToolUiSettings.T("  [覆盖]", "  [Override]");

            if (ImGui.Selectable(label + "##SoundScene" + sound.Index, selected))
            {
                SoundWorkspaceState.HandleSceneClick(sound.Index, ctrl, shift);
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.Select, sound.Index));
            }
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景声音。", "No matching scene sounds."), true);

        int[] selectedIndices = SoundWorkspaceState.SelectedIndices();
        if (selectedIndices.Length == 0) return;

        ImGui.Separator();
        ImGui.TextColored(
            new Num.Vector4(0.62f, 0.84f, 1f, 1f),
            DevToolUiSettings.T($"已选择 {selectedIndices.Length} 个声音", $"{selectedIndices.Length} selected"));

        if (SoundWorkspaceState.TryGetActiveLocalGroup(out SoundGroupSnapshot group))
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("加入工作组 · ", "Add to Working Group · ") + group.Name,
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
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return (sound?.Sample?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (sound?.Type?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               (sound?.ResourceSourceName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static void DrawRoomFloat(string key, string label, float current, float min, float max)
    {
        string stateKey = "room:" + key;
        float value = Get(FloatEdits, stateKey, current);
        bool changed = ImGui.SliderFloat(label + "##SoundRoom" + key, ref value, min, max, "%.3f");
        FloatEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetRoomValue,
                key: key,
                value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            FloatEdits[stateKey] = current;
        }
    }

    private static void DrawSoundFloat(EditorSoundSnapshot sound, string key, string label, float current, float min, float max)
    {
        string stateKey = "sound:" + sound.Index + ":" + key;
        float value = Get(FloatEdits, stateKey, current);
        bool changed = ImGui.SliderFloat(label + "##" + stateKey, ref value, min, max, "%.3f");
        FloatEdits[stateKey] = value;
        if (!sound.Inherited && ImGui.IsItemDeactivatedAfterEdit())
        {
            SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                SoundEditorCommandKind.SetSoundValue,
                index: sound.Index,
                key: key,
                value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            FloatEdits[stateKey] = current;
        }
    }

    private static void DrawSoundVector(EditorSoundSnapshot sound, string key, string label, float x, float y)
    {
        string stateKey = "sound:" + sound.Index + ":" + key;
        Num.Vector2 value = Get(VectorEdits, stateKey, new Num.Vector2(x, y));
        bool changed = ImGui.InputFloat2(label + "##" + stateKey, ref value, "%.2f");
        VectorEdits[stateKey] = value;
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
            VectorEdits[stateKey] = new Num.Vector2(x, y);
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

    private static TValue Get<TValue>(Dictionary<string, TValue> dictionary, string key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
