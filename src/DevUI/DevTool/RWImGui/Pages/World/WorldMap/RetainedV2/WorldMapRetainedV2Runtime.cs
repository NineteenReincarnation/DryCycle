using System.Collections.Generic;
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
    private static readonly List<int> visibleRooms = new();
    private static readonly List<string> visibleRoutes = new();
    private static WorldMapDirtySet lastDirty = new();
    private static ManualLogSource log;
    private static volatile bool enabled;
    private static int activeLayerMask = 7;
    private static int activeShowConnections = 1;
    private static float activeZoom = 1f;
    private static int retainedConnectionsReady;
    private static long lastRenderedViewRevision = long.MinValue;
    private static long lastRenderedSceneRevision = long.MinValue;
    private static long lastRenderedRoomResourceRevision = long.MinValue;
    private static long lastRenderedRouteRevision = long.MinValue;
    private static int lastRenderedLayerMask = int.MinValue;
    private static int lastRenderedShowConnections = int.MinValue;

    internal static WorldMapScene Scene => RenderSceneState;
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
    }

    internal static void Disable()
    {
        enabled = false;
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
            ConnectionResources.ApplyDirty(MainSceneState, mainThreadDirty);
            SpatialIndex.ApplyDirty(MainSceneState, RoomResources, mainThreadDirty);
            RoomRenderer.ApplyDirty(mainThreadDirty);
            ConnectionRenderer.ApplyDirty(mainThreadDirty);

            foreach (string id in mainThreadDirty.RemovedConnections)
                RouteSpatialIndex.Remove(id);

            if (mainThreadDirty.TopologyChanged || mainThreadDirty.FullRebuild)
                Volatile.Write(ref retainedConnectionsReady, 0);
        }

        RoomResources.UpdateMainThread(session, snapshot, MainSceneState);
        RoomResources.DrainGeometryChanges(geometryChangedRooms);
        if (geometryChangedRooms.Count > 0)
        {
            ConnectionResources.InvalidateRooms(geometryChangedRooms);
            SpatialIndex.InvalidateRooms(
                MainSceneState,
                RoomResources,
                geometryChangedRooms);
        }
        ConnectionResources.Update(MainSceneState, RoomResources);
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

        if (ConnectionResources.PendingCount == 0)
        {
            Volatile.Write(
                ref retainedConnectionsReady,
                ConnectionResources.HasCompleteRoutes(MainSceneState) ? 1 : 0);
        }

        if (session?.ToolMode == EditorToolMode.Map &&
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
                WorldMapBackgroundBudget.ViewportInteractionActive;
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
                    RouteSpatialIndex.Query(
                        visibleMin,
                        visibleMax,
                        visibleRoutes);
                else
                    visibleRoutes.Clear();

                if (Surface.Render(
                        view,
                        _ =>
                        {
                            RoomRenderer.SynchronizeVisible(
                                MainSceneState,
                                RoomResources,
                                visibleRooms);
                            ConnectionRenderer.SynchronizeVisible(
                                ConnectionResources,
                                visibleRoutes,
                                showConnections);
                        }))
                {
                    lastRenderedViewRevision = viewRevision;
                    lastRenderedSceneRevision = sceneRevision;
                    lastRenderedRoomResourceRevision = roomResourceRevision;
                    lastRenderedRouteRevision = routeRevision;
                    lastRenderedLayerMask = layerMask;
                    lastRenderedShowConnections = showConnectionsValue;
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

    internal static bool TryHitConnection(
        Num.Vector2 worldPoint,
        float worldRadius,
        out string connectionId,
        out float distanceSquared)
    {
        connectionId = string.Empty;
        distanceSquared = worldRadius * worldRadius;
        return enabled &&
               RetainedConnectionsReady &&
               RouteSpatialIndex.TryHit(
                   worldPoint,
                   worldRadius,
                   out connectionId,
                   out distanceSquared);
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
        visibleRooms.Clear();
        visibleRoutes.Clear();
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
            "· V2 P7 scene " +
            RenderSceneState.Rooms.Count + "/" +
            RenderSceneState.Connections.Count);

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("World Map Retained V2 · Phase 7");
        ImGui.TextUnformatted("rooms: " + RenderSceneState.Rooms.Count);
        ImGui.TextUnformatted("connections: " + RenderSceneState.Connections.Count);
        ImGui.TextUnformatted(
            "room resources: " + RoomResources.Count +
            " · thumbnails " + RoomResources.CommittedThumbnailCount);
        ImGui.TextUnformatted("world-space routes: " + ConnectionResources.Count);
        ImGui.TextUnformatted(
            "surface: " + (Surface.Ready ? "ready" : "waiting") +
            (string.IsNullOrEmpty(Surface.Error) ? string.Empty : " · " + Surface.Error));
        ImGui.TextUnformatted("retained room objects: " + RoomRenderer.RetainedRoomCount);
        ImGui.TextUnformatted(
            "retained connection objects: " + ConnectionRenderer.RetainedRouteCount +
            " · ready " + RetainedConnectionsReady);
        ImGui.TextUnformatted(
            "spatial rooms: " + SpatialIndex.Count +
            " · visible " + visibleRooms.Count);
        ImGui.TextUnformatted(
            "spatial routes: " + RouteSpatialIndex.Count +
            " · visible " + visibleRoutes.Count);
        ImGui.TextUnformatted("scene revision: " + RenderSceneState.SceneRevision);
        ImGui.TextUnformatted("view revision: " + RenderSceneState.ViewRevision);
        ImGui.TextUnformatted("scene delta backlog: " + SceneTransfer.PendingCount);
        ImGui.TextUnformatted("last scene dirty count: " + lastDirty.ChangeCount);
        ImGui.TextDisabled("pan/zoom changes only the view revision");
        ImGui.EndTooltip();
    }
}
