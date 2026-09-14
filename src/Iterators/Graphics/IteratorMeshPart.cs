using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>提供多边形、椭圆和描边的部件基类。默认随身体整体变换，可覆写局部变形。</summary>
public abstract class IteratorMeshPart : IteratorGraphicsPart
{
    private readonly Dictionary<SpriteHandle, Vector2[]> _rest = new();
    protected IteratorMeshPart(string name) : base(name) { }
    protected SpriteHandle Polygon(string name, Vector2[] vertices, Color color, int layer)
        => Remember(Register(name, IteratorMesh.Polygon(vertices, color), layer), vertices);
    protected SpriteHandle Ellipse(string name, Vector2 center, float rx, float ry, Color color, int layer, int segments = 32)
    {
        var points = new Vector2[segments + 1]; var triangles = new int[segments * 3]; points[0] = center;
        for (int i = 0; i < segments; i++)
        {
            float a = i * (float)Math.PI * 2f / segments;
            points[i + 1] = center + new Vector2((float)Math.Cos(a) * rx, (float)Math.Sin(a) * ry);
            triangles[i * 3] = 0; triangles[i * 3 + 1] = i + 1; triangles[i * 3 + 2] = (i + 1) % segments + 1;
        }
        return Remember(Register(name, new IteratorMesh(points, triangles, color), layer), points);
    }
    protected SpriteHandle Stroke(string name, Vector2[] path, float width, Color color, int layer, bool closed = false)
    {
        int count = closed ? path.Length + 1 : path.Length;
        var points = new Vector2[count * 2];
        for (int i = 0; i < count; i++)
        {
            int j = i % path.Length;
            Vector2 previous = path[closed ? (j + path.Length - 1) % path.Length : Math.Max(j - 1, 0)];
            Vector2 next = path[closed ? (j + 1) % path.Length : Math.Min(j + 1, path.Length - 1)];
            Vector2 direction = (next - previous).normalized;
            Vector2 normal = new Vector2(-direction.y, direction.x) * (width * 0.5f);
            points[i * 2] = path[j] - normal; points[i * 2 + 1] = path[j] + normal;
        }
        return Remember(Register(name, new IteratorMesh(points, GridTriangles(2, count), color), layer), points);
    }
    protected SpriteHandle OutlinedPolygon(string name, Vector2[] points, Color color, int layer, float width = 0.65f)
    {
        SpriteHandle fill = Polygon(name, points, color, layer);
        Stroke(name + "Edge", points, width, Profile.OutlineColor, layer + 1, true);
        return fill;
    }
    protected SpriteHandle OutlinedEllipse(string name, Vector2 center, float rx, float ry, Color color, int layer)
    {
        Ellipse(name + "Edge", center, rx + 0.5f, ry + 0.5f, Profile.OutlineColor, layer);
        return Ellipse(name, center, rx, ry, color, layer + 1);
    }
    protected Vector2[] Rest(SpriteHandle handle) => _rest[handle];
    protected override void OnDraw(IteratorDrawState state)
    {
        foreach (KeyValuePair<SpriteHandle, Vector2[]> pair in _rest)
            for (int i = 0; i < pair.Value.Length; i++) pair.Key.Mesh.SetVertex(i, state.Local(pair.Value[i]));
    }
    protected void DrawAt(SpriteHandle handle, Vector2 origin, Vector2 right, Vector2 up, float scale)
    {
        Vector2[] rest = _rest[handle];
        for (int i = 0; i < rest.Length; i++) handle.Mesh.SetVertex(i, origin + (right * rest[i].x + up * rest[i].y) * scale);
    }
    protected SpriteHandle Remember(SpriteHandle handle, Vector2[] rest)
    { _rest.Add(handle, (Vector2[])rest.Clone()); return handle; }
    protected static int[] GridTriangles(int columns, int rows)
    {
        var triangles = new int[(columns - 1) * (rows - 1) * 6]; int i = 0;
        for (int y = 0; y < rows - 1; y++)
        for (int x = 0; x < columns - 1; x++)
        {
            int p = y * columns + x;
            triangles[i++] = p; triangles[i++] = p + 1; triangles[i++] = p + columns;
            triangles[i++] = p + 1; triangles[i++] = p + columns + 1; triangles[i++] = p + columns;
        }
        return triangles;
    }
    protected static Vector2[] RingPath(Vector2 center, float rx, float ry, int segments = 48)
    {
        var points = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = i * (float)Math.PI * 2f / segments;
            points[i] = center + new Vector2((float)Math.Cos(a) * rx, (float)Math.Sin(a) * ry);
        }
        return points;
    }
}
