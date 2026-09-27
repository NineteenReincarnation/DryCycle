using System;
using System.IO;

namespace BepInEx
{
    internal static class Paths
    {
        internal static string CachePath { get; set; } = Path.GetTempPath();
    }
}

namespace UnityEngine
{
    internal static class Application
    {
        internal static string persistentDataPath { get; set; } = Path.GetTempPath();
    }
}

namespace DryCycle
{
    internal static class Plugin
    {
        internal static TestLogger Logger { get; } = new();
    }

    internal sealed class TestLogger
    {
        internal void LogWarning(string message)
        {
        }
    }
}

namespace DryCycle.DevUI.DevTool.Map
{
    public enum EditorMapGeometryKind
    {
        Air = 0,
        BackWall = 1,
        Solid = 2,
        Structure = 3,
        Shortcut = 4,
        Transport = 5,
        Water = 6,
        LocalTerrain = 7,
        CurvedSlope = 8,
        QuicksandBody = 9,
        QuicksandMaterial = 10
    }

    public readonly struct EditorMapPointSnapshot
    {
        public EditorMapPointSnapshot(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; }
        public float Y { get; }
    }

    public readonly struct EditorMapRectSnapshot
    {
        public EditorMapRectSnapshot(
            float x,
            float y,
            float width,
            float height,
            EditorMapGeometryKind kind)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Kind = kind;
        }

        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }
        public EditorMapGeometryKind Kind { get; }
    }

    public sealed class EditorMapPolylineSnapshot
    {
        public EditorMapGeometryKind Kind { get; init; }
        public bool Closed { get; init; }
        public EditorMapPointSnapshot[] Points { get; init; } =
            Array.Empty<EditorMapPointSnapshot>();
    }

    public sealed class EditorMapRoomVisualSnapshot
    {
        public static readonly EditorMapRoomVisualSnapshot Empty = new();

        public bool Available { get; init; }
        public bool DetailedRasterAvailable { get; init; }
        public float WidthTiles { get; init; } = 12f;
        public float HeightTiles { get; init; } = 6f;
        public EditorMapRectSnapshot[] RasterRuns { get; init; } =
            Array.Empty<EditorMapRectSnapshot>();
        public EditorMapRectSnapshot[] TerrainRuns { get; init; } =
            Array.Empty<EditorMapRectSnapshot>();
        public EditorMapPolylineSnapshot[] Curves { get; init; } =
            Array.Empty<EditorMapPolylineSnapshot>();
        public EditorMapNodeVisualSnapshot[] Nodes { get; init; } =
            Array.Empty<EditorMapNodeVisualSnapshot>();
    }

    public readonly struct EditorMapNodeVisualSnapshot
    {
        public EditorMapNodeVisualSnapshot(
            int nodeIndex,
            float x,
            float y)
        {
            NodeIndex = nodeIndex;
            X = x;
            Y = y;
        }

        public int NodeIndex { get; }
        public float X { get; }
        public float Y { get; }
    }
}
