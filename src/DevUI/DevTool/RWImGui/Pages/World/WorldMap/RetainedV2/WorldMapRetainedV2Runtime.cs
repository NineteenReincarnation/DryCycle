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

    private sealed class PresentedRouteSnapshot
    {
        internal readonly HashSet<string> Ids;
        internal readonly WorldMapRouteSpatialIndex SpatialIndex;

        internal PresentedRouteSnapshot(
            HashSet<string> ids,
            WorldMapRouteSpatialIndex spatialIndex)
        {
            Ids = ids ?? new HashSet<string>(StringComparer.Ordinal);
            SpatialIndex = spatialIndex ?? new WorldMapRouteSpatialIndex();
        }

        internal static PresentedRouteSnapshot Empty() =>
            new(
                new HashSet<string>(StringComparer.Ordinal),
                new WorldMapRouteSpatialIndex());
    }

    private static PresentedRouteSnapshot presentedRoutes =
        PresentedRouteSnapshot.Empty();
    private static long presentedRouteRevision = long.MinValue;
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

    private static string readinessRegion = string.Empty;
    private static long readinessStartedTicks;
    private static long firstSurfaceReadyTicks;
    private static long readinessCompletedTicks;
    private static int readinessExpectedRooms;
    private static int readinessExpectedConnections;
    private static bool readinessComplete;

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

    internal static bool ViewReadinessComplete => readinessComplete;
    internal static double FirstSurfaceReadyMilliseconds =>
        ElapsedMilliseconds(readinessStartedTicks, firstSurfaceReadyTicks);
    internal static double ViewReadinessMilliseconds =>
        ElapsedMilliseconds(readinessStartedTicks, readinessCompletedTicks);

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

        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref canvasSeenAt, now);
        TrackViewReadinessStart(
            snapshot,
            now);

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

        if (WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                CanvasVisible,
                session?.ToolMode == EditorToolMode.Map,
                snapshot?.Available == true,
                MainSceneState.ViewTransform.CanvasSize.X,
                MainSceneState.ViewTransform.CanvasSize.Y))
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

        if (WorldMapBackgroundSchedulingPolicy.CanRenderRetainedSurface(
                CanvasVisible,
                session?.ToolMode == EditorToolMode.Map,
                snapshot?.Available == true,
                MainSceneState.ViewTransform.CanvasSize.X,
                MainSceneState.ViewTransform.CanvasSize.Y))
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
                            : null,
                        routeRevision);
                }
            }
        }

        UpdateViewReadiness();
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

        PresentedRouteSnapshot snapshot =
            Volatile.Read(ref presentedRoutes);
        return snapshot?.Ids != null &&
               snapshot.Ids.Contains(connectionId);
    }

    internal static int PresentedRouteCount =>
        Volatile.Read(ref presentedRoutes)?.Ids?.Count ?? 0;

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
        if (!enabled || string.IsNullOrEmpty(connectionId))
            return false;

        // When the route is already on the retained surface, marker placement must use the exact
        // immutable point set that produced those pixels, not a newer live route waiting for the
        // next surface redraw. Non-presented routes may still use the live spatial index.
        PresentedRouteSnapshot presented =
            Volatile.Read(ref presentedRoutes);
        if (presented?.Ids?.Contains(connectionId) == true &&
            presented.SpatialIndex.TryGetPoints(
                connectionId,
                out points))
        {
            return true;
        }

        return RouteSpatialIndex.TryGetPoints(
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

        // Hit testing follows the same immutable route snapshot as the pixels currently displayed
        // by the retained surface. This removes the one-frame mismatch where a newly rebuilt live
        // route could be clickable before the offscreen surface had redrawn it.
        PresentedRouteSnapshot presented =
            Volatile.Read(ref presentedRoutes);
        return presented?.Ids != null &&
               presented.Ids.Count > 0 &&
               presented.SpatialIndex.TryHit(
                   worldPoint,
                   worldRadius,
                   allowedIds: null,
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
        IReadOnlyList<string> routeIds,
        long routeRevision)
    {
        PresentedRouteSnapshot current =
            Volatile.Read(ref presentedRoutes);
        if (routeRevision == presentedRouteRevision &&
            PresentedRouteSetMatches(
                current?.Ids,
                routeIds))
        {
            return;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        WorldMapRouteSpatialIndex index = new();

        if (routeIds != null)
        {
            for (int i = 0; i < routeIds.Count; i++)
            {
                string id = routeIds[i];
                if (string.IsNullOrEmpty(id) ||
                    !ConnectionResources.TryGet(
                        id,
                        out ConnectionRouteResource route) ||
                    route?.Points == null ||
                    route.Points.Length < 2)
                {
                    continue;
                }

                // Surface publication is a frame boundary. Route point arrays are immutable
                // after publication (updates replace the array rather than mutating it), so keeping
                // this exact reference preserves the rendered frame without cloning every visible
                // path on each pan/zoom redraw.
                index.Upsert(
                    id,
                    route.Points);
                ids.Add(id);
            }
        }

        presentedRouteRevision = routeRevision;
        Volatile.Write(
            ref presentedRoutes,
            new PresentedRouteSnapshot(ids, index));
    }

    private static bool PresentedRouteSetMatches(
        HashSet<string> current,
        IReadOnlyList<string> routeIds)
    {
        int expected = 0;
        if (routeIds != null)
        {
            for (int i = 0; i < routeIds.Count; i++)
                if (!string.IsNullOrEmpty(routeIds[i]))
                    expected++;
        }

        if (current == null ||
            current.Count != expected)
            return false;

        if (routeIds == null)
            return current.Count == 0;

        for (int i = 0; i < routeIds.Count; i++)
        {
            string id = routeIds[i];
            if (!string.IsNullOrEmpty(id) &&
                !current.Contains(id))
                return false;
        }

        return true;
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
        presentedRouteRevision = long.MinValue;
        Volatile.Write(
            ref presentedRoutes,
            PresentedRouteSnapshot.Empty());
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
        readinessRegion = string.Empty;
        readinessStartedTicks = 0L;
        firstSurfaceReadyTicks = 0L;
        readinessCompletedTicks = 0L;
        readinessExpectedRooms = 0;
        readinessExpectedConnections = 0;
        readinessComplete = false;
        lastDirty = new WorldMapDirtySet();
    }

    private static void TrackViewReadinessStart(
        EditorMapPresentationSnapshot snapshot,
        long now)
    {
        string nextRegion =
            snapshot?.RegionName ?? string.Empty;
        int roomCount =
            snapshot?.Rooms?.Length ?? 0;
        int connectionCount =
            snapshot?.Connections?.Length ?? 0;

        bool newRegion =
            readinessStartedTicks <= 0L ||
            !string.Equals(
                readinessRegion,
                nextRegion,
                StringComparison.OrdinalIgnoreCase);

        if (newRegion)
        {
            readinessRegion = nextRegion;
            readinessStartedTicks = now;
            firstSurfaceReadyTicks = 0L;
            readinessCompletedTicks = 0L;
            readinessExpectedRooms = roomCount;
            readinessExpectedConnections = connectionCount;
            readinessComplete = false;
            return;
        }

        if (roomCount > readinessExpectedRooms ||
            connectionCount > readinessExpectedConnections)
        {
            readinessExpectedRooms =
                Math.Max(readinessExpectedRooms, roomCount);
            readinessExpectedConnections =
                Math.Max(readinessExpectedConnections, connectionCount);

            if (readinessComplete)
            {
                readinessComplete = false;
                readinessCompletedTicks = 0L;
            }
        }
    }

    private static void UpdateViewReadiness()
    {
        if (readinessStartedTicks <= 0L)
            return;

        long now = Stopwatch.GetTimestamp();
        if (firstSurfaceReadyTicks <= 0L &&
            Surface.Ready)
        {
            firstSurfaceReadyTicks = now;
        }

        if (readinessComplete)
            return;

        bool roomsReady =
            readinessExpectedRooms <= 0 ||
            (RoomResources.ThumbnailLoadComplete &&
             RoomResources.ThumbnailLoadCommitted >= readinessExpectedRooms);
        bool routesReady =
            readinessExpectedConnections <= 0 ||
            (ConnectionResources.RouteSessionComplete &&
             ConnectionResources.Count >= readinessExpectedConnections);

        if (!roomsReady ||
            !routesReady ||
            !Surface.Ready)
            return;

        readinessComplete = true;
        readinessCompletedTicks = now;

        log?.LogInfo(
            "WorldMap view ready: first surface " +
            FirstSurfaceReadyMilliseconds.ToString("F0") +
            " ms, full retained readiness " +
            ViewReadinessMilliseconds.ToString("F0") +
            " ms, rooms " +
            RoomResources.ThumbnailLoadCommitted + "/" +
            readinessExpectedRooms +
            ", routes " +
            ConnectionResources.Count + "/" +
            readinessExpectedConnections + ".");
    }

    private static double ElapsedMilliseconds(
        long start,
        long end)
    {
        if (start <= 0L ||
            end <= 0L ||
            end < start)
            return 0d;

        return (end - start) *
               1000d /
               Stopwatch.Frequency;
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
            "view readiness: first surface " +
            FirstSurfaceReadyMilliseconds.ToString("F0") +
            " ms | " +
            (ViewReadinessComplete
                ? "complete "
                : "active ") +
            ViewReadinessMilliseconds.ToString("F0") +
            " ms | expected " +
            readinessExpectedRooms + " rooms / " +
            readinessExpectedConnections + " routes");
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
        ImGui.TextUnformatted(
            "retained thumbnail readiness: " +
            (RoomResources.ThumbnailLoadComplete ? "complete " : "active ") +
            RoomResources.ThumbnailLoadElapsedMilliseconds.ToString("F0") +
            " ms | " +
            RoomResources.ThumbnailLoadCommitted + "/" +
            RoomResources.ThumbnailLoadExpected +
            " | persistent " +
            RoomResources.ThumbnailPersistentHits +
            " | live " +
            RoomResources.ThumbnailLiveCommits);
        ImGui.TextUnformatted(
            "thumbnail main-thread pump: avg " +
            RoomResources.MainThreadAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            RoomResources.MainThreadPeakMilliseconds.ToString("F2") +
            " ms" +
            (CanvasVisible
                ? " | visible budget 1.35 ms"
                : " | dormant budget 2.00 ms"));

        bool backgroundIncomplete =
            !MapRoomGeometryPresentationHub.SourceRecoverySessionComplete ||
            !RoomResources.ThumbnailLoadComplete;
        double backgroundCatchupAge =
            Math.Max(
                MapRoomGeometryPresentationHub.SourceRecoverySessionElapsedMilliseconds,
                RoomResources.ThumbnailLoadElapsedMilliseconds);
        int backgroundCadence =
            WorldMapBackgroundSchedulingPolicy.GeometrySweepIntervalFrames(
                CanvasVisible,
                backgroundIncomplete,
                backgroundCatchupAge);
        int sourceRecoveryCadence =
            WorldMapBackgroundSchedulingPolicy.SourceRecoveryIntervalFrames(
                CanvasVisible,
                recoveryIncomplete:
                    !MapRoomGeometryPresentationHub.SourceRecoverySessionComplete,
                recoveryAgeMilliseconds:
                    MapRoomGeometryPresentationHub.SourceRecoverySessionElapsedMilliseconds);
        ImGui.TextUnformatted(
            "thumbnail background cadence: geometry every " +
            backgroundCadence +
            "f | source recovery every " +
            sourceRecoveryCadence +
            "f | " +
            (CanvasVisible
                ? "canvas visible"
                : backgroundIncomplete
                    ? "OH/hidden catch-up"
                    : "OH/hidden maintenance"));
        ImGui.TextUnformatted(
            "minimap readbacks: " +
            MapRoomGeometryPresentationHub.RasterReadbackCount +
            " | avg " +
            MapRoomGeometryPresentationHub.RasterReadbackAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            MapRoomGeometryPresentationHub.RasterReadbackPeakMilliseconds.ToString("F2") +
            " ms");
        ImGui.TextUnformatted(
            "authored terrain loads: " +
            MapRoomGeometryPresentationHub.CurveLoadCount +
            " | avg " +
            MapRoomGeometryPresentationHub.CurveLoadAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            MapRoomGeometryPresentationHub.CurveLoadPeakMilliseconds.ToString("F2") +
            " ms");
        ImGui.TextUnformatted(
            "world-space routes: " + ConnectionResources.Count +
            " | pending " + ConnectionResources.PendingCount);
        ImGui.TextUnformatted(
            "route batches: " + ConnectionResources.RouteBuildCount +
            " routes | avg " +
            ConnectionResources.RouteBuildAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            ConnectionResources.RouteBuildPeakMilliseconds.ToString("F2") +
            " ms");
        ImGui.TextUnformatted(
            "route readiness: " +
            (ConnectionResources.RouteSessionComplete ? "complete " : "active ") +
            ConnectionResources.RouteSessionElapsedMilliseconds.ToString("F0") +
            " ms | " +
            ConnectionResources.Count + "/" +
            ConnectionResources.RouteSessionExpected);
        ImGui.TextUnformatted(
            "corridor layout: " + ConnectionResources.CorridorLayoutCount +
            " | avg " +
            ConnectionResources.CorridorLayoutAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            ConnectionResources.CorridorLayoutPeakMilliseconds.ToString("F2") +
            " ms");
        ImGui.TextUnformatted(
            "crossing rebuild: " + ConnectionResources.CrossingBuildCount +
            " | avg " +
            ConnectionResources.CrossingBuildAverageMilliseconds.ToString("F2") +
            " ms | peak " +
            ConnectionResources.CrossingBuildPeakMilliseconds.ToString("F2") +
            " ms");
        ImGui.TextUnformatted(
            "surface: " + (Surface.Ready ? "ready" : "waiting") +
            (string.IsNullOrEmpty(Surface.Error) ? string.Empty : " | " + Surface.Error));
        ImGui.TextUnformatted(
            "offscreen renders: " + Surface.RenderCount +
            " | avg " + Surface.AverageRenderMilliseconds.ToString("F2") +
            " ms | peak " + Surface.PeakRenderMilliseconds.ToString("F2") +
            " ms" +
            (CanvasVisible ? " | canvas visible" : " | canvas dormant"));
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
            "persistent restore: " +
            (WorldMapPersistentRetainedCache.RestoreSnapshotLoaded
                ? "warm-cache "
                : "cold/no-cache ") +
            WorldMapPersistentRetainedCache.RestoreValidationMilliseconds.ToString("F0") +
            " ms | thumbnail hits " +
            WorldMapPersistentRetainedCache.RestoredThumbnailCount +
            " | route hits " +
            WorldMapPersistentRetainedCache.RestoredRouteCount);
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
