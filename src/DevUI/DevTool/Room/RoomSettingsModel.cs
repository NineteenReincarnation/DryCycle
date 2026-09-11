using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Room;

public sealed class EditorRoomEffectSnapshot
{
    public int Index { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Inherited { get; init; }
    public bool OverWrite { get; init; }
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
    public bool HasFadePalette { get; init; }
    public int FadePalette { get; init; } = -1;
    public float[] FadePaletteFades { get; init; } = Array.Empty<float>();

    public bool RoomSpecificScript { get; init; }
    public bool WetTerrain { get; init; }

    public bool TerrainAvailable { get; init; }
    public float TerrainLight { get; init; }
    public float TerrainStainAmount { get; init; }
    public float TerrainStainBrightness { get; init; }
    public float TerrainStainHeight { get; init; }
    public float TerrainWaves { get; init; }
    public float TerrainEdgeRadius { get; init; }
    public float TerrainGooHeight { get; init; }
    public float TerrainGrain { get; init; }
    public float TerrainDepth { get; init; }
    public float TerrainSkyFade { get; init; }
    public string TerrainPalette { get; init; } = string.Empty;
    public string[] TerrainPalettes { get; init; } = Array.Empty<string>();
    public bool HasTerrainFadePalette { get; init; }
    public string TerrainFadePalette { get; init; } = string.Empty;
    public float[] TerrainFadePaletteFades { get; init; } = Array.Empty<float>();

    public int CameraCount { get; init; }

    public bool TemplateControlsAvailable { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public string CurrentTemplate { get; init; } = "NONE";
    public string[] TemplateNames { get; init; } = Array.Empty<string>();

    public string[] LocalSettingKeys { get; init; } = Array.Empty<string>();
    public string[] TemplateOverrideKeys { get; init; } = Array.Empty<string>();

    public EditorRoomEffectSnapshot[] Effects { get; init; } = Array.Empty<EditorRoomEffectSnapshot>();
    public string[] AvailableEffects { get; init; } = Array.Empty<string>();
    public string[] AvailableEffectCategories { get; init; } = Array.Empty<string>();

    public bool IsLocal(string key) => Contains(LocalSettingKeys, key);

    public bool InheritedFromTemplate(string key) =>
        !IsLocal(key) && Contains(TemplateOverrideKeys, key);

    private static bool Contains(string[] values, string key)
    {
        if (values == null || string.IsNullOrEmpty(key)) return false;
        for (int i = 0; i < values.Length; i++)
            if (string.Equals(values[i], key, StringComparison.Ordinal)) return true;
        return false;
    }
}

internal static class RoomSettingsPresentation
{
    internal static EditorRoomSettingsSnapshot Capture(EditorSession session)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null) return EditorRoomSettingsSnapshot.Empty;

        int cameraCount = session?.Owner?.room?.cameraPositions?.Length ?? 0;
        CaptureTemplateInfo(session, settings, out bool templatesAvailable, out string regionName,
            out string currentTemplate, out string[] templateNames);
        CaptureOverrideState(settings, out string[] localKeys, out string[] templateKeys);
        CaptureAvailableEffects(session, out string[] availableEffects, out string[] effectCategories);

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
            HasFadePalette = settings.fadePalette != null,
            FadePalette = settings.fadePalette?.palette ?? -1,
            FadePaletteFades = CopyFades(settings.fadePalette?.fades),
            RoomSpecificScript = settings.roomSpecificScript,
            WetTerrain = settings.wetTerrain,
            TerrainAvailable = session?.Owner?.room?.terrain != null,
            TerrainLight = settings.TerrainLight,
            TerrainStainAmount = settings.TerrainStainAmount,
            TerrainStainBrightness = settings.TerrainStainBrightness,
            TerrainStainHeight = settings.TerrainStainHeight,
            TerrainWaves = settings.TerrainWaves,
            TerrainEdgeRadius = settings.TerrainEdgeRadius,
            TerrainGooHeight = settings.TerrainGooHeight,
            TerrainGrain = settings.TerrainGrain,
            TerrainDepth = settings.TerrainDepth,
            TerrainSkyFade = settings.TerrainSkyFade,
            TerrainPalette = settings.TerrainPalette ?? string.Empty,
            TerrainPalettes = CopyTerrainPalettes(),
            HasTerrainFadePalette = settings.terrainFadePalette != null,
            TerrainFadePalette = settings.terrainFadePalette?.palette ?? string.Empty,
            TerrainFadePaletteFades = CopyFades(settings.terrainFadePalette?.fades),
            CameraCount = cameraCount,
            TemplateControlsAvailable = templatesAvailable,
            RegionName = regionName,
            CurrentTemplate = currentTemplate,
            TemplateNames = templateNames,
            LocalSettingKeys = localKeys,
            TemplateOverrideKeys = templateKeys,
            Effects = CaptureEffects(session, settings),
            AvailableEffects = availableEffects,
            AvailableEffectCategories = effectCategories
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

            result[i] = new EditorRoomEffectSnapshot
            {
                Index = i,
                Type = effect.type?.value ?? string.Empty,
                Category = EffectCategory(page, effect.type),
                Inherited = effect.inherited,
                OverWrite = effect.overWrite,
                Save = effect.save,
                SliderNames = names,
                Values = values
            };
        }
        return result;
    }

    private static void CaptureAvailableEffects(
        EditorSession session,
        out string[] types,
        out string[] categories)
    {
        RoomSettingsPage page = session?.Owner?.activePage as RoomSettingsPage;
        List<(string Type, string Category)> result = new();
        List<string> entries = ExtEnum<RoomSettings.RoomEffect.Type>.values.entries;
        for (int i = 0; i < entries.Count; i++)
        {
            string value = entries[i];
            if (string.IsNullOrEmpty(value) ||
                string.Equals(value, RoomSettings.RoomEffect.Type.None.value, StringComparison.Ordinal))
                continue;

            RoomSettings.RoomEffect.Type type = new(value, false);
            result.Add((value, EffectCategory(page, type)));
        }

        result.Sort((a, b) =>
        {
            int category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return category != 0 ? category : string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
        });

        types = new string[result.Count];
        categories = new string[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            types[i] = result[i].Type;
            categories[i] = result[i].Category;
        }
    }

    private static string EffectCategory(RoomSettingsPage page, RoomSettings.RoomEffect.Type type)
    {
        if (type == null) return string.Empty;
        try { return page?.DevEffectGetCategoryFromEffectType(type)?.value ?? "Unsorted"; }
        catch { return "Unsorted"; }
    }

    private static void CaptureTemplateInfo(
        EditorSession session,
        RoomSettings settings,
        out bool available,
        out string regionName,
        out string current,
        out string[] names)
    {
        Region region = session?.Owner?.room?.world?.region;
        available = region != null;
        regionName = region?.name ?? string.Empty;
        names = region?.roomSettingTemplateNames == null
            ? Array.Empty<string>()
            : (string[])region.roomSettingTemplateNames.Clone();
        current = "NONE";

        if (region?.roomSettingsTemplates == null || settings.parent == null || settings.parent.isAncestor)
            return;

        int count = Math.Min(region.roomSettingsTemplates.Length, names.Length);
        for (int i = 0; i < count; i++)
        {
            if (!ReferenceEquals(region.roomSettingsTemplates[i], settings.parent)) continue;
            current = names[i] ?? "NONE";
            return;
        }

        current = settings.parent.name ?? "NONE";
    }

    private static void CaptureOverrideState(
        RoomSettings settings,
        out string[] localKeys,
        out string[] templateKeys)
    {
        List<string> local = new();
        List<string> template = new();
        RoomSettings parent = settings.parent;

        AddOverride(local, template, RoomSettingKeys.DangerType, settings.dType != null, ParentLocal(parent, RoomSettingKeys.DangerType));
        AddOverride(local, template, RoomSettingKeys.RainIntensity, settings.rInts.HasValue, ParentLocal(parent, RoomSettingKeys.RainIntensity));
        AddOverride(local, template, RoomSettingKeys.RumbleIntensity, settings.rumInts.HasValue, ParentLocal(parent, RoomSettingKeys.RumbleIntensity));
        AddOverride(local, template, RoomSettingKeys.CeilingDrips, settings.cDrips.HasValue, ParentLocal(parent, RoomSettingKeys.CeilingDrips));
        AddOverride(local, template, RoomSettingKeys.WaveSpeed, settings.wSpeed.HasValue, ParentLocal(parent, RoomSettingKeys.WaveSpeed));
        AddOverride(local, template, RoomSettingKeys.WaveLength, settings.wLength.HasValue, ParentLocal(parent, RoomSettingKeys.WaveLength));
        AddOverride(local, template, RoomSettingKeys.WaveAmplitude, settings.wAmp.HasValue, ParentLocal(parent, RoomSettingKeys.WaveAmplitude));
        AddOverride(local, template, RoomSettingKeys.SecondWaveLength, settings.swLength.HasValue, ParentLocal(parent, RoomSettingKeys.SecondWaveLength));
        AddOverride(local, template, RoomSettingKeys.SecondWaveAmplitude, settings.swAmp.HasValue, ParentLocal(parent, RoomSettingKeys.SecondWaveAmplitude));
        AddOverride(local, template, RoomSettingKeys.Clouds, settings.clds.HasValue, ParentLocal(parent, RoomSettingKeys.Clouds));
        AddOverride(local, template, RoomSettingKeys.Grime, settings.grm.HasValue, ParentLocal(parent, RoomSettingKeys.Grime));
        AddOverride(local, template, RoomSettingKeys.RandomItemDensity, settings.rndItmDns.HasValue, ParentLocal(parent, RoomSettingKeys.RandomItemDensity));
        AddOverride(local, template, RoomSettingKeys.RandomItemSpearChance, settings.rndItmSprChnc.HasValue, ParentLocal(parent, RoomSettingKeys.RandomItemSpearChance));
        AddOverride(local, template, RoomSettingKeys.WaterReflectionAlpha, settings.wtrRflctAlpha.HasValue, ParentLocal(parent, RoomSettingKeys.WaterReflectionAlpha));
        AddOverride(local, template, RoomSettingKeys.Palette, settings.pal.HasValue, ParentLocal(parent, RoomSettingKeys.Palette));
        AddOverride(local, template, RoomSettingKeys.EffectColorA, settings.eColA.HasValue, ParentLocal(parent, RoomSettingKeys.EffectColorA));
        AddOverride(local, template, RoomSettingKeys.EffectColorB, settings.eColB.HasValue, ParentLocal(parent, RoomSettingKeys.EffectColorB));
        AddOverride(local, template, RoomSettingKeys.TerrainLight, settings.terrainLight.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainLight));
        AddOverride(local, template, RoomSettingKeys.TerrainStainAmount, settings.terrainStainAmount.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainStainAmount));
        AddOverride(local, template, RoomSettingKeys.TerrainStainBrightness, settings.terrainStainBrightness.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainStainBrightness));
        AddOverride(local, template, RoomSettingKeys.TerrainStainHeight, settings.terrainStainHeight.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainStainHeight));
        AddOverride(local, template, RoomSettingKeys.TerrainWaves, settings.terrainWaves.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainWaves));
        AddOverride(local, template, RoomSettingKeys.TerrainEdgeRadius, settings.terrainEdgeRadius.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainEdgeRadius));
        AddOverride(local, template, RoomSettingKeys.TerrainGooHeight, settings.terrainGooHeight.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainGooHeight));
        AddOverride(local, template, RoomSettingKeys.TerrainGrain, settings.terrainGrain.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainGrain));
        AddOverride(local, template, RoomSettingKeys.TerrainDepth, settings.terrainDepth.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainDepth));
        AddOverride(local, template, RoomSettingKeys.TerrainSkyFade, settings.terrainSkyFade.HasValue, ParentLocal(parent, RoomSettingKeys.TerrainSkyFade));
        AddOverride(local, template, RoomSettingKeys.TerrainPalette, settings.terrainPalette != null, ParentLocal(parent, RoomSettingKeys.TerrainPalette));

        localKeys = local.ToArray();
        templateKeys = template.ToArray();
    }

    private static void AddOverride(
        List<string> local,
        List<string> template,
        string key,
        bool isLocal,
        bool parentLocal)
    {
        if (isLocal) local.Add(key);
        else if (parentLocal) template.Add(key);
    }

    private static bool ParentLocal(RoomSettings parent, string key)
    {
        if (parent == null || parent.isAncestor) return false;
        return key switch
        {
            RoomSettingKeys.DangerType => parent.dType != null,
            RoomSettingKeys.RainIntensity => parent.rInts.HasValue,
            RoomSettingKeys.RumbleIntensity => parent.rumInts.HasValue,
            RoomSettingKeys.CeilingDrips => parent.cDrips.HasValue,
            RoomSettingKeys.WaveSpeed => parent.wSpeed.HasValue,
            RoomSettingKeys.WaveLength => parent.wLength.HasValue,
            RoomSettingKeys.WaveAmplitude => parent.wAmp.HasValue,
            RoomSettingKeys.SecondWaveLength => parent.swLength.HasValue,
            RoomSettingKeys.SecondWaveAmplitude => parent.swAmp.HasValue,
            RoomSettingKeys.Clouds => parent.clds.HasValue,
            RoomSettingKeys.Grime => parent.grm.HasValue,
            RoomSettingKeys.RandomItemDensity => parent.rndItmDns.HasValue,
            RoomSettingKeys.RandomItemSpearChance => parent.rndItmSprChnc.HasValue,
            RoomSettingKeys.WaterReflectionAlpha => parent.wtrRflctAlpha.HasValue,
            RoomSettingKeys.Palette => parent.pal.HasValue,
            RoomSettingKeys.EffectColorA => parent.eColA.HasValue,
            RoomSettingKeys.EffectColorB => parent.eColB.HasValue,
            RoomSettingKeys.TerrainLight => parent.terrainLight.HasValue,
            RoomSettingKeys.TerrainStainAmount => parent.terrainStainAmount.HasValue,
            RoomSettingKeys.TerrainStainBrightness => parent.terrainStainBrightness.HasValue,
            RoomSettingKeys.TerrainStainHeight => parent.terrainStainHeight.HasValue,
            RoomSettingKeys.TerrainWaves => parent.terrainWaves.HasValue,
            RoomSettingKeys.TerrainEdgeRadius => parent.terrainEdgeRadius.HasValue,
            RoomSettingKeys.TerrainGooHeight => parent.terrainGooHeight.HasValue,
            RoomSettingKeys.TerrainGrain => parent.terrainGrain.HasValue,
            RoomSettingKeys.TerrainDepth => parent.terrainDepth.HasValue,
            RoomSettingKeys.TerrainSkyFade => parent.terrainSkyFade.HasValue,
            RoomSettingKeys.TerrainPalette => parent.terrainPalette != null,
            _ => false
        };
    }

    private static string[] CopyTerrainPalettes()
    {
        try
        {
            string[] files = AssetManager.ListDirectory("terrainpalettes");
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            List<string> result = new();
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                if (string.IsNullOrEmpty(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name) || !names.Add(name)) continue;
                result.Add(name);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static float[] CopyFades(float[] source) =>
        source == null ? Array.Empty<float>() : (float[])source.Clone();

    private static string[] CopyEnumValues<T>() where T : ExtEnum<T>
    {
        List<string> entries = ExtEnum<T>.values.entries;
        string[] result = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) result[i] = entries[i];
        return result;
    }
}
