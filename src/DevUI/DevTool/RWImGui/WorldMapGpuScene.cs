using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained Unity/GPU scene for the region editor.
///
/// Room meshes are persistent and chunked. Moving a room dirties only the source/destination
/// chunks instead of rebuilding the whole region. Connections, crossing bridges and interaction
/// highlights are retained GPU meshes as well. Spatial cells back both route hit-testing and room
/// viewport queries so very large maps do not need every retained chunk enabled at once.
/// </summary>
internal static class WorldMapGpuScene
{
    internal const float TileDisplaySize = 2f;
    private const int RenderLayer = 31;
    private const float SpatialChunkSize = 512f;
    private const float RoomGridSize = 256f;
    private const float RouteGridSize = 256f;
    private const float LaneSpacing = 9f;
    private const float MaxLaneOffset = 27f;
    private const float ArrowSpacing = 72f;
    private const float ArrowSize = 7f;
    private const float CrossingRadius = 7f;
    private const float CrossingRise = 5.5f;

    internal readonly struct RoomPlacement
    {
        internal RoomPlacement(int roomIndex, float x, float y)
        {
            RoomIndex = roomIndex;
            X = x;
            Y = y;
        }

        internal int RoomIndex { get; }
        internal float X { get; }
        internal float Y { get; }
    }

    internal sealed class FrameState
    {
        internal bool Visible;
        internal string Region = string.Empty;
        internal Num.Vector2 CanvasMin;
        internal Num.Vector2 CanvasSize;
        internal Num.Vector2 DisplaySize;
        internal Num.Vector2 Pan;
        internal float Zoom = 1f;
        internal int LayerMask = 7;
        internal int LayoutHash;
        internal bool ShowConnections = true;
        internal int SelectedRoomIndex = -1;
        internal int HoveredRoomIndex = -1;
        internal string SelectedConnectionId = string.Empty;
        internal string HoveredConnectionId = string.Empty;
        internal EditorMapPresentationSnapshot Snapshot = EditorMapPresentationSnapshot.Empty;
        internal RoomPlacement[] Placements = Array.Empty<RoomPlacement>();
    }

    private readonly struct ChunkKey : IEquatable<ChunkKey>
    {
        internal ChunkKey(int textureId, int layer, int cellX, int cellY, bool overlay)
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

        public bool Equals(ChunkKey other) =>
            TextureId == other.TextureId && Layer == other.Layer && CellX == other.CellX &&
            CellY == other.CellY && Overlay == other.Overlay;

        public override bool Equals(object obj) => obj is ChunkKey other && Equals(other);

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

    private sealed class RoomQuad
    {
        internal int RoomIndex;
        internal int Order;
        internal int Layer;
        internal Texture2D Texture;
        internal Rect Uv;
        internal float X;
        internal float Y;
        internal float Width;
        internal float Height;
        internal WorldMapGpuCache.RoomBake Bake;
    }

    private sealed class ChunkRenderer
    {
        internal ChunkKey Key;
        internal GameObject Object;
        internal Mesh Mesh;
        internal MeshRenderer Renderer;
    }

    internal sealed class RouteHit
    {
        internal EditorMapConnectionSnapshot Connection;
        internal Num.Vector2[] Points = Array.Empty<Num.Vector2>();
        internal Num.Vector2 Min;
        internal Num.Vector2 Max;
    }

    private sealed class RouteSpatialIndex
    {
        internal static readonly RouteSpatialIndex Empty =
            new(Array.Empty<RouteHit>(), new Dictionary<long, int[]>());

        internal RouteSpatialIndex(RouteHit[] routes, Dictionary<long, int[]> cells)
        {
            Routes = routes ?? Array.Empty<RouteHit>();
            Cells = cells ?? new Dictionary<long, int[]>();
        }

        internal RouteHit[] Routes { get; }
        internal Dictionary<long, int[]> Cells { get; }
    }

    private sealed class RoomHit
    {
        internal int RoomIndex;
        internal int Layer;
        internal int Order;
        internal Num.Vector2 Min;
        internal Num.Vector2 Max;
    }

    private sealed class RoomSpatialIndex
    {
        internal static readonly RoomSpatialIndex Empty =
            new(Array.Empty<RoomHit>(), new Dictionary<long, int[]>());

        internal RoomSpatialIndex(RoomHit[] rooms, Dictionary<long, int[]> cells)
        {
            Rooms = rooms ?? Array.Empty<RoomHit>();
            Cells = cells ?? new Dictionary<long, int[]>();
        }

        internal RoomHit[] Rooms { get; }
        internal Dictionary<long, int[]> Cells { get; }
    }

    private readonly struct Crossing
    {
        internal Crossing(Num.Vector2 point, Num.Vector2 tangent, Color32 color)
        {
            Point = point;
            Tangent = tangent;
            Color = color;
        }

        internal Num.Vector2 Point { get; }
        internal Num.Vector2 Tangent { get; }
        internal Color32 Color { get; }
    }

    private sealed class RouteEntry
    {
        internal EditorMapConnectionSnapshot Connection;
        internal EditorMapRoomSnapshot StartRoom;
        internal EditorMapRoomSnapshot EndRoom;
        internal Num.Vector2 Start;
        internal Num.Vector2 End;
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal WorldConnectionRouter.Route Route;
        internal readonly List<Crossing> Crossings = new();
    }

    private static GameObject root;
    private static Camera mapCamera;
    private static Shader spriteShader;
    private static Material lineMaterial;
    private static Material overlayMaterial;
    private static readonly Dictionary<int, Material> roomMaterials = new();
    private static readonly Dictionary<ChunkKey, ChunkRenderer> chunks = new();

    // Retained membership tables. These are what make room dragging incremental: a changed room
    // removes itself from its old chunk and adds itself to its new chunk, dirtying only those keys.
    private static readonly Dictionary<int, RoomQuad> roomQuads = new();
    private static readonly Dictionary<int, ChunkKey> roomBaseKeys = new();
    private static readonly Dictionary<int, ChunkKey> roomOverlayKeys = new();
    private static readonly Dictionary<ChunkKey, HashSet<int>> chunkMembers = new();

    private static ChunkRenderer connectionRenderer;
    private static ChunkRenderer crossingRenderer;
    private static ChunkRenderer dynamicOverlayRenderer;
    private static volatile RouteSpatialIndex routeIndex = RouteSpatialIndex.Empty;
    private static volatile RoomSpatialIndex roomIndex = RoomSpatialIndex.Empty;

    private static string region = string.Empty;
    private static int lastLayoutHash = int.MinValue;
    private static int lastRoomSourceHash = int.MinValue;
    private static int lastTopologyHash = int.MinValue;
    private static int lastLayerMask = -1;
    private static int lastDynamicOverlayHash = int.MinValue;
    private static bool lastShowConnections;
    private static bool ready;
    private static string error = string.Empty;
    private static int visibleRoomCount;

    internal static bool Ready => ready;
    internal static string Error => error;
    internal static int RetainedChunkCount => chunks.Count;
    internal static int RetainedRouteCount => routeIndex.Routes.Length;
    internal static int RetainedRoomCount => roomIndex.Rooms.Length;
    internal static int VisibleRoomCount => visibleRoomCount;

    internal static void Disable()
    {
        ready = false;
        routeIndex = RouteSpatialIndex.Empty;
        roomIndex = RoomSpatialIndex.Empty;
        region = string.Empty;
        lastLayoutHash = int.MinValue;
        lastRoomSourceHash = int.MinValue;
        lastTopologyHash = int.MinValue;
        lastLayerMask = -1;
        lastDynamicOverlayHash = int.MinValue;
        lastShowConnections = false;
        visibleRoomCount = 0;
        error = string.Empty;

        DestroyChunk(ref dynamicOverlayRenderer);
        DestroyChunk(ref crossingRenderer);
        DestroyChunk(ref connectionRenderer);
        foreach (ChunkRenderer chunk in chunks.Values)
        {
            ChunkRenderer local = chunk;
            DestroyChunk(ref local);
        }
        chunks.Clear();
        roomQuads.Clear();
        roomBaseKeys.Clear();
        roomOverlayKeys.Clear();
        chunkMembers.Clear();

        foreach (Material material in roomMaterials.Values)
            if (material != null) UnityEngine.Object.Destroy(material);
        roomMaterials.Clear();

        if (lineMaterial != null) UnityEngine.Object.Destroy(lineMaterial);
        if (overlayMaterial != null) UnityEngine.Object.Destroy(overlayMaterial);
        lineMaterial = null;
        overlayMaterial = null;
        spriteShader = null;

        if (mapCamera != null) UnityEngine.Object.Destroy(mapCamera.gameObject);
        else if (root != null) UnityEngine.Object.Destroy(root);
        mapCamera = null;
        root = null;
    }

    internal static void Apply(FrameState frame, EditorSession session)
    {
        if (frame?.Visible != true || frame.Snapshot?.Available != true ||
            session?.ToolMode != EditorToolMode.Map || session.Owner?.activePage is not MapPage page)
        {
            if (mapCamera != null) mapCamera.enabled = false;
            return;
        }

        if (!EnsureRenderer()) return;
        mapCamera.enabled = true;
        ApplyViewport(frame);

        string nextRegion = (frame.Region ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            ClearRegionScene();
            region = nextRegion;
        }

        int sourceHash = ComputeRoomSourceHash(page, frame);
        if (frame.LayoutHash != lastLayoutHash || sourceHash != lastRoomSourceHash)
        {
            RebuildRoomBatches(page, frame);
            lastLayoutHash = frame.LayoutHash;
            lastRoomSourceHash = sourceHash;
        }

        ApplyRoomVisibility(frame);

        int topologyHash = ComputeTopologyHash(frame);
        if (frame.ShowConnections != lastShowConnections || frame.LayerMask != lastLayerMask ||
            topologyHash != lastTopologyHash)
        {
            RebuildConnections(frame);
            lastTopologyHash = topologyHash;
            lastShowConnections = frame.ShowConnections;
        }

        UpdateDynamicOverlay(frame, topologyHash);
        lastLayerMask = frame.LayerMask;
    }

    internal static bool TryHitConnection(Num.Vector2 mapPoint, float radius, out RouteHit hit)
    {
        hit = null;
        RouteSpatialIndex index = routeIndex;
        if (index.Routes.Length == 0) return false;

        int minCellX = FloorToInt((mapPoint.X - radius) / RouteGridSize);
        int maxCellX = FloorToInt((mapPoint.X + radius) / RouteGridSize);
        int minCellY = FloorToInt((mapPoint.Y - radius) / RouteGridSize);
        int maxCellY = FloorToInt((mapPoint.Y + radius) / RouteGridSize);
        float best = radius * radius;
        HashSet<int> visited = new();

        for (int y = minCellY; y <= maxCellY; y++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                if (!index.Cells.TryGetValue(CellKey(x, y), out int[] candidates)) continue;
                for (int c = 0; c < candidates.Length; c++)
                {
                    int routeIndexValue = candidates[c];
                    if (!visited.Add(routeIndexValue) || routeIndexValue < 0 ||
                        routeIndexValue >= index.Routes.Length)
                        continue;

                    RouteHit candidate = index.Routes[routeIndexValue];
                    if (mapPoint.X < candidate.Min.X - radius || mapPoint.X > candidate.Max.X + radius ||
                        mapPoint.Y < candidate.Min.Y - radius || mapPoint.Y > candidate.Max.Y + radius)
                        continue;

                    Num.Vector2[] points = candidate.Points;
                    for (int p = 1; p < points.Length; p++)
                    {
                        float distance = DistanceSqToSegment(mapPoint, points[p - 1], points[p]);
                        if (distance > best) continue;
                        best = distance;
                        hit = candidate;
                    }
                }
            }
        }

        return hit != null;
    }

    internal static bool TryHitRoom(Num.Vector2 mapPoint, int layerMask, out int roomIndexValue)
    {
        roomIndexValue = -1;
        RoomSpatialIndex index = roomIndex;
        if (index.Rooms.Length == 0) return false;

        int cellX = FloorToInt(mapPoint.X / RoomGridSize);
        int cellY = FloorToInt(mapPoint.Y / RoomGridSize);
        if (!index.Cells.TryGetValue(CellKey(cellX, cellY), out int[] candidates)) return false;

        int bestOrder = int.MinValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            int candidateIndex = candidates[i];
            if (candidateIndex < 0 || candidateIndex >= index.Rooms.Length) continue;
            RoomHit candidate = index.Rooms[candidateIndex];
            if ((layerMask & (1 << candidate.Layer)) == 0 || candidate.Order < bestOrder) continue;
            if (mapPoint.X < candidate.Min.X || mapPoint.X > candidate.Max.X ||
                mapPoint.Y < candidate.Min.Y || mapPoint.Y > candidate.Max.Y)
                continue;
            bestOrder = candidate.Order;
            roomIndexValue = candidate.RoomIndex;
        }
        return roomIndexValue >= 0;
    }

    internal static int[] QueryVisibleRooms(Num.Vector2 mapMin, Num.Vector2 mapMax, int layerMask)
    {
        RoomSpatialIndex index = roomIndex;
        if (index.Rooms.Length == 0) return Array.Empty<int>();

        int minCellX = FloorToInt(mapMin.X / RoomGridSize);
        int maxCellX = FloorToInt(mapMax.X / RoomGridSize);
        int minCellY = FloorToInt(mapMin.Y / RoomGridSize);
        int maxCellY = FloorToInt(mapMax.Y / RoomGridSize);
        HashSet<int> visited = new();
        List<RoomHit> visible = new();

        for (int y = minCellY; y <= maxCellY; y++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                if (!index.Cells.TryGetValue(CellKey(x, y), out int[] candidates)) continue;
                for (int i = 0; i < candidates.Length; i++)
                {
                    int candidateIndex = candidates[i];
                    if (!visited.Add(candidateIndex) || candidateIndex < 0 || candidateIndex >= index.Rooms.Length)
                        continue;
                    RoomHit candidate = index.Rooms[candidateIndex];
                    if ((layerMask & (1 << candidate.Layer)) == 0 ||
                        candidate.Max.X < mapMin.X || candidate.Min.X > mapMax.X ||
                        candidate.Max.Y < mapMin.Y || candidate.Min.Y > mapMax.Y)
                        continue;
                    visible.Add(candidate);
                }
            }
        }

        visible.Sort((a, b) => a.Order.CompareTo(b.Order));
        int[] result = new int[visible.Count];
        for (int i = 0; i < visible.Count; i++) result[i] = visible[i].RoomIndex;
        return result;
    }

    private static bool EnsureRenderer()
    {
        if (ready && mapCamera != null) return true;
        try
        {
            spriteShader = Shader.Find("Sprites/Default") ??
                           Shader.Find("Unlit/Transparent") ??
                           Shader.Find("UI/Default");
            if (spriteShader == null) throw new InvalidOperationException("No unlit texture shader is available.");

            root = new GameObject("DryCycle.WorldMapGpuScene")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = RenderLayer
            };
            GameObject cameraObject = new("DryCycle.WorldMapGpuCamera")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = RenderLayer
            };
            cameraObject.transform.SetParent(root.transform, false);
            mapCamera = cameraObject.AddComponent<Camera>();
            mapCamera.orthographic = true;
            mapCamera.clearFlags = CameraClearFlags.SolidColor;
            mapCamera.backgroundColor = new Color(0.018f, 0.020f, 0.023f, 1f);
            mapCamera.cullingMask = 1 << RenderLayer;
            mapCamera.depth = 10000f;
            mapCamera.nearClipPlane = 0.1f;
            mapCamera.farClipPlane = 500f;
            mapCamera.allowHDR = false;
            mapCamera.allowMSAA = false;
            mapCamera.useOcclusionCulling = false;

            lineMaterial = NewMaterial(Texture2D.whiteTexture, "WorldMapLines");
            overlayMaterial = NewMaterial(Texture2D.whiteTexture, "WorldMapTerrainOverlay");
            ready = true;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            ready = false;
            if (mapCamera != null) mapCamera.enabled = false;
            return false;
        }
    }

    private static void ApplyViewport(FrameState frame)
    {
        float displayWidth = Math.Max(1f, frame.DisplaySize.X);
        float displayHeight = Math.Max(1f, frame.DisplaySize.Y);
        float width = Math.Max(1f, frame.CanvasSize.X);
        float height = Math.Max(1f, frame.CanvasSize.Y);

        float x = Clamp(frame.CanvasMin.X / displayWidth, 0f, 1f);
        float y = Clamp(1f - (frame.CanvasMin.Y + height) / displayHeight, 0f, 1f);
        float w = Clamp(width / displayWidth, 0.0001f, 1f - x);
        float h = Clamp(height / displayHeight, 0.0001f, 1f - y);
        mapCamera.rect = new Rect(x, y, w, h);

        float zoom = Math.Max(0.0001f, frame.Zoom);
        float left = -frame.Pan.X / zoom;
        float top = -frame.Pan.Y / zoom;
        float visibleWidth = width / zoom;
        float visibleHeight = height / zoom;
        float centerX = left + visibleWidth * 0.5f;
        float centerMapY = top + visibleHeight * 0.5f;

        mapCamera.orthographicSize = visibleHeight * 0.5f;
        mapCamera.transform.position = new Vector3(centerX, -centerMapY, -100f);
        mapCamera.transform.rotation = Quaternion.identity;
    }

    private static int ComputeRoomSourceHash(MapPage page, FrameState frame)
    {
        unchecked
        {
            int hash = 17;
            EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;
                if (TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel))
                {
                    MapObject.RoomRepresentation rep = panel.roomRep;
                    FAtlasElement element = rep?.mapTex;
                    if (TryResolveRoomTextureAtlas(
                            element,
                            out Texture2D atlas,
                            out Rect sourceUv,
                            out float sourceWidth,
                            out float sourceHeight))
                    {
                        // Hash the same source that TryGetRoomQuad will actually render. MapTex can
                        // arrive or finish atlas setup after the immutable map snapshot was published.
                        hash = hash * 397 ^ 1;
                        hash = hash * 397 ^ (element.name?.GetHashCode() ?? 0);
                        hash = hash * 397 ^ atlas.GetInstanceID();
                        hash = hash * 397 ^ atlas.width;
                        hash = hash * 397 ^ atlas.height;
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceWidth * 1000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceHeight * 1000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.x * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.y * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.width * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.height * 1000000f);
                    }
                    else if (rep?.texture != null)
                    {
                        // Some rooms expose their generated minimap through RoomRepresentation.texture
                        // before (or instead of) a usable Futile atlas element. Track that source too;
                        // otherwise a late texture never invalidates the retained room batch.
                        Texture2D direct = rep.texture;
                        hash = hash * 397 ^ 2;
                        hash = hash * 397 ^ direct.GetInstanceID();
                        hash = hash * 397 ^ direct.width;
                        hash = hash * 397 ^ direct.height;
                    }
                    else
                    {
                        hash = hash * 397;
                    }
                }

                if (WorldMapGpuCache.TryGetRoom(room.RoomIndex, out WorldMapGpuCache.RoomBake bake))
                {
                    hash = hash * 397 ^ bake.SourceSignature.GetHashCode();
                    hash = hash * 397 ^ (bake.GeometryReady ? 1 : 0);
                }
            }
            return hash;
        }
    }

    private static int ComputeTopologyHash(FrameState frame)
    {
        unchecked
        {
            int hash = frame.LayoutHash;
            EditorMapConnectionSnapshot[] connections = frame.Snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
            hash = hash * 397 ^ connections.Length;
            for (int i = 0; i < connections.Length; i++)
            {
                EditorMapConnectionSnapshot connection = connections[i];
                if (connection == null) continue;
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(connection.ConnectionId ?? string.Empty);
                hash = hash * 397 ^ connection.FromRoomIndex;
                hash = hash * 397 ^ connection.FromNodeIndex;
                hash = hash * 397 ^ connection.ToRoomIndex;
                hash = hash * 397 ^ connection.ToNodeIndex;
                hash = hash * 397 ^ (int)connection.Direction;
                if (WorldMapGpuCache.TryGetRoom(connection.FromRoomIndex, out WorldMapGpuCache.RoomBake a))
                    hash = hash * 397 ^ a.SourceSignature.GetHashCode();
                if (WorldMapGpuCache.TryGetRoom(connection.ToRoomIndex, out WorldMapGpuCache.RoomBake b))
                    hash = hash * 397 ^ b.SourceSignature.GetHashCode();
            }
            return hash;
        }
    }

    private static void RebuildRoomBatches(MapPage page, FrameState frame)
    {
        Dictionary<int, RoomPlacement> placements = PlacementIndex(frame.Placements);
        EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        HashSet<int> alive = new();
        HashSet<ChunkKey> dirty = new();
        bool spatialDirty = false;

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !placements.TryGetValue(room.RoomIndex, out RoomPlacement placement) ||
                !TryGetRoomQuad(page, room, placement, i, out RoomQuad quad))
                continue;

            int roomId = room.RoomIndex;
            alive.Add(roomId);
            ChunkKey nextBase = BaseKey(quad);
            bool nextHasOverlay = quad.Bake?.GeometryReady == true;
            ChunkKey nextOverlay = OverlayKey(quad);

            if (!roomQuads.TryGetValue(roomId, out RoomQuad previous))
            {
                roomQuads[roomId] = quad;
                roomBaseKeys[roomId] = nextBase;
                AddMember(nextBase, roomId, dirty);
                if (nextHasOverlay)
                {
                    roomOverlayKeys[roomId] = nextOverlay;
                    AddMember(nextOverlay, roomId, dirty);
                }
                spatialDirty = true;
                continue;
            }

            roomBaseKeys.TryGetValue(roomId, out ChunkKey previousBase);
            bool previousHasOverlay = roomOverlayKeys.TryGetValue(roomId, out ChunkKey previousOverlay);
            bool quadChanged = !SameRoomQuad(previous, quad);
            bool baseMoved = !previousBase.Equals(nextBase);
            bool overlayMoved = previousHasOverlay != nextHasOverlay ||
                                previousHasOverlay && nextHasOverlay && !previousOverlay.Equals(nextOverlay);

            if (baseMoved)
            {
                RemoveMember(previousBase, roomId, dirty);
                AddMember(nextBase, roomId, dirty);
                roomBaseKeys[roomId] = nextBase;
            }
            else if (quadChanged)
            {
                dirty.Add(nextBase);
            }

            if (previousHasOverlay && (!nextHasOverlay || overlayMoved))
            {
                RemoveMember(previousOverlay, roomId, dirty);
                roomOverlayKeys.Remove(roomId);
            }
            if (nextHasOverlay && (!previousHasOverlay || overlayMoved))
            {
                AddMember(nextOverlay, roomId, dirty);
                roomOverlayKeys[roomId] = nextOverlay;
            }
            else if (nextHasOverlay && quadChanged)
            {
                dirty.Add(nextOverlay);
            }

            if (quadChanged || baseMoved || overlayMoved) spatialDirty = true;
            roomQuads[roomId] = quad;
        }

        if (roomQuads.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int roomId in roomQuads.Keys)
                if (!alive.Contains(roomId)) stale.Add(roomId);
            for (int i = 0; i < stale.Count; i++)
            {
                int roomId = stale[i];
                if (roomBaseKeys.TryGetValue(roomId, out ChunkKey baseKey))
                    RemoveMember(baseKey, roomId, dirty);
                if (roomOverlayKeys.TryGetValue(roomId, out ChunkKey overlayKey))
                    RemoveMember(overlayKey, roomId, dirty);
                roomBaseKeys.Remove(roomId);
                roomOverlayKeys.Remove(roomId);
                roomQuads.Remove(roomId);
            }
            if (stale.Count > 0) spatialDirty = true;
        }

        foreach (ChunkKey key in dirty) RebuildDirtyChunk(key);
        if (spatialDirty || roomIndex.Rooms.Length != roomQuads.Count) BuildRoomSpatialIndex();
    }

    private static bool TryGetRoomQuad(
        MapPage page,
        EditorMapRoomSnapshot room,
        RoomPlacement placement,
        int order,
        out RoomQuad quad)
    {
        quad = null;
        if (!TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel) || panel.roomRep == null) return false;

        Texture2D texture = null;
        Rect uv = new(0f, 0f, 1f, 1f);
        float widthTiles = 12f;
        float heightTiles = 6f;
        FAtlasElement element = panel.roomRep.mapTex;
        if (TryResolveRoomTextureAtlas(
                element,
                out Texture2D atlas,
                out Rect atlasUv,
                out float atlasWidth,
                out float atlasHeight))
        {
            texture = atlas;
            uv = atlasUv;
            widthTiles = atlasWidth;
            heightTiles = atlasHeight;
        }
        else if (panel.roomRep.texture != null)
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

        quad = new RoomQuad
        {
            RoomIndex = room.RoomIndex,
            Order = order,
            Layer = room.Layer,
            Texture = texture,
            Uv = uv,
            X = placement.X,
            Y = placement.Y,
            Width = widthTiles * TileDisplaySize,
            Height = heightTiles * TileDisplaySize,
            Bake = bake
        };
        return true;
    }

    private static bool TryResolveRoomTextureAtlas(
        FAtlasElement element,
        out Texture2D texture,
        out Rect uv,
        out float width,
        out float height)
    {
        texture = null;
        uv = new Rect(0f, 0f, 1f, 1f);
        width = 0f;
        height = 0f;

        if (element?.atlas?.texture is not Texture2D atlas || atlas == null)
            return false;

        Rect candidateUv = element.uvRect;
        float uvWidth = Math.Abs(candidateUv.width);
        float uvHeight = Math.Abs(candidateUv.height);
        if (uvWidth <= 0.000001f || uvHeight <= 0.000001f)
            return false;

        float sampledWidth = uvWidth * Math.Max(1, atlas.width);
        float sampledHeight = uvHeight * Math.Max(1, atlas.height);
        if (sampledWidth < 0.5f || sampledHeight < 0.5f)
            return false;

        texture = atlas;
        uv = candidateUv;
        width = Math.Max(1f, element.sourcePixelSize.x > 0.5f ? element.sourcePixelSize.x : sampledWidth);
        height = Math.Max(1f, element.sourcePixelSize.y > 0.5f ? element.sourcePixelSize.y : sampledHeight);
        return true;
    }

    private static ChunkKey BaseKey(RoomQuad quad) =>
        new(quad.Texture.GetInstanceID(), quad.Layer,
            FloorToInt(quad.X / SpatialChunkSize), FloorToInt(quad.Y / SpatialChunkSize), false);

    private static ChunkKey OverlayKey(RoomQuad quad) =>
        new(0, quad.Layer,
            FloorToInt(quad.X / SpatialChunkSize), FloorToInt(quad.Y / SpatialChunkSize), true);

    private static bool SameRoomQuad(RoomQuad a, RoomQuad b)
    {
        if (a == null || b == null) return false;
        int aTexture = a.Texture != null ? a.Texture.GetInstanceID() : 0;
        int bTexture = b.Texture != null ? b.Texture.GetInstanceID() : 0;
        ulong aSource = a.Bake?.SourceSignature ?? 0UL;
        ulong bSource = b.Bake?.SourceSignature ?? 0UL;
        bool aGeometry = a.Bake?.GeometryReady == true;
        bool bGeometry = b.Bake?.GeometryReady == true;
        return a.RoomIndex == b.RoomIndex && a.Order == b.Order && a.Layer == b.Layer &&
               aTexture == bTexture && a.Uv.Equals(b.Uv) &&
               NearlyEqual(a.X, b.X) && NearlyEqual(a.Y, b.Y) &&
               NearlyEqual(a.Width, b.Width) && NearlyEqual(a.Height, b.Height) &&
               aSource == bSource && aGeometry == bGeometry;
    }

    private static void AddMember(ChunkKey key, int roomId, HashSet<ChunkKey> dirty)
    {
        if (!chunkMembers.TryGetValue(key, out HashSet<int> members))
        {
            members = new HashSet<int>();
            chunkMembers.Add(key, members);
        }
        members.Add(roomId);
        dirty.Add(key);
    }

    private static void RemoveMember(ChunkKey key, int roomId, HashSet<ChunkKey> dirty)
    {
        if (!chunkMembers.TryGetValue(key, out HashSet<int> members)) return;
        members.Remove(roomId);
        if (members.Count == 0) chunkMembers.Remove(key);
        dirty.Add(key);
    }

    private static void RebuildDirtyChunk(ChunkKey key)
    {
        if (!chunkMembers.TryGetValue(key, out HashSet<int> members) || members.Count == 0)
        {
            if (chunks.TryGetValue(key, out ChunkRenderer stale))
            {
                DestroyChunk(ref stale);
                chunks.Remove(key);
            }
            return;
        }

        List<RoomQuad> roomList = new(members.Count);
        foreach (int roomId in members)
            if (roomQuads.TryGetValue(roomId, out RoomQuad room)) roomList.Add(room);
        roomList.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (roomList.Count == 0) return;

        if (!chunks.TryGetValue(key, out ChunkRenderer chunk))
        {
            chunk = CreateChunk(key, key.Overlay ? overlayMaterial : GetRoomMaterial(roomList[0].Texture));
            chunks[key] = chunk;
        }
        RebuildChunkMesh(chunk, roomList, key.Overlay);
    }

    private static ChunkRenderer CreateChunk(ChunkKey key, Material material)
    {
        GameObject obj = new("DryCycle.WorldMapChunk")
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = RenderLayer
        };
        obj.transform.SetParent(root.transform, false);
        MeshFilter filter = obj.AddComponent<MeshFilter>();
        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        Mesh mesh = new() { name = "DryCycle World Map Chunk", hideFlags = HideFlags.HideAndDontSave };
        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.sortingOrder = key.Overlay ? 50 + key.Layer : key.Layer;
        return new ChunkRenderer { Key = key, Object = obj, Mesh = mesh, Renderer = renderer };
    }

    private static void RebuildChunkMesh(ChunkRenderer chunk, List<RoomQuad> rooms, bool overlay)
    {
        if (overlay)
        {
            BuildOverlayMesh(chunk.Mesh, rooms);
            return;
        }

        int count = rooms.Count;
        Vector3[] vertices = new Vector3[count * 4];
        Vector2[] uv = new Vector2[count * 4];
        Color32[] colors = new Color32[count * 4];
        int[] triangles = new int[count * 6];
        Color32 white = new(255, 255, 255, 255);

        for (int i = 0; i < count; i++)
        {
            RoomQuad room = rooms[i];
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
        }

        chunk.Mesh.Clear();
        chunk.Mesh.vertices = vertices;
        chunk.Mesh.uv = uv;
        chunk.Mesh.colors32 = colors;
        chunk.Mesh.triangles = triangles;
        chunk.Mesh.RecalculateBounds();
    }

    private static void BuildOverlayMesh(Mesh mesh, List<RoomQuad> rooms)
    {
        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> triangles = new();

        for (int r = 0; r < rooms.Count; r++)
        {
            RoomQuad room = rooms[r];
            EditorMapRectSnapshot[] runs = room.Bake?.Visual?.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
            float heightTiles = Math.Max(1f, room.Bake?.Visual?.HeightTiles ?? room.Height / TileDisplaySize);
            for (int i = 0; i < runs.Length; i++)
            {
                EditorMapRectSnapshot run = runs[i];
                if (!IsGpuOverlayKind(run.Kind)) continue;
                Color32 color = OverlayColor(run.Kind);
                float x0 = room.X + run.X * TileDisplaySize;
                float x1 = room.X + (run.X + run.Width) * TileDisplaySize;
                float y0Map = room.Y + (heightTiles - (run.Y + run.Height)) * TileDisplaySize;
                float y1Map = room.Y + (heightTiles - run.Y) * TileDisplaySize;
                int v = vertices.Count;
                float z = room.Layer * 0.02f + 0.01f;
                vertices.Add(new Vector3(x0, -y0Map, z));
                vertices.Add(new Vector3(x1, -y0Map, z));
                vertices.Add(new Vector3(x1, -y1Map, z));
                vertices.Add(new Vector3(x0, -y1Map, z));
                colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
                triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
                triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 3);
            }
        }

        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateBounds();
    }

    private static bool IsGpuOverlayKind(EditorMapGeometryKind kind) =>
        kind == EditorMapGeometryKind.LocalTerrain ||
        kind == EditorMapGeometryKind.CurvedSlope ||
        kind == EditorMapGeometryKind.QuicksandBody ||
        kind == EditorMapGeometryKind.QuicksandMaterial;

    private static Color32 OverlayColor(EditorMapGeometryKind kind)
    {
        return kind switch
        {
            EditorMapGeometryKind.LocalTerrain => new Color32(112, 119, 124, 230),
            EditorMapGeometryKind.CurvedSlope => new Color32(125, 132, 138, 235),
            EditorMapGeometryKind.QuicksandBody => new Color32(94, 70, 50, 220),
            EditorMapGeometryKind.QuicksandMaterial => new Color32(180, 126, 72, 245),
            _ => new Color32(255, 255, 255, 255)
        };
    }

    private static void BuildRoomSpatialIndex()
    {
        List<RoomHit> hits = new(roomQuads.Count);
        foreach (RoomQuad room in roomQuads.Values)
        {
            hits.Add(new RoomHit
            {
                RoomIndex = room.RoomIndex,
                Layer = room.Layer,
                Order = room.Order,
                Min = new Num.Vector2(room.X, room.Y),
                Max = new Num.Vector2(room.X + room.Width, room.Y + room.Height)
            });
        }
        hits.Sort((a, b) => a.Order.CompareTo(b.Order));

        Dictionary<long, List<int>> cells = new();
        for (int i = 0; i < hits.Count; i++)
        {
            RoomHit hit = hits[i];
            int minX = FloorToInt(hit.Min.X / RoomGridSize);
            int maxX = FloorToInt(hit.Max.X / RoomGridSize);
            int minY = FloorToInt(hit.Min.Y / RoomGridSize);
            int maxY = FloorToInt(hit.Max.Y / RoomGridSize);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    long key = CellKey(x, y);
                    if (!cells.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>();
                        cells.Add(key, list);
                    }
                    list.Add(i);
                }
            }
        }

        Dictionary<long, int[]> frozen = new(cells.Count);
        foreach (KeyValuePair<long, List<int>> pair in cells) frozen[pair.Key] = pair.Value.ToArray();
        roomIndex = new RoomSpatialIndex(hits.ToArray(), frozen);
    }

    private static void ApplyRoomVisibility(FrameState frame)
    {
        float zoom = Math.Max(0.0001f, frame.Zoom);
        Num.Vector2 mapMin = -frame.Pan / zoom;
        Num.Vector2 mapMax = mapMin + frame.CanvasSize / zoom;
        float margin = 72f / zoom;
        Num.Vector2 pad = new(margin, margin);
        int[] visibleRooms = QueryVisibleRooms(mapMin - pad, mapMax + pad, frame.LayerMask);
        visibleRoomCount = visibleRooms.Length;

        HashSet<ChunkKey> visibleChunks = new();
        for (int i = 0; i < visibleRooms.Length; i++)
        {
            int roomId = visibleRooms[i];
            if (roomBaseKeys.TryGetValue(roomId, out ChunkKey baseKey)) visibleChunks.Add(baseKey);
            if (roomOverlayKeys.TryGetValue(roomId, out ChunkKey overlayKey)) visibleChunks.Add(overlayKey);
        }

        foreach (KeyValuePair<ChunkKey, ChunkRenderer> pair in chunks)
        {
            ChunkRenderer chunk = pair.Value;
            if (chunk?.Renderer == null) continue;
            bool layer = (frame.LayerMask & (1 << pair.Key.Layer)) != 0;
            chunk.Renderer.enabled = layer && visibleChunks.Contains(pair.Key);
        }
    }

    private static void RebuildConnections(FrameState frame)
    {
        if (!frame.ShowConnections)
        {
            DestroyChunk(ref crossingRenderer);
            DestroyChunk(ref connectionRenderer);
            routeIndex = RouteSpatialIndex.Empty;
            return;
        }

        Dictionary<int, RoomPlacement> placements = PlacementIndex(frame.Placements);
        Dictionary<int, EditorMapRoomSnapshot> roomsByIndex = new();
        EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i] != null) roomsByIndex[rooms[i].RoomIndex] = rooms[i];

        List<WorldConnectionRouter.Obstacle> obstacles = new(rooms.Length);
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || (frame.LayerMask & (1 << room.Layer)) == 0 ||
                !placements.TryGetValue(room.RoomIndex, out RoomPlacement placement))
                continue;
            RoomSize(room.RoomIndex, out float width, out float height);
            obstacles.Add(new WorldConnectionRouter.Obstacle(
                room.RoomIndex,
                new Num.Vector2(placement.X, placement.Y),
                new Num.Vector2(placement.X + width, placement.Y + height)));
        }

        EditorMapConnectionSnapshot[] connections =
            frame.Snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        List<RouteEntry> entries = new(connections.Length);
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null || !roomsByIndex.TryGetValue(connection.FromRoomIndex, out EditorMapRoomSnapshot a) ||
                !roomsByIndex.TryGetValue(connection.ToRoomIndex, out EditorMapRoomSnapshot b) ||
                (frame.LayerMask & (1 << a.Layer)) == 0 || (frame.LayerMask & (1 << b.Layer)) == 0 ||
                !placements.TryGetValue(a.RoomIndex, out RoomPlacement placeA) ||
                !placements.TryGetValue(b.RoomIndex, out RoomPlacement placeB))
                continue;

            RoomSize(a.RoomIndex, out float widthA, out float heightA);
            RoomSize(b.RoomIndex, out float widthB, out float heightB);
            Num.Vector2 minA = new(placeA.X, placeA.Y);
            Num.Vector2 maxA = minA + new Num.Vector2(widthA, heightA);
            Num.Vector2 minB = new(placeB.X, placeB.Y);
            Num.Vector2 maxB = minB + new Num.Vector2(widthB, heightB);
            Num.Vector2 start = EndpointPosition(connection.FromRoomIndex, connection.FromNodeIndex, minA, widthA, heightA);
            Num.Vector2 end = connection.ToNodeIndex >= 0
                ? EndpointPosition(connection.ToRoomIndex, connection.ToNodeIndex, minB, widthB, heightB)
                : BoundaryToward(minB, maxB, start);

            entries.Add(new RouteEntry
            {
                Connection = connection,
                StartRoom = a,
                EndRoom = b,
                Start = start,
                End = end,
                StartDirection = WorldConnectionRouter.InferPortDirection(start, minA, maxA),
                EndDirection = WorldConnectionRouter.InferPortDirection(end, minB, maxB)
            });
        }

        AssignLanes(entries);
        List<WorldConnectionRouter.Request> requests = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            RouteEntry entry = entries[i];
            requests.Add(new WorldConnectionRouter.Request
            {
                Id = "gpu:" + (entry.Connection.ConnectionId ?? string.Empty),
                StartRoom = entry.Connection.FromRoomIndex,
                EndRoom = entry.Connection.ToRoomIndex,
                Start = entry.Start,
                End = entry.End,
                StartDirection = entry.StartDirection,
                EndDirection = entry.EndDirection,
                LaneOffset = entry.LaneOffset
            });
        }

        WorldConnectionRouter.Route[] routed = WorldConnectionRouter.BuildRoutes(requests, obstacles);
        for (int i = 0; i < entries.Count && i < routed.Length; i++) entries[i].Route = routed[i];
        BuildCrossings(entries);
        BuildConnectionMesh(entries);
        BuildCrossingMesh(entries);
        routeIndex = BuildRouteIndex(entries);
    }

    private static void AssignLanes(List<RouteEntry> entries)
    {
        Dictionary<long, List<RouteEntry>> groups = new();
        for (int i = 0; i < entries.Count; i++)
        {
            RouteEntry entry = entries[i];
            int a = Math.Min(entry.Connection.FromRoomIndex, entry.Connection.ToRoomIndex);
            int b = Math.Max(entry.Connection.FromRoomIndex, entry.Connection.ToRoomIndex);
            long key = ((long)(uint)a << 32) | (uint)b;
            if (!groups.TryGetValue(key, out List<RouteEntry> group))
            {
                group = new List<RouteEntry>();
                groups.Add(key, group);
            }
            group.Add(entry);
        }

        foreach (List<RouteEntry> group in groups.Values)
        {
            group.Sort((a, b) =>
            {
                int a0 = Math.Min(a.Connection.FromNodeIndex, a.Connection.ToNodeIndex);
                int b0 = Math.Min(b.Connection.FromNodeIndex, b.Connection.ToNodeIndex);
                int first = a0.CompareTo(b0);
                if (first != 0) return first;
                int a1 = Math.Max(a.Connection.FromNodeIndex, a.Connection.ToNodeIndex);
                int b1 = Math.Max(b.Connection.FromNodeIndex, b.Connection.ToNodeIndex);
                int second = a1.CompareTo(b1);
                return second != 0 ? second : string.CompareOrdinal(a.Connection.ConnectionId, b.Connection.ConnectionId);
            });
            float center = (group.Count - 1) * 0.5f;
            for (int i = 0; i < group.Count; i++)
                group[i].LaneOffset = Clamp((i - center) * LaneSpacing, -MaxLaneOffset, MaxLaneOffset);
        }
    }

    private static void BuildCrossings(List<RouteEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++) entries[i].Crossings.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            Num.Vector2[] a = entries[i].Route?.Points;
            if (a == null || a.Length < 2) continue;
            for (int j = i + 1; j < entries.Count; j++)
            {
                Num.Vector2[] b = entries[j].Route?.Points;
                if (b == null || b.Length < 2) continue;
                FindCrossings(a, b, entries[j]);
            }
        }
    }

    private static void FindCrossings(Num.Vector2[] a, Num.Vector2[] b, RouteEntry bridgeEntry)
    {
        Color32 color = ConnectionColor(bridgeEntry.Connection);
        for (int i = 0; i < a.Length - 1; i++)
        {
            Num.Vector2 ad = a[i + 1] - a[i];
            if (ad.LengthSquared() < 1f) continue;
            for (int j = 0; j < b.Length - 1; j++)
            {
                Num.Vector2 bd = b[j + 1] - b[j];
                if (bd.LengthSquared() < 1f) continue;
                if (!TrySegmentIntersection(a[i], a[i + 1], b[j], b[j + 1], out Num.Vector2 point)) continue;
                if (NearAnyEndpoint(point, a) || NearAnyEndpoint(point, b)) continue;
                Num.Vector2 na = SafeNormalize(ad);
                Num.Vector2 nb = SafeNormalize(bd);
                if (Math.Abs(Cross(na, nb)) < 0.35f) continue;
                bridgeEntry.Crossings.Add(new Crossing(point, nb, color));
            }
        }
    }

    private static void BuildConnectionMesh(List<RouteEntry> entries)
    {
        if (connectionRenderer == null)
        {
            ChunkKey key = new(0, 0, 0, 0, true);
            connectionRenderer = CreateChunk(key, lineMaterial);
            connectionRenderer.Object.name = "DryCycle.WorldMapConnections";
            connectionRenderer.Renderer.sortingOrder = 100;
        }

        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> indices = new();
        for (int i = 0; i < entries.Count; i++)
        {
            RouteEntry entry = entries[i];
            Num.Vector2[] points = entry.Route?.Points ?? Array.Empty<Num.Vector2>();
            if (points.Length < 2) continue;
            Color32 color = ConnectionColor(entry.Connection);

            if (entry.Connection.Direction == WorldConnectionDirection.Bidirectional)
            {
                Num.Vector2[] left = OffsetPath(points, -2.3f);
                Num.Vector2[] right = OffsetPath(points, 2.3f);
                AddPolyline(vertices, colors, indices, left, color, false);
                AddPolyline(vertices, colors, indices, right, color, false);
                AddArrow(vertices, colors, indices, left, 0.58f, false, color);
                AddArrow(vertices, colors, indices, right, 0.42f, true, color);
            }
            else
            {
                bool reverse = entry.Connection.Direction == WorldConnectionDirection.BToA;
                AddPolyline(vertices, colors, indices, points, color, entry.Connection.Ambiguous);
                float length = PolylineLength(points);
                int arrows = Math.Max(1, (int)(length / ArrowSpacing));
                for (int a = 1; a <= arrows; a++)
                    AddArrow(vertices, colors, indices, points, a / (float)(arrows + 1), reverse, color);
            }
        }

        Mesh mesh = connectionRenderer.Mesh;
        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
        mesh.RecalculateBounds();
    }

    private static void BuildCrossingMesh(List<RouteEntry> entries)
    {
        int crossingCount = 0;
        for (int i = 0; i < entries.Count; i++) crossingCount += entries[i].Crossings.Count;
        if (crossingCount == 0)
        {
            DestroyChunk(ref crossingRenderer);
            return;
        }

        if (crossingRenderer == null)
        {
            ChunkKey key = new(0, 0, 0, 0, true);
            crossingRenderer = CreateChunk(key, lineMaterial);
            crossingRenderer.Object.name = "DryCycle.WorldMapCrossingBridges";
            crossingRenderer.Renderer.sortingOrder = 120;
        }

        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> indices = new();
        Color32 mask = new(5, 5, 6, 255);
        Color32 shadow = new(4, 5, 7, 245);

        for (int i = 0; i < entries.Count; i++)
        {
            List<Crossing> crossings = entries[i].Crossings;
            for (int c = 0; c < crossings.Count; c++)
            {
                Crossing crossing = crossings[c];
                Num.Vector2 tangent = SafeNormalize(crossing.Tangent);
                if (tangent.LengthSquared() < 0.5f) continue;
                Num.Vector2 normal = new(-tangent.Y, tangent.X);
                Num.Vector2 a = crossing.Point - tangent * CrossingRadius;
                Num.Vector2 b = crossing.Point - tangent * 2.4f + normal * CrossingRise;
                Num.Vector2 cc = crossing.Point + tangent * 2.4f + normal * CrossingRise;
                Num.Vector2 d = crossing.Point + tangent * CrossingRadius;

                AddThickSegment(vertices, colors, indices,
                    crossing.Point - tangent * (CrossingRadius + 2f),
                    crossing.Point + tangent * (CrossingRadius + 2f), mask, 8.2f, 2f);

                Num.Vector2[] arc = BuildBridgeArc(a, b, cc, d);
                AddThickPolyline(vertices, colors, indices, arc, shadow, 5.4f, 2.1f);
                AddThickPolyline(vertices, colors, indices, arc, crossing.Color, 2.1f, 2.2f);
            }
        }

        Mesh mesh = crossingRenderer.Mesh;
        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, 0);
        mesh.RecalculateBounds();
    }

    private static Num.Vector2[] BuildBridgeArc(Num.Vector2 a, Num.Vector2 b, Num.Vector2 c, Num.Vector2 d)
    {
        Num.Vector2 midpoint = (b + c) * 0.5f;
        Num.Vector2[] result = new Num.Vector2[9];
        result[0] = a;
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            result[i] = Quadratic(a, b, midpoint, t);
        }
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            result[4 + i] = Quadratic(midpoint, c, d, t);
        }
        return result;
    }

    private static RouteSpatialIndex BuildRouteIndex(List<RouteEntry> entries)
    {
        List<RouteHit> hits = new();
        Dictionary<long, List<int>> cells = new();
        for (int i = 0; i < entries.Count; i++)
        {
            Num.Vector2[] points = entries[i].Route?.Points ?? Array.Empty<Num.Vector2>();
            if (points.Length < 2) continue;
            Num.Vector2 min = points[0];
            Num.Vector2 max = points[0];
            for (int p = 1; p < points.Length; p++)
            {
                min = Num.Vector2.Min(min, points[p]);
                max = Num.Vector2.Max(max, points[p]);
            }
            int hitIndex = hits.Count;
            hits.Add(new RouteHit
            {
                Connection = entries[i].Connection,
                Points = (Num.Vector2[])points.Clone(),
                Min = min,
                Max = max
            });

            int minX = FloorToInt(min.X / RouteGridSize);
            int maxX = FloorToInt(max.X / RouteGridSize);
            int minY = FloorToInt(min.Y / RouteGridSize);
            int maxY = FloorToInt(max.Y / RouteGridSize);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    long key = CellKey(x, y);
                    if (!cells.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>();
                        cells.Add(key, list);
                    }
                    list.Add(hitIndex);
                }
            }
        }

        Dictionary<long, int[]> frozen = new(cells.Count);
        foreach (KeyValuePair<long, List<int>> pair in cells) frozen[pair.Key] = pair.Value.ToArray();
        return new RouteSpatialIndex(hits.ToArray(), frozen);
    }

    private static void UpdateDynamicOverlay(FrameState frame, int topologyHash)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ frame.LayoutHash;
            hash = hash * 397 ^ topologyHash;
            hash = hash * 397 ^ frame.SelectedRoomIndex;
            hash = hash * 397 ^ frame.HoveredRoomIndex;
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(frame.SelectedConnectionId ?? string.Empty);
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(frame.HoveredConnectionId ?? string.Empty);
            hash = hash * 397 ^ (int)Math.Round(frame.Zoom * 64f);
            if (hash == lastDynamicOverlayHash) return;
            lastDynamicOverlayHash = hash;
        }

        bool any = frame.SelectedRoomIndex >= 0 || frame.HoveredRoomIndex >= 0 ||
                   !string.IsNullOrEmpty(frame.SelectedConnectionId) ||
                   !string.IsNullOrEmpty(frame.HoveredConnectionId);
        if (!any)
        {
            DestroyChunk(ref dynamicOverlayRenderer);
            return;
        }

        if (dynamicOverlayRenderer == null)
        {
            ChunkKey key = new(0, 0, 0, 0, true);
            dynamicOverlayRenderer = CreateChunk(key, lineMaterial);
            dynamicOverlayRenderer.Object.name = "DryCycle.WorldMapDynamicOverlay";
            dynamicOverlayRenderer.Renderer.sortingOrder = 220;
        }

        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> indices = new();
        float pixel = 1f / Math.Max(0.20f, frame.Zoom);

        if (frame.HoveredRoomIndex >= 0 && frame.HoveredRoomIndex != frame.SelectedRoomIndex)
            AddRoomFocus(vertices, colors, indices, frame.HoveredRoomIndex,
                new Color32(72, 224, 255, 235), pixel, 1f);
        if (frame.SelectedRoomIndex >= 0)
            AddRoomFocus(vertices, colors, indices, frame.SelectedRoomIndex,
                new Color32(92, 184, 255, 255), pixel, 2f);

        string focusConnection = !string.IsNullOrEmpty(frame.HoveredConnectionId)
            ? frame.HoveredConnectionId
            : frame.SelectedConnectionId;
        if (!string.IsNullOrEmpty(focusConnection) && TryFindRoute(focusConnection, out RouteHit route))
        {
            Color32 color = string.Equals(focusConnection, frame.SelectedConnectionId, StringComparison.Ordinal)
                ? new Color32(92, 184, 255, 255)
                : new Color32(72, 224, 255, 245);
            Num.Vector2[] points = route.Points ?? Array.Empty<Num.Vector2>();
            AddPolyline(vertices, colors, indices, points, color, false);
            AddPolyline(vertices, colors, indices, OffsetPath(points, 1.25f * pixel), color, false);
            AddPolyline(vertices, colors, indices, OffsetPath(points, -1.25f * pixel), color, false);
        }

        Mesh mesh = dynamicOverlayRenderer.Mesh;
        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.SetIndices(indices.ToArray(), MeshTopology.Lines, 0);
        mesh.RecalculateBounds();
        dynamicOverlayRenderer.Renderer.enabled = vertices.Count > 0;
    }

    private static void AddRoomFocus(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        int roomId,
        Color32 color,
        float pixel,
        float strength)
    {
        if (!roomQuads.TryGetValue(roomId, out RoomQuad room)) return;
        Num.Vector2 min = new(room.X, room.Y);
        Num.Vector2 max = new(room.X + room.Width, room.Y + room.Height);
        AddRectOutline(vertices, colors, indices, min, max, color);
        float spread = pixel * Math.Max(0.75f, strength);
        AddRectOutline(vertices, colors, indices, min - new Num.Vector2(spread, spread),
            max + new Num.Vector2(spread, spread), color);
        if (strength > 1.5f)
            AddRectOutline(vertices, colors, indices,
                min - new Num.Vector2(spread * 2f, spread * 2f),
                max + new Num.Vector2(spread * 2f, spread * 2f), color);
    }

    private static bool TryFindRoute(string connectionId, out RouteHit hit)
    {
        RouteHit[] routes = routeIndex.Routes;
        for (int i = 0; i < routes.Length; i++)
        {
            if (!string.Equals(routes[i].Connection?.ConnectionId, connectionId, StringComparison.Ordinal)) continue;
            hit = routes[i];
            return true;
        }
        hit = null;
        return false;
    }

    private static void AddRectOutline(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 min,
        Num.Vector2 max,
        Color32 color)
    {
        Num.Vector2 a = min;
        Num.Vector2 b = new(max.X, min.Y);
        Num.Vector2 c = max;
        Num.Vector2 d = new(min.X, max.Y);
        AddLine(vertices, colors, indices, a, b, color);
        AddLine(vertices, colors, indices, b, c, color);
        AddLine(vertices, colors, indices, c, d, color);
        AddLine(vertices, colors, indices, d, a, color);
    }

    private static void AddPolyline(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] points,
        Color32 color,
        bool dashed)
    {
        if (points == null) return;
        for (int i = 1; i < points.Length; i++)
        {
            Num.Vector2 a = points[i - 1];
            Num.Vector2 b = points[i];
            float length = Num.Vector2.Distance(a, b);
            if (!dashed || length < 8f)
            {
                AddLine(vertices, colors, indices, a, b, color);
                continue;
            }

            Num.Vector2 direction = (b - a) / Math.Max(0.001f, length);
            const float dash = 8f;
            const float gap = 5f;
            for (float at = 0f; at < length; at += dash + gap)
            {
                Num.Vector2 start = a + direction * at;
                Num.Vector2 end = a + direction * Math.Min(length, at + dash);
                AddLine(vertices, colors, indices, start, end, color);
            }
        }
    }

    private static void AddArrow(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] points,
        float fraction,
        bool reverse,
        Color32 color)
    {
        if (!PointOnPolyline(points, fraction, out Num.Vector2 point, out Num.Vector2 tangent)) return;
        if (reverse) tangent = -tangent;
        Num.Vector2 normal = new(-tangent.Y, tangent.X);
        Num.Vector2 back = point - tangent * ArrowSize;
        AddLine(vertices, colors, indices, point, back + normal * ArrowSize * 0.55f, color);
        AddLine(vertices, colors, indices, point, back - normal * ArrowSize * 0.55f, color);
    }

    private static void AddLine(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 a,
        Num.Vector2 b,
        Color32 color)
    {
        int start = vertices.Count;
        vertices.Add(new Vector3(a.X, -a.Y, 1f));
        vertices.Add(new Vector3(b.X, -b.Y, 1f));
        colors.Add(color);
        colors.Add(color);
        indices.Add(start);
        indices.Add(start + 1);
    }

    private static void AddThickPolyline(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] points,
        Color32 color,
        float width,
        float z)
    {
        if (points == null) return;
        for (int i = 1; i < points.Length; i++)
            AddThickSegment(vertices, colors, indices, points[i - 1], points[i], color, width, z);
    }

    private static void AddThickSegment(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 a,
        Num.Vector2 b,
        Color32 color,
        float width,
        float z)
    {
        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 0.001f) return;
        Num.Vector2 normal = new(-delta.Y / length, delta.X / length);
        Num.Vector2 half = normal * (width * 0.5f);
        int start = vertices.Count;
        vertices.Add(new Vector3(a.X + half.X, -(a.Y + half.Y), z));
        vertices.Add(new Vector3(b.X + half.X, -(b.Y + half.Y), z));
        vertices.Add(new Vector3(b.X - half.X, -(b.Y - half.Y), z));
        vertices.Add(new Vector3(a.X - half.X, -(a.Y - half.Y), z));
        colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    private static Num.Vector2[] OffsetPath(Num.Vector2[] points, float amount)
    {
        if (points == null || points.Length < 2 || Math.Abs(amount) < 0.001f)
            return points ?? Array.Empty<Num.Vector2>();
        Num.Vector2[] result = new Num.Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Num.Vector2 before = points[Math.Max(0, i - 1)];
            Num.Vector2 after = points[Math.Min(points.Length - 1, i + 1)];
            Num.Vector2 tangent = after - before;
            if (tangent.LengthSquared() < 0.0001f) tangent = new Num.Vector2(1f, 0f);
            tangent = Num.Vector2.Normalize(tangent);
            result[i] = points[i] + new Num.Vector2(-tangent.Y, tangent.X) * amount;
        }
        return result;
    }

    private static bool PointOnPolyline(
        Num.Vector2[] points,
        float fraction,
        out Num.Vector2 point,
        out Num.Vector2 tangent)
    {
        point = default;
        tangent = new Num.Vector2(1f, 0f);
        if (points == null || points.Length < 2) return false;
        float total = PolylineLength(points);
        if (total < 0.001f) return false;
        float target = Clamp(fraction, 0f, 1f) * total;
        float accumulated = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            Num.Vector2 delta = points[i] - points[i - 1];
            float length = delta.Length();
            if (length < 0.001f) continue;
            if (accumulated + length >= target)
            {
                float t = (target - accumulated) / length;
                point = Num.Vector2.Lerp(points[i - 1], points[i], t);
                tangent = delta / length;
                return true;
            }
            accumulated += length;
        }
        point = points[points.Length - 1];
        Num.Vector2 last = points[points.Length - 1] - points[points.Length - 2];
        if (last.LengthSquared() > 0.0001f) tangent = Num.Vector2.Normalize(last);
        return true;
    }

    private static float PolylineLength(Num.Vector2[] points)
    {
        float result = 0f;
        if (points == null) return result;
        for (int i = 1; i < points.Length; i++) result += Num.Vector2.Distance(points[i - 1], points[i]);
        return result;
    }

    private static Color32 ConnectionColor(EditorMapConnectionSnapshot connection)
    {
        if (connection?.Ambiguous == true) return new Color32(150, 155, 164, 210);
        return connection?.Direction == WorldConnectionDirection.Bidirectional
            ? new Color32(179, 196, 210, 235)
            : new Color32(235, 170, 74, 245);
    }

    private static Num.Vector2 EndpointPosition(
        int roomIndexValue,
        int nodeIndex,
        Num.Vector2 roomMin,
        float roomWidth,
        float roomHeight)
    {
        if (WorldMapGpuCache.TryGetExit(roomIndexValue, nodeIndex, out WorldMapShortcutPresentation.ShortcutMarker marker))
        {
            return roomMin + new Num.Vector2(
                marker.X * TileDisplaySize,
                roomHeight - marker.Y * TileDisplaySize);
        }

        if (WorldMapGpuCache.TryGetRoom(roomIndexValue, out WorldMapGpuCache.RoomBake bake))
        {
            EditorMapNodeVisualSnapshot[] nodes = bake.Visual?.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].NodeIndex != nodeIndex) continue;
                return roomMin + new Num.Vector2(
                    nodes[i].X * TileDisplaySize,
                    roomHeight - nodes[i].Y * TileDisplaySize);
            }
        }

        return roomMin + new Num.Vector2(roomWidth * 0.5f, roomHeight * 0.5f);
    }

    private static Num.Vector2 BoundaryToward(Num.Vector2 min, Num.Vector2 max, Num.Vector2 point)
    {
        Num.Vector2 center = (min + max) * 0.5f;
        Num.Vector2 direction = point - center;
        if (Math.Abs(direction.X) >= Math.Abs(direction.Y))
            return new Num.Vector2(direction.X < 0f ? min.X : max.X, center.Y);
        return new Num.Vector2(center.X, direction.Y < 0f ? min.Y : max.Y);
    }

    private static void RoomSize(int roomIndexValue, out float width, out float height)
    {
        if (roomQuads.TryGetValue(roomIndexValue, out RoomQuad quad))
        {
            width = quad.Width;
            height = quad.Height;
            return;
        }
        if (WorldMapGpuCache.TryGetRoom(roomIndexValue, out WorldMapGpuCache.RoomBake bake) &&
            bake.Visual?.Available == true)
        {
            width = Math.Max(1f, bake.Visual.WidthTiles) * TileDisplaySize;
            height = Math.Max(1f, bake.Visual.HeightTiles) * TileDisplaySize;
            return;
        }
        width = 24f;
        height = 12f;
    }

    private static Dictionary<int, RoomPlacement> PlacementIndex(RoomPlacement[] placements)
    {
        Dictionary<int, RoomPlacement> result = new(placements?.Length ?? 0);
        if (placements == null) return result;
        for (int i = 0; i < placements.Length; i++) result[placements[i].RoomIndex] = placements[i];
        return result;
    }

    private static bool TryFindRoomPanel(MapPage page, int roomIndexValue, out RoomPanel panel)
    {
        panel = null;
        if (page?.subNodes == null) return false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel candidate || candidate.roomRep?.room == null ||
                candidate.roomRep.room.index != roomIndexValue)
                continue;
            panel = candidate;
            return true;
        }
        return false;
    }

    private static Material GetRoomMaterial(Texture2D texture)
    {
        int id = texture.GetInstanceID();
        if (roomMaterials.TryGetValue(id, out Material material) && material != null) return material;
        material = NewMaterial(texture, "WorldMapAtlas_" + id);
        roomMaterials[id] = material;
        return material;
    }

    private static Material NewMaterial(Texture texture, string name)
    {
        Material material = new(spriteShader)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            mainTexture = texture
        };
        return material;
    }

    private static void ClearRegionScene()
    {
        DestroyChunk(ref dynamicOverlayRenderer);
        DestroyChunk(ref crossingRenderer);
        DestroyChunk(ref connectionRenderer);
        foreach (ChunkRenderer chunk in chunks.Values)
        {
            ChunkRenderer local = chunk;
            DestroyChunk(ref local);
        }
        chunks.Clear();
        roomQuads.Clear();
        roomBaseKeys.Clear();
        roomOverlayKeys.Clear();
        chunkMembers.Clear();
        routeIndex = RouteSpatialIndex.Empty;
        roomIndex = RoomSpatialIndex.Empty;
        visibleRoomCount = 0;
        WorldConnectionRouter.Clear();
        lastLayoutHash = int.MinValue;
        lastRoomSourceHash = int.MinValue;
        lastTopologyHash = int.MinValue;
        lastLayerMask = -1;
        lastDynamicOverlayHash = int.MinValue;
        lastShowConnections = false;
    }

    private static void DestroyChunk(ref ChunkRenderer chunk)
    {
        if (chunk == null) return;
        if (chunk.Mesh != null) UnityEngine.Object.Destroy(chunk.Mesh);
        if (chunk.Object != null) UnityEngine.Object.Destroy(chunk.Object);
        chunk = null;
    }

    private static bool TrySegmentIntersection(
        Num.Vector2 a0,
        Num.Vector2 a1,
        Num.Vector2 b0,
        Num.Vector2 b1,
        out Num.Vector2 point)
    {
        point = default;
        Num.Vector2 r = a1 - a0;
        Num.Vector2 s = b1 - b0;
        float denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.0001f) return false;
        Num.Vector2 delta = b0 - a0;
        float t = Cross(delta, s) / denominator;
        float u = Cross(delta, r) / denominator;
        if (t <= 0.02f || t >= 0.98f || u <= 0.02f || u >= 0.98f) return false;
        point = a0 + r * t;
        return true;
    }

    private static bool NearAnyEndpoint(Num.Vector2 point, Num.Vector2[] path)
    {
        if (path == null || path.Length == 0) return false;
        const float radiusSq = 144f;
        return Num.Vector2.DistanceSquared(point, path[0]) < radiusSq ||
               Num.Vector2.DistanceSquared(point, path[path.Length - 1]) < radiusSq;
    }

    private static Num.Vector2 SafeNormalize(Num.Vector2 value)
    {
        float length = value.Length();
        return length < 0.0001f ? Num.Vector2.Zero : value / length;
    }

    private static float Cross(Num.Vector2 a, Num.Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static Num.Vector2 Quadratic(Num.Vector2 a, Num.Vector2 b, Num.Vector2 c, float t)
    {
        float u = 1f - t;
        return a * (u * u) + b * (2f * u * t) + c * (t * t);
    }

    private static long CellKey(int x, int y) => ((long)(uint)x << 32) | (uint)y;
    private static int FloorToInt(float value) => (int)Math.Floor(value);
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    private static bool NearlyEqual(float a, float b) => Math.Abs(a - b) <= 0.0001f;

    private static float DistanceSqToSegment(Num.Vector2 point, Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 0.0001f) return Num.Vector2.DistanceSquared(point, a);
        float t = Num.Vector2.Dot(point - a, ab) / lengthSq;
        t = Clamp(t, 0f, 1f);
        return Num.Vector2.DistanceSquared(point, a + ab * t);
    }
}
