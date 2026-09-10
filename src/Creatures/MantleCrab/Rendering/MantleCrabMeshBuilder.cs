using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

internal enum MantleCrabMaterial { Shell, Leg, Joint, Foot, Pincer, Eye, Fringe }

internal static class MantleCrabMeshBuilder
{
    internal const int TileSize = 128, MaterialCount = 7;
    internal const float FootRootFraction = .12f;

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

    internal static Vector2 FootRoot(Vector2 ankle, Vector2 tip) =>
        Vector2.Lerp(ankle, tip, FootRootFraction);

    internal static float WalkingJointTrim(float halfWidth) =>
        Mathf.Clamp(halfWidth * .14f, .25f, .65f);

    internal static float PincerJointTrim(float halfWidth) =>
        Mathf.Clamp(halfWidth * .12f, .22f, .55f);

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
        Vector2 axis = direction.normalized;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);

        // Legacy short segments are retained for eye stalks and compatibility callers. Dedicated
        // appendage joints use JointCapsule below so a tiny connector can no longer accidentally
        // inherit a limb-shaft silhouette.
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
        if (axis.sqrMagnitude < .0001f) axis = Vector2.down;
        axis.Normalize();
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns * 2f - 1f;
            float v = y / (float)rows * 2f - 1f;
            Vector2 p;
            if (block)
            {
                float cap = Mathf.Sqrt(Mathf.Max(0f, 1f - u * u));
                float longitudinal = u * (.88f + .08f * (1f - Mathf.Abs(v)));
                float lateral = v * (.78f + cap * .28f) * (1f - .07f * Mathf.Abs(u));
                p = new Vector2(longitudinal, lateral);
            }
            else
            {
                p = new Vector2(
                    u * Mathf.Sqrt(1f - v * v * .5f),
                    v * Mathf.Sqrt(1f - u * u * .5f));
            }
            mesh.MoveVertice(
                y * (columns + 1) + x,
                center + axis * (p.x * width) + cross * (p.y * height) - camera);
        }
    }

    /// <summary>
    /// Projected underside of the proximal plate. The flattened ellipse fits the shaft exactly;
    /// it is an end face, not a collar around the two segments. The distal plate emerges behind it.
    /// </summary>
    internal static void JointCapsule(
        TriangleMesh mesh,
        int columns,
        int rows,
        Vector2 center,
        Vector2 incoming,
        Vector2 outgoing,
        float halfWidth,
        bool slender,
        Vector2 camera)
    {
        if (incoming.sqrMagnitude < .0001f) incoming = Vector2.down;
        if (outgoing.sqrMagnitude < .0001f) outgoing = incoming;
        incoming.Normalize();
        outgoing.Normalize();

        Vector2 axis = incoming;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);

        float halfLength = halfWidth * .34f;

        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns * 2f - 1f;
            float v = y / (float)rows * 2f - 1f;
            float discX = u * Mathf.Sqrt(1f - v * v * .5f);
            float discY = v * Mathf.Sqrt(1f - u * u * .5f);
            Vector2 point = center +
                            axis * (discX * halfLength - discY * halfWidth * .22f) +
                            cross * (discY * halfWidth);
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
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

            // Long faceted plate with a gently bowed shaft and obliquely cut ends.
            float profile;
            if (u < .10f)
                profile = Mathf.Lerp(.72f, .88f, u / .10f);
            else if (u < .62f)
                profile = Mathf.Lerp(.88f, 1.00f, (u - .10f) / .52f);
            else if (u < .93f)
                profile = Mathf.Lerp(1.00f, .87f, (u - .62f) / .31f);
            else
                profile = Mathf.Lerp(.87f, .90f, (u - .93f) / .07f);

            float organicBow = Mathf.Sin(u * Mathf.PI) * width * (variant % 2 == 0 ? .24f : -.18f);
            float faceting = 1f - .055f * Mathf.Abs(v);
            float round = Mathf.Sqrt(Mathf.Max(0f, 1f - v * v));
            float cut = width * ((-v * .22f - round * .30f) * Mathf.Pow(u, 18f) -
                                round * .18f * Mathf.Pow(1f - u, 18f));
            Vector2 point = Vector2.Lerp(start, end, u) + direction.normalized * cut + cross *
                            (organicBow + v * width * profile * faceting);
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }

    /// <summary>
    /// Capture-arm shaft profile. It is leaner than a walking leg and has smaller collars so the
    /// red appendage remains elegant while still reading as four rigid exoskeletal links.
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
            if (u < .10f)
                profile = Mathf.Lerp(.65f, .77f, Mathf.SmoothStep(0f, 1f, u / .10f));
            else if (u < .70f)
                profile = Mathf.Lerp(.77f, .84f, (u - .10f) / .60f);
            else
                profile = Mathf.Lerp(.84f, 1f, Mathf.SmoothStep(0f, 1f, (u - .70f) / .30f));

            float bow = Mathf.Sin(u * Mathf.PI) * width * (variant % 2 == 0 ? .16f : -.12f);
            float faceting = 1f - .045f * Mathf.Abs(v);
            float round = Mathf.Sqrt(Mathf.Max(0f, 1f - v * v));
            float cut = width * ((-v * .22f - round * .325f) * Mathf.Pow(u, 18f) -
                                round * .22f * Mathf.Pow(1f - u, 18f));
            Vector2 point = Vector2.Lerp(start, end, u) + direction.normalized * cut + cross *
                            (bow + v * width * profile * faceting);
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }

    /// <summary>
    /// Angular manus flowing continuously into a fixed blade. Left/right anatomical proportions
    /// select a long narrow palm or a short palm with a long curved digit.
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
        float clampedOpen = Mathf.Clamp01(open);

        Vector2 fixedRoot = palmEnd + cross * handedness * palmWidth * .55f;
        Vector2 fixedTip = palmEnd + axis * fingerLength +
                           cross * handedness * palmWidth * Mathf.Lerp(.06f, .18f, clampedOpen);
        Vector2 fixedControlA = fixedRoot + axis * fingerLength * .38f +
                                cross * handedness * fingerLength * (palmWidth < 5f ? .045f : .11f);
        Vector2 fixedControlB = palmEnd + axis * fingerLength * .76f +
                                cross * handedness * palmWidth * (palmWidth < 5f ? .40f : .85f);
        float palmFraction = palmLength > fingerLength ? .625f : .25f;

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            Vector2 center;
            Vector2 localCross = cross;
            float halfWidth;

            if (u < palmFraction)
            {
                float t = u / palmFraction;
                center = Vector2.Lerp(wrist, palmEnd, t) + cross * handedness * palmWidth * (.55f * t * t);
                float palmProfile;
                if (t < .30f)
                    palmProfile = Mathf.Lerp(.58f, .88f, t / .30f);
                else if (t < .65f)
                    palmProfile = Mathf.Lerp(.88f, .81f, (t - .30f) / .35f);
                else
                    palmProfile = Mathf.Lerp(.81f, (fingerWidth + .065f) / palmWidth, (t - .65f) / .35f);
                halfWidth = palmWidth * palmProfile;
            }
            else
            {
                float t = (u - palmFraction) / (1f - palmFraction);
                center = Bezier(fixedRoot, fixedControlA, fixedControlB, fixedTip, t);
                Vector2 tangent = BezierTangent(fixedRoot, fixedControlA, fixedControlB, fixedTip, t);
                if (tangent.sqrMagnitude > .0001f)
                    localCross = MantleCrabRenderingMath.Perpendicular(tangent.normalized);
                halfWidth = fingerWidth * Mathf.Pow(1f - t, .82f) + .065f;
            }

            mesh.MoveVertice(y * (columns + 1) + x, center + localCross * (v * halfWidth) - camera);
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
        float clampedOpen = Mathf.Clamp01(open);

        bool slenderPalm = palmLength > fingerLength;
        Vector2 root = palmEnd - axis * palmLength * (slenderPalm ? .22f : .50f) -
                       cross * handedness * palmWidth * (slenderPalm ? .22f : .42f);
        float tipSpread = Mathf.Lerp(.07f, 1.50f, clampedOpen) * palmWidth;
        Vector2 tip = palmEnd + axis * (fingerLength * .94f) - cross * handedness * tipSpread;
        Vector2 controlA = root + axis * fingerLength * .34f -
                           cross * handedness * fingerLength * Mathf.Lerp(.035f, palmWidth < 5f ? .12f : .25f, clampedOpen);
        Vector2 controlB = palmEnd + axis * fingerLength * .72f -
                           cross * handedness * palmWidth * Mathf.Lerp(.30f, 1.5f, clampedOpen);

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            Vector2 center = Bezier(root, controlA, controlB, tip, u);
            Vector2 tangent = BezierTangent(root, controlA, controlB, tip, u);
            Vector2 localCross = tangent.sqrMagnitude > .0001f
                ? MantleCrabRenderingMath.Perpendicular(tangent.normalized)
                : cross;
            float halfWidth = fingerWidth * .94f * Mathf.Pow(1f - u, .82f) + .06f;
            mesh.MoveVertice(y * (columns + 1) + x, center + localCross * (v * halfWidth) - camera);
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
        Vector2 shankDirection = shank.normalized;
        Vector2 shankCross = MantleCrabRenderingMath.Perpendicular(shankDirection);
        if (Vector2.Dot(shankCross, tangent) < 0f) shankCross = -shankCross;

        Vector2 footRoot = FootRoot(ankle, tip);

        for (int x = 0; x <= columns; x++)
        for (int y = 0; y <= rows; y++)
        {
            float u = x / (float)columns;
            float v = y / (float)rows * 2f - 1f;
            float plant = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.78f, 1f, u));

            float bulk;
            if (u < .18f)
                bulk = Mathf.Lerp(.38f, .54f, u / .18f);
            else if (u < .74f)
                bulk = Mathf.Lerp(.54f, 1.08f, (u - .18f) / .56f);
            else
                bulk = Mathf.Lerp(1.08f, .95f, (u - .74f) / .26f);

            Vector2 center = Vector2.Lerp(footRoot, tip, u);
            center += tangent * (Mathf.Sin(u * Mathf.PI) * width * .17f);
            center += normal * (Mathf.Sin(u * Mathf.PI) * width * .28f * plant);

            Vector2 localCross = Vector2.Lerp(shankCross, tangent, plant);
            if (localCross.sqrMagnitude < .0001f) localCross = tangent;
            else localCross.Normalize();

            // Outer-side expansion creates a planted, slightly asymmetric load-bearing foot.
            float lateralBias = Mathf.Lerp(1f, v > 0f ? 1.08f : .94f, plant);
            Vector2 point = center + localCross * (v * width * bulk * lateralBias);

            // Only the terminal edge collapses onto the terrain plane. The heel/body remains thick.
            float sole = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.86f, 1f, u));
            point -= normal * Vector2.Dot(point - tip, normal) * sole;
            mesh.MoveVertice(y * (columns + 1) + x, point - camera);
        }
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float s = 1f - t;
        return a * (s * s * s) + b * (3f * s * s * t) + c * (3f * s * t * t) + d * (t * t * t);
    }

    private static Vector2 BezierTangent(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float s = 1f - t;
        return (b - a) * (3f * s * s) + (c - b) * (6f * s * t) + (d - c) * (3f * t * t);
    }
}
