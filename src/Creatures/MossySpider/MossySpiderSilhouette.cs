using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Shared side-profile used by both MossySpiderGraphics and the moving dorsal platform.
/// Keeping these values in one place prevents the visible moss and the walkable surface
/// from drifting apart.
/// </summary>
internal static class MossySpiderSilhouette
{
    internal const float WalkableStartU = 0.10f;
    internal const float WalkableEndU = 0.94f;

    internal static float CarapaceLow(float u)
    {
        u = Mathf.Clamp01(u);
        float center = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(u * Mathf.PI)), 0.70f);
        float body = -29f - center * 5f;
        return BlendEnds(u, -25f, body, 2f, 0.15f, 0.88f);
    }

    internal static float CarapaceHigh(float u)
    {
        u = Mathf.Clamp01(u);
        float center = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(u * Mathf.PI)), 0.58f);
        float body = 7f + center * 5f;
        return BlendEnds(u, -25f, body, 2f, 0.18f, 0.84f);
    }

    /// <summary>
    /// Bottom edge of the green moss mass. The front remains attached to the low nose
    /// for longer, then rises into the broad body; the rear stays almost flat before
    /// tapering to a point.
    /// </summary>
    internal static float MossLow(float u)
    {
        u = Mathf.Clamp01(u);
        float body = CarapaceHigh(u) + 3.5f;
        return BlendEnds(u, -25f, body, 3f, 0.36f, 0.89f);
    }

    /// <summary>
    /// Top edge drawn from the user's marked silhouette: a steep rounded front rise,
    /// long nearly-flat dorsal plateau, then a smooth rear fall into a narrow point.
    /// </summary>
    internal static float MossHigh(float u)
    {
        u = Mathf.Clamp01(u);

        float plateauShape = Mathf.Sin(Mathf.Clamp01((u - 0.20f) / 0.58f) * Mathf.PI);
        float plateau = 39.5f + Mathf.Max(0f, plateauShape) * 2.2f;

        return BlendEnds(u, -25f, plateau, 3f, 0.24f, 0.72f);
    }

    internal static float MossShadowHigh(float u)
    {
        return Mathf.Lerp(MossLow(u), MossHigh(u), 0.22f);
    }

    internal static float MossCapLow(float u)
    {
        return Mathf.Lerp(MossLow(u), MossHigh(u), 0.08f);
    }

    private static float BlendEnds(
        float u,
        float leftTip,
        float body,
        float rightTip,
        float leftEnd,
        float rightStart)
    {
        if (u < leftEnd)
        {
            return Mathf.Lerp(leftTip, body, Smooth01(u / Mathf.Max(0.001f, leftEnd)));
        }

        if (u > rightStart)
        {
            return Mathf.Lerp(
                body,
                rightTip,
                Smooth01((u - rightStart) / Mathf.Max(0.001f, 1f - rightStart)));
        }

        return body;
    }

    private static float Smooth01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }
}
