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
        List<RoomGeometryBlob.Vertex> authoredTerrainVertices = new();
        List<int> authoredTerrainIndices = new();

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

            float x0 = Math.Max(0f, run.X);
            float y0 = Math.Max(0f, run.Y);
            float x1 = Math.Min(width, run.X + run.Width);
            float y1 = Math.Min(height, run.Y + run.Height);
            if (x1 <= x0 || y1 <= y0)
                continue;

            AddQuad(
                vertices,
                indices,
                x0,
                y0,
                x1 - x0,
                y1 - y0,
                run.Kind);
        }

        EditorMapPolylineSnapshot[] curves =
            visual.Curves ?? Array.Empty<EditorMapPolylineSnapshot>();
        bool exactAuthoredFill = false;

        // New authored-curve snapshots publish the real per-segment fill quads. Build the retained
        // overlay from those polygons instead of the old axis-aligned TerrainRuns approximation;
        // the latter is what turned curved surfaces into large rectangular black slabs.
        for (int i = 0; i < curves.Length; i++)
        {
            EditorMapPolylineSnapshot curve = curves[i];
            EditorMapPointSnapshot[] points =
                curve?.Points ?? Array.Empty<EditorMapPointSnapshot>();
            if (curve?.Closed != true || points.Length < 3)
                continue;

            if (AddClippedFillPolygon(
                    authoredTerrainVertices,
                    authoredTerrainIndices,
                    points,
                    width,
                    height,
                    curve.Kind))
            {
                AddClippedFillPolygon(
                    vertices,
                    indices,
                    points,
                    width,
                    height,
                    curve.Kind);
                exactAuthoredFill = true;
            }
        }

        // Persistent V3 caches written before exact fill polygons existed can still contain only
        // TerrainRuns. Keep a bounded compatibility fallback, but new/live rooms never use it.
        if (!exactAuthoredFill)
        {
            EditorMapRectSnapshot[] authoredRuns =
                visual.TerrainRuns ?? Array.Empty<EditorMapRectSnapshot>();
            for (int i = 0; i < authoredRuns.Length; i++)
            {
                EditorMapRectSnapshot run = authoredRuns[i];
                if (run.Width <= 0f || run.Height <= 0f || run.Kind == EditorMapGeometryKind.Air)
                    continue;

                float x0 = Math.Max(0f, run.X);
                float y0 = Math.Max(0f, run.Y);
                float x1 = Math.Min(width, run.X + run.Width);
                float y1 = Math.Min(height, run.Y + run.Height);
                if (x1 <= x0 || y1 <= y0)
                    continue;

                AddQuad(
                    authoredTerrainVertices,
                    authoredTerrainIndices,
                    x0,
                    y0,
                    x1 - x0,
                    y1 - y0,
                    run.Kind);
                AddQuad(
                    vertices,
                    indices,
                    x0,
                    y0,
                    x1 - x0,
                    y1 - y0,
                    run.Kind);
            }
        }

        List<RoomGeometryBlob.Segment> segments = new();
        for (int i = 0; i < curves.Length; i++)
        {
            EditorMapPolylineSnapshot curve = curves[i];
            EditorMapPointSnapshot[] points = curve?.Points ?? Array.Empty<EditorMapPointSnapshot>();
            if (curve?.Closed == true || points.Length < 2)
                continue;

            for (int p = 0; p < points.Length - 1; p++)
            {
                EditorMapPointSnapshot a = points[p];
                EditorMapPointSnapshot b = points[p + 1];
                if (!TryClipSegmentToRoom(
                        a.X,
                        a.Y,
                        b.X,
                        b.Y,
                        width,
                        height,
                        out float ax,
                        out float ay,
                        out float bx,
                        out float by))
                    continue;

                segments.Add(new RoomGeometryBlob.Segment(
                    ax,
                    ay,
                    bx,
                    by,
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
            authoredTerrainVertices.ToArray(),
            authoredTerrainIndices.ToArray(),
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
            Array.Empty<RoomGeometryBlob.Vertex>(),
            Array.Empty<int>(),
            Array.Empty<RoomGeometryBlob.Segment>(),
            Array.Empty<EditorMapNodeVisualSnapshot>());
    }

    private static bool AddClippedFillPolygon(
        List<RoomGeometryBlob.Vertex> vertices,
        List<int> indices,
        EditorMapPointSnapshot[] source,
        float width,
        float height,
        EditorMapGeometryKind kind)
    {
        if (vertices == null ||
            indices == null ||
            source == null ||
            source.Length < 3 ||
            kind == EditorMapGeometryKind.Air)
            return false;

        List<EditorMapPointSnapshot> polygon =
            new(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            EditorMapPointSnapshot point = source[i];
            polygon.Add(new EditorMapPointSnapshot(
                Math.Max(0f, Math.Min(width, point.X)),
                Math.Max(0f, Math.Min(height, point.Y))));
        }

        RemoveDuplicatePolygonPoints(polygon);
        if (polygon.Count < 3)
            return false;

        float twiceArea = 0f;
        for (int i = 0; i < polygon.Count; i++)
        {
            EditorMapPointSnapshot a = polygon[i];
            EditorMapPointSnapshot b = polygon[(i + 1) % polygon.Count];
            twiceArea += a.X * b.Y - a.Y * b.X;
        }
        if (Math.Abs(twiceArea) < 0.0005f)
            return false;

        int first = vertices.Count;
        for (int i = 0; i < polygon.Count; i++)
            vertices.Add(new RoomGeometryBlob.Vertex(
                polygon[i].X,
                polygon[i].Y,
                kind));

        for (int i = 1; i < polygon.Count - 1; i++)
        {
            indices.Add(first);
            indices.Add(first + i);
            indices.Add(first + i + 1);
        }

        return true;
    }

    private static void RemoveDuplicatePolygonPoints(
        List<EditorMapPointSnapshot> points)
    {
        if (points == null || points.Count < 2)
            return;

        for (int i = points.Count - 1; i > 0; i--)
        {
            EditorMapPointSnapshot a = points[i];
            EditorMapPointSnapshot b = points[i - 1];
            if (Math.Abs(a.X - b.X) <= 0.0001f &&
                Math.Abs(a.Y - b.Y) <= 0.0001f)
                points.RemoveAt(i);
        }

        if (points.Count >= 2)
        {
            EditorMapPointSnapshot first = points[0];
            EditorMapPointSnapshot last = points[points.Count - 1];
            if (Math.Abs(first.X - last.X) <= 0.0001f &&
                Math.Abs(first.Y - last.Y) <= 0.0001f)
                points.RemoveAt(points.Count - 1);
        }
    }

    private static bool TryClipSegmentToRoom(
        float ax,
        float ay,
        float bx,
        float by,
        float width,
        float height,
        out float clippedAx,
        out float clippedAy,
        out float clippedBx,
        out float clippedBy)
    {
        clippedAx = ax;
        clippedAy = ay;
        clippedBx = bx;
        clippedBy = by;

        float dx = bx - ax;
        float dy = by - ay;
        float t0 = 0f;
        float t1 = 1f;

        if (!ClipTest(-dx, ax, ref t0, ref t1) ||
            !ClipTest(dx, width - ax, ref t0, ref t1) ||
            !ClipTest(-dy, ay, ref t0, ref t1) ||
            !ClipTest(dy, height - ay, ref t0, ref t1))
            return false;

        clippedAx = ax + dx * t0;
        clippedAy = ay + dy * t0;
        clippedBx = ax + dx * t1;
        clippedBy = ay + dy * t1;
        return true;
    }

    private static bool ClipTest(
        float p,
        float q,
        ref float t0,
        ref float t1)
    {
        if (Math.Abs(p) <= 0.000001f)
            return q >= 0f;

        float r = q / p;
        if (p < 0f)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }

        return true;
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
