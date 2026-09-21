using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Frontend lifetime owner for the retained V2 scene projection.
/// Phase 1 owns scene synchronization only; rendering remains on the legacy path until later phases.
/// </summary>
internal static class WorldMapRetainedV2Runtime
{
    private static readonly WorldMapScene SceneState = new();
    private static readonly WorldMapSceneSynchronizer Synchronizer = new();
    private static readonly WorldMapRoomResourceStore RoomResources = new();
    private static readonly WorldMapConnectionResourceStore ConnectionResources = new();
    private static readonly WorldMapRenderTextureSurface Surface = new();
    private static readonly WorldMapRetainedRoomRenderer RoomRenderer = new();
    private static readonly WorldMapSpatialIndex SpatialIndex = new();
    private static readonly WorldMapDirtySet pendingResourceDirty = new();
    private static readonly List<int> geometryChangedRooms = new();
    private static readonly List<int> visibleRooms = new();
    private static WorldMapDirtySet lastDirty = new();
    private static ManualLogSource log;
    private static bool enabled;
    private static int activeLayerMask = 7;

    internal static WorldMapScene Scene => SceneState;
    internal static WorldMapDirtySet LastDirty => lastDirty;
    internal static WorldMapRoomResourceStore Resources => RoomResources;
    internal static WorldMapConnectionResourceStore Routes => ConnectionResources;

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
        WorldMapViewTransform viewTransform)
    {
        if (!enabled) return;
        activeLayerMask = layerMask;
        lastDirty = Synchronizer.Synchronize(
            SceneState,
            snapshot,
            localPositions,
            layoutRevision,
            interactiveRoom,
            viewTransform);

        pendingResourceDirty.MergeFrom(lastDirty);
    }

    internal static void UpdateMainThread()
    {
        if (!enabled) return;

        EditorSession session = DevToolRuntime.ActiveSession;
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;

        if (!pendingResourceDirty.IsEmpty)
        {
            RoomResources.ApplyDirty(SceneState, pendingResourceDirty);
            ConnectionResources.ApplyDirty(SceneState, pendingResourceDirty);
            SpatialIndex.ApplyDirty(SceneState, RoomResources, pendingResourceDirty);
            RoomRenderer.ApplyDirty(pendingResourceDirty);
            pendingResourceDirty.Clear();
        }

        RoomResources.UpdateMainThread(session, snapshot, SceneState);
        RoomResources.DrainGeometryChanges(geometryChangedRooms);
        if (geometryChangedRooms.Count > 0)
        {
            ConnectionResources.InvalidateRooms(geometryChangedRooms);
            SpatialIndex.InvalidateRooms(
                SceneState,
                RoomResources,
                geometryChangedRooms);
        }
        ConnectionResources.Update(SceneState, RoomResources);

        if (session?.ToolMode == EditorToolMode.Map &&
            snapshot?.Available == true &&
            SceneState.ViewTransform.CanvasSize.X >= 2f &&
            SceneState.ViewTransform.CanvasSize.Y >= 2f)
        {
            SceneState.ViewTransform.GetVisibleWorldBounds(
                out Num.Vector2 visibleMin,
                out Num.Vector2 visibleMax);
            SpatialIndex.Query(
                visibleMin,
                visibleMax,
                activeLayerMask,
                visibleRooms);

            Surface.Initialize(log);
            Surface.Render(
                SceneState.ViewTransform,
                _ => RoomRenderer.SynchronizeVisible(
                    SceneState,
                    RoomResources,
                    visibleRooms));
        }
    }

    internal static bool TryPresentSurface(
        ImDrawListPtr draw,
        Num.Vector2 min,
        Num.Vector2 max) =>
        enabled && Surface.TryPresent(draw, min, max);

    internal static bool TryHitRoom(
        Num.Vector2 worldPoint,
        int layerMask,
        out int roomIndex) =>
        enabled && SpatialIndex.TryHitRoom(worldPoint, layerMask, out roomIndex);

    internal static bool QueryRooms(
        Num.Vector2 worldMin,
        Num.Vector2 worldMax,
        int layerMask,
        List<int> output) =>
        enabled && SpatialIndex.Query(worldMin, worldMax, layerMask, output);

    internal static bool QueryVisibleRooms(int layerMask, List<int> output)
    {
        if (!enabled || output == null) return false;
        SceneState.ViewTransform.GetVisibleWorldBounds(
            out Num.Vector2 min,
            out Num.Vector2 max);
        return SpatialIndex.Query(min, max, layerMask, output);
    }

    internal static void ResetRetainedState()
    {
        Synchronizer.Reset();
        SceneState.Reset();
        RoomResources.Reset();
        ConnectionResources.Reset();
        SpatialIndex.Reset();
        RoomRenderer.Reset();
        Surface.Reset();
        pendingResourceDirty.Clear();
        geometryChangedRooms.Clear();
        visibleRooms.Clear();
        lastDirty = new WorldMapDirtySet();
    }

    internal static void DrawToolbarDiagnostics()
    {
        ImGui.SameLine(0f, 12f);
        ImGui.TextDisabled(
            "· V2 P1 scene " +
            SceneState.Rooms.Count + "/" +
            SceneState.Connections.Count);

        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("World Map Retained V2 · Phase 5");
        ImGui.TextUnformatted("rooms: " + SceneState.Rooms.Count);
        ImGui.TextUnformatted("connections: " + SceneState.Connections.Count);
        ImGui.TextUnformatted(
            "room resources: " + RoomResources.Count +
            " · thumbnails " + RoomResources.CommittedThumbnailCount);
        ImGui.TextUnformatted("world-space routes: " + ConnectionResources.Count);
        ImGui.TextUnformatted(
            "surface: " + (Surface.Ready ? "ready" : "waiting") +
            (string.IsNullOrEmpty(Surface.Error) ? string.Empty : " · " + Surface.Error));
        ImGui.TextUnformatted("retained room objects: " + RoomRenderer.RetainedRoomCount);
        ImGui.TextUnformatted(
            "spatial rooms: " + SpatialIndex.Count +
            " · visible " + visibleRooms.Count);
        ImGui.TextUnformatted("scene revision: " + SceneState.SceneRevision);
        ImGui.TextUnformatted("view revision: " + SceneState.ViewRevision);
        ImGui.TextUnformatted("last scene dirty count: " + lastDirty.ChangeCount);
        ImGui.TextDisabled("pan/zoom changes only the view revision");
        ImGui.EndTooltip();
    }
}
