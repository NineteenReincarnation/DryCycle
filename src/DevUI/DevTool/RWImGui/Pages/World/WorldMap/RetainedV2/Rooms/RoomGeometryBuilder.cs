using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Pure retained-room geometry builder. It consumes detached visual snapshots only, so later phases
/// can move this exact work to a background job without touching Unity objects.
/// </summary>
internal static class RoomGeometryBuilder
{
    internal static RoomGeometryBlob Build(
        int roomIndex,
        EditorMapRoomVisualSnapshot visual,
        int sourceStamp)
    {
        visual ??= EditorMapRoomVisualSnapshot.Empty;
        float width = Math.Max(1f, visual.WidthTiles);
        float height = Math.Max(1f, visual.HeightTiles);

        List<RoomGeometryBlob.Vertex> vertices = new();
        List<int> indices = new();

        // A neutral Air base is always present. If the room has no committed thumbnail yet, later
        // render stages can still show a visible non-black room instead of an uninitialised surface.
        AddQuad(
            vertices,
            indices,
            0f,
            0f,
            width,
            height,
            EditorMapGeometryKind.Air);

        EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
        for (int i = 0; i < runs.Length; i++)
        {
            EditorMapRectSnapshot run = runs[i];
            if (run.Width <= 0f || run.Height <= 0f || run.Kind == EditorMapGeometryKind.Air)
                continue;

            AddQuad(
                vertices,
                indices,
                run.X,
                run.Y,
                run.Width,
                run.Height,
                run.Kind);
        }

        List<RoomGeometryBlob.Segment> segments = new();
        EditorMapPolylineSnapshot[] curves = visual.Curves ?? Array.Empty<EditorMapPolylineSnapshot>();
        for (int i = 0; i < curves.Length; i++)
        {
            EditorMapPolylineSnapshot curve = curves[i];
            EditorMapPointSnapshot[] points = curve?.Points ?? Array.Empty<EditorMapPointSnapshot>();
            if (points.Length < 2) continue;

            int surfaceCount = curve.Closed && points.Length >= 4
                ? points.Length - 2
                : points.Length;
            for (int p = 0; p < surfaceCount - 1; p++)
            {
                EditorMapPointSnapshot a = points[p];
                EditorMapPointSnapshot b = points[p + 1];
                segments.Add(new RoomGeometryBlob.Segment(
                    a.X,
                    a.Y,
                    b.X,
                    b.Y,
                    curve.Kind));
            }
        }

        EditorMapNodeVisualSnapshot[] nodes =
            visual.Nodes == null
                ? Array.Empty<EditorMapNodeVisualSnapshot>()
                : (EditorMapNodeVisualSnapshot[])visual.Nodes.Clone();

        return new RoomGeometryBlob(
            roomIndex,
            width,
            height,
            sourceStamp,
            vertices.ToArray(),
            indices.ToArray(),
            segments.ToArray(),
            nodes);
    }

    internal static RoomGeometryBlob BuildNeutral(int roomIndex, float widthTiles = 12f, float heightTiles = 6f)
    {
        List<RoomGeometryBlob.Vertex> vertices = new(4);
        List<int> indices = new(6);
        AddQuad(
            vertices,
            indices,
            0f,
            0f,
            Math.Max(1f, widthTiles),
            Math.Max(1f, heightTiles),
            EditorMapGeometryKind.Air);

        return new RoomGeometryBlob(
            roomIndex,
            widthTiles,
            heightTiles,
            0,
            vertices.ToArray(),
            indices.ToArray(),
            Array.Empty<RoomGeometryBlob.Segment>(),
            Array.Empty<EditorMapNodeVisualSnapshot>());
    }

    private static void AddQuad(
        List<RoomGeometryBlob.Vertex> vertices,
        List<int> indices,
        float x,
        float y,
        float width,
        float height,
        EditorMapGeometryKind kind)
    {
        int first = vertices.Count;
        vertices.Add(new RoomGeometryBlob.Vertex(x, y, kind));
        vertices.Add(new RoomGeometryBlob.Vertex(x + width, y, kind));
        vertices.Add(new RoomGeometryBlob.Vertex(x + width, y + height, kind));
        vertices.Add(new RoomGeometryBlob.Vertex(x, y + height, kind));

        indices.Add(first);
        indices.Add(first + 1);
        indices.Add(first + 2);
        indices.Add(first);
        indices.Add(first + 2);
        indices.Add(first + 3);
    }
}
