using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class PaletteLighting
{
    // Runtime RoomCamera.paletteTexture is 32x8. For the six authored terrain rows,
    // Unity texture Y runs bottom-up: shade occupies 0..2 and sunlit 3..5. Row 6 is
    // the rainbow/grime strip and row 7 contains sky/fog/water/control values.
    private const int MainColumns = 30;
    private const int ShadeRowStart = 0;
    private const int SunRowStart = 3;

    private static readonly HashSet<RoomCamera> PendingCameras = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RoomCamera.UpdateDayNightPalette += RoomCamera_UpdateDayNightPalette;
        On.RoomCamera.ApplyPalette += RoomCamera_ApplyPalette;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomCamera.UpdateDayNightPalette -= RoomCamera_UpdateDayNightPalette;
        On.RoomCamera.ApplyPalette -= RoomCamera_ApplyPalette;
        PendingCameras.Clear();
        _enabled = false;
    }

    private static void RoomCamera_UpdateDayNightPalette(
        On.RoomCamera.orig_UpdateDayNightPalette orig,
        RoomCamera self)
    {
        if (self?.room?.game == null || !WorldClockHooks.TryGetClock(self.room.game, out WorldClock clock))
        {
            orig(self);
            return;
        }

        // The original method swaps dusk/night palettes and therefore conflicts with
        // fade palettes and weather palettes. DryCycle rebuilds the room's authored
        // base/fade palette, then relights that result in place instead.
        self.dayNightNeedsRefresh = false;

        float influence = Mathf.Clamp01(self.effect_dayNight);
        if (influence <= 0f || self.paletteTexture == null)
        {
            return;
        }

        // Updating at 10 Hz is already much smoother than the visual rate at which a
        // many-minute solar cycle changes, while avoiding a full sprite palette pass
        // on every rendered frame.
        if (self.frameCount % 4 != 0)
        {
            return;
        }

        PendingCameras.Add(self);
        try
        {
            // ApplyFade reconstructs paletteTexture from fadeTexA/fadeTexB first, so
            // our lighting never compounds on the previous frame's transformed data.
            self.ApplyFade();
        }
        finally
        {
            PendingCameras.Remove(self);
        }
    }

    private static void RoomCamera_ApplyPalette(On.RoomCamera.orig_ApplyPalette orig, RoomCamera self)
    {
        if (PendingCameras.Contains(self)
            && self?.paletteTexture != null
            && self.room?.game != null
            && WorldClockHooks.TryGetClock(self.room.game, out WorldClock clock))
        {
            ApplyAdaptiveLighting(self.paletteTexture, clock.Lighting, Mathf.Clamp01(self.effect_dayNight));
            self.paletteTexture.Apply(false);
        }

        orig(self);
    }

    private static void ApplyAdaptiveLighting(
        Texture2D palette,
        SolarLightingState lighting,
        float influence)
    {
        if (influence <= 0f)
        {
            return;
        }

        // Use the palette's own sun/shade pairs as the region-specific material
        // response. At noon the authored sun rows remain dominant; as direct sunlight
        // fades, sun rows collapse toward their paired shade rows. This is what lets
        // one clock work on a desert, rainforest, snowfield, or monochrome palette
        // without hard-coded biome colors.
        for (int depth = 0; depth < MainColumns; depth++)
        {
            for (int tone = 0; tone < 3; tone++)
            {
                int shadeRow = ShadeRowStart + tone;
                int sunRow = SunRowStart + tone;

                Color baseShade = palette.GetPixel(depth, shadeRow);
                Color baseSun = palette.GetPixel(depth, sunRow);

                float surfaceGain = tone switch
                {
                    0 => 0.92f,
                    1 => 1f,
                    _ => 1.07f
                };

                Color shade = GradeColor(
                    baseShade,
                    Mathf.Lerp(1f, lighting.AmbientLight * surfaceGain, influence),
                    0f,
                    lighting.AmbientCoolness * influence,
                    Mathf.Lerp(1f, lighting.Saturation, influence),
                    lighting.NightFactor * 0.012f * influence);

                float directResponse = Mathf.Lerp(1f, lighting.DirectLight, influence);
                Color relitSunBase = Color.Lerp(baseShade, baseSun, directResponse);
                float sunExposure = Mathf.Lerp(
                    lighting.AmbientLight,
                    1.02f * surfaceGain,
                    lighting.DirectLight);

                Color sun = GradeColor(
                    relitSunBase,
                    Mathf.Lerp(1f, sunExposure, influence),
                    lighting.SunWarmth * influence,
                    lighting.AmbientCoolness * (1f - lighting.DirectLight) * influence,
                    Mathf.Lerp(1f, lighting.Saturation, influence),
                    lighting.NightFactor * 0.010f * influence);

                palette.SetPixel(depth, shadeRow, shade);
                palette.SetPixel(depth, sunRow, sun);
            }
        }

        GradeSpecialRow(palette, lighting, influence);
    }

    private static void GradeSpecialRow(
        Texture2D palette,
        SolarLightingState lighting,
        float influence)
    {
        // RoomPalette semantic slots from RoomCamera.ApplyPalette:
        // x0 sky, x1 fog, x2 black, x4..8 water family, x9 fog amount,
        // x30 darkness. Control values are adjusted as controls, not as colors.
        Color sky = palette.GetPixel(0, 7);
        Color fog = palette.GetPixel(1, 7);

        float horizonWarmth = Mathf.Clamp01(lighting.DawnFactor * 0.45f + lighting.DuskFactor * 0.68f);
        float skyExposure = Mathf.Lerp(0.50f, 1.04f, 1f - lighting.NightFactor);
        skyExposure += lighting.BlueHourFactor * 0.04f;

        sky = GradeColor(
            sky,
            Mathf.Lerp(1f, skyExposure, influence),
            horizonWarmth * influence,
            lighting.AmbientCoolness * 0.75f * influence,
            Mathf.Lerp(1f, Mathf.Lerp(0.84f, 1f, 1f - lighting.NightFactor), influence),
            0.018f * lighting.NightFactor * influence);

        fog = GradeColor(
            fog,
            Mathf.Lerp(1f, Mathf.Lerp(0.62f, 1f, 1f - lighting.NightFactor), influence),
            horizonWarmth * 0.60f * influence,
            lighting.AmbientCoolness * 0.55f * influence,
            Mathf.Lerp(1f, lighting.Saturation, influence),
            0.012f * lighting.NightFactor * influence);

        palette.SetPixel(0, 7, sky);
        palette.SetPixel(1, 7, fog);

        for (int x = 4; x <= 8; x++)
        {
            Color water = palette.GetPixel(x, 7);
            water = GradeColor(
                water,
                Mathf.Lerp(1f, Mathf.Lerp(0.58f, 1f, 1f - lighting.NightFactor), influence),
                horizonWarmth * 0.35f * influence,
                lighting.AmbientCoolness * 0.70f * influence,
                Mathf.Lerp(1f, lighting.Saturation, influence),
                0.008f * lighting.NightFactor * influence);
            palette.SetPixel(x, 7, water);
        }

        Color darknessControl = palette.GetPixel(30, 7);
        float baseDarkness = 1f - darknessControl.r;
        float targetDarkness = Mathf.Clamp01(baseDarkness + lighting.NightFactor * 0.34f * influence);
        darknessControl.r = 1f - targetDarkness;
        palette.SetPixel(30, 7, darknessControl);
    }

    private static Color GradeColor(
        Color source,
        float exposure,
        float warmth,
        float coolness,
        float saturation,
        float shadowLift)
    {
        float alpha = source.a;
        float luminance = Luminance(source);
        float max = Mathf.Max(source.r, Mathf.Max(source.g, source.b));
        float min = Mathf.Min(source.r, Mathf.Min(source.g, source.b));
        float chroma = max - min;

        // Authorial black is semantic in Rain World. If a palette cell is genuinely
        // black, do not invent material color that does not exist in the room art.
        if (luminance < 0.006f && chroma < 0.006f)
        {
            return source;
        }

        float hueProtection = SmoothStep(0.08f, 0.42f, chroma);
        float tintInfluence = Mathf.Lerp(0.92f, 0.30f, hueProtection);

        Color color = source;

        // Relative white-balance style gains instead of lerping toward fixed orange
        // or blue target colors. Saturated biome colors therefore keep their identity.
        float warm = warmth * tintInfluence;
        float cool = coolness * tintInfluence;
        color.r *= 1f + warm * 0.15f - cool * 0.045f;
        color.g *= 1f + warm * 0.035f + cool * 0.018f;
        color.b *= 1f - warm * 0.11f + cool * 0.14f;

        color.r = ToneChannel(color.r, exposure, shadowLift);
        color.g = ToneChannel(color.g, exposure, shadowLift);
        color.b = ToneChannel(color.b, exposure, shadowLift);

        float postLuma = Luminance(color);
        color.r = postLuma + (color.r - postLuma) * saturation;
        color.g = postLuma + (color.g - postLuma) * saturation;
        color.b = postLuma + (color.b - postLuma) * saturation;

        color.r = Mathf.Clamp01(color.r);
        color.g = Mathf.Clamp01(color.g);
        color.b = Mathf.Clamp01(color.b);
        color.a = alpha;
        return color;
    }

    private static float ToneChannel(float value, float exposure, float shadowLift)
    {
        value = Mathf.Clamp01(value);
        exposure = Mathf.Max(0.05f, exposure);

        if (exposure < 1f)
        {
            value = Mathf.Pow(value, 1f / exposure);
        }
        else if (exposure > 1f)
        {
            value = 1f - Mathf.Pow(1f - value, exposure);
        }

        if (shadowLift > 0f)
        {
            value = Mathf.Lerp(value, Mathf.Sqrt(value), Mathf.Clamp01(shadowLift * 8f));
        }

        return Mathf.Clamp01(value);
    }

    private static float Luminance(Color color)
    {
        return color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
    }

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Mathf.InverseLerp(from, to, value);
        return t * t * (3f - 2f * t);
    }
}
