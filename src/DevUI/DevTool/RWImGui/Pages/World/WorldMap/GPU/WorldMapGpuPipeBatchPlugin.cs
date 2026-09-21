using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained GPU batch for static World Map pipe sockets.
///
/// Exit and creature-hole icons used to be composed from many ImGui rectangles/circles every frame.
/// At region overview scale those icons are one of the largest remaining immediate-mode command
/// sources. This layer keeps their centers in map space and emits one colored triangle mesh. Pan is
/// therefore a pure camera transform. Zoom only rebuilds this small marker mesh because the icon
/// language intentionally stays approximately constant in screen pixels.
///
/// Interactive/emphasized room exits remain in ImGui so link creation, hover and selection feedback
/// preserve their existing high-contrast presentation. Node number text is also intentionally left
/// in ImGui until the map-space glyph atlas is introduced.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuPipeBatchPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.PipeBatch";
    public const string PluginName = "DryCycle DevTool GPU World Map Pipe Batch";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => WorldMapGpuPipeBatch.Enable(Logger),
            WorldMapGpuPipeBatch.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            WorldMapGpuPipeBatch.Disable);
}

internal static class WorldMapGpuPipeBatch
{
    private const int RenderLayer = 31;

    private static ManualLogSource log;

    private static GameObject root;
    private static Mesh mesh;
    private static MeshRenderer renderer;
    private static Material material;
    private static Shader shader;
    private static string region = string.Empty;
    private static int lastHash = int.MinValue;
    private static bool enabled;
    private static bool roomPipeGpuReady;
    private static bool creaturePipeGpuReady;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("GPU World Map pipe batching enabled through direct scene/view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        DestroyRenderer();
        region = string.Empty;
        lastHash = int.MinValue;
        roomPipeGpuReady = false;
        creaturePipeGpuReady = false;
        enabled = false;
        log = null;
    }

    internal static void AfterSceneApply(
        WorldMapGpuScene.FrameState frame,
        EditorSession session)
    {
        if (!enabled || !WorldMapGpuScene.Ready || frame?.Visible != true ||
            frame.Snapshot?.Available != true || session?.ToolMode != EditorToolMode.Map)
        {
            SetRendererVisible(false);
            roomPipeGpuReady = false;
            creaturePipeGpuReady = false;
            return;
        }

        try
        {
            Reconcile(frame);
        }
        catch (Exception error)
        {
            roomPipeGpuReady = false;
            creaturePipeGpuReady = false;
            SetRendererVisible(false);
            log?.LogDebug("GPU pipe batch skipped: " + error.Message);
        }
    }

    internal static bool ShouldDrawRoomPipeSocket(bool emphasized) =>
        !enabled || !roomPipeGpuReady || emphasized;

    internal static bool ShouldDrawCreaturePipeSocket() =>
        !enabled || !creaturePipeGpuReady;

    internal static void SuppressScreenPresentation()
    {
        roomPipeGpuReady = false;
        creaturePipeGpuReady = false;
        SetRendererVisible(false);
        if (root != null && root.activeSelf)
            root.SetActive(false);
    }

    private static void Reconcile(WorldMapGpuScene.FrameState frame)
    {
        string nextRegion = NormalizeRegion(frame.Region);
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            region = nextRegion;
            lastHash = int.MinValue;
        }

        bool roomVisible = WorldMapPipeLayers.RoomPipesVisible;
        bool creatureVisible = WorldMapPipeLayers.CreaturePipesVisible;
        bool cacheComplete = WorldMapGpuCache.HasCompleteCachedData(frame.Snapshot);
        roomPipeGpuReady = cacheComplete && roomVisible;
        creaturePipeGpuReady = cacheComplete && creatureVisible;

        if (!cacheComplete || (!roomVisible && !creatureVisible))
        {
            SetRendererVisible(false);
            return;
        }

        int hash = ComputeHash(frame, roomVisible, creatureVisible);
        if (hash != lastHash)
        {
            EnsureRenderer();
            BuildMesh(frame, roomVisible, creatureVisible);
            lastHash = hash;
        }
        SetRendererVisible(true);
    }

    private static int ComputeHash(
        WorldMapGpuScene.FrameState frame,
        bool roomVisible,
        bool creatureVisible)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ frame.LayoutHash;
            hash = hash * 397 ^ frame.LayerMask;
            hash = hash * 397 ^ WorldMapGpuCache.Generation;
            hash = hash * 397 ^ Quantize(frame.Zoom, 1000f);
            hash = hash * 397 ^ (roomVisible ? 1 : 0);
            hash = hash * 397 ^ (creatureVisible ? 1 : 0);

            EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;
                EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
                for (int n = 0; n < nodes.Length; n++)
                {
                    EditorMapRoomNodeSnapshot node = nodes[n];
                    if (!node.Exit) continue;
                    hash = hash * 397 ^ node.NodeIndex;
                    hash = hash * 397 ^ node.ConnectedRoomIndex;
                }
            }
            EditorMapConnectionSnapshot[] connections =
                frame.Snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
            hash = hash * 397 ^ connections.Length;
            for (int i = 0; i < connections.Length; i++)
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(connections[i]?.ConnectionId ?? string.Empty);
            return hash;
        }
    }

    private static void BuildMesh(
        WorldMapGpuScene.FrameState frame,
        bool roomVisible,
        bool creatureVisible)
    {
        Dictionary<int, WorldMapGpuScene.RoomPlacement> placements = new(frame.Placements?.Length ?? 0);
        if (frame.Placements != null)
        {
            for (int i = 0; i < frame.Placements.Length; i++)
                placements[frame.Placements[i].RoomIndex] = frame.Placements[i];
        }

        HashSet<long> connectedEndpoints = new();
        EditorMapConnectionSnapshot[] connections =
            frame.Snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null) continue;
            if (connection.FromNodeIndex >= 0)
                connectedEndpoints.Add(EndpointKey(connection.FromRoomIndex, connection.FromNodeIndex));
            if (connection.ToNodeIndex >= 0)
                connectedEndpoints.Add(EndpointKey(connection.ToRoomIndex, connection.ToNodeIndex));
        }

        List<Vector3> vertices = new(8192);
        List<Color32> colors = new(8192);
        List<int> triangles = new(12288);
        BoundsBuilder bounds = new();
        float zoom = Math.Max(0.20f, frame.Zoom);
        float pixelToMap = 1f / zoom;
        float iconScaleGold = zoom < 0.30f ? 0.92f : 1f;
        float iconScaleGreen = zoom < 0.30f ? 0.90f : 1f;

        EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || (frame.LayerMask & (1 << room.Layer)) == 0 ||
                !placements.TryGetValue(room.RoomIndex, out WorldMapGpuScene.RoomPlacement placement) ||
                !WorldMapGpuCache.TryGetRoom(room.RoomIndex, out WorldMapGpuCache.RoomBake bake) ||
                bake?.Visual?.Available != true)
                continue;

            float roomHeight = Math.Max(1f, bake.Visual.HeightTiles) * WorldMapGpuScene.TileDisplaySize;
            float z = 2f + room.Layer * 0.02f;

            if (roomVisible)
            {
                EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
                for (int n = 0; n < nodes.Length; n++)
                {
                    EditorMapRoomNodeSnapshot node = nodes[n];
                    if (!node.Exit || !WorldMapGpuCache.TryGetExit(
                            room.RoomIndex,
                            node.NodeIndex,
                            out WorldMapShortcutPresentation.ShortcutMarker marker))
                        continue;

                    Vector2 center = new(
                        placement.X + marker.X * WorldMapGpuScene.TileDisplaySize,
                        -(placement.Y + roomHeight - marker.Y * WorldMapGpuScene.TileDisplaySize));
                    bool connected = node.ConnectedRoomIndex >= 0 ||
                                     connectedEndpoints.Contains(EndpointKey(room.RoomIndex, node.NodeIndex));
                    AddRoomPipe(
                        vertices, colors, triangles, ref bounds,
                        center, z, pixelToMap, iconScaleGold, connected);
                }
            }

            if (creatureVisible)
            {
                WorldMapShortcutPresentation.ShortcutMarker[] holes =
                    bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
                for (int h = 0; h < holes.Length; h++)
                {
                    WorldMapShortcutPresentation.ShortcutMarker marker = holes[h];
                    Vector2 center = new(
                        placement.X + marker.X * WorldMapGpuScene.TileDisplaySize,
                        -(placement.Y + roomHeight - marker.Y * WorldMapGpuScene.TileDisplaySize));
                    AddCreaturePipe(
                        vertices, colors, triangles, ref bounds,
                        center, z + 0.004f, pixelToMap, iconScaleGreen);
                }
            }
        }

        EnsureRenderer();
        mesh.Clear();
        mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.bounds = bounds.ToBounds();
    }

    private static void AddRoomPipe(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float z,
        float pixelToMap,
        float iconScale,
        bool connected)
    {
        float half = (connected ? 9.2f : 8.4f) * iconScale * pixelToMap;
        float halo = half + 3.3f * iconScale * pixelToMap;
        float bevel = Math.Max(1.2f, 2.7f * iconScale) * pixelToMap;
        float border = Math.Max(2f, 2.4f * iconScale) * pixelToMap;
        float innerHalf = half - 2.7f * iconScale * pixelToMap;
        float innerBorder = Math.Max(1f, 1.2f * iconScale) * pixelToMap;
        float holeHalf = (connected ? 3.7f : 3.25f) * iconScale * pixelToMap;
        float notchHalf = Math.Max(1f, 1.35f * iconScale) * pixelToMap;
        float notchDepth = Math.Max(2.7f, 3.7f * iconScale) * pixelToMap;

        Color32 shadow = new(5, 5, 6, 255);
        Color32 bright = new(255, 209, 77, 255);
        Color32 mid = new(219, 158, 43, 255);
        Color32 dark = new(87, 56, 14, 255);

        AddOctagonFill(vertices, colors, triangles, ref bounds, center, halo, bevel + pixelToMap, z, shadow);
        AddOctagonFill(vertices, colors, triangles, ref bounds, center, half, bevel, z + 0.001f, connected ? bright : dark);
        AddOctagonRing(vertices, colors, triangles, ref bounds, center, half, Math.Max(0.1f, half - border), bevel, z + 0.002f, mid);
        AddOctagonRing(
            vertices, colors, triangles, ref bounds,
            center, innerHalf, Math.Max(0.1f, innerHalf - innerBorder),
            Math.Max(pixelToMap, bevel * 0.55f), z + 0.003f, mid);
        AddOctagonFill(vertices, colors, triangles, ref bounds, center, holeHalf, holeHalf * 0.42f, z + 0.004f, shadow);
        AddNotches(vertices, colors, triangles, ref bounds, center, half, notchHalf, notchDepth, z + 0.005f, shadow);
    }

    private static void AddCreaturePipe(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float z,
        float pixelToMap,
        float iconScale)
    {
        float half = 7.8f * iconScale * pixelToMap;
        float halo = half + 2.8f * iconScale * pixelToMap;
        float bevel = Math.Max(1.1f, 2.4f * iconScale) * pixelToMap;
        float border = Math.Max(2f, 2.3f * iconScale) * pixelToMap;
        float innerHalf = half - 2.5f * iconScale * pixelToMap;
        float innerBorder = Math.Max(1f, 1.2f * iconScale) * pixelToMap;
        float holeRadius = 2.8f * iconScale * pixelToMap;
        float notchHalf = Math.Max(0.9f, 1.15f * iconScale) * pixelToMap;
        float notchDepth = Math.Max(2.4f, 3.2f * iconScale) * pixelToMap;

        Color32 shadow = new(5, 5, 6, 255);
        Color32 bright = new(82, 255, 117, 255);
        Color32 mid = new(26, 184, 71, 255);
        Color32 dark = new(6, 61, 20, 255);

        AddOctagonFill(vertices, colors, triangles, ref bounds, center, halo, bevel + pixelToMap, z, shadow);
        AddOctagonFill(vertices, colors, triangles, ref bounds, center, half, bevel, z + 0.001f, dark);
        AddOctagonRing(vertices, colors, triangles, ref bounds, center, half, Math.Max(0.1f, half - border), bevel, z + 0.002f, bright);
        AddOctagonRing(
            vertices, colors, triangles, ref bounds,
            center, innerHalf, Math.Max(0.1f, innerHalf - innerBorder),
            Math.Max(pixelToMap, bevel * 0.55f), z + 0.003f, mid);
        AddCircleFill(vertices, colors, triangles, ref bounds, center, holeRadius, z + 0.004f, shadow, 12);
        AddNotches(vertices, colors, triangles, ref bounds, center, half, notchHalf, notchDepth, z + 0.005f, shadow);
    }

    private static void AddOctagonFill(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float half,
        float bevel,
        float z,
        Color32 color)
    {
        bevel = Math.Max(0f, Math.Min(half, bevel));
        Vector2[] ring = Octagon(center, half, bevel);
        int c = vertices.Count;
        vertices.Add(new Vector3(center.x, center.y, z));
        colors.Add(color);
        for (int i = 0; i < 8; i++)
        {
            vertices.Add(new Vector3(ring[i].x, ring[i].y, z));
            colors.Add(color);
        }
        for (int i = 0; i < 8; i++)
        {
            triangles.Add(c);
            triangles.Add(c + 1 + i);
            triangles.Add(c + 1 + ((i + 1) & 7));
        }
        bounds.Add(center, half, z);
    }

    private static void AddOctagonRing(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float outerHalf,
        float innerHalf,
        float bevel,
        float z,
        Color32 color)
    {
        outerHalf = Math.Max(0.1f, outerHalf);
        innerHalf = Math.Max(0.05f, Math.Min(outerHalf - 0.02f, innerHalf));
        Vector2[] outer = Octagon(center, outerHalf, Math.Min(outerHalf, bevel));
        float innerBevel = Math.Min(innerHalf, bevel * innerHalf / outerHalf);
        Vector2[] inner = Octagon(center, innerHalf, innerBevel);
        int start = vertices.Count;
        for (int i = 0; i < 8; i++)
        {
            vertices.Add(new Vector3(outer[i].x, outer[i].y, z));
            colors.Add(color);
        }
        for (int i = 0; i < 8; i++)
        {
            vertices.Add(new Vector3(inner[i].x, inner[i].y, z));
            colors.Add(color);
        }
        for (int i = 0; i < 8; i++)
        {
            int next = (i + 1) & 7;
            int o0 = start + i;
            int o1 = start + next;
            int i0 = start + 8 + i;
            int i1 = start + 8 + next;
            triangles.Add(o0); triangles.Add(o1); triangles.Add(i1);
            triangles.Add(o0); triangles.Add(i1); triangles.Add(i0);
        }
        bounds.Add(center, outerHalf, z);
    }

    private static void AddCircleFill(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float radius,
        float z,
        Color32 color,
        int segments)
    {
        int start = vertices.Count;
        vertices.Add(new Vector3(center.x, center.y, z));
        colors.Add(color);
        for (int i = 0; i < segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            vertices.Add(new Vector3(
                center.x + Mathf.Cos(angle) * radius,
                center.y + Mathf.Sin(angle) * radius,
                z));
            colors.Add(color);
        }
        for (int i = 0; i < segments; i++)
        {
            triangles.Add(start);
            triangles.Add(start + 1 + i);
            triangles.Add(start + 1 + ((i + 1) % segments));
        }
        bounds.Add(center, radius, z);
    }

    private static void AddNotches(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        Vector2 center,
        float half,
        float notchHalf,
        float notchDepth,
        float z,
        Color32 color)
    {
        AddQuad(vertices, colors, triangles, ref bounds,
            center.x - notchHalf, center.y + half - notchDepth,
            center.x + notchHalf, center.y + half + 0.5f * notchHalf,
            z, color);
        AddQuad(vertices, colors, triangles, ref bounds,
            center.x - notchHalf, center.y - half - 0.5f * notchHalf,
            center.x + notchHalf, center.y - half + notchDepth,
            z, color);
        AddQuad(vertices, colors, triangles, ref bounds,
            center.x - half - 0.5f * notchHalf, center.y - notchHalf,
            center.x - half + notchDepth, center.y + notchHalf,
            z, color);
        AddQuad(vertices, colors, triangles, ref bounds,
            center.x + half - notchDepth, center.y - notchHalf,
            center.x + half + 0.5f * notchHalf, center.y + notchHalf,
            z, color);
    }

    private static void AddQuad(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> triangles,
        ref BoundsBuilder bounds,
        float x0,
        float y0,
        float x1,
        float y1,
        float z,
        Color32 color)
    {
        float minX = Math.Min(x0, x1);
        float maxX = Math.Max(x0, x1);
        float minY = Math.Min(y0, y1);
        float maxY = Math.Max(y0, y1);
        int v = vertices.Count;
        vertices.Add(new Vector3(minX, minY, z));
        vertices.Add(new Vector3(maxX, minY, z));
        vertices.Add(new Vector3(maxX, maxY, z));
        vertices.Add(new Vector3(minX, maxY, z));
        colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
        triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
        triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 3);
        bounds.Add(new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f),
            Math.Max(maxX - minX, maxY - minY) * 0.5f, z);
    }

    private static Vector2[] Octagon(Vector2 center, float half, float bevel)
    {
        return new[]
        {
            new Vector2(center.x - half + bevel, center.y + half),
            new Vector2(center.x + half - bevel, center.y + half),
            new Vector2(center.x + half, center.y + half - bevel),
            new Vector2(center.x + half, center.y - half + bevel),
            new Vector2(center.x + half - bevel, center.y - half),
            new Vector2(center.x - half + bevel, center.y - half),
            new Vector2(center.x - half, center.y - half + bevel),
            new Vector2(center.x - half, center.y + half - bevel)
        };
    }

    private static void EnsureRenderer()
    {
        if (root != null && mesh != null && renderer != null && material != null) return;
        shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("UI/Default");
        if (shader == null) throw new InvalidOperationException("No unlit vertex-color shader is available.");
        root = new GameObject("DryCycle.WorldMapGpuPipes")
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = RenderLayer
        };
        MeshFilter filter = root.AddComponent<MeshFilter>();
        renderer = root.AddComponent<MeshRenderer>();
        mesh = new Mesh
        {
            name = "DryCycle World Map Pipe Batch",
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.MarkDynamic();
        filter.sharedMesh = mesh;
        material = new Material(shader)
        {
            name = "DryCycle World Map Pipe Material",
            hideFlags = HideFlags.HideAndDontSave,
            mainTexture = Texture2D.whiteTexture,
            enableInstancing = true
        };
        renderer.sharedMaterial = material;
        renderer.sortingOrder = 150;
    }

    private static void SetRendererVisible(bool visible)
    {
        if (renderer != null) renderer.enabled = visible;
    }

    private static void DestroyRenderer()
    {
        if (mesh != null) UnityEngine.Object.Destroy(mesh);
        if (material != null) UnityEngine.Object.Destroy(material);
        if (root != null) UnityEngine.Object.Destroy(root);
        mesh = null;
        material = null;
        renderer = null;
        root = null;
        shader = null;
    }

    private static long EndpointKey(int roomIndex, int nodeIndex) =>
        ((long)(uint)roomIndex << 32) | (uint)nodeIndex;

    private static int Quantize(float value, float scale) => (int)Math.Round(value * scale);

    private static string NormalizeRegion(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private struct BoundsBuilder
    {
        private bool initialized;
        private Vector3 min;
        private Vector3 max;

        internal void Add(Vector2 center, float radius, float z)
        {
            Vector3 nextMin = new(center.x - radius, center.y - radius, z - 0.02f);
            Vector3 nextMax = new(center.x + radius, center.y + radius, z + 0.02f);
            if (!initialized)
            {
                min = nextMin;
                max = nextMax;
                initialized = true;
                return;
            }
            min = Vector3.Min(min, nextMin);
            max = Vector3.Max(max, nextMax);
        }

        internal Bounds ToBounds()
        {
            if (!initialized) return new Bounds(Vector3.zero, Vector3.one * 0.01f);
            Bounds result = new();
            result.SetMinMax(min, max);
            return result;
        }
    }
}
