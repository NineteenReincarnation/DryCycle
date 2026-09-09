using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

internal static class MantleCrabRenderingMath
{
    internal static Vector2 Perpendicular(Vector2 v) => new(-v.y, v.x);
    internal static float Noise(float x, float y)
    {
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float u = x - ix, v = y - iy;
        u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
        return Mathf.Lerp(Mathf.Lerp(Cell(ix, iy), Cell(ix + 1, iy), u),
            Mathf.Lerp(Cell(ix, iy + 1), Cell(ix + 1, iy + 1), u), v);
    }
    private static float Cell(int x, int y)
    {
        unchecked
        {
            uint n = (uint)x * 374761393u + (uint)y * 668265263u;
            n = (n ^ (n >> 13)) * 1274126177u;
            return ((n ^ (n >> 16)) & 65535) / 65535f;
        }
    }

    internal static float PlateSeam(float x, float y)
    {
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float nearest = 100f, second = 100f;
        for (int cy = -1; cy <= 1; cy++)
        for (int cx = -1; cx <= 1; cx++)
        {
            float dx = ix + cx + .2f + Cell(ix + cx, iy + cy) * .6f - x;
            float dy = iy + cy + .2f + Cell(ix + cx + 37, iy + cy - 19) * .6f - y;
            float distance = dx * dx + dy * dy;
            if (distance < nearest) { second = nearest; nearest = distance; }
            else second = Mathf.Min(second, distance);
        }
        return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.02f, .16f, Mathf.Sqrt(second) - Mathf.Sqrt(nearest)));
    }

    internal static float ShellHalfHeight(float u) => 1.5f + 27f * Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Abs(u * 2 - 1)), .62f);
}
