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
        ImGui.TextDisabled(DevToolUiSettings.T("房间设置", "ROOM SETTINGS"));
        ImGui.Separator();

        DrawSectionButton(Section.Environment, DevToolUiSettings.T("环境", "Environment"));
        DrawSectionButton(Section.Visual, DevToolUiSettings.T("视觉", "Visual"));
        DrawSectionButton(Section.Gameplay, DevToolUiSettings.T("玩法", "Gameplay"));
        DrawSectionButton(Section.Effects, DevToolUiSettings.T("效果", "Effects"));

        if (section != Section.Effects || !snapshot.Available) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("添加效果", "ADD EFFECT"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(DevToolUiSettings.T("搜索##RoomEffectSearch", "Search##RoomEffectSearch"), ref effectSearch, 128);

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

        if (matches == 0) ImGui.TextDisabled(DevToolUiSettings.T("没有匹配的效果。", "No matching effects."));
    }

    internal static void DrawInspector(EditorRoomSettingsSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("房间设置不可用。", "Room settings unavailable."));
            return;
        }

        ImGui.Text(SectionName(section));
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
        ImGui.TextDisabled(DevToolUiSettings.T("雨", "Rain"));
        DrawFloat(RoomSettingKeys.RainIntensity, DevToolUiSettings.T("降雨强度", "Rain Intensity"), snapshot.RainIntensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.RumbleIntensity, DevToolUiSettings.T("震动强度", "Rumble Intensity"), snapshot.RumbleIntensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.CeilingDrips, DevToolUiSettings.T("天花板滴水", "Ceiling Drips"), snapshot.CeilingDrips, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("水面波浪", "Water Waves"));
        DrawFloat(RoomSettingKeys.WaveSpeed, DevToolUiSettings.T("波速", "Wave Speed"), snapshot.WaveSpeed, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaveLength, DevToolUiSettings.T("波长", "Wave Length"), snapshot.WaveLength, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaveAmplitude, DevToolUiSettings.T("波幅", "Wave Amplitude"), snapshot.WaveAmplitude, 0f, 1f);
        DrawFloat(RoomSettingKeys.SecondWaveLength, DevToolUiSettings.T("回卷长度", "Rollback Length"), snapshot.SecondWaveLength, 0f, 1f);
        DrawFloat(RoomSettingKeys.SecondWaveAmplitude, DevToolUiSettings.T("回卷幅度", "Rollback Amplitude"), snapshot.SecondWaveAmplitude, 0f, 1f);
        DrawFloat(RoomSettingKeys.WaterReflectionAlpha, DevToolUiSettings.T("水面亮度", "Water Light"), snapshot.WaterReflectionAlpha, 0f, 1f);
    }

    private static void DrawVisual(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("氛围", "Atmosphere"));
        DrawFloat(RoomSettingKeys.Clouds, DevToolUiSettings.T("云层", "Clouds"), snapshot.Clouds, 0f, 1f);
        DrawFloat(RoomSettingKeys.Grime, DevToolUiSettings.T("污垢", "Grime"), snapshot.Grime, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("色板", "Palette"));
        DrawInt(RoomSettingKeys.Palette, DevToolUiSettings.T("色板", "Palette"), snapshot.Palette);
        DrawInt(RoomSettingKeys.EffectColorA, DevToolUiSettings.T("效果颜色 A", "Effect Color A"), snapshot.EffectColorA);
        DrawInt(RoomSettingKeys.EffectColorB, DevToolUiSettings.T("效果颜色 B", "Effect Color B"), snapshot.EffectColorB);
    }

    private static void DrawGameplay(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("危险", "Danger"));
        if (ImGui.BeginCombo(DevToolUiSettings.T("危险类型##RoomDangerType", "Danger Type##RoomDangerType"), snapshot.DangerType))
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
        if (ImGui.Checkbox(DevToolUiSettings.T("房间专属脚本", "Room Specific Script"), ref script))
            SendSetting(RoomSettingKeys.RoomSpecificScript,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: script));

        bool wetTerrain = snapshot.WetTerrain;
        if (ImGui.Checkbox(DevToolUiSettings.T("湿润地形", "Wet Terrain"), ref wetTerrain))
            SendSetting(RoomSettingKeys.WetTerrain,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: wetTerrain));

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("随机物品", "Random Items"));
        DrawFloat(RoomSettingKeys.RandomItemDensity, DevToolUiSettings.T("物品密度", "Item Density"), snapshot.RandomItemDensity, 0f, 1f);
        DrawFloat(RoomSettingKeys.RandomItemSpearChance, DevToolUiSettings.T("长矛概率", "Spear Chance"), snapshot.RandomItemSpearChance, 0f, 1f);
    }

    private static void DrawEffects(EditorRoomSettingsSnapshot snapshot)
    {
        EditorRoomEffectSnapshot[] effects = snapshot.Effects ?? Array.Empty<EditorRoomEffectSnapshot>();
        if (effects.Length == 0)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前没有房间效果，可从浏览器添加。", "No room effects. Add one from the Browser."));
            return;
        }

        for (int i = 0; i < effects.Length; i++)
        {
            EditorRoomEffectSnapshot effect = effects[i];
            string header = effect.Type;
            if (!string.IsNullOrEmpty(effect.Category)) header += "  ·  " + effect.Category;
            if (effect.Inherited) header += DevToolUiSettings.T("  [继承]", "  [Inherited]");

            if (!ImGui.CollapsingHeader(header + "##RoomEffect" + effect.Index, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            string[] names = effect.SliderNames ?? Array.Empty<string>();
            float[] values = effect.Values ?? Array.Empty<float>();
            int count = Math.Min(names.Length, values.Length);
            for (int slider = 0; slider < count; slider++)
            {
                string key = "effect:" + effect.Index + ":" + slider;
                float value = Get(FloatEdits, key, values[slider]);
                string sliderLabel = string.IsNullOrEmpty(names[slider])
                    ? DevToolUiSettings.T("数值 ", "Value ") + (slider + 1)
                    : names[slider];
                bool changed = ImGui.SliderFloat(sliderLabel + "##" + key, ref value, 0f, 1f, "%.3f");
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
                ImGui.TextDisabled(DevToolUiSettings.T("继承效果 · 请修改来源模板。", "Inherited effect · edit the source template to change it."));
            }
            else if (ImGui.SmallButton(DevToolUiSettings.T("移除##RoomEffectRemove", "Remove##RoomEffectRemove") + effect.Index))
            {
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
                    RoomEditorCommandKind.DeleteEffect,
                    index: effect.Index));
            }
        }
    }

    private static string SectionName(Section value)
    {
        return value switch
        {
            Section.Environment => DevToolUiSettings.T("环境", "Environment"),
            Section.Visual => DevToolUiSettings.T("视觉", "Visual"),
            Section.Gameplay => DevToolUiSettings.T("玩法", "Gameplay"),
            Section.Effects => DevToolUiSettings.T("效果", "Effects"),
            _ => value.ToString()
        };
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
