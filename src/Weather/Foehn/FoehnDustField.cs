using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Procedural advected dust texture for Foehn's thin environmental mineral veil.
/// This is intentionally separate from the point-particle pool: the texture supplies
/// broad suspended dust bodies and obstacle/wake haze, while individual particles keep
/// the wind readable at close range.
/// R = broad dust density, G = clumped dust, B = fine grain breakup, A = phase.
/// </summary>
internal static class FoehnDustField
{
    private const int Width = 256;
    private const int Height = 128;

    internal static Texture2D DustTexture { get; private set; }
    internal static bool IsAvailable => DustTexture != null;

    internal static void Ensure()
    {
        if (DustTexture != null)
        {
            return;
        }

        try
        {
            DustTexture = BuildTexture();
        }
        catch (Exception ex)
        {
            DustTexture = null;
            Plugin.Logger?.LogWarning(
                "DryCycle Foehn could not generate its environmental dust texture.");
            Plugin.Logger?.LogWarning(ex);
        }
    }

    private static Texture2D BuildTexture()
    {
        Color32[] pixels = new Color32[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            float fy = y / (float)Height;
            for (int x = 0; x < Width; x++)
            {
                float fx = x / (float)Width;

                float warp = FractalNoise(
                    fx * 2.1f + 31.7f,
                    fy * 2.8f + 91.3f,
                    3) - 0.5f;
                float broadNoise = FractalNoise(
                    fx * 3.25f + warp * 0.42f + 11.4f,
                    fy * 4.2f - warp * 0.28f + 47.8f,
                    4);
                float clumpNoise = FractalNoise(
                    fx * 8.3f + warp * 0.95f + 173.2f,
                    fy * 10.4f - warp * 0.72f + 62.5f,
                    3);
                float grainNoise = FractalNoise(
                    fx * 19.5f + 217.8f,
                    fy * 23.0f + 139.4f,
                    2);
                float phase = FractalNoise(
                    fx * 1.7f + 331.2f,
                    fy * 2.3f + 271.9f,
                    2);

                float broad = Smooth01(Mathf.InverseLerp(0.32f, 0.76f, broadNoise));
                float clump = Smooth01(Mathf.InverseLerp(
                    0.43f,
                    0.82f,
                    clumpNoise * 0.76f + broad * 0.30f));
                float grain = Smooth01(Mathf.InverseLerp(
                    0.46f,
                    0.86f,
                    grainNoise * 0.82f + clump * 0.24f));

                pixels[y * Width + x] = new Color32(
                    ToByte(broad),
                    ToByte(clump),
                    ToByte(grain),
                    ToByte(phase));
            }
        }

        Texture2D texture = new(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "DryCycleFoehnDustField",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static float FractalNoise(float x, float y, int octaves)
    {
        float value = 0f;
        float weight = 0f;
        float amplitude = 1f;
        float frequency = 1f;

        for (int octave = 0; octave < octaves; octave++)
        {
            value += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
            weight += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return weight > 0f ? value / weight : 0.5f;
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
