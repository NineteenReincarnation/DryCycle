using System;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Immutable CPU-side room geometry prepared for retained rendering.
/// It is derived presentation data only and never persists back into authoring files.
/// </summary>
internal sealed class RoomGeometryBlob
{
    internal readonly struct Vertex
    {
        internal Vertex(float x, float y, EditorMapGeometryKind kind)
        {
            X = x;
            Y = y;
            Kind = kind;
        }

        internal float X { get; }
        internal float Y { get; }
        internal EditorMapGeometryKind Kind { get; }
    }

    internal readonly struct Segment
    {
        internal Segment(float ax, float ay, float bx, float by, EditorMapGeometryKind kind)
        {
            AX = ax;
            AY = ay;
            BX = bx;
            BY = by;
            Kind = kind;
        }

        internal float AX { get; }
        internal float AY { get; }
        internal float BX { get; }
        internal float BY { get; }
        internal EditorMapGeometryKind Kind { get; }
    }

    internal static readonly RoomGeometryBlob Empty = new(
        -1,
        12f,
        6f,
        0,
        Array.Empty<Vertex>(),
        Array.Empty<int>(),
        Array.Empty<Vertex>(),
        Array.Empty<int>(),
        Array.Empty<Segment>(),
        Array.Empty<EditorMapNodeVisualSnapshot>());

    internal RoomGeometryBlob(
        int roomIndex,
        float widthTiles,
        float heightTiles,
        int sourceStamp,
        Vertex[] vertices,
        int[] triangleIndices,
        Vertex[] authoredTerrainVertices,
        int[] authoredTerrainTriangleIndices,
        Segment[] curveSegments,
        EditorMapNodeVisualSnapshot[] nodes)
    {
        RoomIndex = roomIndex;
        WidthTiles = Math.Max(1f, widthTiles);
        HeightTiles = Math.Max(1f, heightTiles);
        SourceStamp = sourceStamp;
        Vertices = vertices ?? Array.Empty<Vertex>();
        TriangleIndices = triangleIndices ?? Array.Empty<int>();
        AuthoredTerrainVertices = authoredTerrainVertices ?? Array.Empty<Vertex>();
        AuthoredTerrainTriangleIndices = authoredTerrainTriangleIndices ?? Array.Empty<int>();
        CurveSegments = curveSegments ?? Array.Empty<Segment>();
        Nodes = nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
    }

    internal int RoomIndex { get; }
    internal float WidthTiles { get; }
    internal float HeightTiles { get; }
    internal int SourceStamp { get; }
    internal Vertex[] Vertices { get; }
    internal int[] TriangleIndices { get; }
    internal Vertex[] AuthoredTerrainVertices { get; }
    internal int[] AuthoredTerrainTriangleIndices { get; }
    internal Segment[] CurveSegments { get; }
    internal EditorMapNodeVisualSnapshot[] Nodes { get; }

    internal int TriangleCount => TriangleIndices.Length / 3;
}
