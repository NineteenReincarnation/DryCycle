using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class TriggerEditorView
{
    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Num.Vector2> VectorEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> StringEdits = new(StringComparer.Ordinal);
    private static bool sceneTab;
    private static string search = string.Empty;

    internal static void DrawBrowser(EditorTriggerPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("触发器编辑器不可用。", "Trigger editor unavailable."), true);
            return;
        }

        string libraryLabel = sceneTab ? DevToolUiSettings.T("资源库", "Library") : DevToolUiSettings.T("资源库*", "Library*");
        string sceneLabel = sceneTab ? DevToolUiSettings.T("场景*", "Scene*") : DevToolUiSettings.T("场景", "Scene");
        if (DevToolWidgets.ActionButton(libraryLabel, "TriggerLibraryTab", sceneTab ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
            sceneTab = false;
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(sceneLabel));
        if (DevToolWidgets.ActionButton(sceneLabel, "TriggerSceneTab", sceneTab ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            sceneTab = true;
        ImGui.Separator();

        if (sceneTab) DrawScene(snapshot);
        else DrawLibrary(snapshot);
    }

    internal static void DrawInspector(EditorTriggerPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("触发器编辑器不可用。", "Trigger editor unavailable."), true);
            return;
        }

        bool collapseAll = DevToolWidgets.PaneTitleWithAction(
            DevToolUiSettings.T("触发器", "Trigger"),
            DevToolUiSettings.T("折叠所有", "Collapse All"),
            "TriggerInspectorCollapseAll");

        EditorTriggerSnapshot selected = FindSelected(snapshot);
        if (selected == null)
        {
            ImGui.TextWrapped(DevToolUiSettings.T("从场景列表或世界 Gizmo 中选择一个触发器。", "Select a trigger from Scene or its world gizmo."));
            return;
        }

        ImGui.Text(selected.Type);
        ImGui.TextDisabled(selected.Event?.HasEvent == true
            ? DevToolUiSettings.T("事件 · ", "Event · ") + selected.Event.Type
            : DevToolUiSettings.T("未分配事件", "No event assigned"));
        ImGui.Separator();

        if (collapseAll)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        if (!ImGui.CollapsingHeader(
                DevToolUiSettings.T("触发器内容##TriggerInspectorDetails", "Trigger Details##TriggerInspectorDetails"),
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled(DevToolUiSettings.T("触发条件", "ACTIVATION"));
        DrawInt(selected, TriggerEditorKeys.ActiveFromCycle, DevToolUiSettings.T("起始周期", "From cycle"), selected.ActiveFromCycle, 0, 80);
        DrawUpperCycle(selected);
        DrawFloat(selected, TriggerEditorKeys.DelaySeconds, DevToolUiSettings.T("延迟（秒）", "Delay (seconds)"), selected.DelaySeconds, 0f, 120f);
        DrawFloat(selected, TriggerEditorKeys.FireChance, DevToolUiSettings.T("触发概率", "Fire chance"), selected.FireChance, 0f, 1f);

        bool multiUse = selected.MultiUse;
        if (ImGui.Checkbox(DevToolUiSettings.T("允许多次触发##TriggerMultiUse", "Can fire multiple times##TriggerMultiUse"), ref multiUse))
            SendValue(selected.Index, TriggerEditorKeys.MultiUse,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: multiUse));

        DrawKarma(selected);
        DrawEntrance(snapshot, selected);

        if (selected.IsSpot)
        {
            ImGui.Separator();
            ImGui.TextDisabled(DevToolUiSettings.T("区域", "SPOT AREA"));
            DrawVector(selected, TriggerEditorKeys.Position, DevToolUiSettings.T("位置", "Position"), selected.X, selected.Y);
            DrawFloat(selected, TriggerEditorKeys.Radius, DevToolUiSettings.T("半径", "Radius"), selected.Radius, 0f, 4000f);
        }

        if (!string.IsNullOrEmpty(selected.CreatureType))
        {
            ImGui.Separator();
            ImGui.TextDisabled(DevToolUiSettings.T("生物", "CREATURE"));
            DrawString(selected, TriggerEditorKeys.CreatureType, DevToolUiSettings.T("生物类型", "Creature type"), selected.CreatureType);
        }

        ImGui.Separator();
        DrawSlugcats(snapshot, selected);

        ImGui.Separator();
        DrawEventEditor(snapshot, selected);

        ImGui.Separator();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("删除触发器", "Delete Trigger"), "DeleteTrigger", DevToolButtonTone.Danger))
            TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(TriggerEditorCommandKind.Delete, selected.Index));
    }

    private static void DrawLibrary(EditorTriggerPresentationSnapshot snapshot)
    {
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "TriggerLibrarySearch", ref search, 128);
        ImGui.Separator();

        string[] types = snapshot.TriggerTypes ?? Array.Empty<string>();
        int matches = 0;
        for (int i = 0; i < types.Length; i++)
        {
            string type = types[i];
            if (!Matches(type, search)) continue;
            matches++;
            if (ImGui.Selectable(type + "##CreateTrigger" + i, false))
                TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(
                    TriggerEditorCommandKind.Create,
                    text: type));
        }

        if (matches == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的触发器类型。", "No matching trigger types."), true);
    }

    private static void DrawScene(EditorTriggerPresentationSnapshot snapshot)
    {
        EditorTriggerSnapshot[] triggers = snapshot.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        ImGui.TextDisabled(DevToolUiSettings.T($"{triggers.Length} 个触发器", $"{triggers.Length} triggers"));
        ImGui.Separator();

        for (int i = 0; i < triggers.Length; i++)
        {
            EditorTriggerSnapshot trigger = triggers[i];
            string label = trigger.Type;
            if (trigger.Event?.HasEvent == true) label += "  →  " + trigger.Event.Type;
            if (ImGui.Selectable(label + "##TriggerScene" + trigger.Index, trigger.Selected))
                TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(TriggerEditorCommandKind.Select, trigger.Index));
        }
    }

    private static void DrawEventEditor(EditorTriggerPresentationSnapshot snapshot, EditorTriggerSnapshot trigger)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("事件", "EVENT"));
        EditorTriggeredEventSnapshot value = trigger.Event ?? new EditorTriggeredEventSnapshot();

        string preview = value.HasEvent ? value.Type : DevToolUiSettings.T("无", "None");
        if (ImGui.BeginCombo(DevToolUiSettings.T("类型##TriggerEventType", "Type##TriggerEventType"), preview))
        {
            bool noneSelected = !value.HasEvent;
            if (ImGui.Selectable(DevToolUiSettings.T("无##TriggerEventNone", "None##TriggerEventNone"), noneSelected))
                TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(TriggerEditorCommandKind.ClearEvent, trigger.Index));
            if (noneSelected) ImGui.SetItemDefaultFocus();

            string[] eventTypes = snapshot.EventTypes ?? Array.Empty<string>();
            for (int i = 0; i < eventTypes.Length; i++)
            {
                string type = eventTypes[i];
                bool selected = value.HasEvent && string.Equals(value.Type, type, StringComparison.Ordinal);
                if (ImGui.Selectable(type + "##TriggerEventType" + i, selected))
                    TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(
                        TriggerEditorCommandKind.SetEventType,
                        index: trigger.Index,
                        text: type));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (!value.HasEvent)
        {
            ImGui.TextWrapped(DevToolUiSettings.T("选择一种事件类型后再配置此触发器。", "Choose an event type to configure this trigger."));
            return;
        }

        if (string.Equals(value.Type, "MusicEvent", StringComparison.Ordinal))
        {
            DrawMusicEvent(snapshot, trigger, value);
            return;
        }

        if (string.Equals(value.Type, "StopMusicEvent", StringComparison.Ordinal))
        {
            DrawStopMusicEvent(snapshot, trigger, value);
            return;
        }

        if (string.Equals(value.Type, "ShowProjectedImageEvent", StringComparison.Ordinal))
        {
            DrawProjectedImageEvent(trigger, value);
            return;
        }

        ImGui.TextWrapped(value.Type);
        ImGui.TextWrapped(DevToolUiSettings.T(
            "该事件没有在这里暴露原版 Rain World 参数。",
            "This event has no built-in Rain World parameters exposed here."));
        ImGui.TextWrapped(DevToolUiSettings.T(
            "如果 Mod 添加了自定义 DevInterface 控件，请切换到原版 UI 编辑。",
            "If a mod adds custom DevInterface controls, switch to Vanilla UI to edit them."));
    }

    private static void DrawMusicEvent(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot trigger,
        EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("音乐", "MUSIC"));
        DrawSongCombo(snapshot, trigger.Index, TriggerEventEditorKeys.SongName, DevToolUiSettings.T("歌曲", "Song"), value.SongName);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Volume, DevToolUiSettings.T("音量", "Volume"), value.Volume, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.FadeInSeconds, DevToolUiSettings.T("淡入（秒）", "Fade in (seconds)"), value.FadeInSeconds, 0f, 15f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Priority, DevToolUiSettings.T("优先级", "Priority"), value.Priority, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.DroneTolerance, DevToolUiSettings.T("Drone 容忍度", "Drone tolerance"), value.DroneTolerance, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.MaxThreatLevel, DevToolUiSettings.T("威胁达到此值时淡出", "Fade out at threat"), value.MaxThreatLevel, 0f, 1f);
        DrawOptionalEventInt(trigger.Index, TriggerEventEditorKeys.RoomsRange, DevToolUiSettings.T("房间切换数", "Room transitions"), value.RoomsRange, 0, 39, DevToolUiSettings.T("无限制", "Unlimited"));
        DrawOptionalEventInt(trigger.Index, TriggerEventEditorKeys.CyclesRest, DevToolUiSettings.T("休息周期", "Rest cycles"), value.CyclesRest, 0, 79, DevToolUiSettings.T("仅一次", "One time"));

        bool loop = value.Loop;
        if (ImGui.Checkbox(DevToolUiSettings.T("循环##TriggerEventLoop", "Loop##TriggerEventLoop"), ref loop))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.Loop,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: loop));

        bool onePerCycle = value.OneSongPerCycle;
        if (ImGui.Checkbox(DevToolUiSettings.T("每周期仅一首歌##TriggerEventOnePerCycle", "One song per cycle##TriggerEventOnePerCycle"), ref onePerCycle))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.OneSongPerCycle,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: onePerCycle));

        bool stopAtDeath = value.StopAtDeath;
        if (ImGui.Checkbox(DevToolUiSettings.T("死亡时停止##TriggerEventStopDeath", "Stop at death##TriggerEventStopDeath"), ref stopAtDeath))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.StopAtDeath,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: stopAtDeath));

        bool stopAtGate = value.StopAtGate;
        if (ImGui.Checkbox(DevToolUiSettings.T("进入业力门时停止##TriggerEventStopGate", "Stop at gate##TriggerEventStopGate"), ref stopAtGate))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.StopAtGate,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: stopAtGate));
    }

    private static void DrawStopMusicEvent(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot trigger,
        EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("停止音乐", "STOP MUSIC"));

        string mode = value.StopMode ?? string.Empty;
        if (ImGui.BeginCombo(DevToolUiSettings.T("停止模式##TriggerStopMode", "Stop mode##TriggerStopMode"), FriendlyStopMode(mode)))
        {
            DrawStopModeOption(trigger.Index, mode, "AllSongs", DevToolUiSettings.T("停止所有歌曲", "Stop all songs"));
            DrawStopModeOption(trigger.Index, mode, "SpecificSong", DevToolUiSettings.T("停止指定歌曲", "Stop specific song"));
            DrawStopModeOption(trigger.Index, mode, "AllButSpecific", DevToolUiSettings.T("停止除指定歌曲外的所有歌曲", "Stop all but specific song"));
            ImGui.EndCombo();
        }

        DrawEventString(trigger.Index, TriggerEventEditorKeys.StopMode, DevToolUiSettings.T("自定义模式", "Custom mode"), mode);

        if (!string.Equals(mode, "AllSongs", StringComparison.Ordinal))
            DrawSongCombo(snapshot, trigger.Index, TriggerEventEditorKeys.SongName, DevToolUiSettings.T("歌曲", "Song"), value.SongName);

        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Priority, DevToolUiSettings.T("优先级", "Priority"), value.Priority, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.FadeOutSeconds, DevToolUiSettings.T("淡出（秒）", "Fade out (seconds)"), value.FadeOutSeconds, 0f, 30f);
    }

    private static void DrawProjectedImageEvent(EditorTriggerSnapshot trigger, EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("投影图像", "PROJECTED IMAGE"));

        bool afterEncounter = value.AfterEncounter;
        if (ImGui.Checkbox(DevToolUiSettings.T("遭遇后##TriggerProjectedAfter", "After encounter##TriggerProjectedAfter"), ref afterEncounter))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.AfterEncounter,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: afterEncounter));

        bool directionOnly = value.OnlyWhenShowingDirection;
        if (ImGui.Checkbox(DevToolUiSettings.T("仅显示方向时##TriggerProjectedDirection", "Only when showing direction##TriggerProjectedDirection"), ref directionOnly))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.OnlyWhenShowingDirection,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: directionOnly));

        DrawEventInt(trigger.Index, TriggerEventEditorKeys.FromCycle, DevToolUiSettings.T("起始周期", "From cycle"), value.FromCycle, 0, 9999);
    }

    private static void DrawSongCombo(
        EditorTriggerPresentationSnapshot snapshot,
        int triggerIndex,
        string key,
        string label,
        string current)
    {
        current ??= string.Empty;
        if (ImGui.BeginCombo(label + "##TriggerEventSong" + key,
                string.IsNullOrEmpty(current) ? DevToolUiSettings.T("无歌曲", "NO SONG") : current))
        {
            string[] songs = snapshot.SongNames ?? Array.Empty<string>();
            for (int i = 0; i < songs.Length; i++)
            {
                string song = songs[i];
                bool selected = string.Equals(song, current, StringComparison.Ordinal);
                if (ImGui.Selectable(song + "##TriggerSong" + key + i, selected))
                    SendEventValue(triggerIndex, key,
                        new EditorPropertyValue(EditorPropertyKind.String, text: song));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DrawEventString(triggerIndex, key, DevToolUiSettings.T("歌曲 ID", "Song ID"), current);
    }

    private static void DrawStopModeOption(int triggerIndex, string current, string value, string label)
    {
        bool selected = string.Equals(current, value, StringComparison.Ordinal);
        if (ImGui.Selectable(label + "##TriggerStopMode" + value, selected))
            SendEventValue(triggerIndex, TriggerEventEditorKeys.StopMode,
                new EditorPropertyValue(EditorPropertyKind.String, text: value));
        if (selected) ImGui.SetItemDefaultFocus();
    }

    private static string FriendlyStopMode(string mode) => mode switch
    {
        "AllSongs" => DevToolUiSettings.T("停止所有歌曲", "Stop all songs"),
        "SpecificSong" => DevToolUiSettings.T("停止指定歌曲", "Stop specific song"),
        "AllButSpecific" => DevToolUiSettings.T("停止除指定歌曲外的所有歌曲", "Stop all but specific song"),
        _ => string.IsNullOrEmpty(mode) ? DevToolUiSettings.T("未知", "Unknown") : mode
    };

    private static void DrawUpperCycle(EditorTriggerSnapshot trigger)
    {
        bool noUpper = trigger.ActiveToCycle < 0;
        bool editedNoUpper = noUpper;
        if (ImGui.Checkbox(DevToolUiSettings.T("无结束周期##TriggerNoUpper", "No upper cycle##TriggerNoUpper"), ref editedNoUpper))
        {
            int next = editedNoUpper ? -1 : trigger.ActiveFromCycle;
            SendValue(trigger.Index, TriggerEditorKeys.ActiveToCycle,
                new EditorPropertyValue(EditorPropertyKind.Integer, integer: next));
        }

        if (noUpper) return;
        DrawInt(trigger, TriggerEditorKeys.ActiveToCycle, DevToolUiSettings.T("截止周期", "Up to cycle"), trigger.ActiveToCycle,
            trigger.ActiveFromCycle, 79);
    }

    private static void DrawKarma(EditorTriggerSnapshot trigger)
    {
        string[] labels = { DevToolUiSettings.T("无", "None"), "2", "3", "4", "5" };
        int value = Math.Max(0, Math.Min(trigger.Karma, labels.Length - 1));
        if (!ImGui.BeginCombo(DevToolUiSettings.T("业力要求##TriggerKarma", "Karma requirement##TriggerKarma"), labels[value])) return;
        for (int i = 0; i < labels.Length; i++)
        {
            bool selected = i == value;
            if (ImGui.Selectable(labels[i] + "##TriggerKarma" + i, selected))
                SendValue(trigger.Index, TriggerEditorKeys.Karma,
                    new EditorPropertyValue(EditorPropertyKind.Integer, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawEntrance(EditorTriggerPresentationSnapshot snapshot, EditorTriggerSnapshot trigger)
    {
        string preview = trigger.Entrance < 0
            ? DevToolUiSettings.T("任意入口", "Any entrance")
            : DevToolUiSettings.T("入口 ", "Entrance ") + trigger.Entrance;
        if (!ImGui.BeginCombo(DevToolUiSettings.T("入口要求##TriggerEntrance", "Entrance requirement##TriggerEntrance"), preview)) return;

        if (ImGui.Selectable(DevToolUiSettings.T("任意入口##TriggerEntranceAny", "Any entrance##TriggerEntranceAny"), trigger.Entrance < 0))
            SendValue(trigger.Index, TriggerEditorKeys.Entrance,
                new EditorPropertyValue(EditorPropertyKind.Integer, integer: -1));

        for (int i = 0; i < snapshot.EntranceCount; i++)
        {
            bool selected = trigger.Entrance == i;
            if (ImGui.Selectable(DevToolUiSettings.T("入口 ", "Entrance ") + i + "##TriggerEntrance" + i, selected))
                SendValue(trigger.Index, TriggerEditorKeys.Entrance,
                    new EditorPropertyValue(EditorPropertyKind.Integer, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawSlugcats(EditorTriggerPresentationSnapshot snapshot, EditorTriggerSnapshot trigger)
    {
        if (!ImGui.CollapsingHeader(DevToolUiSettings.T("蛞蝓猫##TriggerSlugcats", "Slugcats##TriggerSlugcats"), ImGuiTreeNodeFlags.DefaultOpen)) return;
        string[] all = snapshot.SlugcatNames ?? Array.Empty<string>();
        string[] allowed = trigger.AllowedSlugcats ?? Array.Empty<string>();

        for (int i = 0; i < all.Length; i++)
        {
            string name = all[i];
            bool value = Contains(allowed, name);
            if (ImGui.Checkbox(name + "##TriggerSlugcat" + i, ref value))
                TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(
                    TriggerEditorCommandKind.ToggleSlugcat,
                    index: trigger.Index,
                    text: name));
        }
    }

    private static void DrawInt(EditorTriggerSnapshot trigger, string key, string label, int current, int min, int max)
    {
        string stateKey = trigger.Index + ":" + key;
        int value = Get(IntEdits, stateKey, current);
        bool changed = ImGui.InputInt(label + "##Trigger" + stateKey, ref value);
        value = Math.Max(min, Math.Min(max, value));
        IntEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));
        else if (!changed && !ImGui.IsItemActive())
            IntEdits[stateKey] = current;
    }

    private static void DrawFloat(EditorTriggerSnapshot trigger, string key, string label, float current, float min, float max)
    {
        string stateKey = trigger.Index + ":" + key;
        float value = Get(FloatEdits, stateKey, current);
        bool changed = ImGui.SliderFloat(label + "##Trigger" + stateKey, ref value, min, max, "%.3f");
        FloatEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
        else if (!changed && !ImGui.IsItemActive())
            FloatEdits[stateKey] = current;
    }

    private static void DrawVector(EditorTriggerSnapshot trigger, string key, string label, float x, float y)
    {
        string stateKey = trigger.Index + ":" + key;
        Num.Vector2 value = Get(VectorEdits, stateKey, new Num.Vector2(x, y));
        bool changed = ImGui.InputFloat2(label + "##Trigger" + stateKey, ref value, "%.2f");
        VectorEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendValue(trigger.Index, key,
                new EditorPropertyValue(EditorPropertyKind.Vector2, x: value.X, y: value.Y));
        else if (!changed && !ImGui.IsItemActive())
            VectorEdits[stateKey] = new Num.Vector2(x, y);
    }

    private static void DrawString(EditorTriggerSnapshot trigger, string key, string label, string current)
    {
        string stateKey = trigger.Index + ":" + key;
        string value = Get(StringEdits, stateKey, current ?? string.Empty);
        bool changed = ImGui.InputText(label + "##Trigger" + stateKey, ref value, 128);
        StringEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendValue(trigger.Index, key, new EditorPropertyValue(EditorPropertyKind.String, text: value));
        else if (!changed && !ImGui.IsItemActive())
            StringEdits[stateKey] = current ?? string.Empty;
    }

    private static void DrawEventFloat(int triggerIndex, string key, string label, float current, float min, float max)
    {
        string stateKey = "event:" + triggerIndex + ":" + key;
        float value = Get(FloatEdits, stateKey, current);
        bool changed = ImGui.SliderFloat(label + "##Trigger" + stateKey, ref value, min, max, "%.3f");
        FloatEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
        else if (!changed && !ImGui.IsItemActive())
            FloatEdits[stateKey] = current;
    }

    private static void DrawEventInt(int triggerIndex, string key, string label, int current, int min, int max)
    {
        string stateKey = "event:" + triggerIndex + ":" + key;
        int value = Get(IntEdits, stateKey, current);
        bool changed = ImGui.InputInt(label + "##Trigger" + stateKey, ref value);
        value = Math.Max(min, Math.Min(max, value));
        IntEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));
        else if (!changed && !ImGui.IsItemActive())
            IntEdits[stateKey] = current;
    }

    private static void DrawOptionalEventInt(
        int triggerIndex,
        string key,
        string label,
        int current,
        int min,
        int max,
        string negativeLabel)
    {
        bool negative = current < 0;
        bool editedNegative = negative;
        if (ImGui.Checkbox(negativeLabel + "##TriggerOptional" + triggerIndex + key, ref editedNegative))
        {
            int next = editedNegative ? -1 : min;
            SendEventValue(triggerIndex, key,
                new EditorPropertyValue(EditorPropertyKind.Integer, integer: next));
        }

        if (!negative)
            DrawEventInt(triggerIndex, key, label, current, min, max);
    }

    private static void DrawEventString(int triggerIndex, string key, string label, string current)
    {
        string stateKey = "event:" + triggerIndex + ":" + key;
        string value = Get(StringEdits, stateKey, current ?? string.Empty);
        bool changed = ImGui.InputText(label + "##Trigger" + stateKey, ref value, 256);
        StringEdits[stateKey] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendEventValue(triggerIndex, key, new EditorPropertyValue(EditorPropertyKind.String, text: value));
        else if (!changed && !ImGui.IsItemActive())
            StringEdits[stateKey] = current ?? string.Empty;
    }

    private static EditorTriggerSnapshot FindSelected(EditorTriggerPresentationSnapshot snapshot)
    {
        EditorTriggerSnapshot[] triggers = snapshot.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        int index = snapshot.SelectedIndex;
        return index >= 0 && index < triggers.Length ? triggers[index] : null;
    }

    private static void SendValue(int index, string key, EditorPropertyValue value) =>
        TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(
            TriggerEditorCommandKind.SetValue,
            index: index,
            key: key,
            value: value));

    private static void SendEventValue(int index, string key, EditorPropertyValue value) =>
        TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(
            TriggerEditorCommandKind.SetEventValue,
            index: index,
            key: key,
            value: value));

    private static bool Contains(string[] values, string value)
    {
        if (values == null) return false;
        for (int i = 0; i < values.Length; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

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
