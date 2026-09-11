using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Room;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class RoomSettingsView
{
    private enum Section
    {
        Environment,
        Visual,
        Gameplay,
        Effects
    }

    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);
    private static Section section = Section.Environment;
    private static string effectSearch = string.Empty;

    internal static void DrawBrowser(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled("ROOM SETTINGS");
        ImGui.Separator();

        DrawSectionButton(Section.Environment, "Environment");
        DrawSectionButton(Section.Visual, "Visual");
        DrawSectionButton(Section.Gameplay, "Gameplay");
        DrawSectionButton(Section.Effects, "Effects");

        if (section != Section.Effects || !snapshot.Available) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("ADD EFFECT");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search##RoomEffectSearch", ref effectSearch, 128);

        string[] available = snapshot.AvailableEffects ?? Array.Empty<string>();
        int matches = 0;
        for (int i = 0; i < available.Length; i++)
        {
            string type = available[i];
            if (!Matches(type, effectSearch)) continue;
            matches++;
            if (ImGui.Selectable(type + "##RoomAddEffect" + type, false))
            {
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
                    RoomEditorCommandKind.AddEffect,
                    key: type));
            }
        }

        if (matches == 0) ImGui.TextDisabled("No matching effects.");
    }

    internal static void DrawInspector(EditorRoomSettingsSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Room settings unavailable.");
            return;
        }

        ImGui.Text(section.ToString());
        ImGui.Separator();

        switch (section)
        {
            case Section.Environment:
                DrawEnvironment(snapshot);
                break;
            case Section.Visual:
                DrawVisual(snapshot);
                break;
            case Section.Gameplay:
                DrawGameplay(snapshot);
                break;
            case Section.Effects:
                DrawEffects(snapshot);
                break;
        }
    }

    private static void DrawEnvironment(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled("Rain");
        DrawFloat(RoomSettingKeys.RainIntensity, "Rain Intensity", snapshot.RainIntensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.RumbleIntensity, "Rumble Intensity", snapshot.RumbleIntensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.CeilingDrips, "Ceiling Drips", snapshot.CeilingDrips, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled("Water Waves");
        DrawFloat(RoomSettingKeys.WaveSpeed, "Wave Speed", snapshot.WaveSpeed, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaveLength, "Wave Length", snapshot.WaveLength, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaveAmplitude, "Wave Amplitude", snapshot.WaveAmplitude, 0f, 1f);
        DrawFloat(RoomSettingKeys.SecondWaveLength, "Rollback Length", snapshot.SecondWaveLength, 0f, 1f);
        DrawFloat(RoomSettingKeys.SecondWaveAmplitude, "Rollback Amplitude", snapshot.SecondWaveAmplitude, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaterReflectionAlpha, "Water Light", snapshot.WaterReflectionAlpha, 0f, 1f);
    }

    private static void DrawVisual(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled("Atmosphere");
        DrawFloat(RoomSettingKeys.Clouds, "Clouds", snapshot.Clouds, 0f, 1f);
        DrawFloat(RoomSettingKeys.Grime, "Grime", snapshot.Grime, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled("Palette");
        DrawInt(RoomSettingKeys.Palette, "Palette", snapshot.Palette);
        DrawInt(RoomSettingKeys.EffectColorA, "Effect Color A", snapshot.EffectColorA);
        DrawInt(RoomSettingKeys.EffectColorB, "Effect Color B", snapshot.EffectColorB);
    }

    private static void DrawGameplay(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled("Danger");
        if (ImGui.BeginCombo("Danger Type##RoomDangerType", snapshot.DangerType))
        {
            string[] options = snapshot.DangerTypes ?? Array.Empty<string>();
            for (int i = 0; i < options.Length; i++)
            {
                string option = options[i];
                bool selected = string.Equals(option, snapshot.DangerType, StringComparison.Ordinal);
                if (ImGui.Selectable(option + "##RoomDanger" + option, selected))
                {
                    SendSetting(RoomSettingKeys.DangerType,
                        new EditorPropertyValue(EditorPropertyKind.String, text: option));
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        bool script = snapshot.RoomSpecificScript;
        if (ImGui.Checkbox("Room Specific Script", ref script))
            SendSetting(RoomSettingKeys.RoomSpecificScript,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: script));

        bool wetTerrain = snapshot.WetTerrain;
        if (ImGui.Checkbox("Wet Terrain", ref wetTerrain))
            SendSetting(RoomSettingKeys.WetTerrain,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: wetTerrain));

        ImGui.Separator();
        ImGui.TextDisabled("Random Items");
        DrawFloat(RoomSettingKeys.RandomItemDensity, "Item Density", snapshot.RandomItemDensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.RandomItemSpearChance, "Spear Chance", snapshot.RandomItemSpearChance, 0f, 1f);
    }

    private static void DrawEffects(EditorRoomSettingsSnapshot snapshot)
    {
        EditorRoomEffectSnapshot[] effects = snapshot.Effects ?? Array.Empty<EditorRoomEffectSnapshot>();
        if (effects.Length == 0)
        {
            ImGui.TextDisabled("No room effects. Add one from the Browser.");
            return;
        }

        for (int i = 0; i < effects.Length; i++)
        {
            EditorRoomEffectSnapshot effect = effects[i];
            string header = effect.Type;
            if (!string.IsNullOrEmpty(effect.Category)) header += "  ·  " + effect.Category;
            if (effect.Inherited) header += "  [Inherited]";

            if (!ImGui.CollapsingHeader(header + "##RoomEffect" + effect.Index, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            string[] names = effect.SliderNames ?? Array.Empty<string>();
            float[] values = effect.Values ?? Array.Empty<float>();
            int count = Math.Min(names.Length, values.Length);
            for (int slider = 0; slider < count; slider++)
            {
                string key = "effect:" + effect.Index + ":" + slider;
                float value = Get(FloatEdits, key, values[slider]);
                bool changed = ImGui.SliderFloat(
                    (string.IsNullOrEmpty(names[slider]) ? "Value " + (slider + 1) : names[slider]) + "##" + key,
                    ref value,
                    0f,
                    1f,
                    "%.3f");
                FloatEdits[key] = value;

                if (!effect.Inherited && ImGui.IsItemDeactivatedAfterEdit())
                {
                    RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
                        RoomEditorCommandKind.SetEffectAmount,
                        index: effect.Index,
                        secondaryIndex: slider,
                        value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));
                }
                else if (!changed && !ImGui.IsItemActive())
                {
                    FloatEdits[key] = values[slider];
                }
            }

            if (effect.Inherited)
            {
                ImGui.TextDisabled("Inherited effect · edit the source template to change it.");
            }
            else if (ImGui.SmallButton("Remove##RoomEffectRemove" + effect.Index))
            {
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
                    RoomEditorCommandKind.DeleteEffect,
                    index: effect.Index));
            }
        }
    }

    private static void DrawSectionButton(Section value, string label)
    {
        bool active = section == value;
        if (ImGui.Selectable(label + "##RoomSection" + value, active))
            section = value;
    }

    private static void DrawFloat(string key, string label, float current, float min, float max)
    {
        float value = Get(FloatEdits, key, current);
        bool changed = ImGui.SliderFloat(label + "##RoomSetting" + key, ref value, min, max, "%.3f");
        FloatEdits[key] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SendSetting(key, new EditorPropertyValue(EditorPropertyKind.Float, x: value));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            FloatEdits[key] = current;
        }
    }

    private static void DrawInt(string key, string label, int current)
    {
        int value = Get(IntEdits, key, current);
        bool changed = ImGui.InputInt(label + "##RoomSetting" + key, ref value, 1, 10);
        IntEdits[key] = value;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SendSetting(key, new EditorPropertyValue(EditorPropertyKind.Integer, integer: value));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            IntEdits[key] = current;
        }
    }

    private static void SendSetting(string key, EditorPropertyValue value)
    {
        RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
            RoomEditorCommandKind.SetSetting,
            key: key,
            value: value));
    }

    private static bool Matches(string value, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return value?.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static TValue Get<TValue>(Dictionary<string, TValue> dictionary, string key, TValue fallback)
    {
        if (dictionary.TryGetValue(key, out TValue value)) return value;
        dictionary[key] = fallback;
        return fallback;
    }
}
