using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        internal long GeometryGeneration;
    }

    private const int IdleRoomsPerFrame = 6;
    private const int InteractiveRoomsPerFrame = 2;
    private const int SourceAuditIntervalFrames = 8;

    private readonly Dictionary<int, RoomResource> rooms = new();
    private readonly Queue<int> priorityQueue = new();
    private readonly HashSet<int> queued = new();
    private string region = string.Empty;
    private EditorMapRoomSnapshot[] auditRooms = Array.Empty<EditorMapRoomSnapshot>();
    private int auditCursor;
    private int nextAuditFrame;

    internal IReadOnlyDictionary<int, RoomResource> Rooms => rooms;
    internal int Count => rooms.Count;

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

    internal void ApplyDirty(WorldMapScene scene, WorldMapDirtySet dirty)
    {
        if (dirty == null) return;

        foreach (int roomIndex in dirty.RemovedRooms)
        {
            rooms.Remove(roomIndex);
            queued.Remove(roomIndex);
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
        WorldMapScene scene)
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

        int budget = WorldMapBackgroundBudget.InteractionActive
            ? InteractiveRoomsPerFrame
            : IdleRoomsPerFrame;

        while (budget > 0 && priorityQueue.Count > 0)
        {
            int roomIndex = priorityQueue.Dequeue();
            queued.Remove(roomIndex);
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
        priorityQueue.Clear();
        queued.Clear();
        region = string.Empty;
        auditRooms = Array.Empty<EditorMapRoomSnapshot>();
        auditCursor = 0;
        nextAuditFrame = 0;
    }

    private void ProcessRoom(MapPage page, WorldMapScene scene, int roomIndex)
    {
        if (!scene.TryGetRoom(roomIndex, out WorldMapScene.RoomNode sceneRoom))
        {
            rooms.Remove(roomIndex);
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
        }

        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(roomIndex);
        int visualStamp = ComputeVisualStamp(visual);
        if (visual?.Available == true && visualStamp != resource.VisualStamp)
        {
            resource.Geometry = RoomGeometryBuilder.Build(roomIndex, visual, visualStamp);
            resource.VisualStamp = visualStamp;
            unchecked { resource.GeometryGeneration++; }
        }

        if (WorldMapLegacyRoomSourceService.TryGetRoomTexture(
                page,
                roomIndex,
                out WorldMapLegacyRoomSourceService.RoomTextureSource source))
        {
            if (resource.Thumbnail.Stage(source))
                resource.Thumbnail.CommitPending();
        }
        else
        {
            // Missing source is not a command to clear the thumbnail. Keep last-known-good.
            resource.Thumbnail.RejectPending();
        }
    }

    private void Enqueue(int roomIndex)
    {
        if (roomIndex < 0 || !queued.Add(roomIndex)) return;
        priorityQueue.Enqueue(roomIndex);
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
