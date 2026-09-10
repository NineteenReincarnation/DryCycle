using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>Shared broad mantle crown for rendering and the existing walkable surface.</summary>
internal static class MantleCrabShellProfile
{
    internal static float Top(float wing) =>
        4f + 20f * Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(wing / .74f, 2f)), .68f);

    // Sparse, unequal shell spines. These are chitin silhouette, not the ignored items on the back.
    internal static float Crest(float u)
    {
        return Spike(u, .258f, .011f, 6.5f) + Spike(u, .305f, .014f, 4f) +
               Spike(u, .397f, .010f, 5f) + Spike(u, .455f, .015f, 8f) +
               Spike(u, .486f, .009f, 5.5f) + Spike(u, .574f, .012f, 4f) +
               Spike(u, .656f, .013f, 6f) + Spike(u, .729f, .010f, 4.5f);
    }

    private static float Spike(float u, float center, float radius, float height) =>
        Mathf.Clamp01(1f - Mathf.Abs(u - center) / radius) * height;
}
