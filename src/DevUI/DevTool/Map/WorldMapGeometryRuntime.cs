using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.TerrainExt.QuicksandZone;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

public enum EditorMapGeometryKind
{
    Solid,
    Detail,
    Water,
    LocalTerrain,
    CurvedSlope,
    QuicksandBody,
    QuicksandMaterial
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
    public EditorMapPointSnapshot[] Points { get; init; } = Array.Empty<EditorMapPointSnapshot>();
}

public readonly struct EditorMapNodeVisualSnapshot
{
    public EditorMapNodeVisualSnapshot(int nodeIndex, float x, float y)
    {
        NodeIndex = nodeIndex;
        X = x;
        Y = y;
    }

    public int NodeIndex { get; }
    public float X { get; }
    public float Y { get; }
}

public sealed class EditorMapRoomVisualSnapshot
{
    public static readonly EditorMapRoomVisualSnapshot Empty = new();

    public bool Available { get; init; }
    public bool DetailedRasterAvailable { get; init; }
    public float WidthTiles { get; init; } = 12f;
    public float HeightTiles { get; init; } = 6f;
    public EditorMapRectSnapshot[] RasterRuns { get; init; } = Array.Empty<EditorMapRectSnapshot>();
    public EditorMapPolylineSnapshot[] Curves { get; init; } = Array.Empty<EditorMapPolylineSnapshot>();
    public EditorMapNodeVisualSnapshot[] Nodes { get; init; } = Array.Empty<EditorMapNodeVisualSnapshot>();
}

/// <summary>
/// Geometry cache for the unified World Map.
///
/// The vanilla MapObject raster remains the cheap base layer. Continuous terrain is rebuilt from
/// the authored RoomSettings geometry so Watcher terrain (TerrainHandle room curves, LocalTerrain,
/// CurvedSlope and SuperSlope) and DryCycle terrain keep their real silhouette instead of being
/// flattened into tile coverage.
///
/// Curve bodies are converted to narrow cached fill runs. This keeps the frontend draw path cheap
/// while still producing a visually continuous filled terrain band at normal map zoom levels.
/// </summary>
internal static class MapRoomGeometryPresentationHub
{
    private const float PixelsPerTile = 20f;
    private const float CurveSimplifyToleranceTiles = 0.075f;
    private const int MaxCurveSamples = 192;

    // Opening a large region must never synchronously decode every minimap/RoomSettings file.
    // Cheap room bounds appear immediately; detailed raster and authored terrain are filled in
    // incrementally with the current/selected room receiving first priority.
    private const int RasterLoadsPerFrame = 10;
    private const int UnloadedCurveLoadsPerFrame = 3;
    private const int BackgroundRoomsPerFrame = 24;
    private const int StructureSyncIntervalFrames = 120;
    private const int RasterPollIntervalFrames = 180;
    private const int NodePollIntervalFrames = 30;
    private const int LiveSettingsPollIntervalFrames = 12;
    private const int SettingsPollIntervalFrames = 90;

    private sealed class CacheEntry
    {
        internal int RoomIndex;
        internal string RoomName = string.Empty;
        internal AbstractRoom Room;
        internal MapObject.RoomRepresentation RoomRep;
        internal int RasterSourceKey;
        internal int RasterWidth;
        internal int RasterHeight;
        internal bool RasterInitialized;
        internal int NextRasterPollFrame;
        internal int NodeFingerprint;
        internal bool NodesInitialized;
        internal int NextNodePollFrame;
        internal int SettingsFingerprint;
        internal int NextLiveSettingsPollFrame;
        internal string SettingsPath = string.Empty;
        internal DateTime SettingsWriteTimeUtc;
        internal int NextSettingsPollFrame;
        internal bool CurvesInitialized;
        internal float WidthTiles = 12f;
        internal float HeightTiles = 6f;
        internal EditorMapRectSnapshot[] BaseRasterRuns = Array.Empty<EditorMapRectSnapshot>();
        internal EditorMapRectSnapshot[] TerrainFillRuns = Array.Empty<EditorMapRectSnapshot>();
        internal EditorMapPolylineSnapshot[] Curves = Array.Empty<EditorMapPolylineSnapshot>();
        internal EditorMapNodeVisualSnapshot[] Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
        internal EditorMapRoomVisualSnapshot Snapshot = EditorMapRoomVisualSnapshot.Empty;
        internal int Revision = 1;
        internal int PublishedRevision;
    }

    private static readonly Dictionary<int, CacheEntry> cache = new();
    private static readonly List<int> roomOrder = new();
    private static string region = string.Empty;
    private static int lastPrimeFrame = -1;
    private static int lastSubNodeCount = -1;
    private static int nextStructureSyncFrame;
    private static int backgroundCursor;
    private static int rasterLoadsRemaining;
    private static int curveLoadsRemaining;

    internal static EditorMapRoomVisualSnapshot Get(int roomIndex) =>
        cache.TryGetValue(roomIndex, out CacheEntry entry)
            ? entry.Snapshot
            : EditorMapRoomVisualSnapshot.Empty;

    internal static void Prime(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || page.world == null)
        {
            Clear();
            return;
        }

        string nextRegion = page.world.name ?? string.Empty;
        bool regionChanged = !string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase);
        if (regionChanged)
        {
            ResetRegion(nextRegion);
        }

        if (lastPrimeFrame == Time.frameCount) return;
        lastPrimeFrame = Time.frameCount;

        bool structureDue = regionChanged ||
                            cache.Count == 0 ||
                            page.subNodes.Count != lastSubNodeCount ||
                            Time.frameCount >= nextStructureSyncFrame;
        if (structureDue)
            SynchronizeStructure(page);

        rasterLoadsRemaining = RasterLoadsPerFrame;
        curveLoadsRemaining = UnloadedCurveLoadsPerFrame;

        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        int selectedRoom = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;

        // The room being inspected/played is always fully prioritized. This keeps authoring feedback
        // immediate while the rest of a large region continues warming in the background.
        RefreshPriorityRoom(currentRoom, page.world);
        if (selectedRoom != currentRoom) RefreshPriorityRoom(selectedRoom, page.world);

        ProcessBackground(page.world, currentRoom, selectedRoom);
    }

    internal static void InvalidateRoom(int roomIndex)
    {
        if (!cache.TryGetValue(roomIndex, out CacheEntry entry)) return;
        entry.RasterInitialized = false;
        entry.CurvesInitialized = false;
        entry.NodesInitialized = false;
        entry.NextRasterPollFrame = 0;
        entry.NextNodePollFrame = 0;
        entry.NextSettingsPollFrame = 0;
        entry.NextLiveSettingsPollFrame = 0;
        entry.Revision++;
    }

    internal static void Clear()
    {
        cache.Clear();
        roomOrder.Clear();
        region = string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
        rasterLoadsRemaining = 0;
        curveLoadsRemaining = 0;
    }

    private static void ResetRegion(string nextRegion)
    {
        cache.Clear();
        roomOrder.Clear();
        region = nextRegion ?? string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
    }

    private static void SynchronizeStructure(MapPage page)
    {
        HashSet<int> alive = new();
        roomOrder.Clear();

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            alive.Add(room.index);
            roomOrder.Add(room.index);

            if (!cache.TryGetValue(room.index, out CacheEntry entry))
            {
                entry = new CacheEntry
                {
                    RoomIndex = room.index,
                    RoomName = room.name ?? string.Empty
                };
                cache.Add(room.index, entry);
            }

            entry.Room = room;
            entry.RoomRep = panel.roomRep;
            entry.RoomName = room.name ?? entry.RoomName;

            // Bounds are cheap and are enough to display/use the map immediately. Do not decode
            // the room texture or parse RoomSettings while doing this structural pass.
            RefreshDimensions(entry, panel.roomRep);
            RefreshNodes(entry, panel.roomRep, force: !entry.NodesInitialized);
            Publish(entry);
        }

        if (cache.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int key in cache.Keys)
                if (!alive.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
        }

        if (backgroundCursor >= roomOrder.Count) backgroundCursor = 0;
        lastSubNodeCount = page.subNodes.Count;
        nextStructureSyncFrame = Time.frameCount + StructureSyncIntervalFrames;
    }

    private static void RefreshPriorityRoom(int roomIndex, global::World world)
    {
        if (roomIndex < 0 || !cache.TryGetValue(roomIndex, out CacheEntry entry)) return;
        RefreshDimensions(entry, entry.RoomRep);
        RefreshNodes(entry, entry.RoomRep, force: true);
        if (RefreshRaster(entry, entry.RoomRep, allowDecode: true, forcePoll: true))
            rasterLoadsRemaining = Math.Max(0, rasterLoadsRemaining - 1);
        if (RefreshCurves(entry, world, entry.Room, allowDiskLoad: true, forceLivePoll: false))
            curveLoadsRemaining = Math.Max(0, curveLoadsRemaining - 1);
        Publish(entry);
    }

    private static void ProcessBackground(global::World world, int currentRoom, int selectedRoom)
    {
        int count = roomOrder.Count;
        if (count == 0) return;

        int checks = Math.Min(count, BackgroundRoomsPerFrame);
        for (int i = 0; i < checks; i++)
        {
            if (backgroundCursor >= count) backgroundCursor = 0;
            int roomIndex = roomOrder[backgroundCursor++];
            if (roomIndex == currentRoom || roomIndex == selectedRoom) continue;
            if (!cache.TryGetValue(roomIndex, out CacheEntry entry)) continue;

            RefreshDimensions(entry, entry.RoomRep);
            RefreshNodes(entry, entry.RoomRep, force: false);
            if (rasterLoadsRemaining > 0 &&
                RefreshRaster(entry, entry.RoomRep, allowDecode: true, forcePoll: false))
                rasterLoadsRemaining--;

            if (curveLoadsRemaining > 0 &&
                RefreshCurves(entry, world, entry.Room, allowDiskLoad: true, forceLivePoll: false))
                curveLoadsRemaining--;

            Publish(entry);
        }
    }

    private static void RefreshDimensions(CacheEntry entry, MapObject.RoomRepresentation roomRep)
    {
        float width = entry.WidthTiles;
        float height = entry.HeightTiles;
        if (roomRep?.texture != null)
        {
            width = Math.Max(1f, roomRep.texture.width);
            height = Math.Max(1f, roomRep.texture.height);
        }
        else if (roomRep?.mapTex != null)
        {
            width = Math.Max(1f, roomRep.mapTex.sourcePixelSize.x);
            height = Math.Max(1f, roomRep.mapTex.sourcePixelSize.y);
        }

        if (Math.Abs(width - entry.WidthTiles) < 0.001f &&
            Math.Abs(height - entry.HeightTiles) < 0.001f)
            return;

        entry.WidthTiles = width;
        entry.HeightTiles = height;
        entry.CurvesInitialized = false;
        entry.Revision++;
    }

    private readonly struct RasterSourceInfo
    {
        internal RasterSourceInfo(Texture2D texture, int x, int y, int width, int height, int sourceKey)
        {
            Texture = texture;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            SourceKey = sourceKey;
        }

        internal Texture2D Texture { get; }
        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int SourceKey { get; }
    }

    private static bool RefreshRaster(
        CacheEntry entry,
        MapObject.RoomRepresentation roomRep,
        bool allowDecode,
        bool forcePoll)
    {
        if (entry.RasterInitialized && !forcePoll && Time.frameCount < entry.NextRasterPollFrame)
            return false;

        if (!TryGetRasterSourceInfo(roomRep, out RasterSourceInfo source))
            return false;

        if (entry.RasterInitialized &&
            entry.RasterSourceKey == source.SourceKey &&
            entry.RasterWidth == source.Width &&
            entry.RasterHeight == source.Height)
        {
            entry.NextRasterPollFrame = Time.frameCount + RasterPollIntervalFrames + Math.Abs(entry.RoomIndex % 37);
            return false;
        }

        if (!allowDecode) return false;
        if (!TryReadMapPixels(source, out Color[] pixels)) return false;

        List<EditorMapRectSnapshot> runs = new();
        for (int y = 0; y < source.Height; y++)
        {
            int x = 0;
            while (x < source.Width)
            {
                EditorMapGeometryKind? kind = ClassifyPixel(pixels[y * source.Width + x]);
                if (!kind.HasValue)
                {
                    x++;
                    continue;
                }

                int start = x;
                x++;
                while (x < source.Width && ClassifyPixel(pixels[y * source.Width + x]) == kind)
                    x++;
                runs.Add(new EditorMapRectSnapshot(start, y, x - start, 1f, kind.Value));
            }
        }

        entry.RasterSourceKey = source.SourceKey;
        entry.RasterWidth = source.Width;
        entry.RasterHeight = source.Height;
        entry.RasterInitialized = true;
        entry.NextRasterPollFrame = Time.frameCount + RasterPollIntervalFrames + Math.Abs(entry.RoomIndex % 37);
        entry.WidthTiles = Math.Max(1f, source.Width);
        entry.HeightTiles = Math.Max(1f, source.Height);
        entry.BaseRasterRuns = runs.ToArray();
        entry.Revision++;
        return true;
    }

    private static bool TryGetRasterSourceInfo(
        MapObject.RoomRepresentation roomRep,
        out RasterSourceInfo source)
    {
        source = default;
        try
        {
            if (roomRep?.texture != null)
            {
                Texture2D texture = roomRep.texture;
                source = new RasterSourceInfo(
                    texture,
                    0,
                    0,
                    Math.Max(1, texture.width),
                    Math.Max(1, texture.height),
                    texture.GetInstanceID());
                return true;
            }

            FAtlasElement element = roomRep?.mapTex;
            if (element?.atlas?.texture is not Texture2D atlasTexture) return false;

            Rect uv = element.uvRect;
            int atlasX = Mathf.Clamp(Mathf.RoundToInt(uv.x * atlasTexture.width), 0, Math.Max(0, atlasTexture.width - 1));
            int atlasY = Mathf.Clamp(Mathf.RoundToInt(uv.y * atlasTexture.height), 0, Math.Max(0, atlasTexture.height - 1));
            int width = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.width) * atlasTexture.width), 1, atlasTexture.width - atlasX);
            int height = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.height) * atlasTexture.height), 1, atlasTexture.height - atlasY);

            unchecked
            {
                int key = atlasTexture.GetInstanceID();
                key = key * 397 ^ (element.name?.GetHashCode() ?? 0);
                key = key * 397 ^ uv.x.GetHashCode();
                key = key * 397 ^ uv.y.GetHashCode();
                key = key * 397 ^ uv.width.GetHashCode();
                key = key * 397 ^ uv.height.GetHashCode();
                source = new RasterSourceInfo(atlasTexture, atlasX, atlasY, width, height, key);
            }
            return true;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap minimap source unavailable for " + (roomRep?.room?.name ?? "?") + ": " + error.Message);
            return false;
        }
    }

    private static bool TryReadMapPixels(RasterSourceInfo source, out Color[] pixels)
    {
        pixels = null;
        try
        {
            if (source.Texture == null) return false;
            pixels = source.X == 0 && source.Y == 0 &&
                     source.Width == source.Texture.width && source.Height == source.Texture.height
                ? source.Texture.GetPixels()
                : source.Texture.GetPixels(source.X, source.Y, source.Width, source.Height);
            return pixels != null && pixels.Length == source.Width * source.Height;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug("WorldMap minimap raster read failed: " + error.Message);
            return false;
        }
    }

    private static EditorMapGeometryKind? ClassifyPixel(Color color)
    {
        if (color.b > color.r + 0.12f && color.b > color.g + 0.12f)
            return EditorMapGeometryKind.Water;
        if (color.r < 0.40f && color.g < 0.40f && color.b < 0.40f)
            return EditorMapGeometryKind.Solid;
        if (color.r >= 0.42f && color.r <= 0.58f &&
            color.g >= 0.22f && color.g <= 0.42f &&
            color.b >= 0.22f && color.b <= 0.42f)
            return EditorMapGeometryKind.Detail;
        return null;
    }

    private static void RefreshNodes(
        CacheEntry entry,
        MapObject.RoomRepresentation roomRep,
        bool force)
    {
        if (entry.NodesInitialized && !force && Time.frameCount < entry.NextNodePollFrame) return;

        Vector2[] positions = roomRep?.nodePositions;
        if (positions == null || positions.Length == 0)
        {
            // RoomRepresentation often receives its node positions a little later than its bounds.
            // Retry quickly until they exist, then switch to the normal low-frequency poll.
            entry.NextNodePollFrame = Time.frameCount + (entry.NodesInitialized ? NodePollIntervalFrames : 1);
            if (entry.NodesInitialized && entry.Nodes.Length == 0) return;
            entry.Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
            entry.NodesInitialized = true;
            entry.NodeFingerprint = 0;
            entry.Revision++;
            return;
        }

        unchecked
        {
            int fingerprint = positions.Length;
            for (int i = 0; i < positions.Length; i++)
            {
                fingerprint = fingerprint * 31 + positions[i].x.GetHashCode();
                fingerprint = fingerprint * 31 + positions[i].y.GetHashCode();
            }

            entry.NextNodePollFrame = Time.frameCount + NodePollIntervalFrames + Math.Abs(entry.RoomIndex % 11);
            if (entry.NodesInitialized && fingerprint == entry.NodeFingerprint) return;

            List<EditorMapNodeVisualSnapshot> nodes = new(positions.Length);
            for (int i = 0; i < positions.Length; i++)
            {
                Vector2 point = positions[i];
                if (Math.Abs(point.x) < 0.001f && Math.Abs(point.y) < 0.001f) continue;
                nodes.Add(new EditorMapNodeVisualSnapshot(i, point.x, point.y));
            }

            entry.Nodes = nodes.ToArray();
            entry.NodesInitialized = true;
            entry.NodeFingerprint = fingerprint;
            entry.Revision++;
        }
    }

    /// <summary>
    /// Returns true only when this call performed an expensive RoomSettings load/rebuild.
    /// </summary>
    private static bool RefreshCurves(
        CacheEntry entry,
        global::World world,
        AbstractRoom room,
        bool allowDiskLoad,
        bool forceLivePoll)
    {
        RoomSettings liveSettings = room?.realizedRoom?.roomSettings;
        if (liveSettings != null)
        {
            if (entry.CurvesInitialized && !forceLivePoll && Time.frameCount < entry.NextLiveSettingsPollFrame)
                return false;

            entry.NextLiveSettingsPollFrame = Time.frameCount + LiveSettingsPollIntervalFrames;
            int liveFingerprint = GeometrySettingsFingerprint(liveSettings);
            if (entry.CurvesInitialized && liveFingerprint == entry.SettingsFingerprint) return false;
            RebuildCurves(entry, liveSettings, liveFingerprint);
            return true;
        }

        if (!entry.CurvesInitialized && Time.frameCount < entry.NextSettingsPollFrame)
            return false;

        if (entry.CurvesInitialized)
        {
            if (string.IsNullOrWhiteSpace(entry.SettingsPath)) return false;
            if (Time.frameCount < entry.NextSettingsPollFrame) return false;
            entry.NextSettingsPollFrame = Time.frameCount + SettingsPollIntervalFrames + Math.Abs(entry.RoomIndex % 30);
            if (FileWriteTime(entry.SettingsPath) == entry.SettingsWriteTimeUtc) return false;
        }

        if (!allowDiskLoad) return false;

        RoomSettings settings = null;
        try
        {
            string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
            SlugcatStats.Timeline timeline = world.game != null ? world.game.TimelinePoint : null;
            settings = new RoomSettings(roomName, world.region, false, false, timeline, world.game);
        }
        catch (Exception error)
        {
            entry.NextSettingsPollFrame = Time.frameCount + SettingsPollIntervalFrames;
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap could not load room settings for " + entry.RoomName + ": " + error.Message);
        }

        if (settings == null) return false;
        RebuildCurves(entry, settings, GeometrySettingsFingerprint(settings));
        return true;
    }

    private static void RebuildCurves(CacheEntry entry, RoomSettings settings, int fingerprint)
    {
        entry.SettingsFingerprint = fingerprint;
        entry.SettingsPath = settings.filePath ?? string.Empty;
        entry.SettingsWriteTimeUtc = FileWriteTime(entry.SettingsPath);
        entry.NextSettingsPollFrame = Time.frameCount + SettingsPollIntervalFrames + Math.Abs(entry.RoomIndex % 30);

        BuildCurveGeometry(
            settings,
            entry.WidthTiles,
            out List<EditorMapPolylineSnapshot> curves,
            out List<EditorMapRectSnapshot> fills);
        entry.Curves = curves.ToArray();
        entry.TerrainFillRuns = fills.ToArray();
        entry.CurvesInitialized = true;
        entry.Revision++;
    }

    private static DateTime FileWriteTime(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static int GeometrySettingsFingerprint(RoomSettings settings)
    {
        unchecked
        {
            int hash = 17;
            List<PlacedObject> objects = settings?.placedObjects;
            if (objects == null) return hash;
            for (int i = 0; i < objects.Count; i++)
            {
                PlacedObject placed = objects[i];
                if (!IsThumbnailTerrain(placed)) continue;
                hash = hash * 31 + (placed.type?.value?.GetHashCode() ?? 0);
                hash = hash * 31 + placed.pos.GetHashCode();
                hash = hash * 31 + (placed.active ? 1 : 0);
                hash = hash * 31 + (placed.data?.ToString()?.GetHashCode() ?? 0);
            }
            return hash;
        }
    }

    private static bool IsThumbnailTerrain(PlacedObject placed)
    {
        if (placed == null || !placed.active) return false;
        if (placed.type == PlacedObject.Type.TerrainHandle ||
            placed.type == PlacedObject.Type.LocalTerrain ||
            placed.type == PlacedObject.Type.CurvedSlope ||
            placed.type == PlacedObject.Type.SuperSlope)
            return true;
        return string.Equals(placed.type?.value, "QuicksandZone", StringComparison.Ordinal);
    }

    private static void BuildCurveGeometry(
        RoomSettings settings,
        float roomWidthTiles,
        out List<EditorMapPolylineSnapshot> curves,
        out List<EditorMapRectSnapshot> fills)
    {
        curves = new List<EditorMapPolylineSnapshot>();
        fills = new List<EditorMapRectSnapshot>();
        List<PlacedObject> objects = settings?.placedObjects;
        if (objects == null) return;

        // TerrainHandle is not a local spline object. Two or more handles jointly define the
        // room-wide TerrainCurve, so it must be reconstructed as one continuous surface.
        AddRoomTerrainCurve(objects, roomWidthTiles, curves, fills);

        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject placed = objects[i];
            if (!IsThumbnailTerrain(placed) || placed.type == PlacedObject.Type.TerrainHandle) continue;

            if (placed.type == PlacedObject.Type.LocalTerrain &&
                placed.data is PlacedObject.LocalTerrainData localTerrain)
            {
                AddSplineFlatBand(
                    curves,
                    fills,
                    localTerrain.spline,
                    placed.pos,
                    localTerrain.bottom,
                    EditorMapGeometryKind.LocalTerrain,
                    EditorMapGeometryKind.Detail,
                    0f,
                    1f);
                continue;
            }

            if (placed.type == PlacedObject.Type.CurvedSlope &&
                placed.data is PlacedObject.LocalTerrainData curvedSlope)
            {
                AddSplineThicknessBand(
                    curves,
                    fills,
                    curvedSlope.spline,
                    placed.pos,
                    curvedSlope.bottom,
                    EditorMapGeometryKind.CurvedSlope,
                    EditorMapGeometryKind.Solid,
                    0f,
                    1f);
                continue;
            }

            if (placed.type == PlacedObject.Type.SuperSlope &&
                placed.data is PlacedObject.SuperSlopeData superSlope)
            {
                AddSuperSlope(curves, fills, placed.pos, superSlope);
                continue;
            }

            if (string.Equals(placed.type?.value, "QuicksandZone", StringComparison.Ordinal) &&
                placed.data is QuicksandZoneData quicksand &&
                quicksand.SurfaceSpline != null)
            {
                AddSplineFlatBand(
                    curves,
                    fills,
                    quicksand.SurfaceSpline,
                    placed.pos,
                    quicksand.BottomDepth,
                    EditorMapGeometryKind.QuicksandBody,
                    EditorMapGeometryKind.QuicksandBody,
                    0f,
                    1f);

                List<Vector2> intervals = new();
                quicksand.FillQuicksandIntervals(intervals);
                for (int interval = 0; interval < intervals.Count; interval++)
                {
                    Vector2 range = intervals[interval];
                    AddSplineFlatBand(
                        curves,
                        fills,
                        quicksand.SurfaceSpline,
                        placed.pos,
                        quicksand.BottomDepth,
                        EditorMapGeometryKind.QuicksandMaterial,
                        EditorMapGeometryKind.QuicksandMaterial,
                        range.x,
                        range.y);
                }
            }
        }
    }

    private static void AddRoomTerrainCurve(
        List<PlacedObject> objects,
        float roomWidthTiles,
        List<EditorMapPolylineSnapshot> curves,
        List<EditorMapRectSnapshot> fills)
    {
        List<TerrainCurve.Handle> handles = new();
        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject placed = objects[i];
            if (placed == null || !placed.active || placed.type != PlacedObject.Type.TerrainHandle ||
                placed.data is not PlacedObject.TerrainHandleData data)
                continue;

            handles.Add(new TerrainCurve.Handle(
                data.leftOffset + placed.pos,
                placed.pos,
                data.rightOffset + placed.pos,
                data.backHeight));
        }

        if (handles.Count < 2) return;
        handles.Sort((a, b) => a.Middle.x.CompareTo(b.Middle.x));

        float roomWidthPixels = Math.Max(PixelsPerTile, roomWidthTiles * PixelsPerTile);
        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(roomWidthPixels / 10f) + 1, 16, MaxCurveSamples);
        List<EditorMapPointSnapshot> surface = new(sampleCount);
        int handle = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float x = Mathf.Lerp(0f, roomWidthPixels, (float)i / (sampleCount - 1));
            while (handle < handles.Count - 2 && handles[handle + 1].Middle.x < x) handle++;
            float y = TerrainCurve.Handle.Sample(handles[handle], handles[handle + 1], x);
            if (float.IsNaN(y) || float.IsInfinity(y)) continue;
            surface.Add(new EditorMapPointSnapshot(x / PixelsPerTile, y / PixelsPerTile));
        }

        if (surface.Count < 2) return;
        AddFlatFillRuns(fills, surface, 0f, EditorMapGeometryKind.Solid);
        AddSurfaceCurve(curves, surface, EditorMapGeometryKind.CurvedSlope);
    }

    private static void AddSplineFlatBand(
        List<EditorMapPolylineSnapshot> curves,
        List<EditorMapRectSnapshot> fills,
        BezierSpline spline,
        Vector2 origin,
        float depthPixels,
        EditorMapGeometryKind lineKind,
        EditorMapGeometryKind fillKind,
        float startU,
        float endU)
    {
        List<EditorMapPointSnapshot> surface = SampleSpline(spline, origin, startU, endU);
        if (surface.Count < 2) return;

        float bottomY = (origin.y - Math.Max(1f, depthPixels)) / PixelsPerTile;
        AddFlatFillRuns(fills, surface, bottomY, fillKind);
        AddSurfaceCurve(curves, surface, lineKind);
    }

    private static void AddSplineThicknessBand(
        List<EditorMapPolylineSnapshot> curves,
        List<EditorMapRectSnapshot> fills,
        BezierSpline spline,
        Vector2 origin,
        float thicknessPixels,
        EditorMapGeometryKind lineKind,
        EditorMapGeometryKind fillKind,
        float startU,
        float endU)
    {
        List<EditorMapPointSnapshot> surface = SampleSpline(spline, origin, startU, endU);
        if (surface.Count < 2) return;

        float thicknessTiles = Math.Max(1f, thicknessPixels) / PixelsPerTile;
        List<EditorMapPointSnapshot> back = new(surface.Count);
        for (int i = 0; i < surface.Count; i++)
            back.Add(new EditorMapPointSnapshot(surface[i].X, surface[i].Y - thicknessTiles));

        AddPairedFillRuns(fills, surface, back, fillKind);
        AddSurfaceCurve(curves, surface, lineKind);
    }

    private static void AddSuperSlope(
        List<EditorMapPolylineSnapshot> curves,
        List<EditorMapRectSnapshot> fills,
        Vector2 origin,
        PlacedObject.SuperSlopeData data)
    {
        Vector2 a = origin;
        Vector2 b = origin + data.handlePos;
        if (b.x < a.x)
        {
            Vector2 swap = a;
            a = b;
            b = swap;
        }

        float length = Math.Max(1f, Vector2.Distance(a, b));
        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(length / 10f) + 1, 2, 96);
        float thicknessTiles = Math.Max(1f, data.bottom) / PixelsPerTile;
        List<EditorMapPointSnapshot> surface = new(sampleCount);
        List<EditorMapPointSnapshot> back = new(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, sampleCount <= 1 ? 0f : (float)i / (sampleCount - 1));
            EditorMapPointSnapshot top = ToTilePoint(p);
            surface.Add(top);
            back.Add(new EditorMapPointSnapshot(top.X, top.Y - thicknessTiles));
        }

        AddPairedFillRuns(fills, surface, back, EditorMapGeometryKind.Solid);
        // SuperSlope is a straight slope band; use the strong slope surface treatment rather than
        // the subdued LocalTerrain treatment so it remains readable over the vanilla raster.
        AddSurfaceCurve(curves, surface, EditorMapGeometryKind.CurvedSlope);
    }

    private static List<EditorMapPointSnapshot> SampleSpline(
        BezierSpline spline,
        Vector2 origin,
        float startU,
        float endU)
    {
        List<EditorMapPointSnapshot> surface = new();
        if (spline == null || endU <= startU + 0.0001f) return surface;

        float sampledLength = Math.Max(1f, spline.GetFullLength * (endU - startU));
        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(sampledLength / 10f) + 1, 8, MaxCurveSamples);
        surface.Capacity = sampleCount;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount <= 1 ? 0f : (float)i / (sampleCount - 1);
            float u = Mathf.Lerp(startU, endU, t);
            Vector2 point = origin + EvaluateSplineByLength(spline, u);
            surface.Add(ToTilePoint(point));
        }
        return surface;
    }

    private static void AddSurfaceCurve(
        List<EditorMapPolylineSnapshot> output,
        List<EditorMapPointSnapshot> surface,
        EditorMapGeometryKind kind)
    {
        List<EditorMapPointSnapshot> simplified = Simplify(surface, CurveSimplifyToleranceTiles);
        if (simplified.Count < 2) return;
        output.Add(new EditorMapPolylineSnapshot
        {
            Kind = kind,
            Closed = false,
            Points = simplified.ToArray()
        });
    }

    private static void AddFlatFillRuns(
        List<EditorMapRectSnapshot> output,
        List<EditorMapPointSnapshot> surface,
        float bottomY,
        EditorMapGeometryKind kind)
    {
        for (int i = 0; i < surface.Count - 1; i++)
        {
            EditorMapPointSnapshot a = surface[i];
            EditorMapPointSnapshot b = surface[i + 1];
            AddFillRun(output, a.X, b.X, a.Y, b.Y, bottomY, bottomY, kind);
        }
    }

    private static void AddPairedFillRuns(
        List<EditorMapRectSnapshot> output,
        List<EditorMapPointSnapshot> front,
        List<EditorMapPointSnapshot> back,
        EditorMapGeometryKind kind)
    {
        int count = Math.Min(front.Count, back.Count);
        for (int i = 0; i < count - 1; i++)
        {
            EditorMapPointSnapshot a = front[i];
            EditorMapPointSnapshot b = front[i + 1];
            EditorMapPointSnapshot c = back[i];
            EditorMapPointSnapshot d = back[i + 1];
            AddFillRun(output, a.X, b.X, a.Y, b.Y, c.Y, d.Y, kind);
        }
    }

    private static void AddFillRun(
        List<EditorMapRectSnapshot> output,
        float x0,
        float x1,
        float frontY0,
        float frontY1,
        float backY0,
        float backY1,
        EditorMapGeometryKind kind)
    {
        float minX = Math.Min(x0, x1);
        float width = Math.Abs(x1 - x0);
        if (width < 0.0025f) return;

        float minY = Math.Min(Math.Min(frontY0, frontY1), Math.Min(backY0, backY1));
        float maxY = Math.Max(Math.Max(frontY0, frontY1), Math.Max(backY0, backY1));
        float height = maxY - minY;
        if (height < 0.0025f) return;

        output.Add(new EditorMapRectSnapshot(minX, minY, width + 0.015f, height, kind));
    }

    private static Vector2 EvaluateSplineByLength(BezierSpline spline, float u)
    {
        if (spline == null || spline.Segments <= 0) return Vector2.zero;
        u = Mathf.Clamp01(u);
        float total = Mathf.Max(0.001f, spline.GetFullLength);
        float remaining = total * u;
        for (int segment = 0; segment < spline.Segments; segment++)
        {
            float length = Mathf.Max(0.001f, spline.GetSegmentLength(segment));
            if (remaining <= length || segment == spline.Segments - 1)
                return spline.GetBezier(segment).GetPoint(Mathf.Clamp01(remaining / length));
            remaining -= length;
        }
        return spline.posB;
    }

    private static EditorMapPointSnapshot ToTilePoint(Vector2 pixelPoint) =>
        new(pixelPoint.x / PixelsPerTile, pixelPoint.y / PixelsPerTile);

    private static List<EditorMapPointSnapshot> Simplify(List<EditorMapPointSnapshot> points, float tolerance)
    {
        if (points == null || points.Count <= 2) return points ?? new List<EditorMapPointSnapshot>();
        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        SimplifyRange(points, 0, points.Count - 1, tolerance * tolerance, keep);

        List<EditorMapPointSnapshot> result = new();
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static void SimplifyRange(
        List<EditorMapPointSnapshot> points,
        int start,
        int end,
        float toleranceSq,
        bool[] keep)
    {
        if (end <= start + 1) return;
        EditorMapPointSnapshot a = points[start];
        EditorMapPointSnapshot b = points[end];
        float best = -1f;
        int bestIndex = -1;
        for (int i = start + 1; i < end; i++)
        {
            float distance = DistanceToSegmentSquared(points[i], a, b);
            if (distance <= best) continue;
            best = distance;
            bestIndex = i;
        }

        if (bestIndex < 0 || best <= toleranceSq) return;
        keep[bestIndex] = true;
        SimplifyRange(points, start, bestIndex, toleranceSq, keep);
        SimplifyRange(points, bestIndex, end, toleranceSq, keep);
    }

    private static float DistanceToSegmentSquared(
        EditorMapPointSnapshot p,
        EditorMapPointSnapshot a,
        EditorMapPointSnapshot b)
    {
        float abX = b.X - a.X;
        float abY = b.Y - a.Y;
        float lengthSq = abX * abX + abY * abY;
        if (lengthSq <= 0.000001f)
        {
            float dx = p.X - a.X;
            float dy = p.Y - a.Y;
            return dx * dx + dy * dy;
        }

        float t = Mathf.Clamp01(((p.X - a.X) * abX + (p.Y - a.Y) * abY) / lengthSq);
        float x = a.X + abX * t;
        float y = a.Y + abY * t;
        float px = p.X - x;
        float py = p.Y - y;
        return px * px + py * py;
    }

    private static EditorMapRectSnapshot[] MergeRuns(
        EditorMapRectSnapshot[] baseRuns,
        EditorMapRectSnapshot[] terrainRuns)
    {
        int baseCount = baseRuns?.Length ?? 0;
        int terrainCount = terrainRuns?.Length ?? 0;
        if (terrainCount == 0) return baseRuns ?? Array.Empty<EditorMapRectSnapshot>();
        if (baseCount == 0) return terrainRuns ?? Array.Empty<EditorMapRectSnapshot>();

        EditorMapRectSnapshot[] merged = new EditorMapRectSnapshot[baseCount + terrainCount];
        Array.Copy(baseRuns, 0, merged, 0, baseCount);
        Array.Copy(terrainRuns, 0, merged, baseCount, terrainCount);
        return merged;
    }

    private static void Publish(CacheEntry entry)
    {
        if (entry.PublishedRevision == entry.Revision) return;
        entry.Snapshot = new EditorMapRoomVisualSnapshot
        {
            Available = entry.WidthTiles > 0f && entry.HeightTiles > 0f,
            DetailedRasterAvailable = entry.RasterInitialized || entry.TerrainFillRuns.Length > 0,
            WidthTiles = Math.Max(1f, entry.WidthTiles),
            HeightTiles = Math.Max(1f, entry.HeightTiles),
            RasterRuns = MergeRuns(entry.BaseRasterRuns, entry.TerrainFillRuns),
            Curves = entry.Curves,
            Nodes = entry.Nodes
        };
        entry.PublishedRevision = entry.Revision;
    }
}
