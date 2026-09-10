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

    /// <summary>
    /// Capture-arm shaft profile. Compared with a walking leg, the pincer arm has a stronger
    /// joint collar, a leaner middle shaft and a visible terminal collar so its four rigid links
    /// remain readable instead of merging into one red wire.
    /// </summary>
    internal static void PincerShaft(
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
            float profile;
            if (u < .18f)
                profile = Mathf.Lerp(.72f, 1.10f, Mathf.SmoothStep(0f, 1f, u / .18f));
            else if (u < .70f)
                profile = Mathf.Lerp(1.10f, .70f, Mathf.SmoothStep(0f, 1f, (u - .18f) / .52f));
            else
                profile = Mathf.Lerp(.70f, .90f, Mathf.SmoothStep(0f, 1f, (u - .70f) / .30f));

            float bow = Mathf.Sin(u * Mathf.PI) * width * (variant % 2 == 0 ? .045f : -.04f);
            Vector2 point = Vector2.Lerp(start, end, u) + cross * (bow + v * width * profile);
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }

    /// <summary>
    /// The fixed finger mesh also carries the palm. A broad palm followed by a short tapered digit
    /// produces the reference's claw silhouette without spending another sprite slot.
    /// </summary>
    internal static void PincerPalmAndFixedFinger(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 wrist,
        Vector2 axis,
        float palmLength,
        float palmWidth,
        float fingerLength,
        float fingerWidth,
        float open,
        float handedness,
        Vector2 camera)
    {
        if (axis.sqrMagnitude < .0001f) axis = Vector2.down;
        axis.Normalize();
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        Vector2 palmEnd = wrist + axis * palmLength;
        float fingerAngle = Mathf.Lerp(4f, 13f, Mathf.Clamp01(open)) * handedness * Mathf.Deg2Rad;
        Vector2 fingerAxis = Rotate(axis, fingerAngle);

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            Vector2 center;
            float halfWidth;

            if (u < .44f)
            {
                float t = u / .44f;
                center = Vector2.Lerp(wrist, palmEnd, t);
                float palmProfile = .58f + .48f * Mathf.Sin(t * Mathf.PI);
                halfWidth = palmWidth * palmProfile;
            }
            else
            {
                float t = (u - .44f) / .56f;
                center = palmEnd + fingerAxis * (fingerLength * t);
                center += cross * handedness * Mathf.Sin(t * Mathf.PI) * fingerLength * .035f;
                halfWidth = fingerWidth * Mathf.Pow(1f - t, .72f) + .07f;
            }

            mesh.MoveVertice(y * (columns + 1) + x, center + cross * (v * halfWidth) - camera);
        }
    }

    internal static void PincerMovableFinger(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 wrist,
        Vector2 axis,
        float palmLength,
        float palmWidth,
        float fingerLength,
        float fingerWidth,
        float open,
        float handedness,
        Vector2 camera)
    {
        if (axis.sqrMagnitude < .0001f) axis = Vector2.down;
        axis.Normalize();
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        Vector2 palmEnd = wrist + axis * palmLength;
        float fingerAngle = -Mathf.Lerp(8f, 29f, Mathf.Clamp01(open)) * handedness * Mathf.Deg2Rad;
        Vector2 fingerAxis = Rotate(axis, fingerAngle);
        Vector2 root = palmEnd - cross * handedness * palmWidth * .38f;

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            float curve = Mathf.Sin(u * Mathf.PI) * fingerLength * .045f;
            Vector2 center = root + fingerAxis * (fingerLength * .92f * u) - cross * handedness * curve;
            float halfWidth = fingerWidth * .92f * Mathf.Pow(1f - u, .70f) + .055f;
            mesh.MoveVertice(y * (columns + 1) + x, center + cross * (v * halfWidth) - camera);
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

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
    }
}
