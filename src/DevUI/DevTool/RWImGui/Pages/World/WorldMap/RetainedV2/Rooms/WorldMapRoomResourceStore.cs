using System;
using System.Collections.Generic;
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
    }

    private const int IdleRoomsPerFrame = 6;
    private const int HotStartRoomsPerFrame = 24;
    private const int SourceAuditIntervalFrames = 8;

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

    internal IReadOnlyDictionary<int, RoomResource> Rooms => rooms;
    internal int Count => rooms.Count;
    internal long Revision => revision;

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
            foreach (int roomIndex in scene.Rooms.Keys)
                Enqueue(roomIndex);
        }

        EditorMapRoomSnapshot[] snapshotRooms =
            snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (!ReferenceEquals(auditRooms, snapshotRooms))
        {
            auditRooms = snapshotRooms;
            auditCursor = 0;
        }

        // Region reset happens above, so first-open visible promotion cannot be discarded by the
        // store's own lifecycle reset.
        Prioritize(sourcePriorityRooms);

        // Navigation/room drag owns the frame budget. Keep committed thumbnails/geometry stable
        // and resume source capture/build commits after the interaction cooldown.
        if (WorldMapBackgroundBudget.InteractionActive)
            return;

        int budget =
            WorldMapPersistentRetainedCache.ValidatedRoomCount > 0
                ? HotStartRoomsPerFrame
                : IdleRoomsPerFrame;
        DrainBuildResults(budget);

        while (budget > 0 && visiblePriorityQueue.Count > 0)
        {
            int roomIndex = visiblePriorityQueue.Dequeue();
            visiblePriorityQueued.Remove(roomIndex);
            if (!queued.Remove(roomIndex))
                continue;

            ProcessRoom(page, scene, roomIndex);
            budget--;
        }

        while (budget > 0 && priorityQueue.Count > 0)
        {
            int roomIndex = priorityQueue.Dequeue();
            if (!queued.Remove(roomIndex))
                continue;

            visiblePriorityQueued.Remove(roomIndex);
            ProcessRoom(page, scene, roomIndex);
            budget--;
        }

        if (budget <= 0 || snapshotRooms.Length == 0 ||
            UnityEngine.Time.frameCount < nextAuditFrame)
            return;

        nextAuditFrame = UnityEngine.Time.frameCount + SourceAuditIntervalFrames;
        while (budget > 0 && snapshotRooms.Length > 0)
        {
            if (auditCursor >= snapshotRooms.Length) auditCursor = 0;
            EditorMapRoomSnapshot room = snapshotRooms[auditCursor++];
            if (room != null)
            {
                ProcessRoom(page, scene, room.RoomIndex);
                budget--;
            }
        }
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

        bool resolvedPersistent =
            !resource.Thumbnail.HasCommitted &&
            WorldMapPersistentRetainedCache.TryResolveThumbnail(
                roomIndex,
                out WorldMapLegacyRoomSourceService.RoomTextureSource source);

        bool resolvedLive =
            resolvedPersistent ||
            WorldMapLegacyRoomSourceService.TryGetRoomTexture(
                page,
                roomIndex,
                out source);

        if (resolvedLive)
        {
            if (resource.Thumbnail.Stage(source) &&
                resource.Thumbnail.CommitPending())
            {
                AdvanceRevision();
                if (!resolvedPersistent &&
                    !string.IsNullOrEmpty(source.PersistentElementName))
                    MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
            }
        }
        else
        {
            // Missing source is not a command to clear the thumbnail. Keep last-known-good.
            resource.Thumbnail.RejectPending();
        }
    }

    private void DrainBuildResults(int budget)
    {
        int maxResults = Math.Max(1, budget);
        buildScheduler.Drain(maxResults, result =>
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

            resource.Geometry = result.Geometry;
            resource.VisualStamp = result.SourceStamp;
            resource.RequestedVisualStamp = int.MinValue;
            geometryChanged.Add(result.RoomIndex);
            unchecked { resource.GeometryGeneration++; }
            AdvanceRevision();
        });
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
        if (roomIndex < 0 || !queued.Add(roomIndex)) return;
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
