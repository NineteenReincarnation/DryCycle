using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>每实例独立的网格数据。拓扑和 UV 创建时复制，世界坐标及顶点色由部件更新；无需 Unity 渲染器即可预览。</summary>
public sealed class IteratorMesh
{
    internal readonly Vector2[] Points;
    internal readonly Color[] Tints;
    internal readonly int[] Indices;
    internal readonly Vector2[] UVs;
    internal SpriteRegistry Registry;

    public IteratorMesh(IReadOnlyList<Vector2> vertices, IReadOnlyList<int> triangles, Color color,
        IReadOnlyList<Vector2> textureCoordinates = null)
    {
        if (vertices == null || vertices.Count < 3 || vertices.Count > 8192) throw new ArgumentException("Supply 3..8192 vertices.", nameof(vertices));
        if (triangles == null || triangles.Count == 0 || triangles.Count % 3 != 0) throw new ArgumentException("Supply triangle triples.", nameof(triangles));
        if (textureCoordinates != null && textureCoordinates.Count != vertices.Count) throw new ArgumentException("UV count must match vertices.");
        Points = new Vector2[vertices.Count]; Tints = new Color[vertices.Count]; UVs = new Vector2[vertices.Count];
        Indices = new int[triangles.Count];
        for (int i = 0; i < Points.Length; i++)
        {
            SetVertex(i, vertices[i]); SetColor(i, color);
            UVs[i] = textureCoordinates == null ? new Vector2(0.5f, 0.5f) : textureCoordinates[i];
            if (!GraphicsProfile.Finite(UVs[i].x) || !GraphicsProfile.Finite(UVs[i].y)) throw new ArgumentException("UVs must be finite.");
        }
        for (int i = 0; i < Indices.Length; i++)
        {
            int index = triangles[i];
            if (index < 0 || index >= Points.Length) throw new ArgumentOutOfRangeException(nameof(triangles));
            Indices[i] = index;
        }
        Vertices = Array.AsReadOnly(Points); Colors = Array.AsReadOnly(Tints); Triangles = Array.AsReadOnly(Indices);
    }

    public IReadOnlyList<Vector2> Vertices { get; }
    public IReadOnlyList<Color> Colors { get; }
    public IReadOnlyList<int> Triangles { get; }
    public void SetVertex(int index, Vector2 position)
    {
        if (!GraphicsProfile.Finite(position.x) || !GraphicsProfile.Finite(position.y)) throw new ArgumentException("Mesh vertices must be finite.");
        Points[index] = position;
    }
    public void SetColor(int index, Color color) => Tints[index] = GraphicsProfile.ValidColor(color);

    /// <summary>简单多边形，支持凹多边形；不支持相交边或洞。</summary>
    public static IteratorMesh Polygon(IReadOnlyList<Vector2> points, Color color)
    {
        if (points == null || points.Count < 3 || points.Count > 512) throw new ArgumentException("Supply a simple polygon with 3..512 points.");
        var remaining = new List<int>();
        float area = 0f;
        for (int i = 0; i < points.Count; i++) { remaining.Add(i); area += Cross(points[i], points[(i + 1) % points.Count]); }
        float sign = area >= 0f ? 1f : -1f;
        var indices = new List<int>();
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int j = 0; j < remaining.Count; j++)
            {
                int a = remaining[(j + remaining.Count - 1) % remaining.Count], b = remaining[j], c = remaining[(j + 1) % remaining.Count];
                if (Cross(points[b] - points[a], points[c] - points[b]) * sign <= 0.00001f) continue;
                bool inside = false;
                foreach (int k in remaining)
                {
                    if (k == a || k == b || k == c) continue;
                    if (Cross(points[b] - points[a], points[k] - points[a]) * sign >= 0f &&
                        Cross(points[c] - points[b], points[k] - points[b]) * sign >= 0f &&
                        Cross(points[a] - points[c], points[k] - points[c]) * sign >= 0f) { inside = true; break; }
                }
                if (inside) continue;
                indices.Add(a); indices.Add(b); indices.Add(c); remaining.RemoveAt(j); clipped = true; break;
            }
            if (!clipped) throw new ArgumentException("Degenerate or intersecting polygon.");
        }
        indices.AddRange(remaining);
        return new IteratorMesh(points, indices, color);
    }

    internal static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
