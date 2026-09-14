using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Preview;

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
    private static string[] dangerTypesCache = Array.Empty<string>();
    private static int dangerTypesCount = -1;

    private static string[] terrainPalettesCache = Array.Empty<string>();
    private static bool terrainPalettesCached;

    private static string[] availableEffectsCache = Array.Empty<string>();
    private static string[] availableEffectCategoriesCache = Array.Empty<string>();
    private static int availableEffectTypeCount = -1;
    private static Type availableEffectPageType;

    /// <summary>
    /// Warms data that depends only on the installed mod/asset set. This is called during normal
    /// mods initialization so the first O/H DevTool frame does not pay a cold filesystem scan.
    /// Failed early asset access is intentionally not cached and will retry when the UI needs it.
    /// </summary>
    internal static void WarmStaticCatalogs()
    {
        GetDangerTypes();
        GetTerrainPalettes();
    }

    internal static EditorRoomSettingsSnapshot Capture(EditorSession session) =>
        CaptureFull(session);

    /// <summary>
    /// Rebuilds only the immutable Room payloads named by a trusted semantic hint. Every scalar is
    /// cheap to read, but arrays/catalogs/effect descriptors are deliberately retained by reference
    /// unless the edit can affect them. Unknown writers never call this path; the hub falls back to
    /// CaptureFull when no reliable hint is available.
    /// </summary>
    internal static EditorRoomSettingsSnapshot Capture(
        EditorSession session,
        EditorRoomSettingsSnapshot previous,
        RoomPresentationChangeHint hint)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null) return EditorRoomSettingsSnapshot.Empty;
        if (previous?.Available != true || hint.Full || !hint.HasChanges)
            return CaptureFull(session);

        bool readValues = hint.Values;

        bool templatesAvailable = previous.TemplateControlsAvailable;
        string regionName = previous.RegionName;
        string currentTemplate = previous.CurrentTemplate;
        string[] templateNames = previous.TemplateNames ?? Array.Empty<string>();

        string[] localKeys = previous.LocalSettingKeys ?? Array.Empty<string>();
        string[] templateKeys = previous.TemplateOverrideKeys ?? Array.Empty<string>();
        if (hint.Overrides)
            CaptureOverrideState(settings, out localKeys, out templateKeys);

        EditorRoomEffectSnapshot[] effects;
        if (hint.Effects)
        {
            effects = CaptureEffects(session, settings);
        }
        else if (hint.EffectRowIndex >= 0)
        {
            effects = CaptureEffectRowPatch(session, settings, previous.Effects, hint.EffectRowIndex);
        }
        else
        {
            effects = previous.Effects ?? Array.Empty<EditorRoomEffectSnapshot>();
        }

        float[] fadePaletteFades = hint.FadePalette
            ? CopyFades(settings.fadePalette?.fades)
            : previous.FadePaletteFades ?? Array.Empty<float>();
        float[] terrainFadePaletteFades = hint.TerrainFadePalette
            ? CopyFades(settings.terrainFadePalette?.fades)
            : previous.TerrainFadePaletteFades ?? Array.Empty<float>();

        return new EditorRoomSettingsSnapshot
        {
            Available = true,
            DangerType = readValues ? settings.DangerType?.value ?? string.Empty : previous.DangerType,
            DangerTypes = previous.DangerTypes ?? GetDangerTypes(),
            RainIntensity = readValues ? settings.RainIntensity : previous.RainIntensity,
            RumbleIntensity = readValues ? settings.RumbleIntensity : previous.RumbleIntensity,
            CeilingDrips = readValues ? settings.CeilingDrips : previous.CeilingDrips,
            WaveSpeed = readValues ? settings.WaveSpeed : previous.WaveSpeed,
            WaveLength = readValues ? settings.WaveLength : previous.WaveLength,
            WaveAmplitude = readValues ? settings.WaveAmplitude : previous.WaveAmplitude,
            SecondWaveLength = readValues ? settings.SecondWaveLength : previous.SecondWaveLength,
            SecondWaveAmplitude = readValues ? settings.SecondWaveAmplitude : previous.SecondWaveAmplitude,
            Clouds = readValues ? settings.Clouds : previous.Clouds,
            Grime = readValues ? settings.Grime : previous.Grime,
            RandomItemDensity = readValues ? settings.RandomItemDensity : previous.RandomItemDensity,
            RandomItemSpearChance = readValues ? settings.RandomItemSpearChance : previous.RandomItemSpearChance,
            WaterReflectionAlpha = readValues ? settings.WaterReflectionAlpha : previous.WaterReflectionAlpha,
            Palette = readValues ? settings.Palette : previous.Palette,
            EffectColorA = readValues ? settings.EffectColorA : previous.EffectColorA,
            EffectColorB = readValues ? settings.EffectColorB : previous.EffectColorB,
            HasFadePalette = hint.FadePalette ? settings.fadePalette != null : previous.HasFadePalette,
            FadePalette = hint.FadePalette ? settings.fadePalette?.palette ?? -1 : previous.FadePalette,
            FadePaletteFades = fadePaletteFades,
            RoomSpecificScript = readValues ? settings.roomSpecificScript : previous.RoomSpecificScript,
            WetTerrain = readValues ? settings.wetTerrain : previous.WetTerrain,
            TerrainAvailable = previous.TerrainAvailable,
            TerrainLight = readValues ? settings.TerrainLight : previous.TerrainLight,
            TerrainStainAmount = readValues ? settings.TerrainStainAmount : previous.TerrainStainAmount,
            TerrainStainBrightness = readValues ? settings.TerrainStainBrightness : previous.TerrainStainBrightness,
            TerrainStainHeight = readValues ? settings.TerrainStainHeight : previous.TerrainStainHeight,
            TerrainWaves = readValues ? settings.TerrainWaves : previous.TerrainWaves,
            TerrainEdgeRadius = readValues ? settings.TerrainEdgeRadius : previous.TerrainEdgeRadius,
            TerrainGooHeight = readValues ? settings.TerrainGooHeight : previous.TerrainGooHeight,
            TerrainGrain = readValues ? settings.TerrainGrain : previous.TerrainGrain,
            TerrainDepth = readValues ? settings.TerrainDepth : previous.TerrainDepth,
            TerrainSkyFade = readValues ? settings.TerrainSkyFade : previous.TerrainSkyFade,
            TerrainPalette = readValues ? settings.TerrainPalette ?? string.Empty : previous.TerrainPalette,
            TerrainPalettes = previous.TerrainPalettes ?? GetTerrainPalettes(),
            HasTerrainFadePalette = hint.TerrainFadePalette ? settings.terrainFadePalette != null : previous.HasTerrainFadePalette,
            TerrainFadePalette = hint.TerrainFadePalette ? settings.terrainFadePalette?.palette ?? string.Empty : previous.TerrainFadePalette,
            TerrainFadePaletteFades = terrainFadePaletteFades,
            CameraCount = previous.CameraCount,
            TemplateControlsAvailable = templatesAvailable,
            RegionName = regionName,
            CurrentTemplate = currentTemplate,
            TemplateNames = templateNames,
            LocalSettingKeys = localKeys,
            TemplateOverrideKeys = templateKeys,
            Effects = effects,
            AvailableEffects = previous.AvailableEffects ?? Array.Empty<string>(),
            AvailableEffectCategories = previous.AvailableEffectCategories ?? Array.Empty<string>()
        };
    }

    private static EditorRoomSettingsSnapshot CaptureFull(EditorSession session)
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
            DangerTypes = GetDangerTypes(),
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
            TerrainPalettes = GetTerrainPalettes(),
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
        List<EditorRoomEffectSnapshot> result = new(settings.effects.Count);
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = settings.effects[i];
            if (EffectPreviewRuntime.IsPreviewEffect(effect))
                continue;

            int logicalIndex = result.Count;
            result.Add(CaptureEffect(page, effect, logicalIndex));
        }
        return result.ToArray();
    }

    private static EditorRoomEffectSnapshot[] CaptureEffectRowPatch(
        EditorSession session,
        RoomSettings settings,
        EditorRoomEffectSnapshot[] previous,
        int dirtyLogicalIndex)
    {
        if (previous == null || dirtyLogicalIndex < 0 || settings?.effects == null)
            return CaptureEffects(session, settings);

        RoomSettings.RoomEffect dirty = null;
        int logicalCount = 0;
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = settings.effects[i];
            if (EffectPreviewRuntime.IsPreviewEffect(effect))
                continue;

            if (logicalCount == dirtyLogicalIndex)
                dirty = effect;
            logicalCount++;
        }

        // A row-level hint is valid only while collection shape is unchanged. Any add/remove or
        // preview mismatch safely widens to the normal Effects capture instead of patching by index.
        if (logicalCount != previous.Length || dirtyLogicalIndex >= logicalCount)
            return CaptureEffects(session, settings);

        EditorRoomEffectSnapshot[] next = (EditorRoomEffectSnapshot[])previous.Clone();
        RoomSettingsPage page = session?.Owner?.activePage as RoomSettingsPage;
        next[dirtyLogicalIndex] = CaptureEffect(page, dirty, dirtyLogicalIndex);
        return next;
    }

    private static EditorRoomEffectSnapshot CaptureEffect(
        RoomSettingsPage page,
        RoomSettings.RoomEffect effect,
        int logicalIndex)
    {
        if (effect == null)
            return new EditorRoomEffectSnapshot { Index = logicalIndex, Type = "<null>" };

        int count = Math.Max(1, RoomSettings.RoomEffect.GetSliderCount(effect.type));
        string[] names = new string[count];
        float[] values = new float[count];
        for (int slider = 0; slider < count; slider++)
        {
            names[slider] = RoomSettings.RoomEffect.GetSliderName(effect.type, slider) ?? ("Value " + (slider + 1));
            values[slider] = effect.GetAmount(slider);
        }

        return new EditorRoomEffectSnapshot
        {
            Index = logicalIndex,
            Type = effect.type?.value ?? string.Empty,
            Category = EffectCategory(page, effect.type),
            Inherited = effect.inherited,
            OverWrite = effect.overWrite,
            Save = effect.save,
            SliderNames = names,
            Values = values
        };
    }

    private static void CaptureAvailableEffects(
        EditorSession session,
        out string[] types,
        out string[] categories)
    {
        RoomSettingsPage page = session?.Owner?.activePage as RoomSettingsPage;
        Type pageType = page?.GetType();
        List<string> entries = ExtEnum<RoomSettings.RoomEffect.Type>.values.entries;
        int typeCount = entries.Count;

        if (availableEffectTypeCount == typeCount &&
            availableEffectPageType == pageType &&
            availableEffectsCache.Length > 0)
        {
            types = availableEffectsCache;
            categories = availableEffectCategoriesCache;
            return;
        }

        List<(string Type, string Category)> result = new();
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

        string[] builtTypes = new string[result.Count];
        string[] builtCategories = new string[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            builtTypes[i] = result[i].Type;
            builtCategories[i] = result[i].Category;
        }

        availableEffectsCache = builtTypes;
        availableEffectCategoriesCache = builtCategories;
        availableEffectTypeCount = typeCount;
        availableEffectPageType = pageType;
        types = builtTypes;
        categories = builtCategories;
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

    private static string[] GetTerrainPalettes()
    {
        if (terrainPalettesCached) return terrainPalettesCache;

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
            terrainPalettesCache = result.ToArray();
            terrainPalettesCached = true;
            return terrainPalettesCache;
        }
        catch
        {
            // AssetManager may not be ready during an unusually early warm-up. Do not poison the
            // cache in that case; the first real Room snapshot will retry normally.
            return Array.Empty<string>();
        }
    }

    private static string[] GetDangerTypes()
    {
        List<string> entries = ExtEnum<RoomRain.DangerType>.values.entries;
        if (dangerTypesCount == entries.Count && dangerTypesCache.Length == entries.Count)
            return dangerTypesCache;

        string[] result = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) result[i] = entries[i];
        dangerTypesCache = result;
        dangerTypesCount = entries.Count;
        return dangerTypesCache;
    }

    private static float[] CopyFades(float[] source) =>
        source == null ? Array.Empty<float>() : (float[])source.Clone();
}
