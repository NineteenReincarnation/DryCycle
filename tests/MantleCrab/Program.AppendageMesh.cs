using System;
using DryCycle.Creatures.MantleCrab;
using DryCycle.Creatures.MantleCrab.Rendering;
using UnityEngine;

internal static partial class Program
{
    private static void AppendageMeshTests()
    {
        FootGeometryContract();
        JointGeometryContract();
        ChelaGeometryContract();
    }

    private static TriangleMesh EmptyMesh(int columns, int rows)
    {
        TriangleMesh mesh = Empty<TriangleMesh>();
        mesh.vertices = new Vector2[(columns + 1) * (rows + 1)];
        return mesh;
    }

    private static void FootGeometryContract()
    {
        const int columns = 12, rows = 6;
        TriangleMesh mesh = EmptyMesh(columns, rows);
        Vector2 ankle = new(0f, 100f);
        Vector2 tip = Vector2.zero;
        Vector2 expectedRoot = MantleCrabMeshBuilder.FootRoot(ankle, tip);
        MantleCrabMeshBuilder.Foot(mesh, columns, rows, ankle, tip, 8f, 1f, Vector2.up, Vector2.zero);

        float firstColumnAverageY = 0f;
        for (int y = 0; y <= rows; y++)
            firstColumnAverageY += mesh.vertices[y * (columns + 1)].y;
        firstColumnAverageY /= rows + 1f;

        Check(Math.Abs(firstColumnAverageY - expectedRoot.y) < .01f,
            "Foot mesh must begin exactly at the anatomical foot root instead of repainting the whole distal leg link");
        Check(Math.Abs(MantleCrabMeshBuilder.FootRootFraction - .61f) < .0001f,
            "Foot root fraction changed without updating the distal-leg silhouette contract");

        float soleDeviation = 0f;
        for (int y = 0; y <= rows; y++)
            soleDeviation = Math.Max(soleDeviation, Math.Abs(mesh.vertices[y * (columns + 1) + columns].y));
        Check(soleDeviation < .01f,
            "Foot terminal column must form a terrain-aligned bearing sole");
    }

    private static void JointGeometryContract()
    {
        const int columns = 8, rows = 6;
        TriangleMesh walking = EmptyMesh(columns, rows);
        TriangleMesh capture = EmptyMesh(columns, rows);
        Vector2 incoming = new(.30f, -1f);
        Vector2 outgoing = new(-.28f, -1f);

        MantleCrabMeshBuilder.JointCapsule(
            walking, columns, rows,
            Vector2.zero, incoming, outgoing,
            4f, false, Vector2.zero);
        MantleCrabMeshBuilder.JointCapsule(
            capture, columns, rows,
            Vector2.zero, incoming, outgoing,
            4f, true, Vector2.zero);

        Bounds2D walkingBounds = Bounds(walking.vertices);
        Bounds2D captureBounds = Bounds(capture.vertices);
        Check(walkingBounds.Width > 5f && walkingBounds.Height > 8f,
            "Walking joint must render as a readable load-bearing hinge capsule rather than a connector dot");
        Check(captureBounds.Width < walkingBounds.Width || captureBounds.Height < walkingBounds.Height,
            "Capture-arm hinge must remain visibly slimmer than the walking-leg bearing joint");
        Check(MantleCrabMeshBuilder.WalkingJointTrim(4f) > MantleCrabMeshBuilder.PincerJointTrim(4f),
            "Walking joints must reserve more shell overlap/clearance than slender capture-arm hinges");
    }

    private static void ChelaGeometryContract()
    {
        const int columns = 14, rows = 6;
        for (int index = 0; index < 2; index++)
        {
            TriangleMesh mesh = EmptyMesh(columns, rows);
            MantleCrabMeshBuilder.PincerPalmAndFixedFinger(
                mesh, columns, rows,
                Vector2.zero, Vector2.down,
                MantleCrabPincerAnatomy.PalmLengths[index],
                MantleCrabPincerAnatomy.PalmWidths[index],
                MantleCrabPincerAnatomy.FingerLengths[index],
                MantleCrabPincerAnatomy.FingerWidths[index],
                MantleCrabPincerAnatomy.IdleOpen[index],
                index == 0 ? -1f : 1f,
                Vector2.zero);

            int palmColumn = 7;
            float palmSpan = Vector2.Distance(
                mesh.vertices[palmColumn],
                mesh.vertices[rows * (columns + 1) + palmColumn]);
            float tipSpan = Vector2.Distance(
                mesh.vertices[columns],
                mesh.vertices[rows * (columns + 1) + columns]);

            Check(MantleCrabPincerAnatomy.PalmLengths[index] > MantleCrabPincerAnatomy.FingerLengths[index],
                "Chela " + index + " must remain manus-first; a digit may not become longer than the palm again");
            Check(palmSpan > MantleCrabPincerAnatomy.PalmWidths[index] * 1.55f,
                "Chela " + index + " manus must retain a broad readable body at Rain World scale");
            Check(tipSpan < 1f,
                "Chela " + index + " fixed digit must converge to a short sharp tip instead of remaining a second arm shaft");
            Check(palmSpan > tipSpan * 8f,
                "Chela " + index + " silhouette must be palm-dominant rather than fork/needle-dominant");
        }
    }

    private static Bounds2D Bounds(Vector2[] vertices)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 vertex in vertices)
        {
            minX = Math.Min(minX, vertex.x);
            maxX = Math.Max(maxX, vertex.x);
            minY = Math.Min(minY, vertex.y);
            maxY = Math.Max(maxY, vertex.y);
        }
        return new Bounds2D(minX, maxX, minY, maxY);
    }

    private readonly struct Bounds2D
    {
        internal readonly float MinX, MaxX, MinY, MaxY;
        internal float Width => MaxX - MinX;
        internal float Height => MaxY - MinY;

        internal Bounds2D(float minX, float maxX, float minY, float maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }
    }
}
