using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.TerrainExt.QuicksandZone;
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
/// Vanilla's MapObject raster remains the cheap base layer, including its TerrainManager coverage
/// sampling, but continuous terrain is also reconstructed from authored splines. This preserves
/// LocalTerrain/CurvedSlope shape and lets DryCycle terrain such as QuicksandZone expose material
/// identity that the coarse vanilla minimap texture cannot represent.
///
/// The frontend receives compact horizontal raster runs and simplified polylines. No room tile map
/// or spline is re-sampled every ImGui frame.
/// </summary>
internal static class MapRoomGeometryPresentationHub
{
    private const float PixelsPerTile = 20f;
    private const float CurveSimplifyToleranceTiles = 0.075f;
    private const int MaxCurveSamples = 128;

    private sealed class CacheEntry
    {
        internal int RoomIndex;
        internal string RoomName = string.Empty;
        internal int RasterSourceKey;
        internal int RasterWidth;
        internal int RasterHeight;
        internal bool RasterInitialized;
        internal int SettingsFingerprint;
        internal string SettingsPath = string.Empty;
        internal DateTime SettingsWriteTimeUtc;
        internal bool CurvesInitialized;
        internal float WidthTiles = 12f;
        internal float HeightTiles = 6f;
        internal EditorMapRectSnapshot[] RasterRuns = Array.Empty<EditorMapRectSnapshot>();
        internal EditorMapPolylineSnapshot[] Curves = Array.Empty<EditorMapPolylineSnapshot>();
        internal EditorMapNodeVisualSnapshot[] Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
        internal EditorMapRoomVisualSnapshot Snapshot = EditorMapRoomVisualSnapshot.Empty;
    }

    private static readonly Dictionary<int, CacheEntry> cache = new();
    private static string region = string.Empty;

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
        }

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
    }

    private static void RefreshDimensions(CacheEntry entry, MapObject.RoomRepresentation roomRep)
    {
        if (roomRep?.texture != null)
        {
            entry.WidthTiles = Math.Max(1f, roomRep.texture.width);
            entry.HeightTiles = Math.Max(1f, roomRep.texture.height);
            return;
        }

        if (roomRep?.mapTex != null)
        {
            entry.WidthTiles = Math.Max(1f, roomRep.mapTex.sourcePixelSize.x);
            entry.HeightTiles = Math.Max(1f, roomRep.mapTex.sourcePixelSize.y);
        }
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
        entry.RasterRuns = runs.ToArray();
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

            // MapObject may reuse a Futile atlas element without retaining RoomRepresentation.texture.
            // Sample that atlas element instead so cached rooms still keep their real minimap shape.
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
            pixels = null;
            width = 0;
            height = 0;
            sourceKey = 0;
            return false;
        }
    }

    private static EditorMapGeometryKind? ClassifyPixel(Color color)
    {
        // These colours come directly from MapObject.CreateMapTexture. Shortcut colours are not
        // duplicated here because interactive node markers are drawn from real node coordinates.
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
        if (roomRep?.nodePositions == null || roomRep.nodePositions.Length == 0)
        {
            entry.Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
            return;
        }

        EditorMapNodeVisualSnapshot[] nodes = new EditorMapNodeVisualSnapshot[roomRep.nodePositions.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            Vector2 point = roomRep.nodePositions[i];
            nodes[i] = new EditorMapNodeVisualSnapshot(i, point.x, point.y);
        }
        entry.Nodes = nodes;
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

        // Unloaded rooms are only reparsed when their settings file timestamp changes. This keeps
        // hundreds of-room regions cheap while still reflecting external editor/mod changes.
        if (entry.CurvesInitialized && !string.IsNullOrWhiteSpace(entry.SettingsPath))
        {
            DateTime currentWriteTime = FileWriteTime(entry.SettingsPath);
            if (currentWriteTime == entry.SettingsWriteTimeUtc) return;
        }

        RoomSettings settings = null;
        try
        {
            string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
            SlugcatStats.Timeline timeline = world.game != null ? world.game.TimelinePoint : null;
            settings = new RoomSettings(
                roomName,
                world.region,
                template: false,
                firstTemplate: false,
                timeline,
                world.game);
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap could not load room settings for " + entry.RoomName + ": " + error.Message);
        }

        if (settings == null) return;
        RebuildCurves(entry, settings, GeometrySettingsFingerprint(settings));
    }

    private static void RebuildCurves(CacheEntry entry, RoomSettings settings, int fingerprint)
    {
        entry.SettingsFingerprint = fingerprint;
        entry.SettingsPath = settings.filePath ?? string.Empty;
        entry.SettingsWriteTimeUtc = FileWriteTime(entry.SettingsPath);
        entry.Curves = BuildCurveGeometry(settings).ToArray();
        entry.CurvesInitialized = true;
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
        if (placed.type == PlacedObject.Type.LocalTerrain || placed.type == PlacedObject.Type.CurvedSlope)
            return true;
        return string.Equals(placed.type?.value, "QuicksandZone", StringComparison.Ordinal);
    }

    private static List<EditorMapPolylineSnapshot> BuildCurveGeometry(RoomSettings settings)
    {
        List<EditorMapPolylineSnapshot> result = new();
        List<PlacedObject> objects = settings?.placedObjects;
        if (objects == null) return result;

        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject placed = objects[i];
            if (!IsThumbnailTerrain(placed)) continue;

            if ((placed.type == PlacedObject.Type.LocalTerrain || placed.type == PlacedObject.Type.CurvedSlope) &&
                placed.data is PlacedObject.LocalTerrainData local)
            {
                AddSplineBand(
                    result,
                    local.spline,
                    placed.pos,
                    local.bottom,
                    placed.type == PlacedObject.Type.CurvedSlope
                        ? EditorMapGeometryKind.CurvedSlope
                        : EditorMapGeometryKind.LocalTerrain,
                    0f,
                    1f);
                continue;
            }

            if (string.Equals(placed.type?.value, "QuicksandZone", StringComparison.Ordinal) &&
                placed.data is QuicksandZoneData quicksand &&
                quicksand.SurfaceSpline != null)
            {
                AddSplineBand(
                    result,
                    quicksand.SurfaceSpline,
                    placed.pos,
                    quicksand.BottomDepth,
                    EditorMapGeometryKind.QuicksandBody,
                    0f,
                    1f);

                List<Vector2> intervals = new();
                quicksand.FillQuicksandIntervals(intervals);
                for (int interval = 0; interval < intervals.Count; interval++)
                {
                    Vector2 range = intervals[interval];
                    AddSplineBand(
                        result,
                        quicksand.SurfaceSpline,
                        placed.pos,
                        quicksand.BottomDepth,
                        EditorMapGeometryKind.QuicksandMaterial,
                        range.x,
                        range.y);
                }
            }
        }

        return result;
    }

    private static void AddSplineBand(
        List<EditorMapPolylineSnapshot> output,
        BezierSpline spline,
        Vector2 origin,
        float depthPixels,
        EditorMapGeometryKind kind,
        float startU,
        float endU)
    {
        if (spline == null || endU <= startU + 0.0001f) return;

        float sampledLength = Math.Max(1f, spline.GetFullLength * (endU - startU));
        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(sampledLength / 10f) + 1, 8, MaxCurveSamples);
        List<EditorMapPointSnapshot> surface = new(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount <= 1 ? 0f : (float)i / (sampleCount - 1);
            float u = Mathf.Lerp(startU, endU, t);
            Vector2 point = origin + EvaluateSplineByLength(spline, u);
            surface.Add(ToTilePoint(point));
        }

        surface = Simplify(surface, CurveSimplifyToleranceTiles);
        if (surface.Count < 2) return;

        float bottomY = (origin.y - Math.Max(1f, depthPixels)) / PixelsPerTile;
        List<EditorMapPointSnapshot> polygon = new(surface.Count + 2);
        polygon.AddRange(surface);
        polygon.Add(new EditorMapPointSnapshot(surface[surface.Count - 1].X, bottomY));
        polygon.Add(new EditorMapPointSnapshot(surface[0].X, bottomY));
        output.Add(new EditorMapPolylineSnapshot
        {
            Kind = kind,
            Closed = true,
            Points = polygon.ToArray()
        });
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

    private static void Publish(CacheEntry entry)
    {
        entry.Snapshot = new EditorMapRoomVisualSnapshot
        {
            Available = entry.WidthTiles > 0f && entry.HeightTiles > 0f,
            DetailedRasterAvailable = entry.RasterInitialized,
            WidthTiles = Math.Max(1f, entry.WidthTiles),
            HeightTiles = Math.Max(1f, entry.HeightTiles),
            RasterRuns = entry.RasterRuns,
            Curves = entry.Curves,
            Nodes = entry.Nodes
        };
    }
}
