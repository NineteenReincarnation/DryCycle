using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Frontend lifetime owner for the retained V2 World Map.
/// Render-thread scene projection, main-thread retained resources and off-screen presentation meet
/// here without making authoring data or the legacy MapPage presentation authoritative.
/// </summary>
internal static class WorldMapRetainedV2Runtime
{
    private static readonly WorldMapScene RenderSceneState = new();
    private static readonly WorldMapScene MainSceneState = new();
    private static readonly WorldMapSceneSynchronizer Synchronizer = new();
    private static readonly WorldMapSceneTransfer SceneTransfer = new();
    private static readonly WorldMapViewTransformMailbox ViewMailbox = new();
    private static readonly WorldMapRoomResourceStore RoomResources = new();
    private static readonly WorldMapConnectionResourceStore ConnectionResources = new();
    private static readonly WorldMapRenderTextureSurface Surface = new();
    private static readonly WorldMapRetainedRoomRenderer RoomRenderer = new();
    private static readonly WorldMapRetainedConnectionRenderer ConnectionRenderer = new();
    private static readonly WorldMapSpatialIndex SpatialIndex = new();
    private static readonly WorldMapRouteSpatialIndex RouteSpatialIndex = new();
    private static readonly WorldMapDirtySet mainThreadDirty = new();
    private static readonly List<int> geometryChangedRooms = new();
    private static readonly List<string> routeChanged = new();
    private static readonly List<int> sourcePriorityRooms = new();
    private static readonly List<int> visibleRooms = new();
    private static readonly List<string> visibleRoutes = new();
    private static HashSet<string> presentedRouteIds =
        new(StringComparer.Ordinal);
    private static Dictionary<string, WorldMapCrossingMark[]> presentedCrossingsByRoute =
        new(StringComparer.Ordinal);
    private static long presentedCrossingRevision = long.MinValue;
    private static bool presentedCrossingsCurrent;
    private static WorldMapDirtySet lastDirty = new();
    private static ManualLogSource log;
    private static volatile bool enabled;
    private static int activeLayerMask = 7;
    private static int activeShowConnections = 1;
    private static float activeZoom = 1f;
    private static long canvasSeenAt;
    internal static bool CanvasVisible => enabled && !EditorUiModeState.OverlayHidden &&
        Stopwatch.GetTimestamp() - Interlocked.Read(ref canvasSeenAt) < Stopwatch.Frequency / 4;
    private static int retainedConnectionsReady;
    private static long lastRenderedViewRevision = long.MinValue;
    private static long lastRenderedSceneRevision = long.MinValue;
    private static long lastRenderedRoomResourceRevision = long.MinValue;
    private static long lastRenderedRouteRevision = long.MinValue;
    private static int lastRenderedLayerMask = int.MinValue;
    private static int lastRenderedShowConnections = int.MinValue;

    internal static WorldMapScene Scene => RenderSceneState;
    internal static WorldMapScene MainSceneForPersistence => MainSceneState;
    internal static WorldMapDirtySet LastDirty => lastDirty;
    internal static WorldMapRoomResourceStore Resources => RoomResources;
    internal static WorldMapConnectionResourceStore Routes => ConnectionResources;
    internal static float LatestZoom
    {
        get
        {
            float value = Volatile.Read(ref activeZoom);
            return enabled && value > 0.0001f ? value : 1f;
        }
    }

    internal static void Enable(ManualLogSource logger)
    {
        enabled = true;
        log = logger;
        RoomResources.Initialize(logger);
        WorldMapPersistentRetainedCache.Enable(logger);
    }

    internal static void Disable()
    {
        enabled = false;
        WorldMapPersistentRetainedCache.Disable();
        ResetRetainedState();
        log = null;
    }

    internal static void Synchronize(
        EditorMapPresentationSnapshot snapshot,
        IReadOnlyDictionary<int, Num.Vector2> localPositions,
        long layoutRevision,
        int interactiveRoom,
        int layerMask,
        bool showConnections,
        WorldMapViewTransform viewTransform)
    {
        if (!enabled) return;
        Interlocked.Exchange(ref canvasSeenAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref activeLayerMask, layerMask);
        Volatile.Write(ref activeShowConnections, showConnections ? 1 : 0);
        Volatile.Write(ref activeZoom, viewTransform.Zoom > 0.0001f ? viewTransform.Zoom : 1f);

        lastDirty = Synchronizer.Synchronize(
            RenderSceneState,
            snapshot,
            localPositions,
            layoutRevision,
            interactiveRoom,
            viewTransform);

        ViewMailbox.Publish(viewTransform, RenderSceneState.ViewRevision);
        SceneTransfer.Publish(
            WorldMapSceneDelta.Capture(RenderSceneState, lastDirty));
    }

    internal static void UpdateMainThread()
    {
        if (!enabled) return;

        EditorSession session = DevToolRuntime.ActiveSession;
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;

        if (ViewMailbox.TryRead(
                out WorldMapViewTransform latestView,
                out long _))
            MainSceneState.SetViewTransform(latestView);

        mainThreadDirty.Clear();
        SceneTransfer.Drain(MainSceneState, mainThreadDirty);

        if (!mainThreadDirty.IsEmpty)
        {
            RoomResources.ApplyDirty(MainSceneState, mainThreadDirty);
            ConnectionResources.ApplyDirty(
                MainSceneState,
                RoomResources,
                mainThreadDirty);
            SpatialIndex.ApplyDirty(MainSceneState, RoomResources, mainThreadDirty);
            RoomRenderer.ApplyDirty(mainThreadDirty);
            ConnectionRenderer.ApplyDirty(mainThreadDirty);

            foreach (string id in mainThreadDirty.RemovedConnections)
                RouteSpatialIndex.Remove(id);

            if (mainThreadDirty.TopologyChanged || mainThreadDirty.FullRebuild)
                Volatile.Write(ref retainedConnectionsReady, 0);
        }

        if (CanvasVisible && session?.ToolMode == EditorToolMode.Map &&
            snapshot?.Available == true &&
            MainSceneState.ViewTransform.CanvasSize.X >= 2f &&
            MainSceneState.ViewTransform.CanvasSize.Y >= 2f)
        {
            // Promote guard-band rooms ahead of the region-wide initial queue. This changes only
            // processing order; the retained store remains the single owner of room resources.
            Surface.Initialize(log);
            Surface.GetRenderWorldBounds(
                MainSceneState.ViewTransform,
                out Num.Vector2 priorityMin,
                out Num.Vector2 priorityMax);
            SpatialIndex.Query(
                priorityMin,
                priorityMax,
                Volatile.Read(ref activeLayerMask),
                sourcePriorityRooms);
        }
        else
        {
            sourcePriorityRooms.Clear();
        }

        MapRoomGeometryPresentationHub.PrioritizeRooms(sourcePriorityRooms);

        RoomResources.UpdateMainThread(
            session,
            snapshot,
            MainSceneState,
            sourcePriorityRooms);
        RoomResources.DrainGeometryChanges(geometryChangedRooms);
        if (geometryChangedRooms.Count > 0)
        {
            ConnectionResources.InvalidateRooms(
                MainSceneState,
                RoomResources,
                geometryChangedRooms,
                refreshObstacle: true);
            SpatialIndex.InvalidateRooms(
                MainSceneState,
                RoomResources,
                geometryChangedRooms);
        }
        ConnectionResources.Update(MainSceneState, RoomResources);
        PublishCrossingSnapshotIfNeeded();
        ConnectionResources.DrainRouteChanges(routeChanged);
        if (routeChanged.Count > 0)
        {
            for (int i = 0; i < routeChanged.Count; i++)
            {
                string id = routeChanged[i];
                if (ConnectionResources.TryGet(id, out ConnectionRouteResource route))
                    RouteSpatialIndex.Upsert(route);
                else
                    RouteSpatialIndex.Remove(id);
            }
        }

        Volatile.Write(
            ref retainedConnectionsReady,
            ConnectionResources.HasCompleteRoutes(MainSceneState) ? 1 : 0);

        if (CanvasVisible && session?.ToolMode == EditorToolMode.Map &&
            snapshot?.Available == true &&
            MainSceneState.ViewTransform.CanvasSize.X >= 2f &&
            MainSceneState.ViewTransform.CanvasSize.Y >= 2f)
        {
            WorldMapViewTransform view = MainSceneState.ViewTransform;

            int layerMask = Volatile.Read(ref activeLayerMask);
            bool showConnections = Volatile.Read(ref activeShowConnections) != 0;
            int showConnectionsValue = showConnections ? 1 : 0;

            // Pump surface feedback/cleanup every Map main-thread frame. Stable frames do not
            // Camera.Render; they only process short handle swaps and deferred Unity releases.
            Surface.Initialize(log);

            long viewRevision = MainSceneState.ViewRevision;
            long sceneRevision = MainSceneState.SceneRevision;
            long roomResourceRevision = RoomResources.Revision;
            long routeRevision = ConnectionResources.Revision;
            bool viewChanged =
                viewRevision != lastRenderedViewRevision;
            bool nonViewDirty =
                Surface.NeedsRender ||
                sceneRevision != lastRenderedSceneRevision ||
                roomResourceRevision != lastRenderedRoomResourceRevision ||
                routeRevision != lastRenderedRouteRevision ||
                layerMask != lastRenderedLayerMask ||
                showConnectionsValue != lastRenderedShowConnections;
            bool deferViewOnlyRender =
                viewChanged &&
                !nonViewDirty &&
                WorldMapBackgroundBudget.ViewportInteractionActive &&
                Surface.CoversView(view);
            bool needsRender =
                nonViewDirty ||
                (viewChanged && !deferViewOnlyRender);

            if (needsRender)
            {
                Surface.GetRenderWorldBounds(
                    view,
                    out Num.Vector2 visibleMin,
                    out Num.Vector2 visibleMax);

                SpatialIndex.Query(
                    visibleMin,
                    visibleMax,
                    layerMask,
                    visibleRooms);

                if (showConnections)
                {
                    RouteSpatialIndex.Query(
                        visibleMin,
                        visibleMax,
                        visibleRoutes);
                    FilterRoutesByLayer(
                        MainSceneState,
                        layerMask,
                        visibleRoutes);
                }
                else
                {
                    visibleRoutes.Clear();
                }

                if (Surface.Render(
                        view,
                        renderScene =>
                        {
                            RoomRenderer.SynchronizeVisible(
                                MainSceneState,
                                RoomResources,
                                visibleRooms,
                                renderScene);
                            ConnectionRenderer.SynchronizeVisible(
                                ConnectionResources,
                                visibleRoutes,
                                showConnections,
                                renderScene);
                        }))
                {
                    lastRenderedViewRevision = viewRevision;
                    lastRenderedSceneRevision = sceneRevision;
                    lastRenderedRoomResourceRevision = roomResourceRevision;
                    lastRenderedRouteRevision = routeRevision;
                    lastRenderedLayerMask = layerMask;
                    lastRenderedShowConnections = showConnectionsValue;
                    PublishPresentedRoutes(
                        showConnections
                            ? visibleRoutes
                            : null);
                }
            }
        }
    }

    internal static bool TryPresentSurface(
        ImDrawListPtr draw,
        Num.Vector2 min,
        Num.Vector2 max) =>
        enabled &&
        Surface.TryPresent(
            draw,
            min,
            max,
            RenderSceneState.ViewTransform);

    internal static bool TryHitRoom(
        Num.Vector2 worldPoint,
        int layerMask,
        out int roomIndex)
    {
        roomIndex = -1;
        return enabled && SpatialIndex.TryHitRoom(worldPoint, layerMask, out roomIndex);
    }

    internal static bool RetainedConnectionsReady =>
        enabled && Volatile.Read(ref retainedConnectionsReady) != 0;

    internal static bool IsConnectionRetainedOnSurface(string connectionId)
    {
        if (!enabled || string.IsNullOrEmpty(connectionId))
            return false;

        HashSet<string> ids = Volatile.Read(ref presentedRouteIds);
        return ids != null && ids.Contains(connectionId);
    }

    internal static int PresentedRouteCount =>
        Volatile.Read(ref presentedRouteIds)?.Count ?? 0;

    internal static bool TryGetConnectionCrossings(
        string connectionId,
        out WorldMapCrossingMark[] marks)
    {
        marks = null;
        if (!enabled ||
            string.IsNullOrEmpty(connectionId) ||
            !Volatile.Read(ref presentedCrossingsCurrent))
            return false;

        Dictionary<string, WorldMapCrossingMark[]> snapshot =
            Volatile.Read(ref presentedCrossingsByRoute);

        return snapshot != null &&
               snapshot.TryGetValue(
                   connectionId,
                   out marks) &&
               marks != null &&
               marks.Length > 0;
    }

    internal static bool TryGetConnectionRoutePoints(
        string connectionId,
        out Num.Vector2[] points)
    {
        points = null;
        return enabled &&
               RouteSpatialIndex.TryGetPoints(
                   connectionId,
                   out points);
    }

    internal static bool TryHitConnection(
        Num.Vector2 worldPoint,
        float worldRadius,
        out string connectionId,
        out float distanceSquared)
    {
        connectionId = string.Empty;
        distanceSquared = worldRadius * worldRadius;
        if (!enabled)
            return false;

        HashSet<string> allowed = Volatile.Read(ref presentedRouteIds);
        return allowed != null &&
               allowed.Count > 0 &&
               RouteSpatialIndex.TryHit(
                   worldPoint,
                   worldRadius,
                   allowed,
                   out connectionId,
                   out distanceSquared);
    }

    private static void PublishCrossingSnapshotIfNeeded()
    {
        if (!ConnectionResources.CrossingsCurrent)
        {
            if (Volatile.Read(ref presentedCrossingsCurrent))
            {
                Volatile.Write(
                    ref presentedCrossingsByRoute,
                    new Dictionary<string, WorldMapCrossingMark[]>(
                        StringComparer.Ordinal));
                Volatile.Write(
                    ref presentedCrossingsCurrent,
                    false);
                presentedCrossingRevision =
                    long.MinValue;
            }

            return;
        }

        long revision =
            ConnectionResources.CrossingRevision;
        if (Volatile.Read(ref presentedCrossingsCurrent) &&
            revision == presentedCrossingRevision)
            return;

        Dictionary<string, List<WorldMapCrossingMark>> staging =
            new(StringComparer.Ordinal);

        for (int i = 0;
             i < ConnectionResources.Crossings.Count;
             i++)
        {
            WorldMapCrossingMark mark =
                ConnectionResources.Crossings[i];

            AddCrossingToSnapshot(
                staging,
                mark.OverRouteId,
                mark);

            if (!string.Equals(
                    mark.UnderRouteId,
                    mark.OverRouteId,
                    StringComparison.Ordinal))
            {
                AddCrossingToSnapshot(
                    staging,
                    mark.UnderRouteId,
                    mark);
            }
        }

        Dictionary<string, WorldMapCrossingMark[]> next =
            new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, List<WorldMapCrossingMark>> pair
                 in staging)
        {
            next[pair.Key] =
                pair.Value.ToArray();
        }

        presentedCrossingRevision = revision;
        Volatile.Write(
            ref presentedCrossingsByRoute,
            next);
        Volatile.Write(
            ref presentedCrossingsCurrent,
            true);
    }

    private static void AddCrossingToSnapshot(
        Dictionary<string, List<WorldMapCrossingMark>> staging,
        string routeId,
        WorldMapCrossingMark mark)
    {
        if (string.IsNullOrEmpty(routeId))
            return;

        if (!staging.TryGetValue(
                routeId,
                out List<WorldMapCrossingMark> marks))
        {
            marks =
                new List<WorldMapCrossingMark>();
            staging.Add(
                routeId,
                marks);
        }

        marks.Add(mark);
    }

    private static void FilterRoutesByLayer(
        WorldMapScene scene,
        int layerMask,
        List<string> routeIds)
    {
        if (scene == null ||
            routeIds == null ||
            routeIds.Count == 0)
            return;

        for (int i = routeIds.Count - 1;
             i >= 0;
             i--)
        {
            string id = routeIds[i];
            if (!scene.TryGetConnection(
                    id,
                    out WorldMapScene.ConnectionNode connection) ||
                !scene.TryGetRoom(
                    connection.FromRoomIndex,
                    out WorldMapScene.RoomNode fromRoom) ||
                !scene.TryGetRoom(
                    connection.ToRoomIndex,
                    out WorldMapScene.RoomNode toRoom) ||
                !LayerVisible(
                    fromRoom.Layer,
                    layerMask) ||
                !LayerVisible(
                    toRoom.Layer,
                    layerMask))
            {
                routeIds.RemoveAt(i);
            }
        }
    }

    private static bool LayerVisible(
        int layer,
        int layerMask)
    {
        // WorldMapView exposes exactly L1/L2/L3. Keep its fallback semantics for any extension or
        // malformed layer value: values outside the three UI layers remain visible.
        if (layer < 0 ||
            layer >= 3)
            return true;

        return (layerMask &
                (1 << layer)) != 0;
    }

    private static void PublishPresentedRoutes(
        IReadOnlyList<string> routeIds)
    {
        HashSet<string> next = new(StringComparer.Ordinal);
        if (routeIds != null)
        {
            for (int i = 0; i < routeIds.Count; i++)
            {
                string id = routeIds[i];
                if (!string.IsNullOrEmpty(id))
                    next.Add(id);
            }
        }

        Volatile.Write(ref presentedRouteIds, next);
    }

    internal static bool QueryRooms(
        Num.Vector2 worldMin,
        Num.Vector2 worldMax,
        int layerMask,
        List<int> output) =>
        enabled && SpatialIndex.Query(worldMin, worldMax, layerMask, output);

    internal static bool QueryVisibleRooms(int layerMask, List<int> output)
    {
        if (!enabled || output == null) return false;
        RenderSceneState.ViewTransform.GetVisibleWorldBounds(
            out Num.Vector2 min,
            out Num.Vector2 max);
        return SpatialIndex.Query(min, max, layerMask, output);
    }

    internal static void ResetRetainedState()
    {
        Synchronizer.Reset();
        RenderSceneState.Reset();
        MainSceneState.Reset();
        SceneTransfer.Clear();
        RoomResources.Reset();
        WorldMapLegacyRoomSourceService.Reset();
        ConnectionResources.Reset();
        SpatialIndex.Reset();
        RouteSpatialIndex.Reset();
        RoomRenderer.Reset();
        ConnectionRenderer.Reset();
        Surface.Reset();
        mainThreadDirty.Clear();
        geometryChangedRooms.Clear();
        routeChanged.Clear();
        sourcePriorityRooms.Clear();
        MapRoomGeometryPresentationHub.PrioritizeRooms(null);
        Interlocked.Exchange(ref canvasSeenAt, 0L);
        visibleRooms.Clear();
        visibleRoutes.Clear();
        Volatile.Write(
            ref presentedRouteIds,
            new HashSet<string>(StringComparer.Ordinal));
        Volatile.Write(
            ref presentedCrossingsByRoute,
            new Dictionary<string, WorldMapCrossingMark[]>(
                StringComparer.Ordinal));
        Volatile.Write(
            ref presentedCrossingsCurrent,
            false);
        presentedCrossingRevision =
            long.MinValue;
        Volatile.Write(ref retainedConnectionsReady, 0);
        Volatile.Write(ref activeZoom, 1f);
        lastRenderedViewRevision = long.MinValue;
        lastRenderedSceneRevision = long.MinValue;
        lastRenderedRoomResourceRevision = long.MinValue;
        lastRenderedRouteRevision = long.MinValue;
        lastRenderedLayerMask = int.MinValue;
        lastRenderedShowConnections = int.MinValue;
        lastDirty = new WorldMapDirtySet();
    }

    internal static void DrawToolbarDiagnostics()
    {
        ImGui.SameLine(0f, 12f);
        ImGui.TextDisabled(
            "| WorldMap retained " +
            RenderSceneState.Rooms.Count + "/" +
            RenderSceneState.Connections.Count);

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("World Map Retained V2 | hardened + cache V3");
        ImGui.TextUnformatted("rooms: " + RenderSceneState.Rooms.Count);
        ImGui.TextUnformatted("connections: " + RenderSceneState.Connections.Count);
        ImGui.TextUnformatted(
            "room resources: " + RoomResources.Count +
            " | thumbnails " + RoomResources.CommittedThumbnailCount +
            " | source-priority " + sourcePriorityRooms.Count);
        ImGui.TextUnformatted(
            "thumbnail source recovery: avg " +
            MapRoomGeometryPresentationHub.SourceRecoveryAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            MapRoomGeometryPresentationHub.SourceRecoveryPeakMilliseconds.ToString("F2") +
            " ms | completed " +
            MapRoomGeometryPresentationHub.SourceRecoveryCompletedRooms +
            " | retrying " +
            MapRoomGeometryPresentationHub.SourceRecoveryBackoffCount);
        ImGui.TextUnformatted(
            "thumbnail load session: " +
            (MapRoomGeometryPresentationHub.SourceRecoverySessionReadyAtEntry
                ? "ready-at-entry"
                : "recovery") +
            " | " +
            (MapRoomGeometryPresentationHub.SourceRecoverySessionComplete
                ? "complete "
                : "active ") +
            MapRoomGeometryPresentationHub.SourceRecoverySessionElapsedMilliseconds.ToString("F0") +
            " ms | remaining " +
            MapRoomGeometryPresentationHub.SourceRecoverySessionRemainingRooms +
            "/" +
            MapRoomGeometryPresentationHub.SourceRecoverySessionInitialMissingRooms);
        ImGui.TextUnformatted(
            "retained geometry builds: avg " +
            RoomResources.GeometryBuildAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            RoomResources.GeometryBuildPeakMilliseconds.ToString("F2") +
            " ms | completed " +
            RoomResources.GeometryBuildCount);
        ImGui.TextUnformatted("world-space routes: " + ConnectionResources.Count);
        ImGui.TextUnformatted(
            "surface: " + (Surface.Ready ? "ready" : "waiting") +
            (string.IsNullOrEmpty(Surface.Error) ? string.Empty : " | " + Surface.Error));
        ImGui.TextUnformatted("retained room objects: " + RoomRenderer.RetainedRoomCount);
        ImGui.TextUnformatted(
            "retained connection objects: " + ConnectionRenderer.RetainedRouteCount +
            " | surface " + PresentedRouteCount +
            " | route-set complete " + RetainedConnectionsReady);
        ImGui.TextUnformatted(
            "persistent cache v3: validated rooms " +
            WorldMapPersistentRetainedCache.ValidatedRoomCount +
            " | staged routes " +
            WorldMapPersistentRetainedCache.StagedRouteCount +
            " | source audit " +
            (WorldMapPersistentRetainedCache.RoomValidationComplete
                ? "complete"
                : "pending") +
            (WorldMapPersistentRetainedCache.TopologyRejected
                ? " | topology rejected"
                : string.Empty));
        ImGui.TextUnformatted(
            "spatial rooms: " + SpatialIndex.Count +
            " | visible " + visibleRooms.Count);
        ImGui.TextUnformatted(
            "spatial routes: " + RouteSpatialIndex.Count +
            " | visible " + visibleRoutes.Count);
        ImGui.TextUnformatted(
            "crossings: " + ConnectionResources.Crossings.Count +
            " | checks " + ConnectionResources.CrossingCandidateChecks +
            (ConnectionResources.CrossingBudgetLimited
                ? " | density budget active"
                : string.Empty));
        ImGui.TextUnformatted("scene revision: " + RenderSceneState.SceneRevision);
        ImGui.TextUnformatted("view revision: " + RenderSceneState.ViewRevision);
        ImGui.TextUnformatted("scene delta backlog: " + SceneTransfer.PendingCount);
        ImGui.TextUnformatted("last scene dirty count: " + lastDirty.ChangeCount);
        ImGui.TextDisabled("pan/zoom changes only the view revision");
        ImGui.EndTooltip();
    }
}
