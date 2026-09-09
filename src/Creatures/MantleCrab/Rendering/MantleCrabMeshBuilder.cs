using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

internal enum MantleCrabMaterial { Shell, Leg, Joint, Foot, Pincer, Eye, Fringe }

internal static class MantleCrabMeshBuilder
{
    internal const int TileSize = 128, MaterialCount = 7;

    internal static TriangleMesh Grid(string atlas, int columns, int rows, MantleCrabMaterial material)
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[columns * rows * 2];
        int triangle = 0;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int i = y * (columns + 1) + x;
            triangles[triangle++] = new TriangleMesh.Triangle(i, i + 1, i + columns + 1);
            triangles[triangle++] = new TriangleMesh.Triangle(i + 1, i + columns + 2, i + columns + 1);
        }

        TriangleMesh mesh = new(atlas, triangles, true);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
            mesh.UVvertices[y * (columns + 1) + x] = AtlasUV(
                material,
                new Vector2(x / (float)columns, y / (float)rows));
        return mesh;
    }

    // Half-texel gutters: derivative height taps are clamped to the same anatomical tile.
    internal static Vector2 AtlasUV(MantleCrabMaterial material, Vector2 uv) => new(
        ((int)material * TileSize + .5f + uv.x * (TileSize - 1)) / (TileSize * MaterialCount),
        (.5f + uv.y * (TileSize - 1)) / (TileSize * 2));

    internal static void Segment(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 start,
        Vector2 end,
        float width,
        float taper,
        Vector2 camera)
    {
        Vector2 direction = end - start;
        if (direction.sqrMagnitude < .0001f) direction = Vector2.down;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(direction.normalized);

        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            float profile = Mathf.Lerp(1f, taper, u) * (.76f + .24f * Mathf.Sin(u * Mathf.PI));
            mesh.MoveVertice(
                y * (columns + 1) + x,
                Vector2.Lerp(start, end, u) + cross * (v * width * profile) - camera);
        }
    }

    internal static void Rounded(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 center,
        Vector2 axis,
        float width,
        float height,
        Vector2 camera,
        bool block = false)
    {
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns * 2f - 1f;
            float v = y / (float)rows * 2f - 1f;
            Vector2 p = block
                ? new Vector2(
                    u * (.9f - .25f * v) * (1f - .1f * Mathf.Abs(v)),
                    v * (1f - .08f * Mathf.Abs(u)))
                : new Vector2(
                    u * Mathf.Sqrt(1f - v * v * .5f),
                    v * Mathf.Sqrt(1f - u * u * .5f));
            mesh.MoveVertice(
                y * (columns + 1) + x,
                center + axis * (p.x * width) + cross * (p.y * height) - camera);
        }
    }

    internal static void Chitin(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 start,
        Vector2 end,
        float width,
        int variant,
        Vector2 camera)
    {
        Vector2 direction = end - start;
        if (direction.sqrMagnitude < .0001f) direction = Vector2.down;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(direction.normalized);

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;

            // A long Mantle Crab limb is not a uniform pipe. It leaves the joint with some
            // mass, narrows through the shaft, then gains a small terminal collar before the
            // next articulation. This profile is intentionally subtle; the authored joint
            // positions provide the actual bend.
            float profile;
            if (u < .16f)
                profile = Mathf.Lerp(.78f, 1.06f, Mathf.SmoothStep(0f, 1f, u / .16f));
            else if (u < .76f)
                profile = Mathf.Lerp(1.06f, .68f, Mathf.SmoothStep(0f, 1f, (u - .16f) / .60f));
            else
                profile = Mathf.Lerp(.68f, .86f, Mathf.SmoothStep(0f, 1f, (u - .76f) / .24f));

            float organicBow = Mathf.Sin(u * Mathf.PI) * width * (variant % 2 == 0 ? .07f : -.055f);
            Vector2 point = Vector2.Lerp(start, end, u) + cross * (organicBow + v * width * profile);
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }

    internal static void Foot(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 ankle,
        Vector2 tip,
        float width,
        float side,
        Vector2 groundNormal,
        Vector2 camera)
    {
        Vector2 normal = groundNormal.sqrMagnitude > .0001f ? groundNormal.normalized : Vector2.up;
        Vector2 tangent = MantleCrabRenderingMath.Perpendicular(normal).normalized;
        Vector2 outward = new(side, 0f);
        if (Vector2.Dot(tangent, outward) < 0f) tangent = -tangent;

        Vector2 shank = tip - ankle;
        if (shank.sqrMagnitude < .0001f) shank = -normal;
        Vector2 shankCross = MantleCrabRenderingMath.Perpendicular(shank.normalized);
        if (Vector2.Dot(shankCross, tangent) < 0f) shankCross = -shankCross;

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            float plant = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.48f, 1f, u));

            // Thin ankle -> expanded bearing pad. The centre line bows slightly outward but
            // still terminates exactly at the terrain contact used by the physical rig.
            float bulk;
            if (u < .34f)
                bulk = Mathf.Lerp(.22f, .46f, Mathf.SmoothStep(0f, 1f, u / .34f));
            else if (u < .72f)
                bulk = Mathf.Lerp(.46f, 1f, Mathf.SmoothStep(0f, 1f, (u - .34f) / .38f));
            else
                bulk = Mathf.Lerp(1f, .82f, Mathf.SmoothStep(0f, 1f, (u - .72f) / .28f));

            Vector2 center = Vector2.Lerp(ankle, tip, u);
            center += tangent * (Mathf.Sin(u * Mathf.PI) * width * .22f * plant);

            Vector2 cross = Vector2.Lerp(shankCross, tangent, plant).normalized;
            Vector2 point = center + cross * (v * width * bulk);

            // The terminal half progressively conforms to the terrain plane, producing a
            // real bearing face instead of rotating a rectangular shoe at the end of the leg.
            float flatten = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.58f, 1f, u));
            point -= normal * Vector2.Dot(point - tip, normal) * flatten;

            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }
}
