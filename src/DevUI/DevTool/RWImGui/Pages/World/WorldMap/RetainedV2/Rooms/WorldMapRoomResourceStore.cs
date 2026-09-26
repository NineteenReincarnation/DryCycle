using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Main-thread capture/commit owner for retained room resources.
///
/// It never mutates authoring data. Vanilla RoomPanel/MapTex access stays behind
/// WorldMapLegacyRoomSourceService, and missing/rebuilding sources preserve last-known-good
/// thumbnails instead of clearing them.
/// </summary>
internal sealed class WorldMapRoomResourceStore
{
    internal sealed class RoomResource
    {
        internal int RoomIndex;
        internal RoomGeometryBlob Geometry = RoomGeometryBlob.Empty;
        internal readonly RoomThumbnailResource Thumbnail = new();
        internal int VisualStamp;
        internal int RequestedVisualStamp = int.MinValue;
        internal long GeometryGeneration;
        internal int NextThumbnailPollFrame;
    }

    private const int IdleRoomsPerFrame = 6;
    private const int DormantRoomsPerFrame = 12;
    private const int HotStartRoomsPerFrame = 24;
    private const double VisibleWorkBudgetMilliseconds = 1.35d;
    private const double DormantWorkBudgetMilliseconds = 2.00d;
    private const int SourceAuditIntervalFrames = 8;
    private const int ThumbnailPollIntervalFrames = 120;

    private readonly Dictionary<int, RoomResource> rooms = new();
    private readonly Queue<int> visiblePriorityQueue = new();
    private readonly HashSet<int> visiblePriorityQueued = new();
    private readonly Queue<int> priorityQueue = new();
    private readonly HashSet<int> queued = new();
    private readonly HashSet<int> geometryChanged = new();
    private readonly WorldMapBuildScheduler buildScheduler = new();
    private string region = string.Empty;
    private EditorMapRoomSnapshot[] auditRooms = Array.Empty<EditorMapRoomSnapshot>();
    private int auditCursor;
    private int nextAuditFrame;
    private long revision;

    private long thumbnailSessionStartedTicks;
    private long thumbnailSessionCompletedTicks;
    private int thumbnailSessionExpected;
    private int thumbnailSessionCommitted;
    private int thumbnailSessionPersistentHits;
    private int thumbnailSessionLiveCommits;
    private bool thumbnailSessionComplete;
    private long mainThreadPerfTotalTicks;
    private long mainThreadPerfPeakTicks;
    private int mainThreadPerfSamples;

    internal IReadOnlyDictionary<int, RoomResource> Rooms => rooms;
    internal int Count => rooms.Count;
    internal long Revision => revision;
    internal int GeometryBuildCount => buildScheduler.CompletedBuildCount;
    internal double GeometryBuildAverageMilliseconds => buildScheduler.AverageBuildMilliseconds;
    internal double GeometryBuildPeakMilliseconds => buildScheduler.PeakBuildMilliseconds;
    internal int ThumbnailLoadExpected => thumbnailSessionExpected;
    internal int ThumbnailLoadCommitted => thumbnailSessionCommitted;
    internal int ThumbnailPersistentHits => thumbnailSessionPersistentHits;
    internal int ThumbnailLiveCommits => thumbnailSessionLiveCommits;
    internal bool ThumbnailLoadComplete => thumbnailSessionComplete;
    internal double ThumbnailLoadElapsedMilliseconds
    {
        get
        {
            if (thumbnailSessionStartedTicks <= 0L) return 0d;
            long end =
                thumbnailSessionCompletedTicks > 0L
                    ? thumbnailSessionCompletedTicks
                    : Stopwatch.GetTimestamp();
            return Math.Max(0L, end - thumbnailSessionStartedTicks) *
                   1000d / Stopwatch.Frequency;
        }
    }
    internal double MainThreadAverageMilliseconds =>
        mainThreadPerfSamples <= 0
            ? 0d
            : mainThreadPerfTotalTicks * 1000d /
              Stopwatch.Frequency / mainThreadPerfSamples;
    internal double MainThreadPeakMilliseconds =>
        mainThreadPerfPeakTicks * 1000d / Stopwatch.Frequency;

    internal int CommittedThumbnailCount
    {
        get
        {
            int count = 0;
            foreach (RoomResource room in rooms.Values)
                if (room.Thumbnail.HasCommitted) count++;
            return count;
        }
    }

    internal bool TryGet(int roomIndex, out RoomResource resource) =>
        rooms.TryGetValue(roomIndex, out resource);

    internal void Initialize(ManualLogSource logger) =>
        buildScheduler.Initialize(logger);

    internal void DrainGeometryChanges(List<int> output)
    {
        if (output == null) return;
        output.Clear();
        if (geometryChanged.Count == 0) return;
        output.AddRange(geometryChanged);
        geometryChanged.Clear();
    }

    internal void Prioritize(IReadOnlyList<int> roomIndices)
    {
        if (roomIndices == null) return;

        for (int i = 0; i < roomIndices.Count; i++)
        {
            int roomIndex = roomIndices[i];
            if (roomIndex < 0 || !NeedsPriorityRefresh(roomIndex))
                continue;

            queued.Add(roomIndex);
            if (visiblePriorityQueued.Add(roomIndex))
                visiblePriorityQueue.Enqueue(roomIndex);
        }
    }

    internal void ApplyDirty(WorldMapScene scene, WorldMapDirtySet dirty)
    {
        if (dirty == null) return;

        foreach (int roomIndex in dirty.RemovedRooms)
        {
            if (rooms.Remove(roomIndex))
                AdvanceRevision();
            queued.Remove(roomIndex);
            visiblePriorityQueued.Remove(roomIndex);
        }

        if (dirty.FullRebuild)
        {
            foreach (int roomIndex in scene.Rooms.Keys)
                Enqueue(roomIndex);
        }

        foreach (int roomIndex in dirty.RoomPorts)
            Enqueue(roomIndex);
        foreach (int roomIndex in dirty.RoomMetadata)
            if (!rooms.ContainsKey(roomIndex))
                Enqueue(roomIndex);
        foreach (int roomIndex in dirty.RoomTransforms)
            if (!rooms.ContainsKey(roomIndex))
                Enqueue(roomIndex);
    }

    internal void UpdateMainThread(
        EditorSession session,
        EditorMapPresentationSnapshot snapshot,
        WorldMapScene scene,
        IReadOnlyList<int> sourcePriorityRooms)
    {
        if (session?.ToolMode != EditorToolMode.Map ||
            session.Owner?.activePage is not MapPage page ||
            page.world == null ||
            snapshot?.Available != true)
            return;

        string nextRegion = snapshot.RegionName ?? page.world.name ?? string.Empty;
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            region = nextRegion;
            BeginThumbnailLoadSession(scene.Rooms.Count);
            foreach (int roomIndex in scene.Rooms.Keys)
                Enqueue(roomIndex);
        }

        UpdateThumbnailLoadExpected(scene.Rooms.Count);

        EditorMapRoomSnapshot[] snapshotRooms =
            snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (!ReferenceEquals(auditRooms, snapshotRooms))
        {
            auditRooms = snapshotRooms;
            // Presentation snapshots are replaced every few frames. Restarting here starves all
            // rooms beyond the first audit batch even when their textures are already available.
            if (auditCursor >= snapshotRooms.Length) auditCursor = 0;
        }

        // Region reset happens above, so first-open visible promotion cannot be discarded by the
        // store's own lifecycle reset.
        Prioritize(sourcePriorityRooms);

        // Navigation/room drag owns the frame budget. Keep committed thumbnails/geometry stable
        // and resume source capture/build commits after the interaction cooldown.
        bool canvasVisible =
            WorldMapRetainedV2Runtime.CanvasVisible;
        int budget =
            WorldMapPersistentRetainedCache.ValidatedRoomCount > 0
                ? HotStartRoomsPerFrame
                : canvasVisible
                    ? IdleRoomsPerFrame
                    : DormantRoomsPerFrame;
        if (WorldMapBackgroundBudget.InteractionActive)
            budget = 1;

        long workStarted = Stopwatch.GetTimestamp();
        double workBudgetMilliseconds =
            canvasVisible
                ? VisibleWorkBudgetMilliseconds
                : DormantWorkBudgetMilliseconds;
        long workDeadline =
            workStarted +
            (long)(Stopwatch.Frequency *
                   workBudgetMilliseconds / 1000d);

        budget -= DrainBuildResults(
            budget,
            workDeadline);

        while (budget > 0 &&
               Stopwatch.GetTimestamp() < workDeadline &&
               visiblePriorityQueue.Count > 0)
        {
            int roomIndex = visiblePriorityQueue.Dequeue();
            visiblePriorityQueued.Remove(roomIndex);
            if (!queued.Remove(roomIndex))
                continue;

            ProcessRoom(page, scene, roomIndex);
            budget--;
        }

        while (budget > 0 &&
               Stopwatch.GetTimestamp() < workDeadline &&
               priorityQueue.Count > 0)
        {
            int roomIndex = priorityQueue.Dequeue();
            if (!queued.Remove(roomIndex))
                continue;

            visiblePriorityQueued.Remove(roomIndex);
            ProcessRoom(page, scene, roomIndex);
            budget--;
        }

        if (budget <= 0 || snapshotRooms.Length == 0 ||
            Stopwatch.GetTimestamp() >= workDeadline ||
            UnityEngine.Time.frameCount < nextAuditFrame)
        {
            RecordMainThreadWork(workStarted);
            UpdateThumbnailLoadSession();
            return;
        }

        nextAuditFrame = UnityEngine.Time.frameCount + SourceAuditIntervalFrames;
        while (budget > 0 &&
               Stopwatch.GetTimestamp() < workDeadline &&
               snapshotRooms.Length > 0)
        {
            if (auditCursor >= snapshotRooms.Length) auditCursor = 0;
            EditorMapRoomSnapshot room = snapshotRooms[auditCursor++];
            if (room != null)
            {
                ProcessRoom(page, scene, room.RoomIndex);
                budget--;
            }
        }

        RecordMainThreadWork(workStarted);
        UpdateThumbnailLoadSession();
    }

    internal void Reset()
    {
        rooms.Clear();
        visiblePriorityQueue.Clear();
        visiblePriorityQueued.Clear();
        priorityQueue.Clear();
        queued.Clear();
        geometryChanged.Clear();
        buildScheduler.Reset();
        region = string.Empty;
        auditRooms = Array.Empty<EditorMapRoomSnapshot>();
        auditCursor = 0;
        nextAuditFrame = 0;
        thumbnailSessionStartedTicks = 0L;
        thumbnailSessionCompletedTicks = 0L;
        thumbnailSessionExpected = 0;
        thumbnailSessionCommitted = 0;
        thumbnailSessionPersistentHits = 0;
        thumbnailSessionLiveCommits = 0;
        thumbnailSessionComplete = false;
        mainThreadPerfTotalTicks = 0L;
        mainThreadPerfPeakTicks = 0L;
        mainThreadPerfSamples = 0;
        AdvanceRevision();
    }

    private void ProcessRoom(MapPage page, WorldMapScene scene, int roomIndex)
    {
        if (!scene.TryGetRoom(roomIndex, out WorldMapScene.RoomNode sceneRoom))
        {
            if (rooms.Remove(roomIndex))
                AdvanceRevision();
            return;
        }

        if (!rooms.TryGetValue(roomIndex, out RoomResource resource))
        {
            resource = new RoomResource
            {
                RoomIndex = roomIndex,
                Geometry = RoomGeometryBuilder.BuildNeutral(roomIndex)
            };
            rooms.Add(roomIndex, resource);
            geometryChanged.Add(roomIndex);
            AdvanceRevision();
        }

        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(roomIndex);
        int visualStamp = ComputeVisualStamp(visual);
        if (visual?.Available == true &&
            visualStamp != resource.VisualStamp &&
            visualStamp != resource.RequestedVisualStamp)
        {
            resource.RequestedVisualStamp = visualStamp;
            if (!buildScheduler.ScheduleRoom(roomIndex, visual, visualStamp))
                resource.RequestedVisualStamp = int.MinValue;
        }

        WorldMapLegacyRoomSourceService.RoomTextureSource source = default;
        bool hasCommittedThumbnail = resource.Thumbnail.HasCommitted;
        bool resolvedPersistent =
            !hasCommittedThumbnail &&
            WorldMapPersistentRetainedCache.TryResolveThumbnail(
                roomIndex,
                out source);

        // A committed thumbnail is immutable for almost every frame. Previously every low-frequency
        // room audit still resolved Futile atlas state for every room, even when the descriptor had
        // not changed. Poll committed sources at a staggered interval while unresolved rooms keep
        // probing immediately so freshly generated MapTex appears without extra latency.
        bool shouldProbeLive =
            !hasCommittedThumbnail ||
            UnityEngine.Time.frameCount >= resource.NextThumbnailPollFrame;
        bool resolvedLive = resolvedPersistent;
        if (!resolvedPersistent && shouldProbeLive)
        {
            resolvedLive =
                WorldMapLegacyRoomSourceService.TryGetRoomTexture(
                    page,
                    roomIndex,
                    out source);
        }

        if (resolvedPersistent || shouldProbeLive)
        {
            resource.NextThumbnailPollFrame =
                UnityEngine.Time.frameCount +
                ThumbnailPollIntervalFrames +
                Math.Abs(roomIndex % 31);
        }

        if (resolvedLive)
        {
            if (resource.Thumbnail.Stage(source) &&
                resource.Thumbnail.CommitPending())
            {
                AdvanceRevision();
                if (!hasCommittedThumbnail)
                {
                    thumbnailSessionCommitted++;
                    if (resolvedPersistent)
                        thumbnailSessionPersistentHits++;
                    else
                        thumbnailSessionLiveCommits++;
                }

                if (!resolvedPersistent &&
                    !string.IsNullOrEmpty(source.PersistentElementName))
                    MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
            }
        }
        else if (shouldProbeLive)
        {
            // Missing source is not a command to clear the thumbnail. Keep last-known-good.
            resource.Thumbnail.RejectPending();
        }
    }

    private void BeginThumbnailLoadSession(int expectedRooms)
    {
        thumbnailSessionStartedTicks = Stopwatch.GetTimestamp();
        thumbnailSessionCompletedTicks = 0L;
        thumbnailSessionExpected = Math.Max(0, expectedRooms);
        thumbnailSessionCommitted = 0;
        thumbnailSessionPersistentHits = 0;
        thumbnailSessionLiveCommits = 0;
        // Initial retained deltas can arrive one frame before the full room set. Do not declare a
        // zero-room snapshot complete; UpdateThumbnailLoadExpected can grow the target as the scene
        // converges without losing the original cold/warm-start timestamp.
        thumbnailSessionComplete = false;
    }

    private void UpdateThumbnailLoadExpected(int expectedRooms)
    {
        int next = Math.Max(0, expectedRooms);
        if (next == thumbnailSessionExpected)
            return;

        thumbnailSessionExpected = next;
        if (thumbnailSessionCommitted < thumbnailSessionExpected)
        {
            thumbnailSessionComplete = false;
            thumbnailSessionCompletedTicks = 0L;
        }
    }

    private void UpdateThumbnailLoadSession()
    {
        if (thumbnailSessionStartedTicks <= 0L ||
            thumbnailSessionComplete)
            return;

        if (thumbnailSessionExpected <= 0 ||
            thumbnailSessionCommitted < thumbnailSessionExpected)
            return;

        thumbnailSessionComplete = true;
        thumbnailSessionCompletedTicks = Stopwatch.GetTimestamp();
        global::DryCycle.Plugin.Logger?.LogInfo(
            "WorldMap retained thumbnails ready: " +
            thumbnailSessionCommitted + "/" +
            thumbnailSessionExpected + " in " +
            ThumbnailLoadElapsedMilliseconds.ToString("F0") +
            " ms (persistent " +
            thumbnailSessionPersistentHits + ", live " +
            thumbnailSessionLiveCommits + ").");
    }

    private void RecordMainThreadWork(long started)
    {
        long elapsed =
            Math.Max(0L, Stopwatch.GetTimestamp() - started);
        mainThreadPerfTotalTicks += elapsed;
        mainThreadPerfPeakTicks =
            Math.Max(mainThreadPerfPeakTicks, elapsed);
        mainThreadPerfSamples++;
    }

    private int DrainBuildResults(
        int budget,
        long workDeadline)
    {
        int maxResults = Math.Max(1, budget);
        return buildScheduler.Drain(
            maxResults,
            workDeadline,
            result =>
        {
            if (!rooms.TryGetValue(result.RoomIndex, out RoomResource resource))
                return;

            if (resource.RequestedVisualStamp != result.SourceStamp)
                return;

            if (result.Error != null || result.Geometry == null)
            {
                resource.RequestedVisualStamp = int.MinValue;
                return;
            }

            bool routingChanged = RoutingGeometryChanged(resource.Geometry, result.Geometry);
            resource.Geometry = result.Geometry;
            resource.VisualStamp = result.SourceStamp;
            resource.RequestedVisualStamp = int.MinValue;
            // Raster/curve updates rebuild the thumbnail, not the unchanged pipe paths.
            if (routingChanged) geometryChanged.Add(result.RoomIndex);
            unchecked { resource.GeometryGeneration++; }
            AdvanceRevision();
        });
    }

    internal static bool RoutingGeometryChanged(RoomGeometryBlob previous, RoomGeometryBlob next)
    {
        if (previous == null || next == null || previous.WidthTiles != next.WidthTiles || previous.HeightTiles != next.HeightTiles ||
            previous.Nodes.Length != next.Nodes.Length) return true;
        for (int i = 0; i < previous.Nodes.Length; i++)
        {
            var a = previous.Nodes[i]; var b = next.Nodes[i];
            if (a.NodeIndex != b.NodeIndex || a.X != b.X || a.Y != b.Y) return true;
        }
        return false;
    }

    private bool NeedsPriorityRefresh(int roomIndex)
    {
        if (!rooms.TryGetValue(roomIndex, out RoomResource resource))
            return true;

        if (!resource.Thumbnail.HasCommitted)
            return true;

        return resource.GeometryGeneration <= 0 &&
               resource.RequestedVisualStamp == int.MinValue;
    }

    private void Enqueue(int roomIndex)
    {
        if (roomIndex < 0)
            return;

        // Explicit scene/source invalidation is stronger than the steady-state thumbnail poll
        // throttle. Let the next queued pass resolve the live descriptor immediately so an edited
        // room never waits up to the maintenance interval before its thumbnail refreshes.
        if (rooms.TryGetValue(roomIndex, out RoomResource resource))
            resource.NextThumbnailPollFrame = 0;

        if (!queued.Add(roomIndex))
            return;

        priorityQueue.Enqueue(roomIndex);
    }

    private void AdvanceRevision()
    {
        unchecked { revision++; }
    }

    private static int ComputeVisualStamp(EditorMapRoomVisualSnapshot visual)
    {
        if (visual == null) return 0;

        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ visual.Available.GetHashCode();
            hash = hash * 397 ^ visual.DetailedRasterAvailable.GetHashCode();
            hash = hash * 397 ^ visual.WidthTiles.GetHashCode();
            hash = hash * 397 ^ visual.HeightTiles.GetHashCode();

            EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
            EditorMapPolylineSnapshot[] curves = visual.Curves ?? Array.Empty<EditorMapPolylineSnapshot>();
            EditorMapNodeVisualSnapshot[] nodes = visual.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();

            hash = hash * 397 ^ runs.Length;
            hash = hash * 397 ^ curves.Length;
            hash = hash * 397 ^ nodes.Length;
            hash = hash * 397 ^ RuntimeHelpers.GetHashCode(runs);
            hash = hash * 397 ^ RuntimeHelpers.GetHashCode(curves);
            hash = hash * 397 ^ RuntimeHelpers.GetHashCode(nodes);
            return hash;
        }
    }
}
