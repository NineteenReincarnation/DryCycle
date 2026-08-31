using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Shared side-profile values used by MossySpiderGraphics and the dorsal platform.
/// The torso may flex, but the actual walkable moss surface is supplied by
/// MossySpiderDorsalPlane as one continuous straight plane.
/// </summary>
internal static class MossySpiderSilhouette
{
    internal const float WalkableStartU = 0.08f;
    internal const float WalkableEndU = 0.95f;

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

    internal static float MossLow(float u)
    {
        u = Mathf.Clamp01(u);
        float body = CarapaceHigh(u) + 3.5f;
        return BlendEnds(u, -25f, body, 3f, 0.36f, 0.89f);
    }

    /// <summary>
    /// Thickness reference for the moss layer. The visible dorsal edge itself is drawn
    /// by MossySpiderDorsalPlane, so the middle section is deliberately constant rather
    /// than carrying the old sine-shaped hump.
    /// </summary>
    internal static float MossHigh(float u)
    {
        u = Mathf.Clamp01(u);
        const float plateau = MossySpiderDorsalPlane.SurfaceHeight;

        if (u < 0.20f)
        {
            return Mathf.Lerp(-25f, plateau, u / 0.20f);
        }

        if (u > 0.80f)
        {
            return Mathf.Lerp(plateau, 3f, (u - 0.80f) / 0.20f);
        }

        return plateau;
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
