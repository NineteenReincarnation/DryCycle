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
/// The design deliberately follows Rain World's original MapTex + persistent FSprite model: room
/// atlas pixels are never decomposed into thousands of immediate-mode rectangles. Room quads are
/// grouped by atlas/layer/spatial cell into long-lived meshes. Connections are routed once in map
/// space and also retained as a GPU line mesh. Pan and zoom therefore change only the orthographic
/// camera transform; they do not rebuild room or connection geometry.
/// </summary>
internal static class WorldMapGpuScene
{
    internal const float TileDisplaySize = 2f;
    private const int RenderLayer = 31;
    private const float SpatialChunkSize = 512f;
    private const float RouteGridSize = 256f;
    private const float LaneSpacing = 9f;
    private const float MaxLaneOffset = 27f;
    private const float ArrowSpacing = 72f;
    private const float ArrowSize = 7f;

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
        internal static readonly RouteSpatialIndex Empty = new(Array.Empty<RouteHit>(), new Dictionary<long, int[]>());

        internal RouteSpatialIndex(RouteHit[] routes, Dictionary<long, int[]> cells)
        {
            Routes = routes ?? Array.Empty<RouteHit>();
            Cells = cells ?? new Dictionary<long, int[]>();
        }

        internal RouteHit[] Routes { get; }
        internal Dictionary<long, int[]> Cells { get; }
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
    }

    private static GameObject root;
    private static Camera mapCamera;
    private static Shader spriteShader;
    private static Material lineMaterial;
    private static Material overlayMaterial;
    private static readonly Dictionary<int, Material> roomMaterials = new();
    private static readonly Dictionary<ChunkKey, ChunkRenderer> chunks = new();
    private static ChunkRenderer connectionRenderer;
    private static volatile RouteSpatialIndex routeIndex = RouteSpatialIndex.Empty;

    private static string region = string.Empty;
    private static int lastLayoutHash = int.MinValue;
    private static int lastRoomSourceHash = int.MinValue;
    private static int lastTopologyHash = int.MinValue;
    private static int lastLayerMask = -1;
    private static bool lastShowConnections;
    private static bool ready;
    private static string error = string.Empty;

    internal static bool Ready => ready;
    internal static string Error => error;
    internal static int RetainedChunkCount => chunks.Count;
    internal static int RetainedRouteCount => routeIndex.Routes.Length;

    internal static void Disable()
    {
        ready = false;
        routeIndex = RouteSpatialIndex.Empty;
        region = string.Empty;
        lastLayoutHash = int.MinValue;
        lastRoomSourceHash = int.MinValue;
        lastTopologyHash = int.MinValue;
        lastLayerMask = -1;
        lastShowConnections = false;
        error = string.Empty;

        DestroyChunk(ref connectionRenderer);
        foreach (ChunkRenderer chunk in chunks.Values)
        {
            ChunkRenderer local = chunk;
            DestroyChunk(ref local);
        }
        chunks.Clear();

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
        else
        {
            ApplyLayerVisibility(frame.LayerMask);
        }

        int topologyHash = ComputeTopologyHash(frame);
        if (frame.ShowConnections != lastShowConnections || frame.LayerMask != lastLayerMask ||
            topologyHash != lastTopologyHash || frame.LayoutHash != lastLayoutHash)
        {
            RebuildConnections(frame);
            lastTopologyHash = topologyHash;
            lastShowConnections = frame.ShowConnections;
        }
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
                    FAtlasElement element = panel.roomRep?.mapTex;
                    if (element != null)
                    {
                        hash = hash * 397 ^ (element.name?.GetHashCode() ?? 0);
                        Texture2D texture = element.atlas?.texture as Texture2D;
                        hash = hash * 397 ^ (texture != null ? texture.GetInstanceID() : 0);
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
        Dictionary<ChunkKey, List<RoomQuad>> desired = new();
        EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !placements.TryGetValue(room.RoomIndex, out RoomPlacement placement) ||
                !TryGetRoomQuad(page, room, placement, out RoomQuad quad))
                continue;

            int cellX = FloorToInt(quad.X / SpatialChunkSize);
            int cellY = FloorToInt(quad.Y / SpatialChunkSize);
            ChunkKey key = new(quad.Texture.GetInstanceID(), room.Layer, cellX, cellY, false);
            if (!desired.TryGetValue(key, out List<RoomQuad> list))
            {
                list = new List<RoomQuad>();
                desired.Add(key, list);
            }
            list.Add(quad);

            if (quad.Bake?.GeometryReady == true)
            {
                ChunkKey overlayKey = new(0, room.Layer, cellX, cellY, true);
                if (!desired.TryGetValue(overlayKey, out List<RoomQuad> overlayList))
                {
                    overlayList = new List<RoomQuad>();
                    desired.Add(overlayKey, overlayList);
                }
                overlayList.Add(quad);
            }
        }

        List<ChunkKey> stale = new();
        foreach (ChunkKey key in chunks.Keys)
            if (!desired.ContainsKey(key)) stale.Add(key);
        for (int i = 0; i < stale.Count; i++)
        {
            ChunkRenderer chunk = chunks[stale[i]];
            DestroyChunk(ref chunk);
            chunks.Remove(stale[i]);
        }

        foreach (KeyValuePair<ChunkKey, List<RoomQuad>> pair in desired)
        {
            if (!chunks.TryGetValue(pair.Key, out ChunkRenderer chunk))
            {
                chunk = CreateChunk(pair.Key, pair.Key.Overlay ? overlayMaterial : GetRoomMaterial(pair.Value[0].Texture));
                chunks[pair.Key] = chunk;
            }
            RebuildChunkMesh(chunk, pair.Value, pair.Key.Overlay);
        }

        ApplyLayerVisibility(frame.LayerMask);
    }

    private static bool TryGetRoomQuad(
        MapPage page,
        EditorMapRoomSnapshot room,
        RoomPlacement placement,
        out RoomQuad quad)
    {
        quad = null;
        if (!TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel) || panel.roomRep == null) return false;

        Texture2D texture = null;
        Rect uv = new(0f, 0f, 1f, 1f);
        float widthTiles = 12f;
        float heightTiles = 6f;
        FAtlasElement element = panel.roomRep.mapTex;
        if (element?.atlas?.texture is Texture2D atlas)
        {
            texture = atlas;
            uv = element.uvRect;
            widthTiles = Math.Max(1f, element.sourcePixelSize.x);
            heightTiles = Math.Max(1f, element.sourcePixelSize.y);
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
            float widthTiles = Math.Max(1f, room.Bake?.Visual?.WidthTiles ?? room.Width / TileDisplaySize);
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

    private static void ApplyLayerVisibility(int layerMask)
    {
        foreach (ChunkRenderer chunk in chunks.Values)
        {
            if (chunk?.Renderer == null) continue;
            chunk.Renderer.enabled = (layerMask & (1 << chunk.Key.Layer)) != 0;
        }
    }

    private static void RebuildConnections(FrameState frame)
    {
        if (!frame.ShowConnections)
        {
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
        BuildConnectionMesh(entries);
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

    private static void AddPolyline(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] points,
        Color32 color,
        bool dashed)
    {
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
        int roomIndex,
        int nodeIndex,
        Num.Vector2 roomMin,
        float roomWidth,
        float roomHeight)
    {
        if (WorldMapGpuCache.TryGetExit(roomIndex, nodeIndex, out WorldMapShortcutPresentation.ShortcutMarker marker))
        {
            return roomMin + new Num.Vector2(
                marker.X * TileDisplaySize,
                roomHeight - marker.Y * TileDisplaySize);
        }

        if (WorldMapGpuCache.TryGetRoom(roomIndex, out WorldMapGpuCache.RoomBake bake))
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

    private static void RoomSize(int roomIndex, out float width, out float height)
    {
        if (WorldMapGpuCache.TryGetRoom(roomIndex, out WorldMapGpuCache.RoomBake bake) &&
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

    private static bool TryFindRoomPanel(MapPage page, int roomIndex, out RoomPanel panel)
    {
        panel = null;
        if (page?.subNodes == null) return false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel candidate || candidate.roomRep?.room == null ||
                candidate.roomRep.room.index != roomIndex)
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
        DestroyChunk(ref connectionRenderer);
        foreach (ChunkRenderer chunk in chunks.Values)
        {
            ChunkRenderer local = chunk;
            DestroyChunk(ref local);
        }
        chunks.Clear();
        routeIndex = RouteSpatialIndex.Empty;
        WorldConnectionRouter.Clear();
        lastLayoutHash = int.MinValue;
        lastRoomSourceHash = int.MinValue;
        lastTopologyHash = int.MinValue;
        lastLayerMask = -1;
        lastShowConnections = false;
    }

    private static void DestroyChunk(ref ChunkRenderer chunk)
    {
        if (chunk == null) return;
        if (chunk.Mesh != null) UnityEngine.Object.Destroy(chunk.Mesh);
        if (chunk.Object != null) UnityEngine.Object.Destroy(chunk.Object);
        chunk = null;
    }

    private static long CellKey(int x, int y) => ((long)(uint)x << 32) | (uint)y;
    private static int FloorToInt(float value) => (int)Math.Floor(value);
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

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
