using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Room;

public sealed class EditorRoomEffectSnapshot
{
    public int Index { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Inherited { get; init; }
    public bool Save { get; init; }
    public string[] SliderNames { get; init; } = Array.Empty<string>();
    public float[] Values { get; init; } = Array.Empty<float>();
}

public sealed class EditorRoomSettingsSnapshot
{
    public static readonly EditorRoomSettingsSnapshot Empty = new();

    public bool Available { get; init; }
    public string DangerType { get; init; } = string.Empty;
    public string[] DangerTypes { get; init; } = Array.Empty<string>();

    public float RainIntensity { get; init; }
    public float RumbleIntensity { get; init; }
    public float CeilingDrips { get; init; }
    public float WaveSpeed { get; init; }
    public float WaveLength { get; init; }
    public float WaveAmplitude { get; init; }
    public float SecondWaveLength { get; init; }
    public float SecondWaveAmplitude { get; init; }
    public float Clouds { get; init; }
    public float Grime { get; init; }
    public float RandomItemDensity { get; init; }
    public float RandomItemSpearChance { get; init; }
    public float WaterReflectionAlpha { get; init; }

    public int Palette { get; init; }
    public int EffectColorA { get; init; }
    public int EffectColorB { get; init; }
    public bool RoomSpecificScript { get; init; }
    public bool WetTerrain { get; init; }

    public EditorRoomEffectSnapshot[] Effects { get; init; } = Array.Empty<EditorRoomEffectSnapshot>();
    public string[] AvailableEffects { get; init; } = Array.Empty<string>();
}

internal static class RoomSettingsPresentation
{
    internal static EditorRoomSettingsSnapshot Capture(EditorSession session)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null) return EditorRoomSettingsSnapshot.Empty;

        return new EditorRoomSettingsSnapshot
        {
            Available = true,
            DangerType = settings.DangerType?.value ?? string.Empty,
            DangerTypes = CopyEnumValues<RoomRain.DangerType>(),
            RainIntensity = settings.RainIntensity,
            RumbleIntensity = settings.RumbleIntensity,
            CeilingDrips = settings.CeilingDrips,
            WaveSpeed = settings.WaveSpeed,
            WaveLength = settings.WaveLength,
            WaveAmplitude = settings.WaveAmplitude,
            SecondWaveLength = settings.SecondWaveLength,
            SecondWaveAmplitude = settings.SecondWaveAmplitude,
            Clouds = settings.Clouds,
            Grime = settings.Grime,
            RandomItemDensity = settings.RandomItemDensity,
            RandomItemSpearChance = settings.RandomItemSpearChance,
            WaterReflectionAlpha = settings.WaterReflectionAlpha,
            Palette = settings.Palette,
            EffectColorA = settings.EffectColorA,
            EffectColorB = settings.EffectColorB,
            RoomSpecificScript = settings.roomSpecificScript,
            WetTerrain = settings.wetTerrain,
            Effects = CaptureEffects(session, settings),
            AvailableEffects = CopyEffectTypes()
        };
    }

    private static EditorRoomEffectSnapshot[] CaptureEffects(EditorSession session, RoomSettings settings)
    {
        if (settings.effects == null || settings.effects.Count == 0)
            return Array.Empty<EditorRoomEffectSnapshot>();

        RoomSettingsPage page = session?.Owner?.activePage as RoomSettingsPage;
        EditorRoomEffectSnapshot[] result = new EditorRoomEffectSnapshot[settings.effects.Count];
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = settings.effects[i];
            if (effect == null)
            {
                result[i] = new EditorRoomEffectSnapshot { Index = i, Type = "<null>" };
                continue;
            }

            int count = Math.Max(1, RoomSettings.RoomEffect.GetSliderCount(effect.type));
            string[] names = new string[count];
            float[] values = new float[count];
            for (int slider = 0; slider < count; slider++)
            {
                names[slider] = RoomSettings.RoomEffect.GetSliderName(effect.type, slider) ?? ("Value " + (slider + 1));
                values[slider] = effect.GetAmount(slider);
            }

            string category = string.Empty;
            try { category = page?.DevEffectGetCategoryFromEffectType(effect.type)?.value ?? string.Empty; }
            catch { }

            result[i] = new EditorRoomEffectSnapshot
            {
                Index = i,
                Type = effect.type?.value ?? string.Empty,
                Category = category,
                Inherited = effect.inherited,
                Save = effect.save,
                SliderNames = names,
                Values = values
            };
        }
        return result;
    }

    private static string[] CopyEffectTypes()
    {
        List<string> result = new();
        List<string> entries = ExtEnum<RoomSettings.RoomEffect.Type>.values.entries;
        for (int i = 0; i < entries.Count; i++)
        {
            string value = entries[i];
            if (string.IsNullOrEmpty(value) || string.Equals(value, RoomSettings.RoomEffect.Type.None.value, StringComparison.Ordinal))
                continue;
            result.Add(value);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result.ToArray();
    }

    private static string[] CopyEnumValues<T>() where T : ExtEnum<T>
    {
        List<string> entries = ExtEnum<T>.values.entries;
        string[] result = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) result[i] = entries[i];
        return result;
    }
}
