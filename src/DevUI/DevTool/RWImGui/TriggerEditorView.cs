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
        ImGui.TextDisabled(selected.EventType.Length == 0 ? "No event assigned" : "Event · " + selected.EventType);
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
        ImGui.TextDisabled("EVENT");
        if (string.IsNullOrEmpty(selected.EventType))
            ImGui.TextDisabled("No event. Event sub-editor is the next migration layer.");
        else
            ImGui.TextWrapped(selected.EventType);

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
            if (!string.IsNullOrEmpty(trigger.EventType)) label += "  →  " + trigger.EventType;
            if (ImGui.Selectable(label + "##TriggerScene" + trigger.Index, trigger.Selected))
                TriggerEditorCommandQueue.Enqueue(new TriggerEditorCommand(TriggerEditorCommandKind.Select, trigger.Index));
        }
    }

    private static void DrawUpperCycle(EditorTriggerSnapshot trigger)
    {
        bool noUpper = trigger.ActiveToCycle < 0;
        bool editedNoUpper = noUpper;
        if (ImGui.Checkbox("No upper cycle##TriggerNoUpper", ref editedNoUpper))
        {
            int next = editedNoUpper ? -1 : Math.Max(trigger.ActiveFromCycle, trigger.ActiveFromCycle);
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
