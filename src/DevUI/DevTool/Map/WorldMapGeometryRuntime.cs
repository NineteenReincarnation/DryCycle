using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using DryCycle.TerrainExt.QuicksandZone;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

public enum EditorMapGeometryKind
{
    Air,
    BackWall,
    Solid,
    Structure,
    Shortcut,
    Transport,
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
    // Authored RoomSettings terrain only. Retained thumbnails use this as a semantic overlay so
    // curved/custom terrain remains visible even when the vanilla map texture is the base layer.
    public EditorMapRectSnapshot[] TerrainRuns { get; init; } = Array.Empty<EditorMapRectSnapshot>();
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
internal static partial class MapRoomGeometryPresentationHub
{
    private const float PixelsPerTile = 20f;
    private const float CurveSimplifyToleranceTiles = 0.075f;
    private const int MaxCurveSamples = 192;

    // Opening a large region must never synchronously decode every minimap/RoomSettings file.
    // Cheap room bounds appear immediately; detailed raster and authored terrain are filled in
    // incrementally with the current/selected room receiving first priority.
    private const int RasterLoadsPerFrame = 2;
    private const int RasterBuildCommitsPerFrame = 4;
    private const double RasterMainThreadBudgetMilliseconds = 1.50d;
    private const int MaxRasterBuildWorkers = 2;
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
        internal int RasterRequestedSourceKey = int.MinValue;
        internal int RasterRequestedWidth;
        internal int RasterRequestedHeight;
        internal int RasterRequestVersion;
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

    private readonly struct PixelClassification
    {
        internal PixelClassification(EditorMapGeometryKind kind, bool water)
        {
            Kind = kind;
            Water = water;
        }

        internal EditorMapGeometryKind Kind { get; }
        internal bool Water { get; }
    }

    private sealed class RasterBuildRequest
    {
        internal int RoomIndex;
        internal int SourceKey;
        internal int Width;
        internal int Height;
        internal int RequestVersion;
        internal int Generation;
        internal Color[] Pixels;
    }

    private sealed class RasterBuildResult
    {
        internal int RoomIndex;
        internal int SourceKey;
        internal int Width;
        internal int Height;
        internal int RequestVersion;
        internal int Generation;
        internal EditorMapRectSnapshot[] Runs = Array.Empty<EditorMapRectSnapshot>();
        internal Exception Error;
    }

    private static readonly Dictionary<int, CacheEntry> cache = new();
    private static readonly object publishedGate = new();
    private static readonly Dictionary<int, EditorMapRoomVisualSnapshot> published = new();
    private static readonly object rasterBuildGate = new();
    private static readonly Queue<RasterBuildRequest> rasterBuildPending = new();
    private static readonly ConcurrentQueue<RasterBuildResult> rasterBuildCompleted = new();
    private static int rasterBuildActiveWorkers;
    private static int rasterBuildGeneration;
    private static int publishedGeneration;
    private static readonly List<int> roomOrder = new();
    private static readonly List<int> priorityRooms = new();
    internal static void PrioritizeRooms(IReadOnlyList<int> rooms)
    {
        priorityRooms.Clear();
        if (rooms != null) for (int i = 0; i < rooms.Count; i++) priorityRooms.Add(rooms[i]);
    }
    private static string region = string.Empty;
    private static int lastPrimeFrame = -1;
    private static int lastSubNodeCount = -1;
    private static int nextStructureSyncFrame;
    private static int backgroundCursor;
    private static int rasterLoadsRemaining;
    private static int curveLoadsRemaining;
    private static long rasterFrameDeadlineTicks;
    private static long rasterReadbackTotalTicks;
    private static long rasterReadbackPeakTicks;
    private static int rasterReadbackCount;

    internal static int PublishedGeneration =>
        Volatile.Read(ref publishedGeneration);

    internal static int RasterReadbackCount => rasterReadbackCount;
    internal static double RasterReadbackAverageMilliseconds =>
        rasterReadbackCount <= 0
            ? 0d
            : rasterReadbackTotalTicks * 1000d / Stopwatch.Frequency / rasterReadbackCount;
    internal static double RasterReadbackPeakMilliseconds =>
        rasterReadbackPeakTicks * 1000d / Stopwatch.Frequency;

    internal static EditorMapRoomVisualSnapshot Get(int roomIndex)
    {
        lock (publishedGate)
        {
            return published.TryGetValue(roomIndex, out EditorMapRoomVisualSnapshot snapshot)
                ? snapshot
                : EditorMapRoomVisualSnapshot.Empty;
        }
    }

    internal static void Prime(EditorSession session)
    {
        if (!WorldMapFrontendBridge.ShouldPrimeGeometry(session))
            return;

        if (session?.Owner?.activePage is not MapPage page || page.world == null)
        {
            Clear();
            return;
        }

        string nextRegion = page.world.name ?? string.Empty;
        bool regionChanged = !string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase) ||
                             PersistentContextChanged(page.world);
        if (regionChanged)
        {
            PersistentBeforeRegionReset();
            ResetRegion(nextRegion);
            PersistentAfterRegionReset(page.world);
        }

        if (lastPrimeFrame == Time.frameCount) return;
        lastPrimeFrame = Time.frameCount;

        bool structureDue = regionChanged ||
                            cache.Count == 0 ||
                            page.subNodes.Count != lastSubNodeCount ||
                            Time.frameCount >= nextStructureSyncFrame;
        if (structureDue)
            SynchronizeStructure(page);

        rasterFrameDeadlineTicks =
            Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency *
                   RasterMainThreadBudgetMilliseconds / 1000d);
        DrainRasterBuildResults(
            RasterBuildCommitsPerFrame,
            rasterFrameDeadlineTicks);
        rasterLoadsRemaining = RasterLoadsPerFrame;
        curveLoadsRemaining = UnloadedCurveLoadsPerFrame;

        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        int selectedRoom = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;

        // The player's/current room may use one readback even if the budget is already exhausted;
        // every other room obeys the frame deadline so one expensive texture cannot trigger a
        // second synchronous GetPixels spike in the same editor frame.
        RefreshPriorityRoom(currentRoom, page.world, allowOverBudget: true);
        if (selectedRoom != currentRoom)
            RefreshPriorityRoom(selectedRoom, page.world, allowOverBudget: false);

        ProcessBackground(page.world, currentRoom, selectedRoom);
        PersistentTryScheduleSave(force: false);
    }

    internal static void InvalidateRoom(int roomIndex)
    {
        ClearRecoveryFailure(roomIndex);
        if (!cache.TryGetValue(roomIndex, out CacheEntry entry)) return;
        entry.RasterInitialized = false;
        entry.RasterRequestedSourceKey = int.MinValue;
        entry.RasterRequestedWidth = 0;
        entry.RasterRequestedHeight = 0;
        unchecked { entry.RasterRequestVersion++; }
        entry.CurvesInitialized = false;
        entry.NodesInitialized = false;
        entry.NextRasterPollFrame = 0;
        entry.NextNodePollFrame = 0;
        entry.NextSettingsPollFrame = 0;
        entry.NextLiveSettingsPollFrame = 0;
        entry.Revision++;
        PersistentOnRoomInvalidated(entry);
    }

    internal static void Clear()
    {
        PersistentBeforeClear();
        ResetRasterBuildScheduler();
        cache.Clear();
        lock (publishedGate) published.Clear();
        Interlocked.Increment(ref publishedGeneration);
        roomOrder.Clear();
        priorityRooms.Clear();
        region = string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
        rasterLoadsRemaining = 0;
        curveLoadsRemaining = 0;
        rasterFrameDeadlineTicks = 0L;
        rasterReadbackTotalTicks = 0L;
        rasterReadbackPeakTicks = 0L;
        rasterReadbackCount = 0;
    }

    private static void ResetRegion(string nextRegion)
    {
        ResetRasterBuildScheduler();
        cache.Clear();
        lock (publishedGate) published.Clear();
        Interlocked.Increment(ref publishedGeneration);
        roomOrder.Clear();
        priorityRooms.Clear();
        region = nextRegion ?? string.Empty;
        lastPrimeFrame = -1;
        lastSubNodeCount = -1;
        nextStructureSyncFrame = 0;
        backgroundCursor = 0;
        rasterReadbackTotalTicks = 0L;
        rasterReadbackPeakTicks = 0L;
        rasterReadbackCount = 0;
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

            PersistentTryRestore(entry, page.world, room, panel.roomRep);
            RefreshDimensions(entry, panel.roomRep);
            RefreshNodes(entry, panel.roomRep, force: !entry.NodesInitialized);
            Publish(entry, allowRasterReadback: false);
        }

        if (cache.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int key in cache.Keys)
                if (!alive.Contains(key)) stale.Add(key);
            for (int i = 0; i < stale.Count; i++)
            {
                cache.Remove(stale[i]);
                bool removedPublished;
                lock (publishedGate)
                    removedPublished = published.Remove(stale[i]);
                if (removedPublished)
                    Interlocked.Increment(ref publishedGeneration);
                PersistentOnRoomRemoved(stale[i]);
            }
        }

        WorldMapFrontendBridge.CompletePersistentRoomValidation(alive);

        if (backgroundCursor >= roomOrder.Count) backgroundCursor = 0;
        lastSubNodeCount = page.subNodes.Count;
        nextStructureSyncFrame = Time.frameCount + StructureSyncIntervalFrames;
    }

    private static void RefreshPriorityRoom(
        int roomIndex,
        global::World world,
        bool allowOverBudget)
    {
        if (roomIndex < 0 || !cache.TryGetValue(roomIndex, out CacheEntry entry)) return;
        RefreshDimensions(entry, entry.RoomRep);
        RefreshNodes(entry, entry.RoomRep, force: true);
        bool rasterBudgetAvailable =
            rasterLoadsRemaining > 0 &&
            (allowOverBudget ||
             Stopwatch.GetTimestamp() < rasterFrameDeadlineTicks);
        if (rasterBudgetAvailable &&
            RefreshRaster(entry, entry.RoomRep, allowDecode: true, forcePoll: true))
            rasterLoadsRemaining = Math.Max(0, rasterLoadsRemaining - 1);
        if (RefreshCurves(entry, world, entry.Room, allowDiskLoad: true, forceLivePoll: false))
            curveLoadsRemaining = Math.Max(0, curveLoadsRemaining - 1);
        Publish(entry, allowRasterReadback: true);
    }

    private static void ProcessBackground(global::World world, int currentRoom, int selectedRoom)
    {
        // World Map zoom throttles its raster work, but Player Map also consumes this cache's
        // authored-terrain semantics. Those scans must keep progressing while Player Map is open
        // (or finishing an explicit render), even when the hidden World Map remains zoomed out.
        bool processVisuals = WorldMapFrontendBridge.ShouldProcessGeometryBackground(world);
        if (!processVisuals && !PlayerMapActivityGate.ShouldProcess)
            return;

        int count = roomOrder.Count;
        if (count == 0) return;

        // The same visible-room order feeds native texture recovery and semantic geometry. A room
        // already complete is cheap to skip; it must not consume the decode budget of a new room.
        if (processVisuals)
            for (int i = 0;
                 i < priorityRooms.Count &&
                 rasterLoadsRemaining > 0 &&
                 Stopwatch.GetTimestamp() < rasterFrameDeadlineTicks;
                 i++)
            {
                int index = priorityRooms[i];
                if (index == currentRoom || index == selectedRoom || !cache.TryGetValue(index, out CacheEntry priority)) continue;
                RefreshDimensions(priority, priority.RoomRep);
                RefreshNodes(priority, priority.RoomRep, force: false);
                if (RefreshRaster(priority, priority.RoomRep, allowDecode: true, forcePoll: false)) rasterLoadsRemaining--;
                Publish(priority, allowRasterReadback: true);
            }

        int checks = Math.Min(count, BackgroundRoomsPerFrame);
        for (int i = 0; i < checks; i++)
        {
            if (backgroundCursor >= count) backgroundCursor = 0;
            int roomIndex = roomOrder[backgroundCursor++];
            if (roomIndex == currentRoom || roomIndex == selectedRoom) continue;
            if (!cache.TryGetValue(roomIndex, out CacheEntry entry)) continue;

            if (processVisuals)
            {
                RefreshDimensions(entry, entry.RoomRep);
                RefreshNodes(entry, entry.RoomRep, force: false);
                if (rasterLoadsRemaining > 0 &&
                    Stopwatch.GetTimestamp() < rasterFrameDeadlineTicks &&
                    RefreshRaster(entry, entry.RoomRep, allowDecode: true, forcePoll: false))
                    rasterLoadsRemaining--;
            }

            if (curveLoadsRemaining > 0 &&
                RefreshCurves(entry, world, entry.Room, allowDiskLoad: true, forceLivePoll: false))
                curveLoadsRemaining--;

            Publish(entry, allowRasterReadback: processVisuals);
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
        PersistentMarkDirty();
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

        if (PersistentTryBindRestoredRaster(entry, roomRep, source))
            return false;

        if (entry.RasterInitialized &&
            entry.RasterSourceKey == source.SourceKey &&
            entry.RasterWidth == source.Width &&
            entry.RasterHeight == source.Height)
        {
            entry.NextRasterPollFrame = Time.frameCount + RasterPollIntervalFrames + Math.Abs(entry.RoomIndex % 37);
            return false;
        }

        if (entry.RasterRequestedSourceKey == source.SourceKey &&
            entry.RasterRequestedWidth == source.Width &&
            entry.RasterRequestedHeight == source.Height)
            return false;

        if (!allowDecode) return false;
        if (!TryReadMapPixels(source, out Color[] pixels)) return false;

        unchecked { entry.RasterRequestVersion++; }
        entry.RasterRequestedSourceKey = source.SourceKey;
        entry.RasterRequestedWidth = source.Width;
        entry.RasterRequestedHeight = source.Height;

        ScheduleRasterBuild(
            entry.RoomIndex,
            source,
            entry.RasterRequestVersion,
            pixels);
        return true;
    }

    private static void ScheduleRasterBuild(
        int roomIndex,
        RasterSourceInfo source,
        int requestVersion,
        Color[] pixels)
    {
        lock (rasterBuildGate)
        {
            rasterBuildPending.Enqueue(new RasterBuildRequest
            {
                RoomIndex = roomIndex,
                SourceKey = source.SourceKey,
                Width = source.Width,
                Height = source.Height,
                RequestVersion = requestVersion,
                Generation = rasterBuildGeneration,
                Pixels = pixels
            });
            StartRasterWorkersLocked();
        }
    }

    private static void StartRasterWorkersLocked()
    {
        while (rasterBuildActiveWorkers < MaxRasterBuildWorkers &&
               rasterBuildPending.Count > 0)
        {
            RasterBuildRequest request = rasterBuildPending.Dequeue();
            rasterBuildActiveWorkers++;
            Task.Run(() => ExecuteRasterBuild(request));
        }
    }

    private static void ExecuteRasterBuild(RasterBuildRequest request)
    {
        RasterBuildResult result = new()
        {
            RoomIndex = request.RoomIndex,
            SourceKey = request.SourceKey,
            Width = request.Width,
            Height = request.Height,
            RequestVersion = request.RequestVersion,
            Generation = request.Generation
        };

        try
        {
            result.Runs = BuildRasterRuns(
                request.Pixels,
                request.Width,
                request.Height);
        }
        catch (Exception error)
        {
            result.Error = error;
        }
        finally
        {
            request.Pixels = null;
            rasterBuildCompleted.Enqueue(result);
            lock (rasterBuildGate)
            {
                rasterBuildActiveWorkers = Math.Max(0, rasterBuildActiveWorkers - 1);
                StartRasterWorkersLocked();
            }
        }
    }

    private static void DrainRasterBuildResults(
        int maxResults,
        long deadlineTicks)
    {
        int drained = 0;
        while (drained < Math.Max(0, maxResults) &&
               Stopwatch.GetTimestamp() < deadlineTicks &&
               rasterBuildCompleted.TryDequeue(out RasterBuildResult result))
        {
            drained++;

            if (result.Generation != Volatile.Read(ref rasterBuildGeneration) ||
                !cache.TryGetValue(result.RoomIndex, out CacheEntry entry) ||
                entry.RasterRequestVersion != result.RequestVersion ||
                entry.RasterRequestedSourceKey != result.SourceKey ||
                entry.RasterRequestedWidth != result.Width ||
                entry.RasterRequestedHeight != result.Height)
                continue;

            entry.RasterRequestedSourceKey = int.MinValue;
            entry.RasterRequestedWidth = 0;
            entry.RasterRequestedHeight = 0;

            if (result.Error != null)
            {
                global::DryCycle.Plugin.Logger?.LogError(
                    "WorldMap raster worker failed for room " +
                    result.RoomIndex + ": " + result.Error);
                continue;
            }

            if (!TryGetRasterSourceInfo(entry.RoomRep, out RasterSourceInfo currentSource) ||
                currentSource.SourceKey != result.SourceKey ||
                currentSource.Width != result.Width ||
                currentSource.Height != result.Height)
                continue;

            entry.RasterSourceKey = result.SourceKey;
            entry.RasterWidth = result.Width;
            entry.RasterHeight = result.Height;
            entry.RasterInitialized = true;
            entry.NextRasterPollFrame =
                Time.frameCount +
                RasterPollIntervalFrames +
                Math.Abs(entry.RoomIndex % 37);
            entry.WidthTiles = Math.Max(1f, result.Width);
            entry.HeightTiles = Math.Max(1f, result.Height);
            entry.BaseRasterRuns =
                result.Runs ?? Array.Empty<EditorMapRectSnapshot>();
            entry.Revision++;
            PersistentOnRasterRebuilt(entry, entry.RoomRep, currentSource);
            Publish(entry, allowRasterReadback: false);
        }
    }

    private static EditorMapRectSnapshot[] BuildRasterRuns(
        Color[] pixels,
        int width,
        int height)
    {
        if (pixels == null || width <= 0 || height <= 0 ||
            pixels.Length != width * height)
            return Array.Empty<EditorMapRectSnapshot>();

        PixelClassification[] row = new PixelClassification[width];
        List<EditorMapRectSnapshot> baseRuns = new();
        List<EditorMapRectSnapshot> waterRuns = new();

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int column = 0; column < width; column++)
                row[column] = ClassifyPixel(pixels[rowOffset + column]);

            int x = 0;
            while (x < width)
            {
                EditorMapGeometryKind kind = row[x].Kind;
                int start = x++;
                while (x < width && row[x].Kind == kind)
                    x++;
                baseRuns.Add(
                    new EditorMapRectSnapshot(
                        start,
                        y,
                        x - start,
                        1f,
                        kind));
            }

            x = 0;
            while (x < width)
            {
                if (!row[x].Water)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width && row[x].Water)
                    x++;
                waterRuns.Add(
                    new EditorMapRectSnapshot(
                        start,
                        y,
                        x - start,
                        1f,
                        EditorMapGeometryKind.Water));
            }
        }

        baseRuns.AddRange(waterRuns);
        return baseRuns.ToArray();
    }

    private static void ResetRasterBuildScheduler()
    {
        lock (rasterBuildGate)
        {
            unchecked { rasterBuildGeneration++; }
            rasterBuildPending.Clear();
        }

        while (rasterBuildCompleted.TryDequeue(out _))
        {
        }
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
        if (source.Texture == null) return false;

        long started = Stopwatch.GetTimestamp();
        try
        {
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
        finally
        {
            long elapsed = Math.Max(0L, Stopwatch.GetTimestamp() - started);
            rasterReadbackTotalTicks += elapsed;
            rasterReadbackPeakTicks = Math.Max(rasterReadbackPeakTicks, elapsed);
            rasterReadbackCount++;
        }
    }

    private static PixelClassification ClassifyPixel(Color color)
    {
        if (TryClassifyVanillaMapColor(color, out EditorMapGeometryKind kind))
            return new PixelClassification(kind, false);

        // MapObject applies water last with Lerp(base, blue, 0.3). Undo that blend before
        // classifying so poles, background walls and shortcut tiles remain visible underwater.
        if (color.b >= 0.28f)
        {
            Color unblended = new(
                Clamp01(color.r / 0.7f),
                Clamp01(color.g / 0.7f),
                Clamp01((color.b - 0.3f) / 0.7f));
            if (TryClassifyVanillaMapColor(unblended, out kind))
                return new PixelClassification(kind, true);
        }

        // Modded MapTex producers occasionally use nearby greys rather than the exact vanilla
        // palette. Keep them visible instead of dropping them from the preview.
        float greySpread = Math.Max(color.r, Math.Max(color.g, color.b)) - Math.Min(color.r, Math.Min(color.g, color.b));
        if (greySpread < 0.08f)
        {
            if (color.r < 0.40f) return new PixelClassification(EditorMapGeometryKind.Solid, false);
            if (color.r < 0.55f) return new PixelClassification(EditorMapGeometryKind.BackWall, false);
            return new PixelClassification(EditorMapGeometryKind.Air, false);
        }

        return new PixelClassification(EditorMapGeometryKind.Structure, false);
    }

    private static float Clamp01(float value) =>
        value <= 0f ? 0f : value >= 1f ? 1f : value;

    private static bool TryClassifyVanillaMapColor(Color color, out EditorMapGeometryKind kind)
    {
        const float tolerance = 0.055f;

        if (Near(color, 0f, 1f, 0.2f, tolerance) ||
            Near(color, 1f, 0f, 1f, tolerance) ||
            Near(color, 1f, 1f, 1f, tolerance))
        {
            kind = EditorMapGeometryKind.Shortcut;
            return true;
        }

        if (Near(color, 0.7f, 0f, 0f, tolerance) || Near(color, 0f, 0f, 0f, tolerance))
        {
            kind = EditorMapGeometryKind.Transport;
            return true;
        }

        if (Near(color, 0.3f, 0.3f, 0.3f, tolerance))
        {
            kind = EditorMapGeometryKind.Solid;
            return true;
        }

        if (Near(color, 0.5f, 0.5f, 0.5f, tolerance))
        {
            kind = EditorMapGeometryKind.BackWall;
            return true;
        }

        if (Near(color, 0.6f, 0.6f, 0.6f, tolerance))
        {
            kind = EditorMapGeometryKind.Air;
            return true;
        }

        if (Near(color, 0.5f, 0.3f, 0.3f, tolerance))
        {
            kind = EditorMapGeometryKind.Structure;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool Near(Color color, float r, float g, float b, float tolerance) =>
        Math.Abs(color.r - r) <= tolerance &&
        Math.Abs(color.g - g) <= tolerance &&
        Math.Abs(color.b - b) <= tolerance;

    private static void RefreshNodes(
        CacheEntry entry,
        MapObject.RoomRepresentation roomRep,
        bool force)
    {
        if (entry.NodesInitialized && !force && Time.frameCount < entry.NextNodePollFrame) return;

        Vector2[] positions = roomRep?.nodePositions;
        if (PersistentShouldKeepRestoredNodes(entry, positions)) return;

        if (positions == null || positions.Length == 0)
        {
            entry.NextNodePollFrame = Time.frameCount + (entry.NodesInitialized ? NodePollIntervalFrames : 1);
            if (entry.NodesInitialized && entry.Nodes.Length == 0) return;
            entry.Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
            entry.NodesInitialized = true;
            entry.NodeFingerprint = 0;
            entry.Revision++;
            PersistentOnNodesObserved(entry, changed: true);
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
            if (entry.NodesInitialized && fingerprint == entry.NodeFingerprint)
            {
                PersistentOnNodesObserved(entry, changed: false);
                return;
            }

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
            PersistentOnNodesObserved(entry, changed: true);
        }
    }

    private static bool RefreshCurves(
        CacheEntry entry,
        global::World world,
        AbstractRoom room,
        bool allowDiskLoad,
        bool forceLivePoll)
    {
        PersistentPrepareSettingsPath(entry, world, room);

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
        PersistentOnCurvesRebuilt(entry);
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
        out List<EditorMapRectSnapshot> fills) =>
        BuildCurveGeometry(
            settings?.placedObjects,
            roomWidthTiles,
            out curves,
            out fills);

    /// <summary>
    /// Shared authored-terrain geometry compiler used by World Map and Cartography. It deliberately
    /// returns semantic geometry only: callers decide presentation colors. That keeps curved terrain
    /// visually identical to ordinary Solid/Structure terrain instead of inventing a map-only style.
    /// </summary>
    internal static void BuildCurveGeometry(
        IReadOnlyList<PlacedObject> objects,
        float roomWidthTiles,
        out List<EditorMapPolylineSnapshot> curves,
        out List<EditorMapRectSnapshot> fills)
    {
        curves = new List<EditorMapPolylineSnapshot>();
        fills = new List<EditorMapRectSnapshot>();
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
                    EditorMapGeometryKind.Structure,
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
        IReadOnlyList<PlacedObject> objects,
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

    private readonly struct RasterMergeKey : IEquatable<RasterMergeKey>
    {
        internal RasterMergeKey(EditorMapGeometryKind kind, float x, float width)
        {
            Kind = kind;
            X = x;
            Width = width;
        }

        internal EditorMapGeometryKind Kind { get; }
        internal float X { get; }
        internal float Width { get; }

        public bool Equals(RasterMergeKey other) =>
            Kind == other.Kind && X.Equals(other.X) && Width.Equals(other.Width);

        public override bool Equals(object obj) => obj is RasterMergeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = hash * 397 ^ X.GetHashCode();
                return hash * 397 ^ Width.GetHashCode();
            }
        }
    }

    /// <summary>
    /// Losslessly merges vertically adjacent raster runs with the same X/width/kind.
    /// The legacy MapTex decoder emits one horizontal rectangle per row; without this compaction
    /// a single wall can become hundreds of ImGui rectangles every frame.
    /// </summary>
    internal static EditorMapRectSnapshot[] CompactRasterRuns(EditorMapRectSnapshot[] source)
    {
        if (source == null || source.Length < 2)
            return source ?? Array.Empty<EditorMapRectSnapshot>();

        List<EditorMapRectSnapshot> compact = new(source.Length);
        Dictionary<RasterMergeKey, int> active = new();

        for (int i = 0; i < source.Length; i++)
        {
            EditorMapRectSnapshot run = source[i];
            if (run.Width <= 0f || run.Height <= 0f)
                continue;

            RasterMergeKey key = new(run.Kind, run.X, run.Width);
            if (active.TryGetValue(key, out int index))
            {
                EditorMapRectSnapshot previous = compact[index];
                if (Math.Abs(previous.Y + previous.Height - run.Y) <= 0.001f)
                {
                    compact[index] = new EditorMapRectSnapshot(
                        previous.X,
                        previous.Y,
                        previous.Width,
                        previous.Height + run.Height,
                        previous.Kind);
                    continue;
                }
            }

            active[key] = compact.Count;
            compact.Add(run);
        }

        if (compact.Count == source.Length)
            return source;
        return compact.ToArray();
    }

    private static EditorMapRectSnapshot[] MergeRuns(
        EditorMapRectSnapshot[] baseRuns,
        EditorMapRectSnapshot[] terrainRuns)
    {
        int baseCount = baseRuns?.Length ?? 0;
        int terrainCount = terrainRuns?.Length ?? 0;
        if (terrainCount == 0)
            return CompactRasterRuns(baseRuns ?? Array.Empty<EditorMapRectSnapshot>());
        if (baseCount == 0)
            return CompactRasterRuns(terrainRuns ?? Array.Empty<EditorMapRectSnapshot>());

        EditorMapRectSnapshot[] merged = new EditorMapRectSnapshot[baseCount + terrainCount];
        Array.Copy(baseRuns, 0, merged, 0, baseCount);
        Array.Copy(terrainRuns, 0, merged, baseCount, terrainCount);
        return CompactRasterRuns(merged);
    }

    private static void Publish(CacheEntry entry, bool allowRasterReadback)
    {
        if (entry.PublishedRevision != entry.Revision)
        {
            entry.Snapshot = new EditorMapRoomVisualSnapshot
            {
                Available = entry.WidthTiles > 0f && entry.HeightTiles > 0f,
                DetailedRasterAvailable = entry.RasterInitialized || entry.TerrainFillRuns.Length > 0,
                WidthTiles = Math.Max(1f, entry.WidthTiles),
                HeightTiles = Math.Max(1f, entry.HeightTiles),
                RasterRuns = MergeRuns(entry.BaseRasterRuns, entry.TerrainFillRuns),
                TerrainRuns = CompactRasterRuns(entry.TerrainFillRuns ?? Array.Empty<EditorMapRectSnapshot>()),
                Curves = entry.Curves,
                Nodes = entry.Nodes
            };
            entry.PublishedRevision = entry.Revision;
        }

        // Cached raster enhancement is always reusable, but only priority/background passes may
        // authorize a new synchronous readback. Structure synchronization therefore stays cheap
        // even when it republishes every room in a newly opened region.
        EditorMapRoomVisualSnapshot snapshot =
            WorldMapFrontendBridge.EnhanceRaster(
                entry.RoomIndex,
                entry.Snapshot,
                allowRasterReadback) ??
            EditorMapRoomVisualSnapshot.Empty;

        bool changed;
        lock (publishedGate)
        {
            changed =
                !published.TryGetValue(
                    entry.RoomIndex,
                    out EditorMapRoomVisualSnapshot previous) ||
                !ReferenceEquals(previous, snapshot);
            published[entry.RoomIndex] = snapshot;
        }

        if (changed)
            Interlocked.Increment(ref publishedGeneration);
    }
}
