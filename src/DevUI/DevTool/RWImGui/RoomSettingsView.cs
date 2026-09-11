using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Room;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class RoomSettingsView
{
    private enum Section
    {
        Environment,
        Visual,
        Gameplay,
        Terrain,
        Templates,
        Effects
    }

    private static readonly Dictionary<string, float> FloatEdits = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> IntEdits = new(StringComparer.Ordinal);
    private static Section section = Section.Environment;
    private static string effectSearch = string.Empty;
    private const float BrowserBodyFontScale = 1.22f;
    private const float EffectSourceHeaderFontScale = 1.52f;

    internal static void DrawBrowser(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("房间设置", "ROOM SETTINGS"));
        ImGui.Separator();

        DrawSectionButton(Section.Environment, DevToolUiSettings.T("环境", "Environment"));
        DrawSectionButton(Section.Visual, DevToolUiSettings.T("视觉 / 色板", "Visual / Palette"));
        DrawSectionButton(Section.Gameplay, DevToolUiSettings.T("玩法", "Gameplay"));
        DrawSectionButton(Section.Terrain, DevToolUiSettings.T("地形", "Terrain"));
        DrawSectionButton(Section.Templates, DevToolUiSettings.T("模板", "Templates"));
        DrawSectionButton(Section.Effects, DevToolUiSettings.T("效果", "Effects"));

        if (section != Section.Effects || !snapshot.Available) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("添加效果", "ADD EFFECT"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(DevToolUiSettings.T("搜索##RoomEffectSearch", "Search##RoomEffectSearch"), ref effectSearch, 128);

        string[] available = snapshot.AvailableEffects ?? Array.Empty<string>();
        string[] categories = snapshot.AvailableEffectCategories ?? Array.Empty<string>();
        string lastCategory = null;
        int matches = 0;
        for (int i = 0; i < available.Length; i++)
        {
            string type = available[i];
            if (!Matches(type, effectSearch)) continue;
            matches++;

            string category = i < categories.Length ? categories[i] : string.Empty;
            if (!string.Equals(lastCategory, category, StringComparison.Ordinal))
            {
                lastCategory = category;
                if (!string.IsNullOrEmpty(category))
                    DrawEffectSourceHeader(category);
            }

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
            case Section.Terrain:
                DrawTerrain(snapshot);
                break;
            case Section.Templates:
                DrawTemplates(snapshot);
                break;
            case Section.Effects:
                DrawEffects(snapshot);
                break;
        }
    }

    private static void DrawEnvironment(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("雨", "RAIN"));
        DrawFloatInherited(snapshot, RoomSettingKeys.RainIntensity, DevToolUiSettings.T("降雨强度", "Rain Intensity"), snapshot.RainIntensity, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.RumbleIntensity, DevToolUiSettings.T("震动强度", "Rumble Intensity"), snapshot.RumbleIntensity, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.CeilingDrips, DevToolUiSettings.T("天花板滴水", "Ceiling Drips"), snapshot.CeilingDrips, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("水面波浪", "WATER WAVES"));
        DrawFloatInherited(snapshot, RoomSettingKeys.WaveSpeed, DevToolUiSettings.T("波速", "Wave Speed"), snapshot.WaveSpeed, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.WaveLength, DevToolUiSettings.T("波长", "Wave Length"), snapshot.WaveLength, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.WaveAmplitude, DevToolUiSettings.T("波幅", "Wave Amplitude"), snapshot.WaveAmplitude, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.SecondWaveLength, DevToolUiSettings.T("回卷长度", "Rollback Length"), snapshot.SecondWaveLength, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.SecondWaveAmplitude, DevToolUiSettings.T("回卷幅度", "Rollback Amplitude"), snapshot.SecondWaveAmplitude, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.WaterReflectionAlpha, DevToolUiSettings.T("水面亮度", "Water Light"), snapshot.WaterReflectionAlpha, 0f, 1f);
    }

    private static void DrawVisual(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("氛围", "ATMOSPHERE"));
        DrawFloatInherited(snapshot, RoomSettingKeys.Clouds, DevToolUiSettings.T("云层", "Clouds"), snapshot.Clouds, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.Grime, DevToolUiSettings.T("污垢", "Grime"), snapshot.Grime, 0f, 1f);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("主色板", "PALETTE"));
        DrawIntInherited(snapshot, RoomSettingKeys.Palette, DevToolUiSettings.T("色板", "Palette"), snapshot.Palette);
        DrawIntInherited(snapshot, RoomSettingKeys.EffectColorA, DevToolUiSettings.T("效果颜色 A", "Effect Color A"), snapshot.EffectColorA);
        DrawIntInherited(snapshot, RoomSettingKeys.EffectColorB, DevToolUiSettings.T("效果颜色 B", "Effect Color B"), snapshot.EffectColorB);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("渐变色板", "FADE PALETTE"));
        int fadePalette = Get(IntEdits, RoomSettingKeys.FadePalette, snapshot.HasFadePalette ? snapshot.FadePalette : -1);
        bool fadeChanged = ImGui.InputInt(DevToolUiSettings.T("渐变色板编号##RoomFadePalette", "Fade Palette##RoomFadePalette"), ref fadePalette, 1, 10);
        IntEdits[RoomSettingKeys.FadePalette] = fadePalette;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SendSetting(RoomSettingKeys.FadePalette, new EditorPropertyValue(EditorPropertyKind.Integer, integer: fadePalette));
        else if (!fadeChanged && !ImGui.IsItemActive())
            IntEdits[RoomSettingKeys.FadePalette] = snapshot.HasFadePalette ? snapshot.FadePalette : -1;

        if (snapshot.HasFadePalette)
            DrawScreenFades(snapshot.FadePaletteFades, false);
        else
            ImGui.TextDisabled(DevToolUiSettings.T("-1 / 负数表示不使用渐变色板。", "-1 / negative disables the fade palette."));
    }

    private static void DrawGameplay(EditorRoomSettingsSnapshot snapshot)
    {
        ImGui.TextDisabled(DevToolUiSettings.T("危险", "DANGER"));
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
        DrawInheritanceControl(snapshot, RoomSettingKeys.DangerType);

        bool script = snapshot.RoomSpecificScript;
        if (ImGui.Checkbox(DevToolUiSettings.T("房间专属脚本", "Room Specific Script"), ref script))
            SendSetting(RoomSettingKeys.RoomSpecificScript,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: script));

        bool wetTerrain = snapshot.WetTerrain;
        if (ImGui.Checkbox(DevToolUiSettings.T("湿润地形", "Wet Terrain"), ref wetTerrain))
            SendSetting(RoomSettingKeys.WetTerrain,
                new EditorPropertyValue(EditorPropertyKind.Boolean, boolean: wetTerrain));

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("随机物品", "RANDOM ITEMS"));
        DrawFloatInherited(snapshot, RoomSettingKeys.RandomItemDensity, DevToolUiSettings.T("物品密度", "Item Density"), snapshot.RandomItemDensity, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.RandomItemSpearChance, DevToolUiSettings.T("长矛概率", "Spear Chance"), snapshot.RandomItemSpearChance, 0f, 1f);
    }

    private static void DrawTerrain(EditorRoomSettingsSnapshot snapshot)
    {
        if (!snapshot.TerrainAvailable)
        {
            ImGui.TextDisabled(DevToolUiSettings.T(
                "当前房间没有 TerrainCurve；原版 Terrain 面板默认折叠，但参数仍可写入 RoomSettings。",
                "This room has no TerrainCurve; vanilla collapses the Terrain panel, but the RoomSettings values remain editable."));
            ImGui.Separator();
        }

        ImGui.TextDisabled(DevToolUiSettings.T("地形色板", "TERRAIN PALETTE"));
        DrawTerrainPalette(snapshot, false);
        DrawTerrainPalette(snapshot, true);

        if (snapshot.HasTerrainFadePalette)
            DrawScreenFades(snapshot.TerrainFadePaletteFades, true);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("地形参数", "TERRAIN PARAMETERS"));
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainLight, DevToolUiSettings.T("光照", "Light"), snapshot.TerrainLight, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainStainAmount, DevToolUiSettings.T("污渍量", "Stain Amount"), snapshot.TerrainStainAmount, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainStainBrightness, DevToolUiSettings.T("污渍亮度", "Stain Brightness"), snapshot.TerrainStainBrightness, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainStainHeight, DevToolUiSettings.T("污渍高度", "Stain Height"), snapshot.TerrainStainHeight, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainGooHeight, DevToolUiSettings.T("Goo 高度", "Goo Height"), snapshot.TerrainGooHeight, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainWaves, DevToolUiSettings.T("波浪", "Waves"), snapshot.TerrainWaves, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainEdgeRadius, DevToolUiSettings.T("边缘半径", "Edge Radius"), snapshot.TerrainEdgeRadius, 0f, TerrainCurve.edgeRadiusSliderMax);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainGrain, DevToolUiSettings.T("颗粒", "Grain"), snapshot.TerrainGrain, 0f, 1f);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainDepth, DevToolUiSettings.T("深度", "Depth"), snapshot.TerrainDepth, TerrainCurve.depthSliderMin, TerrainCurve.depthSliderMax);
        DrawFloatInherited(snapshot, RoomSettingKeys.TerrainSkyFade, DevToolUiSettings.T("天空淡出", "Sky Fade"), snapshot.TerrainSkyFade, 0f, 1f);
    }

    private static void DrawTerrainPalette(EditorRoomSettingsSnapshot snapshot, bool fade)
    {
        string current = fade
            ? (snapshot.HasTerrainFadePalette ? snapshot.TerrainFadePalette : "NO PALETTE")
            : snapshot.TerrainPalette;
        string label = fade
            ? DevToolUiSettings.T("地形渐变色板##TerrainFadePalette", "Terrain Fade Palette##TerrainFadePalette")
            : DevToolUiSettings.T("地形色板##TerrainPalette", "Terrain Palette##TerrainPalette");

        if (ImGui.BeginCombo(label, string.IsNullOrEmpty(current) ? "NO PALETTE" : current))
        {
            if (fade && ImGui.Selectable("NO PALETTE##TerrainFadeNone", !snapshot.HasTerrainFadePalette))
                SendSetting(RoomSettingKeys.TerrainFadePalette,
                    new EditorPropertyValue(EditorPropertyKind.String, text: "NO PALETTE"));

            string[] palettes = snapshot.TerrainPalettes ?? Array.Empty<string>();
            for (int i = 0; i < palettes.Length; i++)
            {
                string palette = palettes[i];
                bool selected = string.Equals(current, palette, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(palette + "##TerrainPalette" + fade + i, selected))
                {
                    SendSetting(fade ? RoomSettingKeys.TerrainFadePalette : RoomSettingKeys.TerrainPalette,
                        new EditorPropertyValue(EditorPropertyKind.String, text: palette));
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (!fade) DrawInheritanceControl(snapshot, RoomSettingKeys.TerrainPalette);
    }

    private static void DrawTemplates(EditorRoomSettingsSnapshot snapshot)
    {
        if (!snapshot.TemplateControlsAvailable)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前房间没有区域模板可用。", "No region templates are available for this room."));
            return;
        }

        ImGui.TextDisabled(DevToolUiSettings.T("继承自模板", "INHERIT FROM TEMPLATE"));
        string preview = string.Equals(snapshot.CurrentTemplate, "NONE", StringComparison.OrdinalIgnoreCase)
            ? "NONE"
            : snapshot.RegionName + " - " + snapshot.CurrentTemplate;

        if (ImGui.BeginCombo(DevToolUiSettings.T("当前模板##RoomTemplate", "Current Template##RoomTemplate"), preview))
        {
            bool none = string.Equals(snapshot.CurrentTemplate, "NONE", StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable("NONE##RoomTemplateNone", none))
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(RoomEditorCommandKind.SetTemplate, key: "NONE"));
            if (none) ImGui.SetItemDefaultFocus();

            string[] names = snapshot.TemplateNames ?? Array.Empty<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                bool selected = string.Equals(snapshot.CurrentTemplate, name, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(snapshot.RegionName + " - " + name + "##RoomTemplate" + i, selected))
                    RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(RoomEditorCommandKind.SetTemplate, key: name));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("保存为模板", "SAVE AS TEMPLATE"));
        ImGui.TextWrapped(DevToolUiSettings.T(
            "与原版一致：点击后会立即覆盖对应区域模板文件，并重置当前房间的本地 RoomSettings。",
            "Vanilla behavior: clicking immediately overwrites the selected region template file and resets the room's local RoomSettings."));

        string[] templates = snapshot.TemplateNames ?? Array.Empty<string>();
        for (int i = 0; i < templates.Length; i++)
        {
            string name = templates[i];
            if (ImGui.Button(DevToolUiSettings.T("写入 ", "Save to ") + snapshot.RegionName + " - " + name + "##SaveRoomTemplate" + i))
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(RoomEditorCommandKind.SaveAsTemplate, key: name));
        }
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
            else if (effect.OverWrite) header += DevToolUiSettings.T("  [覆盖模板]", "  [Overrides template]");

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

    private static void DrawScreenFades(float[] fades, bool terrain)
    {
        fades ??= Array.Empty<float>();
        for (int i = 0; i < fades.Length; i++)
        {
            string stateKey = (terrain ? "terrainFade:" : "fade:") + i;
            float value = Get(FloatEdits, stateKey, fades[i]);
            bool changed = ImGui.SliderFloat(
                DevToolUiSettings.T("屏幕 ", "Screen ") + i + "##" + stateKey,
                ref value,
                0f,
                1f,
                "%.3f");
            FloatEdits[stateKey] = value;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(
                    terrain ? RoomEditorCommandKind.SetTerrainPaletteFade : RoomEditorCommandKind.SetPaletteFade,
                    index: i,
                    value: new EditorPropertyValue(EditorPropertyKind.Float, x: value)));
            }
            else if (!changed && !ImGui.IsItemActive())
            {
                FloatEdits[stateKey] = fades[i];
            }
        }
    }

    private static void DrawEffectSourceHeader(string category)
    {
        ImGui.Spacing();
        ImGui.SetWindowFontScale(EffectSourceHeaderFontScale);

        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        const uint outline = 0xFF000000u;
        const float stroke = 2f;

        draw.AddText(pos + new Num.Vector2(-stroke, 0f), outline, category);
        draw.AddText(pos + new Num.Vector2(stroke, 0f), outline, category);
        draw.AddText(pos + new Num.Vector2(0f, -stroke), outline, category);
        draw.AddText(pos + new Num.Vector2(0f, stroke), outline, category);
        draw.AddText(pos + new Num.Vector2(-stroke, -stroke), outline, category);
        draw.AddText(pos + new Num.Vector2(stroke, -stroke), outline, category);
        draw.AddText(pos + new Num.Vector2(-stroke, stroke), outline, category);
        draw.AddText(pos + new Num.Vector2(stroke, stroke), outline, category);

        ImGui.TextColored(EffectSourceColor(category), category);
        ImGui.SetWindowFontScale(BrowserBodyFontScale);
        ImGui.Separator();
    }

    private static Num.Vector4 EffectSourceColor(string category)
    {
        if (category.IndexOf("DryCycle", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.36f, 0.72f, 1f, 1f);

        if (category.IndexOf("RegionKit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            category.StartsWith("RK", StringComparison.OrdinalIgnoreCase))
            return new Num.Vector4(1f, 0.70f, 0.34f, 1f);

        if (category.IndexOf("Vanilla", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.88f, 0.88f, 0.88f, 1f);

        return new Num.Vector4(0.78f, 0.72f, 1f, 1f);
    }

    private static string SectionName(Section value)
    {
        return value switch
        {
            Section.Environment => DevToolUiSettings.T("环境", "Environment"),
            Section.Visual => DevToolUiSettings.T("视觉 / 色板", "Visual / Palette"),
            Section.Gameplay => DevToolUiSettings.T("玩法", "Gameplay"),
            Section.Terrain => DevToolUiSettings.T("地形", "Terrain"),
            Section.Templates => DevToolUiSettings.T("模板", "Templates"),
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

    private static void DrawFloatInherited(
        EditorRoomSettingsSnapshot snapshot,
        string key,
        string label,
        float current,
        float min,
        float max)
    {
        DrawFloat(key, label, current, min, max);
        DrawInheritanceControl(snapshot, key);
    }

    private static void DrawIntInherited(
        EditorRoomSettingsSnapshot snapshot,
        string key,
        string label,
        int current)
    {
        DrawInt(key, label, current);
        DrawInheritanceControl(snapshot, key);
    }

    private static void DrawInheritanceControl(EditorRoomSettingsSnapshot snapshot, string key)
    {
        ImGui.SameLine();
        if (snapshot.IsLocal(key))
        {
            if (ImGui.SmallButton(DevToolUiSettings.T("继承##", "Inherit##") + key))
                RoomEditorCommandQueue.Enqueue(new RoomEditorCommand(RoomEditorCommandKind.ResetSetting, key: key));
        }
        else
        {
            ImGui.TextDisabled(snapshot.InheritedFromTemplate(key) ? "<T>" : "<A>");
        }
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
