using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Self-authored texture fields for DryCycle HeatWave optics.
///
/// The presentation deliberately separates transport, refraction and mirage data:
/// FlowField RGBA   = flow XY / heat-body strength / spatial phase.
/// NormalField RGBA = base normal XY / detail normal XY.
/// MirageField RGBA = mirage band / vertical remap / blur / spatial phase.
///
/// All textures are periodic, deterministic and generated once at runtime. The shader
/// then advects the normal fields through the flow texture with spatially de-synchronised
/// phases instead of scrolling one generic noise map over the camera.
/// </summary>
internal static class HeatWaveNoiseField
{
    private const int FlowSize = 256;
    private const int NormalSize = 256;
    private const int MirageWidth = 256;
    private const int MirageHeight = 128;
    private const float Tau = Mathf.PI * 2f;

    internal static Texture2D FlowTexture { get; private set; }
    internal static Texture2D NormalTexture { get; private set; }
    internal static Texture2D MirageTexture { get; private set; }

    internal static bool IsAvailable =>
        FlowTexture != null &&
        NormalTexture != null &&
        MirageTexture != null;

    internal static void Ensure()
    {
        if (IsAvailable)
        {
            return;
        }

        try
        {
            FlowTexture ??= BuildFlowTexture();
            NormalTexture ??= BuildNormalTexture();
            MirageTexture ??= BuildMirageTexture();

            Plugin.Logger?.LogInfo(
                "DryCycle HeatWave optical textures generated: " +
                $"flow={FlowSize}x{FlowSize}, normal={NormalSize}x{NormalSize}, " +
                $"mirage={MirageWidth}x{MirageHeight}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle HeatWave could not generate its optical texture fields. " +
                "The atmosphere shader will fall back to its analytic field.");
            Plugin.Logger?.LogWarning(ex);
        }
    }

    private static Texture2D BuildFlowTexture()
    {
        Color32[] pixels = new Color32[FlowSize * FlowSize];

        for (int y = 0; y < FlowSize; y++)
        {
            float v = y / (float)FlowSize;
            for (int x = 0; x < FlowSize; x++)
            {
                float u = x / (float)FlowSize;

                // Low X frequency and still lower Y frequency create tall coherent heat
                // bodies instead of round Perlin-cloud blobs.
                float body = PeriodicFbmAnisotropic(u, v, 0x1F123BB5u, 3, 2);
                float breakup = PeriodicFbmAnisotropic(u, v, 0x91E10DA5u, 7, 4);
                float turn = PeriodicFbmAnisotropic(u, v, 0xD1B54A35u, 5, 3);
                float phase = PeriodicFbmAnisotropic(u, v, 0xA24BAED5u, 4, 4);

                float strength = Smooth01(Mathf.Clamp01(
                    (body * 0.72f + breakup * 0.28f - 0.24f) * 1.58f));

                // HeatWave air has a strong upward bias. Lateral movement is coherent
                // but secondary; this keeps the field from reading as water normals.
                float lateral =
                    (turn - 0.5f) * 0.82f +
                    Mathf.Sin((v * 3f + phase * 0.71f) * Tau) * 0.13f;
                float vertical =
                    0.82f +
                    (body - 0.5f) * 0.28f +
                    (breakup - 0.5f) * 0.10f;

                Vector2 flow = new(lateral, Mathf.Max(0.22f, vertical));
                float magnitude = flow.magnitude;
                if (magnitude > 0.0001f)
                {
                    flow /= magnitude;
                }
                else
                {
                    flow = Vector2.up;
                }

                pixels[y * FlowSize + x] = new Color32(
                    EncodeSigned(flow.x),
                    EncodeSigned(flow.y),
                    ToByte(strength),
                    ToByte(phase));
            }
        }

        return CreateTexture(
            FlowSize,
            FlowSize,
            pixels,
            "DryCycleHeatWaveFlowField");
    }

    private static Texture2D BuildNormalTexture()
    {
        float[] baseDensity = new float[NormalSize * NormalSize];
        float[] detailDensity = new float[NormalSize * NormalSize];

        for (int y = 0; y < NormalSize; y++)
        {
            float v = y / (float)NormalSize;
            for (int x = 0; x < NormalSize; x++)
            {
                float u = x / (float)NormalSize;
                int index = y * NormalSize + x;

                baseDensity[index] =
                    PeriodicFbmAnisotropic(u, v, 0xC2B2AE3Du, 4, 3) * 0.68f +
                    PeriodicFbmAnisotropic(u, v, 0x165667B1u, 9, 5) * 0.32f;

                detailDensity[index] =
                    PeriodicFbmAnisotropic(u, v, 0x85EBCA77u, 13, 9) * 0.62f +
                    PeriodicFbmAnisotropic(u, v, 0x27D4EB2Fu, 23, 15) * 0.38f;
            }
        }

        Color32[] pixels = new Color32[NormalSize * NormalSize];
        for (int y = 0; y < NormalSize; y++)
        {
            int ym = PositiveModulo(y - 1, NormalSize);
            int yp = PositiveModulo(y + 1, NormalSize);

            for (int x = 0; x < NormalSize; x++)
            {
                int xm = PositiveModulo(x - 1, NormalSize);
                int xp = PositiveModulo(x + 1, NormalSize);

                float baseX =
                    baseDensity[y * NormalSize + xp] -
                    baseDensity[y * NormalSize + xm];
                float baseY =
                    baseDensity[yp * NormalSize + x] -
                    baseDensity[ym * NormalSize + x];

                float detailX =
                    detailDensity[y * NormalSize + xp] -
                    detailDensity[y * NormalSize + xm];
                float detailY =
                    detailDensity[yp * NormalSize + x] -
                    detailDensity[ym * NormalSize + x];

                // Encode scalar-density gradients, not velocity. The shader may use the
                // flow field to transport these normals, but optical bending comes from
                // refractive gradients as it should.
                baseX = Mathf.Clamp(baseX * 7.4f, -1f, 1f);
                baseY = Mathf.Clamp(baseY * 8.8f, -1f, 1f);
                detailX = Mathf.Clamp(detailX * 5.8f, -1f, 1f);
                detailY = Mathf.Clamp(detailY * 6.6f, -1f, 1f);

                pixels[y * NormalSize + x] = new Color32(
                    EncodeSigned(baseX),
                    EncodeSigned(baseY),
                    EncodeSigned(detailX),
                    EncodeSigned(detailY));
            }
        }

        return CreateTexture(
            NormalSize,
            NormalSize,
            pixels,
            "DryCycleHeatWaveNormalField");
    }

    private static Texture2D BuildMirageTexture()
    {
        Color32[] pixels = new Color32[MirageWidth * MirageHeight];

        for (int y = 0; y < MirageHeight; y++)
        {
            float v = y / (float)MirageHeight;
            for (int x = 0; x < MirageWidth; x++)
            {
                float u = x / (float)MirageWidth;

                float warp = PeriodicFbmAnisotropic(u, v, 0x9E3779B9u, 4, 3);
                float breakup = PeriodicFbmAnisotropic(u, v, 0x7F4A7C15u, 9, 5);
                float phase = PeriodicFbmAnisotropic(u, v, 0x94D049BBu, 5, 5);

                // Horizontal heat lenses with irregular phase produce vertical
                // compression/stretch rather than a horizontally scrolling water wave.
                float broadWave = Mathf.Sin(
                    (v * 6f + (warp - 0.5f) * 0.82f + phase * 0.31f) * Tau);
                float fineWave = Mathf.Sin(
                    (v * 13f - u * 2f + (breakup - 0.5f) * 0.66f + phase * 0.47f) * Tau);

                float band = Smooth01(Mathf.Clamp01((broadWave * 0.5f + 0.5f - 0.34f) * 1.62f));
                band *= Mathf.Lerp(0.56f, 1f, breakup);

                float stretch = Mathf.Clamp(
                    fineWave * (0.34f + band * 0.66f) +
                    (warp - 0.5f) * 0.32f,
                    -1f,
                    1f);

                float blur = Mathf.Clamp01(
                    band * 0.78f +
                    Mathf.Abs(stretch) * 0.22f);

                pixels[y * MirageWidth + x] = new Color32(
                    ToByte(band),
                    EncodeSigned(stretch),
                    ToByte(blur),
                    ToByte(phase));
            }
        }

        return CreateTexture(
            MirageWidth,
            MirageHeight,
            pixels,
            "DryCycleHeatWaveMirageField");
    }

    private static Texture2D CreateTexture(
        int width,
        int height,
        Color32[] pixels,
        string name)
    {
        Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = 0
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static float PeriodicFbmAnisotropic(
        float u,
        float v,
        uint seed,
        int baseCellsX,
        int baseCellsY)
    {
        float sum = 0f;
        float weight = 0f;

        AddOctave(ref sum, ref weight, u, v, baseCellsX, baseCellsY, 1.00f, seed + 0u);
        AddOctave(ref sum, ref weight, u, v, baseCellsX * 2, baseCellsY * 2, 0.56f, seed + 1u);
        AddOctave(ref sum, ref weight, u, v, baseCellsX * 4, baseCellsY * 4, 0.31f, seed + 2u);
        AddOctave(ref sum, ref weight, u, v, baseCellsX * 8, baseCellsY * 8, 0.16f, seed + 3u);

        return Mathf.Clamp01(sum / Mathf.Max(0.0001f, weight));
    }

    private static void AddOctave(
        ref float sum,
        ref float weightSum,
        float u,
        float v,
        int cellsX,
        int cellsY,
        float weight,
        uint seed)
    {
        sum += PeriodicValueNoise(u, v, cellsX, cellsY, seed) * weight;
        weightSum += weight;
    }

    private static float PeriodicValueNoise(
        float u,
        float v,
        int cellsX,
        int cellsY,
        uint seed)
    {
        float gx = u * cellsX;
        float gy = v * cellsY;
        int x0 = Mathf.FloorToInt(gx);
        int y0 = Mathf.FloorToInt(gy);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        float tx = Smooth01(gx - x0);
        float ty = Smooth01(gy - y0);

        x0 = PositiveModulo(x0, cellsX);
        x1 = PositiveModulo(x1, cellsX);
        y0 = PositiveModulo(y0, cellsY);
        y1 = PositiveModulo(y1, cellsY);

        float a = Hash01(x0, y0, seed);
        float b = Hash01(x1, y0, seed);
        float c = Hash01(x0, y1, seed);
        float d = Hash01(x1, y1, seed);

        return Mathf.Lerp(
            Mathf.Lerp(a, b, tx),
            Mathf.Lerp(c, d, tx),
            ty);
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

    private static byte EncodeSigned(float value)
    {
        return ToByte(value * 0.5f + 0.5f);
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
    }
}
