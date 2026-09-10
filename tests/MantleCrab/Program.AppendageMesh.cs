using System;
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
        MantleCrabMeshBuilder.Foot(mesh, columns, rows, ankle, tip, 8f, 1f, Vector2.up, Vector2.zero);

        float firstColumnAverageY = 0f;
        for (int y = 0; y <= rows; y++)
            firstColumnAverageY += mesh.vertices[y * (columns + 1)].y;
        firstColumnAverageY /= rows + 1f;

        Check(firstColumnAverageY < 50f && firstColumnAverageY > 30f,
            "Foot mesh must begin near the terminal 39% of the final leg link, not at the ankle landmark");

        float soleDeviation = 0f;
        for (int y = 0; y <= rows; y++)
            soleDeviation = Math.Max(soleDeviation, Math.Abs(mesh.vertices[y * (columns + 1) + columns].y));
        Check(soleDeviation < .01f,
            "Foot terminal column must form a terrain-aligned bearing sole");
    }

    private static void JointGeometryContract()
    {
        const int columns = 6, rows = 4;
        TriangleMesh mesh = EmptyMesh(columns, rows);
        MantleCrabMeshBuilder.Segment(
            mesh, columns, rows,
            new Vector2(-1.4f, 0f), new Vector2(1.4f, 0f),
            4f, .62f, Vector2.zero);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 vertex in mesh.vertices)
        {
            minX = Math.Min(minX, vertex.x);
            maxX = Math.Max(maxX, vertex.x);
            minY = Math.Min(minY, vertex.y);
            maxY = Math.Max(maxY, vertex.y);
        }

        Check(maxX - minX > 8f && maxY - minY > 7f,
            "Walking joint must render as a readable hinge capsule rather than a tiny connector dot");
    }

    private static void ChelaGeometryContract()
    {
        const int columns = 14, rows = 6;
        TriangleMesh mesh = EmptyMesh(columns, rows);
        MantleCrabMeshBuilder.PincerPalmAndFixedFinger(
            mesh, columns, rows,
            Vector2.zero, Vector2.down,
            23.5f, 6.8f, 16.5f, 2.65f,
            .13f, -1f, Vector2.zero);

        int palmColumn = 7;
        float palmSpan = Vector2.Distance(
            mesh.vertices[palmColumn],
            mesh.vertices[rows * (columns + 1) + palmColumn]);
        float tipSpan = Vector2.Distance(
            mesh.vertices[columns],
            mesh.vertices[rows * (columns + 1) + columns]);

        Check(palmSpan > 10f,
            "Chela manus must retain a broad readable body at Rain World scale");
        Check(tipSpan < 1f,
            "Fixed pincer digit must converge to a short sharp tip instead of remaining a second arm shaft");
        Check(palmSpan > tipSpan * 10f,
            "Chela silhouette must be palm-dominant rather than fork/needle-dominant");
    }
}
