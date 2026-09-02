using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Self-authored phase textures for the HeatWave atmosphere pass.
///
/// Macro channels drive broad rising heat bands and meso-scale whole-air deformation;
/// micro channels drive fast fine shimmer. Keeping the two spectra separate prevents
/// the weather from degenerating into one scrolling water-like normal map and keeps its
/// appearance stable across regions, palettes and other mods.
/// </summary>
internal static class HeatWaveNoiseField
{
    private const int MacroSize = 256;
    private const int MicroSize = 128;

    internal static Texture2D MacroTexture { get; private set; }
    internal static Texture2D MicroTexture { get; private set; }
    internal static bool IsAvailable => MacroTexture != null && MicroTexture != null;

    internal static void Ensure()
    {
        if (IsAvailable)
        {
            return;
        }

        try
        {
            MacroTexture ??= BuildMacroTexture();
            MicroTexture ??= BuildMicroTexture();
            Plugin.Logger?.LogInfo(
                $"DryCycle HeatWave phase fields generated: macro={MacroSize}x{MacroSize}, " +
                $"micro={MicroSize}x{MicroSize}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle HeatWave could not generate custom phase textures. " +
                "The atmosphere shader will use its analytic fallback motion.");
            Plugin.Logger?.LogWarning(ex);
        }
    }

    private static Texture2D BuildMacroTexture()
    {
        Color32[] pixels = new Color32[MacroSize * MacroSize];
        for (int y = 0; y < MacroSize; y++)
        {
            for (int x = 0; x < MacroSize; x++)
            {
                float u = x / (float)MacroSize;
                float v = y / (float)MacroSize;

                // Three independent broad phase channels let the shader build heat
                // ridges, rising bodies and breakup without baking a particular visual
                // shape into the texture itself.
                float a = PeriodicFbm(u, v, 0x1F123BB5u);
                float b = PeriodicFbm(u, v, 0x91E10DA5u);
                float c = PeriodicFbm(u, v, 0xD1B54A35u);

                pixels[y * MacroSize + x] = new Color32(
                    ToByte(a),
                    ToByte(b),
                    ToByte(c),
                    255);
            }
        }

        Texture2D texture = new(MacroSize, MacroSize, TextureFormat.RGBA32, false)
        {
            name = "DryCycleHeatWaveMacroPhase",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static Texture2D BuildMicroTexture()
    {
        Color32[] pixels = new Color32[MicroSize * MicroSize];
        for (int y = 0; y < MicroSize; y++)
        {
            for (int x = 0; x < MicroSize; x++)
            {
                float r = HighPassNoise(x, y, 0xA511E9B3u);
                float g = HighPassNoise(x, y, 0x63D83595u);
                float b = HighPassNoise(x, y, 0xC2B2AE3Du);

                pixels[y * MicroSize + x] = new Color32(
                    ToByte(r),
                    ToByte(g),
                    ToByte(b),
                    255);
            }
        }

        Texture2D texture = new(MicroSize, MicroSize, TextureFormat.RGBA32, false)
        {
            name = "DryCycleHeatWaveMicroPhase",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static float PeriodicFbm(float u, float v, uint seed)
    {
        float sum = 0f;
        float weight = 0f;

        AddOctave(ref sum, ref weight, u, v, 2, 1.00f, seed + 0u);
        AddOctave(ref sum, ref weight, u, v, 4, 0.58f, seed + 1u);
        AddOctave(ref sum, ref weight, u, v, 8, 0.33f, seed + 2u);
        AddOctave(ref sum, ref weight, u, v, 16, 0.18f, seed + 3u);
        AddOctave(ref sum, ref weight, u, v, 32, 0.09f, seed + 4u);

        return Mathf.Clamp01(sum / Mathf.Max(0.0001f, weight));
    }

    private static void AddOctave(
        ref float sum,
        ref float weightSum,
        float u,
        float v,
        int cells,
        float weight,
        uint seed)
    {
        sum += PeriodicValueNoise(u, v, cells, seed) * weight;
        weightSum += weight;
    }

    private static float PeriodicValueNoise(float u, float v, int cells, uint seed)
    {
        float gx = u * cells;
        float gy = v * cells;
        int x0 = Mathf.FloorToInt(gx);
        int y0 = Mathf.FloorToInt(gy);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        float tx = Smooth01(gx - x0);
        float ty = Smooth01(gy - y0);

        x0 = PositiveModulo(x0, cells);
        x1 = PositiveModulo(x1, cells);
        y0 = PositiveModulo(y0, cells);
        y1 = PositiveModulo(y1, cells);

        float a = Hash01(x0, y0, seed);
        float b = Hash01(x1, y0, seed);
        float c = Hash01(x0, y1, seed);
        float d = Hash01(x1, y1, seed);

        return Mathf.Lerp(
            Mathf.Lerp(a, b, tx),
            Mathf.Lerp(c, d, tx),
            ty);
    }

    private static float HighPassNoise(int x, int y, uint seed)
    {
        float center = Hash01(
            PositiveModulo(x, MicroSize),
            PositiveModulo(y, MicroSize),
            seed);

        float average = 0f;
        average += Hash01(PositiveModulo(x - 1, MicroSize), PositiveModulo(y, MicroSize), seed);
        average += Hash01(PositiveModulo(x + 1, MicroSize), PositiveModulo(y, MicroSize), seed);
        average += Hash01(PositiveModulo(x, MicroSize), PositiveModulo(y - 1, MicroSize), seed);
        average += Hash01(PositiveModulo(x, MicroSize), PositiveModulo(y + 1, MicroSize), seed);
        average += Hash01(PositiveModulo(x - 1, MicroSize), PositiveModulo(y - 1, MicroSize), seed);
        average += Hash01(PositiveModulo(x + 1, MicroSize), PositiveModulo(y - 1, MicroSize), seed);
        average += Hash01(PositiveModulo(x - 1, MicroSize), PositiveModulo(y + 1, MicroSize), seed);
        average += Hash01(PositiveModulo(x + 1, MicroSize), PositiveModulo(y + 1, MicroSize), seed);
        average *= 0.125f;

        // Suppressing low-frequency energy lets bilinear filtering produce dense edge
        // shimmer without introducing slow cloudy patches that belong in the macro field.
        return Mathf.Clamp01(0.5f + (center - average) * 0.72f);
    }

    private static float Hash01(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed;
            h ^= (uint)x * 0x9E3779B9u;
            h = (h ^ (h >> 16)) * 0x85EBCA6Bu;
            h ^= (uint)y * 0xC2B2AE35u;
            h = (h ^ (h >> 13)) * 0x27D4EB2Fu;
            h ^= h >> 15;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static int PositiveModulo(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
