using UnityEngine;

namespace DryCycle.Thirst;

/// <summary>
/// Deterministic physiological texture atlas generated once per process. The tear-film
/// texture stores a refractive normal, film thickness and salt/crack mask; the retinal
/// texture stores fine grain, slow clouding, crystalline specks and dark-island seeds.
/// Runtime generation keeps the effect resolution-independent and avoids authored noise
/// repeating at an obvious screen-space scale.
/// </summary>
internal static class DehydrationVisualTextures
{
    private const int TearFilmSize = 256;
    private const int RetinalSize = 192;

    internal static Texture2D TearFilm { get; private set; }
    internal static Texture2D RetinalNoise { get; private set; }
    internal static bool IsAvailable => TearFilm != null && RetinalNoise != null;

    internal static void Ensure()
    {
        if (IsAvailable)
        {
            return;
        }

        Dispose();
        TearFilm = BuildTearFilm();
        RetinalNoise = BuildRetinalNoise();
    }

    internal static void Dispose()
    {
        if (TearFilm != null)
        {
            Object.Destroy(TearFilm);
            TearFilm = null;
        }
        if (RetinalNoise != null)
        {
            Object.Destroy(RetinalNoise);
            RetinalNoise = null;
        }
    }

    private static Texture2D BuildTearFilm()
    {
        int size = TearFilmSize;
        float[] film = new float[size * size];
        float[] detail = new float[size * size];
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                int index = y * size + x;
                film[index] = Fbm(u * 3.1f + 7.13f, v * 2.35f + 3.71f, 4);
                detail[index] = Fbm(u * 8.7f + 17.31f, v * 6.2f + 11.83f, 3);
            }
        }

        for (int y = 0; y < size; y++)
        {
            int down = ((y - 1 + size) % size) * size;
            int up = ((y + 1) % size) * size;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                int left = row + (x - 1 + size) % size;
                int right = row + (x + 1) % size;
                int index = row + x;
                float dx = (film[right] - film[left]) * 7.5f;
                float dy = (film[up + x] - film[down + x]) * 7.5f;
                float ridge = 1f - Mathf.Abs(detail[index] * 2f - 1f);
                float saltCrack = SmoothStep(0.80f, 0.985f, ridge);
                saltCrack *= 0.55f + 0.45f * SmoothStep(0.48f, 0.90f, film[index]);

                pixels[index] = new Color(
                    Mathf.Clamp01(dx * 0.5f + 0.5f),
                    Mathf.Clamp01(dy * 0.5f + 0.5f),
                    film[index],
                    Mathf.Clamp01(saltCrack));
            }
        }

        return CreateTexture("DryCycle_Dehydration_TearFilm", size, size, pixels);
    }

    private static Texture2D BuildRetinalNoise()
    {
        int size = RetinalSize;
        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            float v = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float grain = Hash01(x, y, 0x5f3759dfU);
                float cloud = Fbm(u * 2.15f + 31.7f, v * 1.83f + 19.3f, 5);
                float crystalField = Fbm(u * 13.4f + 5.8f, v * 10.9f + 41.2f, 2);
                float crystals = SmoothStep(0.76f, 0.96f, crystalField);

                float islandA = Fbm(u * 4.2f + 73.1f, v * 3.6f + 51.9f, 3);
                float islandB = Fbm(u * 7.7f + 13.4f, v * 5.1f + 91.7f, 2);
                float islands = SmoothStep(0.57f, 0.89f, islandA * 0.72f + islandB * 0.28f);

                pixels[y * size + x] = new Color(
                    grain,
                    cloud,
                    crystals,
                    islands);
            }
        }

        return CreateTexture("DryCycle_Dehydration_RetinalNoise", size, size, pixels);
    }

    private static Texture2D CreateTexture(
        string name,
        int width,
        int height,
        Color32[] pixels)
    {
        Texture2D texture = new(width, height, TextureFormat.RGBA32, mipChain: false, linear: true)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 0,
            hideFlags = HideFlags.DontSave
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }

    private static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f;
        float amplitude = 0.56f;
        float frequency = 1f;
        float normalization = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
            normalization += amplitude;
            frequency *= 2.071f;
            amplitude *= 0.52f;
        }
        return normalization > 0f ? Mathf.Clamp01(sum / normalization) : 0f;
    }

    private static float Hash01(int x, int y, uint seed)
    {
        unchecked
        {
            uint value = (uint)x * 0x8da6b343U ^ (uint)y * 0xd8163841U ^ seed;
            value ^= value >> 13;
            value *= 0x85ebca6bU;
            value ^= value >> 16;
            return (value & 0x00ffffffU) / 16777215f;
        }
    }

    private static float SmoothStep(float min, float max, float value)
    {
        float t = Mathf.Clamp01((value - min) / Mathf.Max(0.0001f, max - min));
        return t * t * (3f - 2f * t);
    }
}
