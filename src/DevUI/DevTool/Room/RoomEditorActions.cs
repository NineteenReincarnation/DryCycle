using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

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
    public const string FadePalette = "fadePalette";
    public const string RoomSpecificScript = "roomSpecificScript";
    public const string WetTerrain = "wetTerrain";
    public const string TerrainLight = "terrainLight";
    public const string TerrainStainAmount = "terrainStainAmount";
    public const string TerrainStainBrightness = "terrainStainBrightness";
    public const string TerrainStainHeight = "terrainStainHeight";
    public const string TerrainWaves = "terrainWaves";
    public const string TerrainEdgeRadius = "terrainEdgeRadius";
    public const string TerrainGooHeight = "terrainGooHeight";
    public const string TerrainGrain = "terrainGrain";
    public const string TerrainDepth = "terrainDepth";
    public const string TerrainSkyFade = "terrainSkyFade";
    public const string TerrainPalette = "terrainPalette";
    public const string TerrainFadePalette = "terrainFadePalette";
}

internal static class RoomEditorActions
{
    internal static bool SetRoomSetting(EditorSession session, string key, EditorPropertyValue value)
    {
        if (session?.RoomSettings == null || string.IsNullOrEmpty(key)) return false;

        bool changed = Mutate(session, "Change " + key, settings =>
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
                case RoomSettingKeys.FadePalette:
                    return SetFadePalette(session, settings, value);
                case RoomSettingKeys.RoomSpecificScript:
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    settings.roomSpecificScript = value.Boolean;
                    return true;
                case RoomSettingKeys.WetTerrain:
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    settings.wetTerrain = value.Boolean;
                    return true;
                case RoomSettingKeys.TerrainLight:
                    return SetFloat(value, v => settings.TerrainLight = v);
                case RoomSettingKeys.TerrainStainAmount:
                    return SetFloat(value, v => settings.TerrainStainAmount = v);
                case RoomSettingKeys.TerrainStainBrightness:
                    return SetFloat(value, v => settings.TerrainStainBrightness = v);
                case RoomSettingKeys.TerrainStainHeight:
                    return SetFloat(value, v => settings.TerrainStainHeight = v);
                case RoomSettingKeys.TerrainWaves:
                    return SetFloat(value, v => settings.TerrainWaves = v);
                case RoomSettingKeys.TerrainEdgeRadius:
                    return SetFloat(value, v => settings.TerrainEdgeRadius = v);
                case RoomSettingKeys.TerrainGooHeight:
                    return SetFloat(value, v => settings.TerrainGooHeight = v);
                case RoomSettingKeys.TerrainGrain:
                    return SetFloat(value, v => settings.TerrainGrain = v);
                case RoomSettingKeys.TerrainDepth:
                    return SetFloat(value, v => settings.TerrainDepth = v);
                case RoomSettingKeys.TerrainSkyFade:
                    return SetFloat(value, v => settings.TerrainSkyFade = v);
                case RoomSettingKeys.TerrainPalette:
                    if (value.Kind != EditorPropertyKind.String || string.IsNullOrEmpty(value.Text)) return false;
                    settings.TerrainPalette = value.Text;
                    return true;
                case RoomSettingKeys.TerrainFadePalette:
                    return SetTerrainFadePalette(session, settings, value);
                default:
                    return false;
            }
        });

        if (changed) ApplyLiveSideEffect(session, key);
        return changed;
    }

    internal static bool ResetRoomSetting(EditorSession session, string key)
    {
        if (session?.RoomSettings == null || string.IsNullOrEmpty(key)) return false;

        bool changed = Mutate(session, "Inherit " + key, settings =>
        {
            switch (key)
            {
                case RoomSettingKeys.DangerType: settings.dType = null; return true;
                case RoomSettingKeys.RainIntensity: settings.rInts = null; return true;
                case RoomSettingKeys.RumbleIntensity: settings.rumInts = null; return true;
                case RoomSettingKeys.CeilingDrips: settings.cDrips = null; return true;
                case RoomSettingKeys.WaveSpeed: settings.wSpeed = null; return true;
                case RoomSettingKeys.WaveLength: settings.wLength = null; return true;
                case RoomSettingKeys.WaveAmplitude: settings.wAmp = null; return true;
                case RoomSettingKeys.SecondWaveLength: settings.swLength = null; return true;
                case RoomSettingKeys.SecondWaveAmplitude: settings.swAmp = null; return true;
                case RoomSettingKeys.Clouds: settings.clds = null; return true;
                case RoomSettingKeys.Grime: settings.grm = null; return true;
                case RoomSettingKeys.RandomItemDensity: settings.rndItmDns = null; return true;
                case RoomSettingKeys.RandomItemSpearChance: settings.rndItmSprChnc = null; return true;
                case RoomSettingKeys.WaterReflectionAlpha: settings.wtrRflctAlpha = null; return true;
                case RoomSettingKeys.Palette: settings.pal = null; return true;
                case RoomSettingKeys.EffectColorA: settings.eColA = null; return true;
                case RoomSettingKeys.EffectColorB: settings.eColB = null; return true;
                case RoomSettingKeys.TerrainLight: settings.terrainLight = null; return true;
                case RoomSettingKeys.TerrainStainAmount: settings.terrainStainAmount = null; return true;
                case RoomSettingKeys.TerrainStainBrightness: settings.terrainStainBrightness = null; return true;
                case RoomSettingKeys.TerrainStainHeight: settings.terrainStainHeight = null; return true;
                case RoomSettingKeys.TerrainWaves: settings.terrainWaves = null; return true;
                case RoomSettingKeys.TerrainEdgeRadius: settings.terrainEdgeRadius = null; return true;
                case RoomSettingKeys.TerrainGooHeight: settings.terrainGooHeight = null; return true;
                case RoomSettingKeys.TerrainGrain: settings.terrainGrain = null; return true;
                case RoomSettingKeys.TerrainDepth: settings.terrainDepth = null; return true;
                case RoomSettingKeys.TerrainSkyFade: settings.terrainSkyFade = null; return true;
                case RoomSettingKeys.TerrainPalette: settings.terrainPalette = null; return true;
                default: return false;
            }
        });

        if (changed) ApplyLiveSideEffect(session, key);
        return changed;
    }

    internal static bool SetPaletteFade(EditorSession session, int cameraIndex, float value)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings?.fadePalette?.fades == null || cameraIndex < 0 || cameraIndex >= settings.fadePalette.fades.Length)
            return false;

        bool changed = Mutate(session, "Change palette fade", _ =>
        {
            settings.fadePalette.fades[cameraIndex] = Mathf.Clamp01(value);
            return true;
        });
        if (changed) RefreshFadePalette(session, cameraIndex);
        return changed;
    }

    internal static bool SetTerrainPaletteFade(EditorSession session, int cameraIndex, float value)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings?.terrainFadePalette?.fades == null || cameraIndex < 0 || cameraIndex >= settings.terrainFadePalette.fades.Length)
            return false;

        bool changed = Mutate(session, "Change terrain palette fade", _ =>
        {
            settings.terrainFadePalette.fades[cameraIndex] = Mathf.Clamp01(value);
            return true;
        });
        if (changed) RefreshTerrainFade(session, cameraIndex);
        return changed;
    }

    internal static bool SetRoomTemplate(EditorSession session, string templateName)
    {
        Region region = session?.Owner?.room?.world?.region;
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || region == null || string.IsNullOrEmpty(templateName)) return false;

        string before = CurrentTemplateName(settings, region);
        if (string.Equals(before, templateName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!ApplyTemplate(session, templateName)) return false;

        string after = CurrentTemplateName(settings, region);
        session.History.Push(new DelegateHistoryEntry(
            "Change room template",
            current => ApplyTemplate(current, before),
            current => ApplyTemplate(current, after)));
        return true;
    }

    internal static bool SaveRoomAsTemplate(EditorSession session, string templateName)
    {
        Region region = session?.Owner?.room?.world?.region;
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || region == null || string.IsNullOrEmpty(templateName) ||
            string.Equals(templateName, "NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            settings.SaveAsTemplate(TemplateButtonText(region, templateName), region);
            session.Owner?.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool save room template failed: " + error.Message);
            return false;
        }
    }

    internal static bool AddRoomEffect(EditorSession session, string typeName)
    {
        if (session?.Owner == null || session.RoomSettings?.effects == null || string.IsNullOrEmpty(typeName)) return false;
        if (session.ToolMode != EditorToolMode.Room) session.SetToolMode(EditorToolMode.Room);
        if (session.Owner.activePage is not RoomSettingsPage page) return false;

        RoomSettings.RoomEffect.Type type = new(typeName, false);
        for (int i = 0; i < session.RoomSettings.effects.Count; i++)
        {
            RoomSettings.RoomEffect existing = session.RoomSettings.effects[i];
            if (existing != null && !existing.inherited && existing.type == type)
                return true;
        }

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
            current.RemoveEffect(effect.type);
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

        bool changed = Mutate(session, "Change " + (effect.type?.value ?? "effect"), _ =>
        {
            if (sliderIndex == 0)
            {
                effect.amount = Mathf.Clamp01(value);
                return true;
            }

            int extraIndex = sliderIndex - 1;
            if (effect.extraAmounts == null || extraIndex < 0 || extraIndex >= effect.extraAmounts.Length)
                return false;
            effect.extraAmounts[extraIndex] = Mathf.Clamp01(value);
            return true;
        });

        if (changed) ApplyEffectLiveSideEffect(session, effect, sliderIndex);
        return changed;
    }

    private static bool SetFadePalette(EditorSession session, RoomSettings settings, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Integer) return false;
        int palette = value.Integer;
        if (palette < 0)
        {
            settings.fadePalette = null;
            return true;
        }

        int screens = Math.Max(1, session?.Owner?.room?.cameraPositions?.Length ?? 1);
        if (settings.fadePalette == null)
            settings.fadePalette = new RoomSettings.FadePalette(palette, screens);
        else
            settings.fadePalette.palette = palette;
        return true;
    }

    private static bool SetTerrainFadePalette(EditorSession session, RoomSettings settings, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.String) return false;
        string palette = value.Text;
        if (string.IsNullOrEmpty(palette) || string.Equals(palette, "NO PALETTE", StringComparison.OrdinalIgnoreCase))
        {
            settings.terrainFadePalette = null;
            return true;
        }

        int screens = Math.Max(1, session?.Owner?.room?.cameraPositions?.Length ?? 1);
        if (settings.terrainFadePalette == null)
            settings.terrainFadePalette = new RoomSettings.TerrainFadePalette(palette, screens);
        else
            settings.terrainFadePalette.palette = palette;
        return true;
    }

    private static void ApplyLiveSideEffect(EditorSession session, string key)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null) return;

        try
        {
            if (key == RoomSettingKeys.Grime)
            {
                Shader.SetGlobalFloat(RainWorld.ShadPropGrime, settings.Grime);
                return;
            }

            RoomCamera camera = PrimaryCamera(session);
            if (camera == null) return;

            if (key == RoomSettingKeys.Palette)
                camera.ChangeMainPalette(settings.Palette);
            else if (key == RoomSettingKeys.EffectColorA || key == RoomSettingKeys.EffectColorB)
                camera.ApplyEffectColorsToAllPaletteTextures(settings.EffectColorA, settings.EffectColorB);
            else if (key == RoomSettingKeys.FadePalette)
                RefreshFadePalette(session, camera.currentCameraPosition);
            else if (key == RoomSettingKeys.TerrainPalette)
                camera.ReloadTerrainPalette();
            else if (key == RoomSettingKeys.TerrainFadePalette)
                camera.ChangeTerrainPaletteFade();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool room live refresh failed: " + error.Message);
        }
    }

    private static void ApplyEffectLiveSideEffect(EditorSession session, RoomSettings.RoomEffect effect, int sliderIndex)
    {
        if (effect == null) return;
        try
        {
            RoomCamera camera = PrimaryCamera(session);
            if (camera == null) return;

            if (sliderIndex == 0 && effect.type == RoomSettings.RoomEffect.Type.VoidMelt)
            {
                camera.levelGraphic.alpha = effect.amount;
                if (camera.fullScreenEffect != null) camera.fullScreenEffect.alpha = effect.amount;
            }
            else if (sliderIndex == 0 && effect.type == RoomSettings.RoomEffect.Type.HeatWave)
            {
                camera.levelGraphic.alpha = effect.amount / 2f;
            }

            if (effect.type == RoomSettings.RoomEffect.Type.ModifyEffectColorA ||
                effect.type == RoomSettings.RoomEffect.Type.ModifyEffectColorB)
            {
                RoomSettings settings = session.RoomSettings;
                camera.ApplyEffectColorsToAllPaletteTextures(settings.EffectColorA, settings.EffectColorB);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool effect live refresh failed: " + error.Message);
        }
    }

    private static void RefreshFadePalette(EditorSession session, int cameraIndex)
    {
        try
        {
            RoomSettings settings = session?.RoomSettings;
            RoomCamera camera = PrimaryCamera(session);
            if (settings == null || camera == null) return;
            if (settings.fadePalette == null)
            {
                camera.ChangeFadePalette(-1, 0f);
                return;
            }

            float[] fades = settings.fadePalette.fades;
            int index = cameraIndex >= 0 && fades != null && cameraIndex < fades.Length
                ? cameraIndex
                : camera.currentCameraPosition;
            float fade = fades != null && index >= 0 && index < fades.Length ? fades[index] : 0f;
            camera.ChangeFadePalette(settings.fadePalette.palette, fade);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool fade palette refresh failed: " + error.Message);
        }
    }

    private static void RefreshTerrainFade(EditorSession session, int cameraIndex)
    {
        try
        {
            RoomCamera camera = PrimaryCamera(session);
            if (camera == null || camera.currentCameraPosition != cameraIndex) return;
            camera.ChangeTerrainPaletteFade();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool terrain fade refresh failed: " + error.Message);
        }
    }

    private static RoomCamera PrimaryCamera(EditorSession session)
    {
        try
        {
            RoomCamera[] cameras = session?.Owner?.room?.game?.cameras;
            return cameras != null && cameras.Length > 0 ? cameras[0] : null;
        }
        catch { return null; }
    }

    private static bool ApplyTemplate(EditorSession session, string templateName)
    {
        Region region = session?.Owner?.room?.world?.region;
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || region == null || string.IsNullOrEmpty(templateName)) return false;

        try
        {
            settings.SetTemplate(TemplateButtonText(region, templateName), region);
            session.Owner?.activePage?.Refresh();
            ApplyLiveSideEffect(session, RoomSettingKeys.Palette);
            ApplyLiveSideEffect(session, RoomSettingKeys.EffectColorA);
            ApplyLiveSideEffect(session, RoomSettingKeys.TerrainPalette);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool room template change failed: " + error.Message);
            return false;
        }
    }

    private static string CurrentTemplateName(RoomSettings settings, Region region)
    {
        if (settings?.parent == null || settings.parent.isAncestor || region?.roomSettingsTemplates == null)
            return "NONE";

        string[] names = region.roomSettingTemplateNames ?? Array.Empty<string>();
        int count = Math.Min(names.Length, region.roomSettingsTemplates.Length);
        for (int i = 0; i < count; i++)
            if (ReferenceEquals(settings.parent, region.roomSettingsTemplates[i])) return names[i] ?? "NONE";
        return "NONE";
    }

    private static string TemplateButtonText(Region region, string templateName) =>
        string.Equals(templateName, "NONE", StringComparison.OrdinalIgnoreCase)
            ? "NONE"
            : (region?.name ?? string.Empty) + " - " + templateName;

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
