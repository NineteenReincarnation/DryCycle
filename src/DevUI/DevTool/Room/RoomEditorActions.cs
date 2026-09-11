using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Room;

public static class RoomSettingKeys
{
    public const string DangerType = "dangerType";
    public const string RainIntensity = "rainIntensity";
    public const string RumbleIntensity = "rumbleIntensity";
    public const string CeilingDrips = "ceilingDrips";
    public const string WaveSpeed = "waveSpeed";
    public const string WaveLength = "waveLength";
    public const string WaveAmplitude = "waveAmplitude";
    public const string SecondWaveLength = "secondWaveLength";
    public const string SecondWaveAmplitude = "secondWaveAmplitude";
    public const string Clouds = "clouds";
    public const string Grime = "grime";
    public const string RandomItemDensity = "randomItemDensity";
    public const string RandomItemSpearChance = "randomItemSpearChance";
    public const string WaterReflectionAlpha = "waterReflectionAlpha";
    public const string Palette = "palette";
    public const string EffectColorA = "effectColorA";
    public const string EffectColorB = "effectColorB";
    public const string RoomSpecificScript = "roomSpecificScript";
    public const string WetTerrain = "wetTerrain";
}

internal static class RoomEditorActions
{
    internal static bool SetRoomSetting(EditorSession session, string key, EditorPropertyValue value)
    {
        if (session?.RoomSettings == null || string.IsNullOrEmpty(key)) return false;

        return Mutate(session, "Change " + key, settings =>
        {
            switch (key)
            {
                case RoomSettingKeys.DangerType:
                    if (value.Kind != EditorPropertyKind.String || string.IsNullOrEmpty(value.Text)) return false;
                    settings.DangerType = new RoomRain.DangerType(value.Text, false);
                    return true;
                case RoomSettingKeys.RainIntensity:
                    return SetFloat(value, v => settings.RainIntensity = v);
                case RoomSettingKeys.RumbleIntensity:
                    return SetFloat(value, v => settings.RumbleIntensity = v);
                case RoomSettingKeys.CeilingDrips:
                    return SetFloat(value, v => settings.CeilingDrips = v);
                case RoomSettingKeys.WaveSpeed:
                    return SetFloat(value, v => settings.WaveSpeed = v);
                case RoomSettingKeys.WaveLength:
                    return SetFloat(value, v => settings.WaveLength = v);
                case RoomSettingKeys.WaveAmplitude:
                    return SetFloat(value, v => settings.WaveAmplitude = v);
                case RoomSettingKeys.SecondWaveLength:
                    return SetFloat(value, v => settings.SecondWaveLength = v);
                case RoomSettingKeys.SecondWaveAmplitude:
                    return SetFloat(value, v => settings.SecondWaveAmplitude = v);
                case RoomSettingKeys.Clouds:
                    return SetFloat(value, v => settings.Clouds = v);
                case RoomSettingKeys.Grime:
                    return SetFloat(value, v => settings.Grime = v);
                case RoomSettingKeys.RandomItemDensity:
                    return SetFloat(value, v => settings.RandomItemDensity = v);
                case RoomSettingKeys.RandomItemSpearChance:
                    return SetFloat(value, v => settings.RandomItemSpearChance = v);
                case RoomSettingKeys.WaterReflectionAlpha:
                    return SetFloat(value, v => settings.WaterReflectionAlpha = v);
                case RoomSettingKeys.Palette:
                    return SetInt(value, v => settings.Palette = v);
                case RoomSettingKeys.EffectColorA:
                    return SetInt(value, v => settings.EffectColorA = v);
                case RoomSettingKeys.EffectColorB:
                    return SetInt(value, v => settings.EffectColorB = v);
                case RoomSettingKeys.RoomSpecificScript:
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    settings.roomSpecificScript = value.Boolean;
                    return true;
                case RoomSettingKeys.WetTerrain:
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    settings.wetTerrain = value.Boolean;
                    return true;
                default:
                    return false;
            }
        });
    }

    internal static bool AddRoomEffect(EditorSession session, string typeName)
    {
        if (session?.Owner == null || string.IsNullOrEmpty(typeName)) return false;
        if (session.ToolMode != EditorToolMode.Room) session.SetToolMode(EditorToolMode.Room);
        if (session.Owner.activePage is not RoomSettingsPage page) return false;

        return Mutate(session, "Add effect " + typeName, _ =>
        {
            page.Signal(DevUISignalType.Create, page, typeName);
            return true;
        }, refreshPage: false);
    }

    internal static bool DeleteRoomEffect(EditorSession session, int index)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings?.effects == null || index < 0 || index >= settings.effects.Count) return false;
        RoomSettings.RoomEffect effect = settings.effects[index];
        if (effect == null || effect.inherited) return false;

        string type = effect.type?.value ?? "effect";
        return Mutate(session, "Delete effect " + type, current =>
        {
            if (index < 0 || index >= current.effects.Count || !ReferenceEquals(current.effects[index], effect))
            {
                index = current.effects.IndexOf(effect);
                if (index < 0) return false;
            }
            current.effects.RemoveAt(index);
            return true;
        });
    }

    internal static bool SetRoomEffectAmount(EditorSession session, int effectIndex, int sliderIndex, float value)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings?.effects == null || effectIndex < 0 || effectIndex >= settings.effects.Count) return false;
        RoomSettings.RoomEffect effect = settings.effects[effectIndex];
        if (effect == null || effect.inherited) return false;

        int sliderCount = Math.Max(1, RoomSettings.RoomEffect.GetSliderCount(effect.type));
        if (sliderIndex < 0 || sliderIndex >= sliderCount) return false;

        return Mutate(session, "Change " + (effect.type?.value ?? "effect"), _ =>
        {
            if (sliderIndex == 0)
            {
                effect.amount = value;
                return true;
            }

            int extraIndex = sliderIndex - 1;
            if (effect.extraAmounts == null || extraIndex < 0 || extraIndex >= effect.extraAmounts.Length)
                return false;
            effect.extraAmounts[extraIndex] = value;
            return true;
        });
    }

    private static bool Mutate(
        EditorSession session,
        string label,
        Func<RoomSettings, bool> mutation,
        bool refreshPage = true)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || mutation == null) return false;

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(settings);
        if (before == null || !mutation(settings)) return false;

        if (refreshPage)
        {
            try { session.Owner?.activePage?.Refresh(); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool room page refresh failed: " + error.Message);
            }
        }

        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(settings);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static bool SetFloat(EditorPropertyValue value, Action<float> setter)
    {
        if (value.Kind != EditorPropertyKind.Float || setter == null) return false;
        setter(value.X);
        return true;
    }

    private static bool SetInt(EditorPropertyValue value, Action<int> setter)
    {
        if (value.Kind != EditorPropertyKind.Integer || setter == null) return false;
        setter(value.Integer);
        return true;
    }
}
