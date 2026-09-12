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
/// Builds the geometry used by the unified World Map room thumbnails.
///
/// Vanilla MapObject rasterises room terrain into a tile-sized texture. We keep that raster as a
/// cheap base layer, but continuous terrain is reconstructed from the authored spline data instead
/// of being reduced to coverage thresholds. DryCycle terrain such as QuicksandZone is sampled from
/// the same data that drives its runtime surface, so the thumbnail can show geometry vanilla's
/// coarse minimap cannot express cleanly.
///
/// Geometry is cached per room. Raster runs are rebuilt only when MapObject produces a new texture;
/// curve data is rebuilt only when relevant placed-object settings change or the settings file is
/// modified. The RWImGui frontend only consumes compact rectangles/polylines and never resamples a
/// room every frame.
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
        internal int TextureId;
        internal int RasterWidth;
        internal int RasterHeight;
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

    internal static EditorMapRoomVisualSnapshot Get(int roomIndex)
    {
        return cache.TryGetValue(roomIndex, out CacheEntry entry)
            ? entry.Snapshot
            : EditorMapRoomVisualSnapshot.Empty;
    }

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

        if (cache.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int key in cache.Keys)
                if (!alive.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++) cache.Remove(stale[i]);
        }
    }

    internal static void InvalidateRoom(int roomIndex)
    {
        cache.Remove(roomIndex);
    }

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
        Texture2D texture = roomRep?.texture;
        if (texture == null) return;

        int textureId = texture.GetInstanceID();
        if (entry.TextureId == textureId &&
            entry.RasterWidth == texture.width &&
            entry.RasterHeight == texture.height &&
            entry.RasterRuns.Length > 0)
            return;

        try
        {
            Color[] pixels = texture.GetPixels();
            List<EditorMapRectSnapshot> runs = new();
            int width = texture.width;
            int height = texture.height;

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

            entry.TextureId = textureId;
            entry.RasterWidth = width;
            entry.RasterHeight = height;
            entry.WidthTiles = Math.Max(1f, width);
            entry.HeightTiles = Math.Max(1f, height);
            entry.RasterRuns = runs.ToArray();
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldMap thumbnail raster unavailable for " + entry.RoomName + ": " + error.Message);
        }
    }

    private static EditorMapGeometryKind? ClassifyPixel(Color color)
    {
        // MapObject colours are intentionally coarse. Treat them as semantic buckets and keep
        // curved/custom geometry in the vector overlay rather than trying to recover it here.
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
            Vector2 p = roomRep.nodePositions[i];
            nodes[i] = new EditorMapNodeVisualSnapshot(i, p.x, p.y);
        }
        entry.Nodes = nodes;
    }

    private static void RefreshCurves(CacheEntry entry, global::World world, AbstractRoom room)
    {
        RoomSettings settings = room?.realizedRoom?.roomSettings;
        bool liveSettings = settings != null;

        if (settings == null && !entry.CurvesInitialized)
        {
            try
            {
                string roomName = WorldLoader.RoomNameManipulator(room.FileName, world.game);
                settings = new RoomSettings(
                    roomName,
                    world.region,
                    template: false,
                    firstTemplate: false,
                    world.game?.TimelinePoint,
                    world.game);
            }
            catch (Exception error)
            {
                global::DryCycle.Plugin.Logger?.LogDebug(
                    "WorldMap could not load room settings for " + entry.RoomName + ": " + error.Message);
            }
        }

        if (settings == null) return;

        int fingerprint = GeometrySettingsFingerprint(settings);
        DateTime writeTime = SettingsWriteTime(settings);
        if (entry.CurvesInitialized &&
            fingerprint == entry.SettingsFingerprint &&
            (!liveSettings || writeTime == entry.SettingsWriteTimeUtc))
            return;

        entry.SettingsFingerprint = fingerprint;
        entry.SettingsPath = settings.filePath ?? string.Empty;
        entry.SettingsWriteTimeUtc = writeTime;
        entry.Curves = BuildCurveGeometry(settings).ToArray();
        entry.CurvesInitialized = true;
    }

    private static DateTime SettingsWriteTime(RoomSettings settings)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(settings?.filePath) && File.Exists(settings.filePath)
                ? File.GetLastWriteTimeUtc(settings.filePath)
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
                EditorMapGeometryKind kind = placed.type == PlacedObject.Type.CurvedSlope
                    ? EditorMapGeometryKind.CurvedSlope
                    : EditorMapGeometryKind.LocalTerrain;
                AddSplineBand(result, local.spline, placed.pos, local.bottom, kind, 0f, 1f);
                continue;
            }

            if (string.Equals(placed.type?.value, "QuicksandZone", StringComparison.Ordinal) &&
                placed.data is QuicksandZoneData quicksand && quicksand.SurfaceSpline != null)
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

        float segmentLength = Math.Max(1f, spline.GetFullLength * (endU - startU));
        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(segmentLength / 10f) + 1, 8, MaxCurveSamples);
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

    private static List<EditorMapPointSnapshot> Simplify(
        List<EditorMapPointSnapshot> points,
        float tolerance)
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
            DetailedRasterAvailable = entry.RasterRuns.Length > 0,
            WidthTiles = Math.Max(1f, entry.WidthTiles),
            HeightTiles = Math.Max(1f, entry.HeightTiles),
            RasterRuns = entry.RasterRuns,
            Curves = entry.Curves,
            Nodes = entry.Nodes
        };
    }
}
