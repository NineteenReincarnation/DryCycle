using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class SoundEditorView
{
    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> VectorEdits = new(StringComparer.Ordinal);
    private static bool sceneTab;
    private static int createType;
    private static string search = string.Empty;

    internal static void DrawBrowser(EditorSoundPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音编辑器不可用。", "Sound editor unavailable."), true);
            return;
        }

        string libraryLabel = sceneTab ? DevToolUiSettings.T("资源库", "Library") : DevToolUiSettings.T("资源库*", "Library*");
        string sceneLabel = sceneTab ? DevToolUiSettings.T("场景*", "Scene*") : DevToolUiSettings.T("场景", "Scene");
        if (DevToolWidgets.ActionButton(libraryLabel, "SoundLibraryTab", sceneTab ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            sceneTab = false;
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(sceneLabel));
        if (DevToolWidgets.ActionButton(sceneLabel, "SoundSceneTab", sceneTab ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            sceneTab = true;
        ImGui.Separator();

        if (sceneTab) DrawScene(snapshot);
        else DrawLibrary(snapshot);
    }

    internal static void DrawInspector(EditorSoundPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("声音编辑器不可用。", "Sound editor unavailable."), true);
            return;
        }

        bool collapseAll = DevToolWidgets.PaneTitleWithAction(
            DevToolUiSettings.T("声音", "Sound"),
            DevToolUiSettings.T("折叠所有", "Collapse All"),
            "SoundInspectorCollapseAll");

        if (collapseAll)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(
                DevToolUiSettings.T("房间音频##SoundRoomAudio", "Room Audio##SoundRoomAudio"),
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRoomFloat(SoundEditorKeys.BackgroundDroneVolume, DevToolUiSettings.T("背景低鸣", "Bkg Drone"), snapshot.BackgroundDroneVolume, 0f, 1f);
            DrawRoomFloat(SoundEditorKeys.NoThreatDroneVolume, DevToolUiSettings.T("无威胁低鸣", "No Threat Drone"), snapshot.NoThreatDroneVolume, 0f, 1f);
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

        if (collapseAll)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader(
                DevToolUiSettings.T("声音内容##SoundSelectedDetails", "Sound Details##SoundSelectedDetails"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

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
        {
            ImGui.TextWrapped(DevToolUiSettings.T("继承声音 · 请修改来源模板，或添加本地覆盖。", "Inherited sound · edit its source template or add a local override."));
        }
        else
        {
            ImGui.Separator();
            if (DevToolWidgets.ActionButton(DevToolUiSettings.T("删除声音", "Delete Sound"), "DeleteSound", DevToolButtonTone.Danger))
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.Delete, selected.Index));
        }
    }

    private static void DrawLibrary(EditorSoundPresentationSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("创建声音", "CREATE SOUND"));
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
        ImGui.Separator();

        string[] samples = snapshot.Samples ?? Array.Empty<string>();
        int matches = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            string sample = samples[i];
            if (!Matches(sample, search)) continue;
            matches++;
            if (ImGui.Selectable(sample + "##CreateSound" + i, false))
            {
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(
                    SoundEditorCommandKind.Create,
                    text: sample,
                    secondaryIndex: createType));
            }
            if (ImGui.IsItemHovered())
                DevToolTooltip.Show(DevToolUiSettings.T("添加为 ", "Add as ") + TypeName(createType));
        }
        if (matches == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的环境音频。", "No matching ambient samples."), true);
    }

    private static void DrawScene(EditorSoundPresentationSnapshot snapshot)
    {
        EditorSoundSnapshot[] sounds = snapshot.Sounds ?? Array.Empty<EditorSoundSnapshot>();
        ImGui.TextDisabled(DevToolUiSettings.T($"{sounds.Length} 个环境声音", $"{sounds.Length} ambient sounds"));
        ImGui.Separator();

        for (int i = 0; i < sounds.Length; i++)
        {
            EditorSoundSnapshot sound = sounds[i];
            string prefix = sound.Type switch
            {
                "Omnidirectional" => "O",
                "Directional" => "D",
                "Spot" => "S",
                _ => "?"
            };
            string label = "[" + prefix + "] " + sound.Sample;
            if (sound.Inherited) label += DevToolUiSettings.T("  [继承]", "  [Inherited]");
            else if (sound.OverWrite) label += DevToolUiSettings.T("  [覆盖]", "  [Override]");
            if (ImGui.Selectable(label + "##SoundScene" + sound.Index, sound.Selected))
                SoundEditorCommandQueue.Enqueue(new SoundEditorCommand(SoundEditorCommandKind.Select, sound.Index));
        }
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
        return index >= 0 && index < sounds.Length ? sounds[index] : null;
    }

    private static string TypeName(int type) => type switch
    {
        0 => DevToolUiSettings.T("全向声音", "Omnidirectional"),
        1 => DevToolUiSettings.T("定向声音", "Directional"),
        2 => DevToolUiSettings.T("点声源", "Spot"),
        _ => DevToolUiSettings.T("声音", "Sound")
    };

    private static bool Matches(string value, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);

    private static TValue Get<TValue>(Dictionary<string, TValue> dictionary, string key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
