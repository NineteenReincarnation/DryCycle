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
    private const int UnloadedCurveLoadsPerFrame = 4;
    private const int SettingsPollIntervalFrames = 90;

    private sealed class CacheEntry
    {
        internal int RoomIndex;
        internal string RoomName = string.Empty;
        internal int RasterSourceKey;
        internal int RasterWidth;
        internal int RasterHeight;
        internal bool RasterInitialized;
        internal int NodeFingerprint;
        internal bool NodesInitialized;
        internal int SettingsFingerprint;
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
    private static string region = string.Empty;
    private static int lastPrimeFrame = -1;
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
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            cache.Clear();
            region = nextRegion;
            lastPrimeFrame = -1;
        }

        if (lastPrimeFrame == Time.frameCount) return;
        lastPrimeFrame = Time.frameCount;
        curveLoadsRemaining = UnloadedCurveLoadsPerFrame;

        HashSet<int> alive = new();
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            alive.Add(room.index);

            if (!cache.TryGetValue(room.index, out CacheEntry entry))
            {
                entry = new CacheEntry
                {
                    RoomIndex = room.index,
                    RoomName = room.name ?? string.Empty
                };
                cache.Add(room.index, entry);
            }

            RefreshDimensions(entry, panel.roomRep);
            RefreshRaster(entry, panel.roomRep);
            RefreshNodes(entry, panel.roomRep);
            RefreshCurves(entry, page.world, room);
            Publish(entry);
        }

        if (cache.Count == alive.Count) return;
        List<int> stale = new();
        foreach (int key in cache.Keys)
            if (!alive.Contains(key)) stale.Add(key);
        for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
    }

    internal static void InvalidateRoom(int roomIndex) => cache.Remove(roomIndex);

    internal static void Clear()
    {
        cache.Clear();
        region = string.Empty;
        lastPrimeFrame = -1;
        curveLoadsRemaining = 0;
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

    private static void RefreshRaster(CacheEntry entry, MapObject.RoomRepresentation roomRep)
    {
        if (!TryReadMapPixels(roomRep, out Color[] pixels, out int width, out int height, out int sourceKey))
            return;

        if (entry.RasterInitialized &&
            entry.RasterSourceKey == sourceKey &&
            entry.RasterWidth == width &&
            entry.RasterHeight == height)
            return;

        List<EditorMapRectSnapshot> runs = new();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                EditorMapGeometryKind? kind = ClassifyPixel(pixels[y * width + x]);
                if (!kind.HasValue)
                {
                    x++;
                    continue;
                }

                int start = x;
                x++;
                while (x < width && ClassifyPixel(pixels[y * width + x]) == kind)
                    x++;
                runs.Add(new EditorMapRectSnapshot(start, y, x - start, 1f, kind.Value));
            }
        }

        entry.RasterSourceKey = sourceKey;
        entry.RasterWidth = width;
        entry.RasterHeight = height;
        entry.RasterInitialized = true;
        entry.WidthTiles = Math.Max(1f, width);
        entry.HeightTiles = Math.Max(1f, height);
        entry.BaseRasterRuns = runs.ToArray();
        entry.Revision++;
    }

    private static bool TryReadMapPixels(
        MapObject.RoomRepresentation roomRep,
        out Color[] pixels,
        out int width,
        out int height,
        out int sourceKey)
    {
        pixels = null;
        width = 0;
        height = 0;
        sourceKey = 0;

        try
        {
            if (roomRep?.texture != null)
            {
                width = roomRep.texture.width;
                height = roomRep.texture.height;
                pixels = roomRep.texture.GetPixels();
                sourceKey = roomRep.texture.GetInstanceID();
                return pixels != null && pixels.Length == width * height;
            }

            FAtlasElement element = roomRep?.mapTex;
            if (element?.atlas?.texture is not Texture2D atlasTexture) return false;

            Rect uv = element.uvRect;
            int atlasX = Mathf.Clamp(Mathf.RoundToInt(uv.x * atlasTexture.width), 0, Math.Max(0, atlasTexture.width - 1));
            int atlasY = Mathf.Clamp(Mathf.RoundToInt(uv.y * atlasTexture.height), 0, Math.Max(0, atlasTexture.height - 1));
            width = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.width) * atlasTexture.width), 1, atlasTexture.width - atlasX);
            height = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.height) * atlasTexture.height), 1, atlasTexture.height - atlasY);
            pixels = atlasTexture.GetPixels(atlasX, atlasY, width, height);
            sourceKey = atlasTexture.GetInstanceID() ^ (element.name?.GetHashCode() ?? 0);
            return pixels != null && pixels.Length == width * height;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap minimap raster unavailable for " + (roomRep?.room?.name ?? "?") + ": " + error.Message);
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

    private static void RefreshNodes(CacheEntry entry, MapObject.RoomRepresentation roomRep)
    {
        Vector2[] positions = roomRep?.nodePositions;
        if (positions == null || positions.Length == 0)
        {
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

    private static void RefreshCurves(CacheEntry entry, global::World world, AbstractRoom room)
    {
        RoomSettings liveSettings = room?.realizedRoom?.roomSettings;
        if (liveSettings != null)
        {
            int liveFingerprint = GeometrySettingsFingerprint(liveSettings);
            if (entry.CurvesInitialized && liveFingerprint == entry.SettingsFingerprint) return;
            RebuildCurves(entry, liveSettings, liveFingerprint);
            return;
        }

        if (entry.CurvesInitialized)
        {
            if (string.IsNullOrWhiteSpace(entry.SettingsPath)) return;
            if (Time.frameCount < entry.NextSettingsPollFrame) return;
            entry.NextSettingsPollFrame = Time.frameCount + SettingsPollIntervalFrames + Math.Abs(entry.RoomIndex % 30);
            if (FileWriteTime(entry.SettingsPath) == entry.SettingsWriteTimeUtc) return;
        }

        if (curveLoadsRemaining <= 0)
        {
            entry.NextSettingsPollFrame = Time.frameCount + 1;
            return;
        }
        curveLoadsRemaining--;

        RoomSettings settings = null;
        try
        {
            string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
            SlugcatStats.Timeline timeline = world.game != null ? world.game.TimelinePoint : null;
            settings = new RoomSettings(roomName, world.region, false, false, timeline, world.game);
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap could not load room settings for " + entry.RoomName + ": " + error.Message);
        }

        if (settings != null)
            RebuildCurves(entry, settings, GeometrySettingsFingerprint(settings));
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
