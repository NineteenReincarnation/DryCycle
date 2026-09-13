using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Second-stage retained renderer optimization for the GPU World Map.
///
/// The first GPU scene moved room rasterization off ImGui, but its compatibility rebuild path still
/// rebuilt every room chunk whenever one room moved. This layer replaces that path with a retained
/// room table and dirty chunk membership. A room drag therefore uploads only the old/new spatial
/// chunk touched by that room. Stable chunks do not allocate arrays, rewrite Mesh data, or scan the
/// legacy MapPage.
///
/// It also replaces the old per-frame source hash (which linearly searched MapPage.subNodes once per
/// room) with an O(room count) revision hash driven by WorldMapGpuCache.Generation. The persistent
/// baker owns source validation, so the renderer only needs to react when that authoritative cache
/// revision changes.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRetainedOptimizerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RetainedOptimizer";
    public const string PluginName = "DryCycle DevTool GPU World Map Retained Optimizer";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRetainedOptimizer.Enable(Logger);
    private void OnDisable() => WorldMapGpuRetainedOptimizer.Disable();
}

internal static class WorldMapGpuRetainedOptimizer
{
    private const int RenderLayer = 31;
    private const float SpatialChunkSize = 512f;

    private delegate int OrigComputeRoomSourceHash(MapPage page, WorldMapGpuScene.FrameState frame);
    private delegate int HookComputeRoomSourceHash(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame);

    private delegate void OrigRebuildRoomBatches(MapPage page, WorldMapGpuScene.FrameState frame);
    private delegate void HookRebuildRoomBatches(
        OrigRebuildRoomBatches orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame);

    private delegate void OrigApplyLayerVisibility(int layerMask);
    private delegate void HookApplyLayerVisibility(OrigApplyLayerVisibility orig, int layerMask);

    private delegate int OrigGetInt();
    private delegate int HookGetInt(OrigGetInt orig);

    private readonly struct BatchKey : IEquatable<BatchKey>
    {
        internal BatchKey(int textureId, int layer, int cellX, int cellY, bool overlay)
        {
            TextureId = textureId;
            Layer = layer;
            CellX = cellX;
            CellY = cellY;
            Overlay = overlay;
        }

        internal int TextureId { get; }
        internal int Layer { get; }
        internal int CellX { get; }
        internal int CellY { get; }
        internal bool Overlay { get; }

        public bool Equals(BatchKey other) =>
            TextureId == other.TextureId && Layer == other.Layer && CellX == other.CellX &&
            CellY == other.CellY && Overlay == other.Overlay;

        public override bool Equals(object obj) => obj is BatchKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TextureId;
                hash = hash * 397 ^ Layer;
                hash = hash * 397 ^ CellX;
                hash = hash * 397 ^ CellY;
                return hash * 397 ^ (Overlay ? 1 : 0);
            }
        }
    }

    private sealed class RoomState
    {
        internal int RoomIndex;
        internal int Layer;
        internal Texture2D Texture;
        internal Rect Uv;
        internal float X;
        internal float Y;
        internal float Width;
        internal float Height;
        internal WorldMapGpuCache.RoomBake Bake;
        internal BatchKey BaseKey;
        internal BatchKey OverlayKey;
        internal bool HasOverlay;
    }

    private sealed class BatchRenderer
    {
        internal GameObject Object;
        internal Mesh Mesh;
        internal MeshRenderer Renderer;
        internal BatchKey Key;
    }

    private static readonly HookComputeRoomSourceHash SourceHashHookDelegate = ComputeRoomSourceHashHook;
    private static readonly HookRebuildRoomBatches RoomBatchesHookDelegate = RebuildRoomBatchesHook;
    private static readonly HookApplyLayerVisibility LayerVisibilityHookDelegate = ApplyLayerVisibilityHook;
    private static readonly HookGetInt ChunkCountHookDelegate = ChunkCountHook;

    private static readonly Dictionary<int, RoomState> roomStates = new();
    private static readonly Dictionary<BatchKey, HashSet<int>> members = new();
    private static readonly Dictionary<BatchKey, BatchRenderer> batches = new();
    private static readonly Dictionary<int, Material> roomMaterials = new();
    private static readonly HashSet<BatchKey> dirtyBatches = new();

    private static ManualLogSource log;
    private static IDisposable sourceHashHook;
    private static IDisposable roomBatchesHook;
    private static IDisposable layerVisibilityHook;
    private static IDisposable chunkCountHook;
    private static GameObject root;
    private static Shader spriteShader;
    private static Material overlayMaterial;
    private static string region = string.Empty;
    private static int currentLayerMask = 7;
    private static bool enabled;

    internal static int BatchCount => batches.Count;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type sceneType = typeof(WorldMapGpuScene);
            MethodInfo sourceHash = sceneType.GetMethod(
                "ComputeRoomSourceHash", flags, null,
                new[] { typeof(MapPage), typeof(WorldMapGpuScene.FrameState) }, null);
            MethodInfo rebuild = sceneType.GetMethod(
                "RebuildRoomBatches", flags, null,
                new[] { typeof(MapPage), typeof(WorldMapGpuScene.FrameState) }, null);
            MethodInfo layerVisibility = sceneType.GetMethod(
                "ApplyLayerVisibility", flags, null, new[] { typeof(int) }, null);
            MethodInfo chunkCountGetter = sceneType.GetProperty(
                "RetainedChunkCount", flags)?.GetGetMethod(nonPublic: true);

            if (sourceHash == null || rebuild == null || layerVisibility == null)
                throw new MissingMemberException("Retained World Map room batch targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sourceHashHook = constructor.Invoke(new object[] { sourceHash, SourceHashHookDelegate }) as IDisposable;
            roomBatchesHook = constructor.Invoke(new object[] { rebuild, RoomBatchesHookDelegate }) as IDisposable;
            layerVisibilityHook = constructor.Invoke(new object[] { layerVisibility, LayerVisibilityHookDelegate }) as IDisposable;
            if (chunkCountGetter != null)
                chunkCountHook = constructor.Invoke(new object[] { chunkCountGetter, ChunkCountHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("GPU World Map dirty-chunk room batching enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("GPU World Map retained optimizer could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref chunkCountHook);
        DisposeHook(ref layerVisibilityHook);
        DisposeHook(ref roomBatchesHook);
        DisposeHook(ref sourceHashHook);
        ClearAll();
        enabled = false;
        log = null;
    }

    private static int ComputeRoomSourceHashHook(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || frame?.Snapshot?.Available != true)
            return orig(page, frame);

        // Source validation belongs to WorldMapGpuCache. Its generation changes only when a room
        // cache record is invalidated or replaced. Keep layer in this hash because it controls the
        // retained renderer batch even though it is not part of the persistent room bake.
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ WorldMapGpuCache.Generation;
            EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            hash = hash * 397 ^ rooms.Length;
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;
            }
            return hash;
        }
    }

    private static void RebuildRoomBatchesHook(
        OrigRebuildRoomBatches orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || page == null || frame?.Snapshot?.Available != true)
        {
            orig(page, frame);
            return;
        }

        try
        {
            Reconcile(page, frame);
        }
        catch (Exception error)
        {
            // A renderer optimization must never make the editor unusable. If retained reconciliation
            // fails, clear our objects and fall back to the proven first-generation GPU batcher.
            log?.LogWarning("GPU World Map dirty-chunk reconcile failed; using fallback batcher: " + error.Message);
            ClearAll();
            orig(page, frame);
        }
    }

    private static void ApplyLayerVisibilityHook(OrigApplyLayerVisibility orig, int layerMask)
    {
        orig(layerMask);
        currentLayerMask = layerMask;
        ApplyVisibility();
    }

    private static int ChunkCountHook(OrigGetInt orig) =>
        Math.Max(orig(), batches.Count);

    private static void Reconcile(MapPage page, WorldMapGpuScene.FrameState frame)
    {
        EnsureRenderer();
        string nextRegion = NormalizeRegion(frame.Region ?? page.world?.name);
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            ClearSceneObjects();
            region = nextRegion;
        }

        currentLayerMask = frame.LayerMask;
        Dictionary<int, RoomPanel> panels = BuildPanelIndex(page);
        Dictionary<int, WorldMapGpuScene.RoomPlacement> placements = BuildPlacementIndex(frame.Placements);
        HashSet<int> seen = new();
        EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !panels.TryGetValue(room.RoomIndex, out RoomPanel panel) ||
                !placements.TryGetValue(room.RoomIndex, out WorldMapGpuScene.RoomPlacement placement) ||
                !TryBuildState(room, panel, placement, out RoomState next))
                continue;

            seen.Add(room.RoomIndex);
            if (!roomStates.TryGetValue(room.RoomIndex, out RoomState previous))
            {
                roomStates[room.RoomIndex] = next;
                AddMembership(next.BaseKey, room.RoomIndex);
                Dirty(next.BaseKey);
                if (next.HasOverlay)
                {
                    AddMembership(next.OverlayKey, room.RoomIndex);
                    Dirty(next.OverlayKey);
                }
                continue;
            }

            if (Equivalent(previous, next)) continue;

            Dirty(previous.BaseKey);
            if (previous.HasOverlay) Dirty(previous.OverlayKey);

            if (!previous.BaseKey.Equals(next.BaseKey))
            {
                RemoveMembership(previous.BaseKey, room.RoomIndex);
                AddMembership(next.BaseKey, room.RoomIndex);
            }
            if (previous.HasOverlay && (!next.HasOverlay || !previous.OverlayKey.Equals(next.OverlayKey)))
                RemoveMembership(previous.OverlayKey, room.RoomIndex);
            if (next.HasOverlay && (!previous.HasOverlay || !previous.OverlayKey.Equals(next.OverlayKey)))
                AddMembership(next.OverlayKey, room.RoomIndex);

            roomStates[room.RoomIndex] = next;
            Dirty(next.BaseKey);
            if (next.HasOverlay) Dirty(next.OverlayKey);
        }

        if (roomStates.Count != seen.Count)
        {
            List<int> stale = new();
            foreach (int roomIndex in roomStates.Keys)
                if (!seen.Contains(roomIndex)) stale.Add(roomIndex);
            for (int i = 0; i < stale.Count; i++) RemoveRoom(stale[i]);
        }

        FlushDirtyBatches();
        ApplyVisibility();
    }

    private static Dictionary<int, RoomPanel> BuildPanelIndex(MapPage page)
    {
        Dictionary<int, RoomPanel> result = new(page?.subNodes?.Count ?? 0);
        if (page?.subNodes == null) return result;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room != null)
                result[panel.roomRep.room.index] = panel;
        }
        return result;
    }

    private static Dictionary<int, WorldMapGpuScene.RoomPlacement> BuildPlacementIndex(
        WorldMapGpuScene.RoomPlacement[] placements)
    {
        Dictionary<int, WorldMapGpuScene.RoomPlacement> result = new(placements?.Length ?? 0);
        if (placements == null) return result;
        for (int i = 0; i < placements.Length; i++)
            result[placements[i].RoomIndex] = placements[i];
        return result;
    }

    private static bool TryBuildState(
        EditorMapRoomSnapshot room,
        RoomPanel panel,
        WorldMapGpuScene.RoomPlacement placement,
        out RoomState state)
    {
        state = null;
        Texture2D texture = null;
        Rect uv = new(0f, 0f, 1f, 1f);
        float widthTiles = 12f;
        float heightTiles = 6f;

        FAtlasElement element = panel.roomRep?.mapTex;
        if (element?.atlas?.texture is Texture2D atlas)
        {
            texture = atlas;
            uv = element.uvRect;
            widthTiles = Math.Max(1f, element.sourcePixelSize.x);
            heightTiles = Math.Max(1f, element.sourcePixelSize.y);
        }
        else if (panel.roomRep?.texture != null)
        {
            texture = panel.roomRep.texture;
            widthTiles = Math.Max(1f, texture.width);
            heightTiles = Math.Max(1f, texture.height);
        }
        if (texture == null) return false;

        WorldMapGpuCache.TryGetRoom(room.RoomIndex, out WorldMapGpuCache.RoomBake bake);
        if (bake?.Visual?.Available == true)
        {
            widthTiles = Math.Max(1f, bake.Visual.WidthTiles);
            heightTiles = Math.Max(1f, bake.Visual.HeightTiles);
        }

        float width = widthTiles * WorldMapGpuScene.TileDisplaySize;
        float height = heightTiles * WorldMapGpuScene.TileDisplaySize;
        int cellX = FloorToInt(placement.X / SpatialChunkSize);
        int cellY = FloorToInt(placement.Y / SpatialChunkSize);
        BatchKey baseKey = new(texture.GetInstanceID(), room.Layer, cellX, cellY, false);
        bool hasOverlay = bake?.GeometryReady == true && HasOverlayGeometry(bake.Visual);
        BatchKey overlayKey = new(0, room.Layer, cellX, cellY, true);

        state = new RoomState
        {
            RoomIndex = room.RoomIndex,
            Layer = room.Layer,
            Texture = texture,
            Uv = uv,
            X = placement.X,
            Y = placement.Y,
            Width = width,
            Height = height,
            Bake = bake,
            BaseKey = baseKey,
            OverlayKey = overlayKey,
            HasOverlay = hasOverlay
        };
        return true;
    }

    private static bool Equivalent(RoomState a, RoomState b)
    {
        if (a == null || b == null) return false;
        return a.Layer == b.Layer &&
               ReferenceEquals(a.Texture, b.Texture) &&
               RectEqual(a.Uv, b.Uv) &&
               NearlyEqual(a.X, b.X) && NearlyEqual(a.Y, b.Y) &&
               NearlyEqual(a.Width, b.Width) && NearlyEqual(a.Height, b.Height) &&
               ReferenceEquals(a.Bake, b.Bake) &&
               a.HasOverlay == b.HasOverlay &&
               a.BaseKey.Equals(b.BaseKey) &&
               (!a.HasOverlay || a.OverlayKey.Equals(b.OverlayKey));
    }

    private static bool HasOverlayGeometry(EditorMapRoomVisualSnapshot visual)
    {
        EditorMapRectSnapshot[] runs = visual?.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
        for (int i = 0; i < runs.Length; i++)
            if (IsOverlayKind(runs[i].Kind)) return true;
        return false;
    }

    private static void RemoveRoom(int roomIndex)
    {
        if (!roomStates.TryGetValue(roomIndex, out RoomState state)) return;
        roomStates.Remove(roomIndex);
        RemoveMembership(state.BaseKey, roomIndex);
        Dirty(state.BaseKey);
        if (state.HasOverlay)
        {
            RemoveMembership(state.OverlayKey, roomIndex);
            Dirty(state.OverlayKey);
        }
    }

    private static void AddMembership(BatchKey key, int roomIndex)
    {
        if (!members.TryGetValue(key, out HashSet<int> set))
        {
            set = new HashSet<int>();
            members.Add(key, set);
        }
        set.Add(roomIndex);
    }

    private static void RemoveMembership(BatchKey key, int roomIndex)
    {
        if (!members.TryGetValue(key, out HashSet<int> set)) return;
        set.Remove(roomIndex);
        if (set.Count == 0) members.Remove(key);
    }

    private static void Dirty(BatchKey key) => dirtyBatches.Add(key);

    private static void FlushDirtyBatches()
    {
        if (dirtyBatches.Count == 0) return;
        BatchKey[] keys = new BatchKey[dirtyBatches.Count];
        dirtyBatches.CopyTo(keys);
        dirtyBatches.Clear();
        for (int i = 0; i < keys.Length; i++) RebuildBatch(keys[i]);
    }

    private static void RebuildBatch(BatchKey key)
    {
        if (!members.TryGetValue(key, out HashSet<int> ids) || ids.Count == 0)
        {
            if (batches.TryGetValue(key, out BatchRenderer stale))
            {
                DestroyBatch(stale);
                batches.Remove(key);
            }
            return;
        }

        List<RoomState> rooms = new(ids.Count);
        foreach (int roomIndex in ids)
            if (roomStates.TryGetValue(roomIndex, out RoomState room)) rooms.Add(room);
        if (rooms.Count == 0) return;

        if (!batches.TryGetValue(key, out BatchRenderer batch))
        {
            Material material = key.Overlay ? GetOverlayMaterial() : GetRoomMaterial(rooms[0].Texture);
            batch = CreateBatch(key, material);
            batches.Add(key, batch);
        }

        if (key.Overlay) BuildOverlayMesh(batch.Mesh, rooms);
        else BuildRoomMesh(batch.Mesh, rooms);
    }

    private static BatchRenderer CreateBatch(BatchKey key, Material material)
    {
        GameObject obj = new("DryCycle.WorldMapRetainedChunk")
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = RenderLayer
        };
        obj.transform.SetParent(root.transform, false);
        MeshFilter filter = obj.AddComponent<MeshFilter>();
        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        Mesh mesh = new()
        {
            name = "DryCycle World Map Retained Chunk",
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.MarkDynamic();
        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.sortingOrder = key.Overlay ? 50 + key.Layer : key.Layer;
        renderer.enabled = (currentLayerMask & (1 << key.Layer)) != 0;
        return new BatchRenderer { Object = obj, Mesh = mesh, Renderer = renderer, Key = key };
    }

    private static void BuildRoomMesh(Mesh mesh, List<RoomState> rooms)
    {
        int count = rooms.Count;
        int vertexCount = count * 4;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uv = new Vector2[vertexCount];
        Color32[] colors = new Color32[vertexCount];
        int[] triangles = new int[count * 6];
        Color32 white = new(255, 255, 255, 255);
        BoundsAccumulator bounds = new();

        for (int i = 0; i < count; i++)
        {
            RoomState room = rooms[i];
            int v = i * 4;
            float left = room.X;
            float top = -room.Y;
            float right = room.X + room.Width;
            float bottom = -(room.Y + room.Height);
            float z = room.Layer * 0.02f;
            vertices[v] = new Vector3(left, top, z);
            vertices[v + 1] = new Vector3(right, top, z);
            vertices[v + 2] = new Vector3(right, bottom, z);
            vertices[v + 3] = new Vector3(left, bottom, z);
            uv[v] = new Vector2(room.Uv.xMin, room.Uv.yMax);
            uv[v + 1] = new Vector2(room.Uv.xMax, room.Uv.yMax);
            uv[v + 2] = new Vector2(room.Uv.xMax, room.Uv.yMin);
            uv[v + 3] = new Vector2(room.Uv.xMin, room.Uv.yMin);
            colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = white;

            int t = i * 6;
            triangles[t] = v;
            triangles[t + 1] = v + 1;
            triangles[t + 2] = v + 2;
            triangles[t + 3] = v;
            triangles[t + 4] = v + 2;
            triangles[t + 5] = v + 3;
            bounds.Add(left, bottom, right, top, z);
        }

        mesh.Clear();
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        mesh.bounds = bounds.ToBounds();
    }

    private static void BuildOverlayMesh(Mesh mesh, List<RoomState> rooms)
    {
        int quadCount = 0;
        for (int r = 0; r < rooms.Count; r++)
        {
            EditorMapRectSnapshot[] runs = rooms[r].Bake?.Visual?.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
            for (int i = 0; i < runs.Length; i++) if (IsOverlayKind(runs[i].Kind)) quadCount++;
        }

        int vertexCount = quadCount * 4;
        Vector3[] vertices = new Vector3[vertexCount];
        Color32[] colors = new Color32[vertexCount];
        int[] triangles = new int[quadCount * 6];
        BoundsAccumulator bounds = new();
        int q = 0;

        for (int r = 0; r < rooms.Count; r++)
        {
            RoomState room = rooms[r];
            EditorMapRoomVisualSnapshot visual = room.Bake?.Visual;
            EditorMapRectSnapshot[] runs = visual?.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
            float heightTiles = Math.Max(1f, visual?.HeightTiles ?? room.Height / WorldMapGpuScene.TileDisplaySize);
            for (int i = 0; i < runs.Length; i++)
            {
                EditorMapRectSnapshot run = runs[i];
                if (!IsOverlayKind(run.Kind)) continue;

                float x0 = room.X + run.X * WorldMapGpuScene.TileDisplaySize;
                float x1 = room.X + (run.X + run.Width) * WorldMapGpuScene.TileDisplaySize;
                float y0Map = room.Y + (heightTiles - (run.Y + run.Height)) * WorldMapGpuScene.TileDisplaySize;
                float y1Map = room.Y + (heightTiles - run.Y) * WorldMapGpuScene.TileDisplaySize;
                float top = -y0Map;
                float bottom = -y1Map;
                float z = room.Layer * 0.02f + 0.01f;
                int v = q * 4;
                vertices[v] = new Vector3(x0, top, z);
                vertices[v + 1] = new Vector3(x1, top, z);
                vertices[v + 2] = new Vector3(x1, bottom, z);
                vertices[v + 3] = new Vector3(x0, bottom, z);
                Color32 color = OverlayColor(run.Kind);
                colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = color;
                int t = q * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
                bounds.Add(x0, bottom, x1, top, z);
                q++;
            }
        }

        mesh.Clear();
        mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.colors32 = colors;
        mesh.triangles = triangles;
        mesh.bounds = bounds.ToBounds();
    }

    private static bool IsOverlayKind(EditorMapGeometryKind kind) =>
        kind == EditorMapGeometryKind.LocalTerrain ||
        kind == EditorMapGeometryKind.CurvedSlope ||
        kind == EditorMapGeometryKind.QuicksandBody ||
        kind == EditorMapGeometryKind.QuicksandMaterial;

    private static Color32 OverlayColor(EditorMapGeometryKind kind) =>
        kind switch
        {
            EditorMapGeometryKind.LocalTerrain => new Color32(112, 119, 124, 230),
            EditorMapGeometryKind.CurvedSlope => new Color32(125, 132, 138, 235),
            EditorMapGeometryKind.QuicksandBody => new Color32(94, 70, 50, 220),
            EditorMapGeometryKind.QuicksandMaterial => new Color32(180, 126, 72, 245),
            _ => new Color32(255, 255, 255, 255)
        };

    private static Material GetRoomMaterial(Texture2D texture)
    {
        int id = texture.GetInstanceID();
        if (roomMaterials.TryGetValue(id, out Material material) && material != null) return material;
        material = NewMaterial(texture, "WorldMapRetainedAtlas_" + id);
        roomMaterials[id] = material;
        return material;
    }

    private static Material GetOverlayMaterial()
    {
        if (overlayMaterial != null) return overlayMaterial;
        overlayMaterial = NewMaterial(Texture2D.whiteTexture, "WorldMapRetainedOverlay");
        return overlayMaterial;
    }

    private static Material NewMaterial(Texture texture, string name)
    {
        EnsureRenderer();
        Material material = new(spriteShader)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            mainTexture = texture
        };
        return material;
    }

    private static void EnsureRenderer()
    {
        if (root != null && spriteShader != null) return;
        spriteShader = Shader.Find("Sprites/Default") ??
                       Shader.Find("Unlit/Transparent") ??
                       Shader.Find("UI/Default");
        if (spriteShader == null) throw new InvalidOperationException("No unlit texture shader is available.");
        root = new GameObject("DryCycle.WorldMapGpuRetainedRooms")
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = RenderLayer
        };
    }

    private static void ApplyVisibility()
    {
        foreach (BatchRenderer batch in batches.Values)
            if (batch?.Renderer != null)
                batch.Renderer.enabled = (currentLayerMask & (1 << batch.Key.Layer)) != 0;
    }

    private static void ClearAll()
    {
        ClearSceneObjects();
        foreach (Material material in roomMaterials.Values)
            if (material != null) UnityEngine.Object.Destroy(material);
        roomMaterials.Clear();
        if (overlayMaterial != null) UnityEngine.Object.Destroy(overlayMaterial);
        overlayMaterial = null;
        spriteShader = null;
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null;
        region = string.Empty;
        currentLayerMask = 7;
    }

    private static void ClearSceneObjects()
    {
        foreach (BatchRenderer batch in batches.Values) DestroyBatch(batch);
        batches.Clear();
        members.Clear();
        roomStates.Clear();
        dirtyBatches.Clear();
    }

    private static void DestroyBatch(BatchRenderer batch)
    {
        if (batch == null) return;
        if (batch.Mesh != null) UnityEngine.Object.Destroy(batch.Mesh);
        if (batch.Object != null) UnityEngine.Object.Destroy(batch.Object);
    }

    private static bool RectEqual(Rect a, Rect b) =>
        NearlyEqual(a.x, b.x) && NearlyEqual(a.y, b.y) &&
        NearlyEqual(a.width, b.width) && NearlyEqual(a.height, b.height);

    private static bool NearlyEqual(float a, float b) => Math.Abs(a - b) < 0.0005f;
    private static int FloorToInt(float value) => (int)Math.Floor(value);
    private static string NormalizeRegion(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }

    private struct BoundsAccumulator
    {
        private bool initialized;
        private Vector3 min;
        private Vector3 max;

        internal void Add(float left, float bottom, float right, float top, float z)
        {
            Vector3 nextMin = new(Math.Min(left, right), Math.Min(bottom, top), z - 0.05f);
            Vector3 nextMax = new(Math.Max(left, right), Math.Max(bottom, top), z + 0.05f);
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
            Bounds bounds = new();
            bounds.SetMinMax(min, max);
            return bounds;
        }
    }
}
