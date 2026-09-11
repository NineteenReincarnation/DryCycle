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
            ImGui.TextDisabled("Trigger editor unavailable.");
            return;
        }

        if (ImGui.Button(sceneTab ? "Library" : "Library*")) sceneTab = false;
        ImGui.SameLine();
        if (ImGui.Button(sceneTab ? "Scene*" : "Scene")) sceneTab = true;
        ImGui.Separator();

        if (sceneTab) DrawScene(snapshot);
        else DrawLibrary(snapshot);
    }

    internal static void DrawInspector(EditorTriggerPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Trigger editor unavailable.");
            return;
        }

        EditorTriggerSnapshot selected = FindSelected(snapshot);
        if (selected == null)
        {
            ImGui.TextDisabled("Select a trigger from Scene or its world gizmo.");
            return;
        }

        ImGui.Text(selected.Type);
        ImGui.TextDisabled(selected.Event?.HasEvent == true ? "Event · " + selected.Event.Type : "No event assigned");
        ImGui.Separator();

        ImGui.TextDisabled("ACTIVATION");
        DrawInt(selected, TriggerEditorKeys.ActiveFromCycle, "From cycle", selected.ActiveFromCycle, 0, 80);
        DrawUpperCycle(selected);
        DrawFloat(selected, TriggerEditorKeys.DelaySeconds, "Delay (seconds)", selected.DelaySeconds, 0f, 120f);
        DrawFloat(selected, TriggerEditorKeys.FireChance, "Fire chance", selected.FireChance, 0f, 1f);

        bool multiUse = selected.MultiUse;
        if (ImGui.Checkbox("Can fire multiple times##TriggerMultiUse", ref multiUse))
            SendValue(selected.Index, TriggerEditorKeys.MultiUse,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: multiUse));

        DrawKarma(selected);
        DrawEntrance(snapshot, selected);

        if (selected.IsSpot)
        {
            ImGui.Separator();
            ImGui.TextDisabled("SPOT AREA");
            DrawVector(selected, TriggerEditorKeys.Position, "Position", selected.X, selected.Y);
            DrawFloat(selected, TriggerEditorKeys.Radius, "Radius", selected.Radius, 0f, 4000f);
        }

        if (!string.IsNullOrEmpty(selected.CreatureType))
        {
            ImGui.Separator();
            ImGui.TextDisabled("CREATURE");
            DrawString(selected, TriggerEditorKeys.CreatureType, "Creature type", selected.CreatureType);
        }

        ImGui.Separator();
        DrawSlugcats(snapshot, selected);

        ImGui.Separator();
        DrawEventEditor(snapshot, selected);

        ImGui.Separator();
        if (ImGui.Button("Delete Trigger"))
            TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(TriggerEditorCommandKind.Delete, selected.Index));
    }

    private static void DrawLibrary(EditorTriggerPresentationSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search##TriggerLibrarySearch", ref search, 128);
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

        if (matches == 0) ImGui.TextDisabled("No matching trigger types.");
    }

    private static void DrawScene(EditorTriggerPresentationSnapshot snapshot)
    {
        EditorTriggerSnapshot[] triggers = snapshot.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        ImGui.TextDisabled(triggers.Length + " triggers");
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
        ImGui.TextDisabled("EVENT");
        EditorTriggeredEventSnapshot value = trigger.Event ?? new EditorTriggeredEventSnapshot();

        string preview = value.HasEvent ? value.Type : "None";
        if (ImGui.BeginCombo("Type##TriggerEventType", preview))
        {
            bool noneSelected = !value.HasEvent;
            if (ImGui.Selectable("None##TriggerEventNone", noneSelected))
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
            ImGui.TextDisabled("Choose an event type to configure this trigger.");
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
        ImGui.TextDisabled("This event has no built-in Rain World parameters exposed here.");
        ImGui.TextDisabled("If a mod adds custom DevInterface controls, switch to Vanilla UI to edit them.");
    }

    private static void DrawMusicEvent(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot trigger,
        EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled("MUSIC");
        DrawSongCombo(snapshot, trigger.Index, TriggerEventEditorKeys.SongName, "Song", value.SongName);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Volume, "Volume", value.Volume, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.FadeInSeconds, "Fade in (seconds)", value.FadeInSeconds, 0f, 15f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Priority, "Priority", value.Priority, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.DroneTolerance, "Drone tolerance", value.DroneTolerance, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.MaxThreatLevel, "Fade out at threat", value.MaxThreatLevel, 0f, 1f);
        DrawOptionalEventInt(trigger.Index, TriggerEventEditorKeys.RoomsRange, "Room transitions", value.RoomsRange, 0, 39, "Unlimited");
        DrawOptionalEventInt(trigger.Index, TriggerEventEditorKeys.CyclesRest, "Rest cycles", value.CyclesRest, 0, 79, "One time");

        bool loop = value.Loop;
        if (ImGui.Checkbox("Loop##TriggerEventLoop", ref loop))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.Loop,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: loop));

        bool onePerCycle = value.OneSongPerCycle;
        if (ImGui.Checkbox("One song per cycle##TriggerEventOnePerCycle", ref onePerCycle))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.OneSongPerCycle,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: onePerCycle));

        bool stopAtDeath = value.StopAtDeath;
        if (ImGui.Checkbox("Stop at death##TriggerEventStopDeath", ref stopAtDeath))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.StopAtDeath,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: stopAtDeath));

        bool stopAtGate = value.StopAtGate;
        if (ImGui.Checkbox("Stop at gate##TriggerEventStopGate", ref stopAtGate))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.StopAtGate,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: stopAtGate));
    }

    private static void DrawStopMusicEvent(
        EditorTriggerPresentationSnapshot snapshot,
        EditorTriggerSnapshot trigger,
        EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled("STOP MUSIC");

        string mode = value.StopMode ?? string.Empty;
        if (ImGui.BeginCombo("Stop mode##TriggerStopMode", FriendlyStopMode(mode)))
        {
            DrawStopModeOption(trigger.Index, mode, "AllSongs", "Stop all songs");
            DrawStopModeOption(trigger.Index, mode, "SpecificSong", "Stop specific song");
            DrawStopModeOption(trigger.Index, mode, "AllButSpecific", "Stop all but specific song");
            ImGui.EndCombo();
        }

        // Preserve custom ExtEnum values from other mods instead of coercing them into one
        // of the three vanilla modes. A custom value can still be edited as text.
        DrawEventString(trigger.Index, TriggerEventEditorKeys.StopMode, "Custom mode", mode);

        if (!string.Equals(mode, "AllSongs", StringComparison.Ordinal))
            DrawSongCombo(snapshot, trigger.Index, TriggerEventEditorKeys.SongName, "Song", value.SongName);

        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.Priority, "Priority", value.Priority, 0f, 1f);
        DrawEventFloat(trigger.Index, TriggerEventEditorKeys.FadeOutSeconds, "Fade out (seconds)", value.FadeOutSeconds, 0f, 30f);
    }

    private static void DrawProjectedImageEvent(EditorTriggerSnapshot trigger, EditorTriggeredEventSnapshot value)
    {
        ImGui.TextDisabled("PROJECTED IMAGE");

        bool afterEncounter = value.AfterEncounter;
        if (ImGui.Checkbox("After encounter##TriggerProjectedAfter", ref afterEncounter))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.AfterEncounter,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: afterEncounter));

        bool directionOnly = value.OnlyWhenShowingDirection;
        if (ImGui.Checkbox("Only when showing direction##TriggerProjectedDirection", ref directionOnly))
            SendEventValue(trigger.Index, TriggerEventEditorKeys.OnlyWhenShowingDirection,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: directionOnly));

        DrawEventInt(trigger.Index, TriggerEventEditorKeys.FromCycle, "From cycle", value.FromCycle, 0, 9999);
    }

    private static void DrawSongCombo(
        EditorTriggerPresentationSnapshot snapshot,
        int triggerIndex,
        string key,
        string label,
        string current)
    {
        current ??= string.Empty;
        if (ImGui.BeginCombo(label + "##TriggerEventSong" + key, string.IsNullOrEmpty(current) ? "NO SONG" : current))
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

        // The current song may come from a mod and therefore not exist in vanilla's discovered
        // list. Keep a text path so such values remain editable rather than being discarded.
        DrawEventString(triggerIndex, key, "Song ID", current);
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
        "AllSongs" => "Stop all songs",
        "SpecificSong" => "Stop specific song",
        "AllButSpecific" => "Stop all but specific song",
        _ => string.IsNullOrEmpty(mode) ? "Unknown" : mode
    };

    private static void DrawUpperCycle(EditorTriggerSnapshot trigger)
    {
        bool noUpper = trigger.ActiveToCycle < 0;
        bool editedNoUpper = noUpper;
        if (ImGui.Checkbox("No upper cycle##TriggerNoUpper", ref editedNoUpper))
        {
            int next = editedNoUpper ? -1 : trigger.ActiveFromCycle;
            SendValue(trigger.Index, TriggerEditorKeys.ActiveToCycle,
                new EditorPropertyValue(EditorPropertyKind.Integer, integer: next));
        }

        if (noUpper) return;
        DrawInt(trigger, TriggerEditorKeys.ActiveToCycle, "Up to cycle", trigger.ActiveToCycle,
            trigger.ActiveFromCycle, 79);
    }

    private static void DrawKarma(EditorTriggerSnapshot trigger)
    {
        string[] labels = { "None", "2", "3", "4", "5" };
        int value = Math.Max(0, Math.Min(trigger.Karma, labels.Length - 1));
        if (!ImGui.BeginCombo("Karma requirement##TriggerKarma", labels[value])) return;
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
        string preview = trigger.Entrance < 0 ? "Any entrance" : "Entrance " + trigger.Entrance;
        if (!ImGui.BeginCombo("Entrance requirement##TriggerEntrance", preview)) return;

        if (ImGui.Selectable("Any entrance##TriggerEntranceAny", trigger.Entrance < 0))
            SendValue(trigger.Index, TriggerEditorKeys.Entrance,
                new EditorPropertyValue(EditorPropertyKind.Integer, integer: -1));

        for (int i = 0; i < snapshot.EntranceCount; i++)
        {
            bool selected = trigger.Entrance == i;
            if (ImGui.Selectable("Entrance " + i + "##TriggerEntrance" + i, selected))
                SendValue(trigger.Index, TriggerEditorKeys.Entrance,
                    new EditorPropertyValue(EditorPropertyKind.Integer, integer: i));
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private static void DrawSlugcats(EditorTriggerPresentationSnapshot snapshot, EditorTriggerSnapshot trigger)
    {
        if (!ImGui.CollapsingHeader("Slugcats##TriggerSlugcats", ImGuiTreeNodeFlags.DefaultOpen)) return;
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
