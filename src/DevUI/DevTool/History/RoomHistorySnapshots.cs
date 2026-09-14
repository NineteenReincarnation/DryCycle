using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Room;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// In-memory history for one ordinary RoomSettings value. The snapshot stores the local backing
/// field rather than the resolved property value, so template inheritance (null vs local override)
/// survives Undo/Redo without serializing the complete RoomSettings document.
/// </summary>
internal sealed class RoomSettingStateSnapshot : IEditorStateSnapshot
{
    private enum ValueKind
    {
        DangerType,
        Float,
        Integer,
        Boolean,
        Text
    }

    private readonly RoomSettings settings;
    private readonly string key;
    private readonly ValueKind kind;
    private readonly RoomRain.DangerType dangerType;
    private readonly float? floatValue;
    private readonly int? intValue;
    private readonly bool boolValue;
    private readonly string textValue;

    private RoomSettingStateSnapshot(
        RoomSettings settings,
        string key,
        ValueKind kind,
        RoomRain.DangerType dangerType = null,
        float? floatValue = null,
        int? intValue = null,
        bool boolValue = false,
        string textValue = null)
    {
        this.settings = settings;
        this.key = key ?? string.Empty;
        this.kind = kind;
        this.dangerType = dangerType;
        this.floatValue = floatValue;
        this.intValue = intValue;
        this.boolValue = boolValue;
        this.textValue = textValue;
        Fingerprint = BuildFingerprint();
    }

    public string Kind =>
        "RoomSetting:" + (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings)) + ":" + key;

    public string Fingerprint { get; }

    internal static RoomSettingStateSnapshot Capture(RoomSettings settings, string key)
    {
        if (settings == null || string.IsNullOrEmpty(key)) return null;

        return key switch
        {
            RoomSettingKeys.DangerType => new RoomSettingStateSnapshot(settings, key, ValueKind.DangerType, dangerType: settings.dType),
            RoomSettingKeys.RainIntensity => Float(settings, key, settings.rInts),
            RoomSettingKeys.RumbleIntensity => Float(settings, key, settings.rumInts),
            RoomSettingKeys.CeilingDrips => Float(settings, key, settings.cDrips),
            RoomSettingKeys.WaveSpeed => Float(settings, key, settings.wSpeed),
            RoomSettingKeys.WaveLength => Float(settings, key, settings.wLength),
            RoomSettingKeys.WaveAmplitude => Float(settings, key, settings.wAmp),
            RoomSettingKeys.SecondWaveLength => Float(settings, key, settings.swLength),
            RoomSettingKeys.SecondWaveAmplitude => Float(settings, key, settings.swAmp),
            RoomSettingKeys.Clouds => Float(settings, key, settings.clds),
            RoomSettingKeys.Grime => Float(settings, key, settings.grm),
            RoomSettingKeys.RandomItemDensity => Float(settings, key, settings.rndItmDns),
            RoomSettingKeys.RandomItemSpearChance => Float(settings, key, settings.rndItmSprChnc),
            RoomSettingKeys.WaterReflectionAlpha => Float(settings, key, settings.wtrRflctAlpha),
            RoomSettingKeys.Palette => Integer(settings, key, settings.pal),
            RoomSettingKeys.EffectColorA => Integer(settings, key, settings.eColA),
            RoomSettingKeys.EffectColorB => Integer(settings, key, settings.eColB),
            RoomSettingKeys.RoomSpecificScript => new RoomSettingStateSnapshot(settings, key, ValueKind.Boolean, boolValue: settings.roomSpecificScript),
            RoomSettingKeys.WetTerrain => new RoomSettingStateSnapshot(settings, key, ValueKind.Boolean, boolValue: settings.wetTerrain),
            RoomSettingKeys.TerrainLight => Float(settings, key, settings.terrainLight),
            RoomSettingKeys.TerrainStainAmount => Float(settings, key, settings.terrainStainAmount),
            RoomSettingKeys.TerrainStainBrightness => Float(settings, key, settings.terrainStainBrightness),
            RoomSettingKeys.TerrainStainHeight => Float(settings, key, settings.terrainStainHeight),
            RoomSettingKeys.TerrainWaves => Float(settings, key, settings.terrainWaves),
            RoomSettingKeys.TerrainEdgeRadius => Float(settings, key, settings.terrainEdgeRadius),
            RoomSettingKeys.TerrainGooHeight => Float(settings, key, settings.terrainGooHeight),
            RoomSettingKeys.TerrainGrain => Float(settings, key, settings.terrainGrain),
            RoomSettingKeys.TerrainDepth => Float(settings, key, settings.terrainDepth),
            RoomSettingKeys.TerrainSkyFade => Float(settings, key, settings.terrainSkyFade),
            RoomSettingKeys.TerrainPalette => new RoomSettingStateSnapshot(settings, key, ValueKind.Text, textValue: settings.terrainPalette),
            _ => null
        };
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings, key) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings)) return false;

        switch (key)
        {
            case RoomSettingKeys.DangerType: settings.dType = dangerType; break;
            case RoomSettingKeys.RainIntensity: settings.rInts = floatValue; break;
            case RoomSettingKeys.RumbleIntensity: settings.rumInts = floatValue; break;
            case RoomSettingKeys.CeilingDrips: settings.cDrips = floatValue; break;
            case RoomSettingKeys.WaveSpeed: settings.wSpeed = floatValue; break;
            case RoomSettingKeys.WaveLength: settings.wLength = floatValue; break;
            case RoomSettingKeys.WaveAmplitude: settings.wAmp = floatValue; break;
            case RoomSettingKeys.SecondWaveLength: settings.swLength = floatValue; break;
            case RoomSettingKeys.SecondWaveAmplitude: settings.swAmp = floatValue; break;
            case RoomSettingKeys.Clouds: settings.clds = floatValue; break;
            case RoomSettingKeys.Grime: settings.grm = floatValue; break;
            case RoomSettingKeys.RandomItemDensity: settings.rndItmDns = floatValue; break;
            case RoomSettingKeys.RandomItemSpearChance: settings.rndItmSprChnc = floatValue; break;
            case RoomSettingKeys.WaterReflectionAlpha: settings.wtrRflctAlpha = floatValue; break;
            case RoomSettingKeys.Palette: settings.pal = intValue; break;
            case RoomSettingKeys.EffectColorA: settings.eColA = intValue; break;
            case RoomSettingKeys.EffectColorB: settings.eColB = intValue; break;
            case RoomSettingKeys.RoomSpecificScript: settings.roomSpecificScript = boolValue; break;
            case RoomSettingKeys.WetTerrain: settings.wetTerrain = boolValue; break;
            case RoomSettingKeys.TerrainLight: settings.terrainLight = floatValue; break;
            case RoomSettingKeys.TerrainStainAmount: settings.terrainStainAmount = floatValue; break;
            case RoomSettingKeys.TerrainStainBrightness: settings.terrainStainBrightness = floatValue; break;
            case RoomSettingKeys.TerrainStainHeight: settings.terrainStainHeight = floatValue; break;
            case RoomSettingKeys.TerrainWaves: settings.terrainWaves = floatValue; break;
            case RoomSettingKeys.TerrainEdgeRadius: settings.terrainEdgeRadius = floatValue; break;
            case RoomSettingKeys.TerrainGooHeight: settings.terrainGooHeight = floatValue; break;
            case RoomSettingKeys.TerrainGrain: settings.terrainGrain = floatValue; break;
            case RoomSettingKeys.TerrainDepth: settings.terrainDepth = floatValue; break;
            case RoomSettingKeys.TerrainSkyFade: settings.terrainSkyFade = floatValue; break;
            case RoomSettingKeys.TerrainPalette: settings.terrainPalette = textValue; break;
            default: return false;
        }

        RoomPresentationChangeHintHub.MarkSetting(session, key);
        RoomEditorActions.ApplyLiveSideEffect(session, key);
        RoomEditorActions.RefreshLegacyPageOrDefer(session);
        return true;
    }

    private string BuildFingerprint()
    {
        return kind switch
        {
            ValueKind.DangerType => dangerType?.value ?? "<inherit>",
            ValueKind.Float => floatValue.HasValue
                ? floatValue.Value.ToString("R", CultureInfo.InvariantCulture)
                : "<inherit>",
            ValueKind.Integer => intValue.HasValue
                ? intValue.Value.ToString(CultureInfo.InvariantCulture)
                : "<inherit>",
            ValueKind.Boolean => boolValue ? "1" : "0",
            ValueKind.Text => textValue ?? "<inherit>",
            _ => string.Empty
        };
    }

    private static RoomSettingStateSnapshot Float(RoomSettings settings, string key, float? value) =>
        new(settings, key, ValueKind.Float, floatValue: value);

    private static RoomSettingStateSnapshot Integer(RoomSettings settings, string key, int? value) =>
        new(settings, key, ValueKind.Integer, intValue: value);
}

/// <summary>
/// Snapshot for the two palette-fade objects. Their arrays are tiny (camera count) and are copied in
/// memory so palette creation/removal and per-camera fade edits stay independent from RoomSettings I/O.
/// </summary>
internal sealed class RoomPaletteFadeStateSnapshot : IEditorStateSnapshot
{
    private readonly RoomSettings settings;
    private readonly bool terrain;
    private readonly bool present;
    private readonly int palette;
    private readonly string terrainPalette;
    private readonly float[] fades;

    private RoomPaletteFadeStateSnapshot(
        RoomSettings settings,
        bool terrain,
        bool present,
        int palette,
        string terrainPalette,
        float[] fades)
    {
        this.settings = settings;
        this.terrain = terrain;
        this.present = present;
        this.palette = palette;
        this.terrainPalette = terrainPalette;
        this.fades = fades ?? Array.Empty<float>();
        Fingerprint = BuildFingerprint();
    }

    public string Kind =>
        (terrain ? "TerrainFadePalette:" : "FadePalette:") +
        (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings));

    public string Fingerprint { get; }

    internal static RoomPaletteFadeStateSnapshot Capture(RoomSettings settings, bool terrain)
    {
        if (settings == null) return null;

        if (terrain)
        {
            RoomSettings.TerrainFadePalette value = settings.terrainFadePalette;
            return new RoomPaletteFadeStateSnapshot(
                settings,
                true,
                value != null,
                -1,
                value?.palette,
                value?.fades == null ? Array.Empty<float>() : (float[])value.fades.Clone());
        }

        RoomSettings.FadePalette fade = settings.fadePalette;
        return new RoomPaletteFadeStateSnapshot(
            settings,
            false,
            fade != null,
            fade?.palette ?? -1,
            null,
            fade?.fades == null ? Array.Empty<float>() : (float[])fade.fades.Clone());
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings, terrain) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings)) return false;

        if (terrain)
        {
            if (!present)
            {
                settings.terrainFadePalette = null;
            }
            else
            {
                RoomSettings.TerrainFadePalette value =
                    new(terrainPalette ?? string.Empty, Math.Max(1, fades.Length));
                CopyFades(fades, value.fades);
                settings.terrainFadePalette = value;
            }

            RoomPresentationChangeHintHub.MarkPaletteFade(session, terrain: true);
            RoomEditorActions.ApplyLiveSideEffect(session, RoomSettingKeys.TerrainFadePalette);
        }
        else
        {
            if (!present)
            {
                settings.fadePalette = null;
            }
            else
            {
                RoomSettings.FadePalette value = new(palette, Math.Max(1, fades.Length));
                CopyFades(fades, value.fades);
                settings.fadePalette = value;
            }

            RoomPresentationChangeHintHub.MarkPaletteFade(session, terrain: false);
            RoomEditorActions.ApplyLiveSideEffect(session, RoomSettingKeys.FadePalette);
        }

        RoomEditorActions.RefreshLegacyPageOrDefer(session);
        return true;
    }

    private string BuildFingerprint()
    {
        if (!present) return "0";

        string result = terrain
            ? "1|" + (terrainPalette ?? string.Empty)
            : "1|" + palette.ToString(CultureInfo.InvariantCulture);

        for (int i = 0; i < fades.Length; i++)
            result += "|" + fades[i].ToString("R", CultureInfo.InvariantCulture);
        return result;
    }

    private static void CopyFades(float[] source, float[] destination)
    {
        if (source == null || destination == null) return;
        Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
    }
}

/// <summary>
/// History unit for one RoomEffect amount vector. Collection membership changes use the separate
/// collection snapshot because vanilla effect add/remove can replace inherited rows.
/// </summary>
internal sealed class SingleRoomEffectStateSnapshot : IEditorStateSnapshot
{
    private readonly RoomSettings settings;
    private readonly RoomSettings.RoomEffect target;
    private readonly int index;
    private readonly bool present;
    private readonly bool inherited;
    private readonly bool overWrite;
    private readonly bool save;
    private readonly float amount;
    private readonly float[] extraAmounts;

    private SingleRoomEffectStateSnapshot(
        RoomSettings settings,
        RoomSettings.RoomEffect target,
        int index,
        bool present,
        bool inherited,
        bool overWrite,
        bool save,
        float amount,
        float[] extraAmounts)
    {
        this.settings = settings;
        this.target = target;
        this.index = index;
        this.present = present;
        this.inherited = inherited;
        this.overWrite = overWrite;
        this.save = save;
        this.amount = amount;
        this.extraAmounts = extraAmounts ?? Array.Empty<float>();
        Fingerprint = BuildFingerprint();
    }

    public string Kind =>
        "RoomEffect:" + (target == null ? 0 : RuntimeHelpers.GetHashCode(target));

    public string Fingerprint { get; }

    internal static SingleRoomEffectStateSnapshot Capture(RoomSettings settings, RoomSettings.RoomEffect target)
    {
        if (settings?.effects == null || target == null) return null;

        int index = settings.effects.IndexOf(target);
        bool present = index >= 0;
        return new SingleRoomEffectStateSnapshot(
            settings,
            target,
            index,
            present,
            target.inherited,
            target.overWrite,
            target.save,
            target.amount,
            target.extraAmounts == null ? Array.Empty<float>() : (float[])target.extraAmounts.Clone());
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings, target) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.effects == null || target == null)
            return false;

        if (!present)
        {
            settings.effects.Remove(target);
        }
        else
        {
            int currentIndex = settings.effects.IndexOf(target);
            if (currentIndex >= 0) settings.effects.RemoveAt(currentIndex);

            int insertIndex = Math.Max(0, Math.Min(index, settings.effects.Count));
            settings.effects.Insert(insertIndex, target);
            target.inherited = inherited;
            target.overWrite = overWrite;
            target.save = save;
            target.amount = amount;

            if (target.extraAmounts != null)
            {
                int count = Math.Min(target.extraAmounts.Length, extraAmounts.Length);
                for (int i = 0; i < count; i++) target.extraAmounts[i] = extraAmounts[i];
            }

            int sliderCount = Math.Max(1, RoomSettings.RoomEffect.GetSliderCount(target.type));
            for (int slider = 0; slider < sliderCount; slider++)
                RoomEditorActions.ApplyEffectLiveSideEffect(session, target, slider);
        }

        int logicalIndex = settings.effects.IndexOf(target);
        if (logicalIndex >= 0)
            RoomPresentationChangeHintHub.MarkEffectAmount(session, logicalIndex);
        else
            RoomPresentationChangeHintHub.MarkEffects(session);

        RoomEditorActions.RefreshLegacyPageOrDefer(session);
        RoomEffectLiveCompatibility.Reconcile(session);
        return true;
    }

    private string BuildFingerprint()
    {
        if (!present) return "0";

        string result = "1|" + index + "|" + (target.type?.value ?? string.Empty) + "|" +
                        (inherited ? "1" : "0") + "|" + (overWrite ? "1" : "0") + "|" +
                        (save ? "1" : "0") + "|" + amount.ToString("R", CultureInfo.InvariantCulture);
        for (int i = 0; i < extraAmounts.Length; i++)
            result += "|" + extraAmounts[i].ToString("R", CultureInfo.InvariantCulture);
        return result;
    }
}
