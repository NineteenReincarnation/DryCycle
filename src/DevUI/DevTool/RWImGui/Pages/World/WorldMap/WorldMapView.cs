using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Unified region canvas: dragging room geometry edits MapPage Dev Position; dragging an Exit edits
/// endpoint topology. The mapper sees one coherent map while the command paths remain independent.
/// </summary>
internal static class WorldMapView
{
    private const float TileDisplaySize = 2f;
    private const float MinZoom = 0.20f;
    private const float MaxZoom = 3.25f;

    private sealed class ExitPortHit
    {
        internal EditorMapRoomSnapshot Room;
        internal EditorMapRoomNodeSnapshot Node;
        internal Num.Vector2 Position;
        internal bool Free;
        internal EditorMapConnectionSnapshot Connection;
    }

    private sealed class EdgeHit
    {
        internal EditorMapConnectionSnapshot Connection;
        internal float DistanceSq;
    }

    private static readonly Dictionary<int, Num.Vector2> localPositions = new();
    private static readonly List<int> retainedVisibleRoomIds = new();
    private static readonly List<int> retainedHoverRoomIds = new();
    private static readonly List<Num.Vector2> connectionPathScratch = new(12);
    private static readonly List<Num.Vector2> roundedConnectionScratch = new(64);
    private static readonly List<Num.Vector4> directionMarkerOccluders = new(128);
    private static readonly List<Num.Vector4> overlayLabelRects = new(128);
    private static long localPositionRevision;
    private static EditorMapRoomSnapshot[] synchronizedPositionRooms;
    private static readonly Dictionary<int, EditorMapRoomSnapshot> hoverRoomLookup = new();
    private static readonly bool[] layerVisible = { true, true, true };
    private static readonly uint[] geometryColorCache = new uint[16];
    private static readonly bool[] geometryColorCacheValid = new bool[16];
    private static EditorMapPresentationSnapshot hoverIndexedSnapshot;

    private static string region = string.Empty;
    private static Num.Vector2 pan;
    private static float zoom = 1f;
    private static bool fitRequested = true;
    private static int focusRoomRequested = -1;
    private static bool showConnections = true;
    private static bool showPortLabels = true;
    private static bool showSubregionLabels = true;

    private static int draggingRoom = -1;
    private static Num.Vector2 dragStartMouse;
    private static Num.Vector2 dragStartWorld;
    private static int linkingRoom = -1;
    private static int linkingNode = -1;
    private static WorldConnectionDirection linkDirection = WorldConnectionDirection.Bidirectional;

    private static string selectedConnectionId = string.Empty;
    private static string hoveredConnectionId = string.Empty;

    // Viewport navigation freezes the last hover presentation instead of deleting it. Expensive
    // hit-tests stop while panning/zooming, but the information already on screen remains visible.
    private static int navigationHoverRoomIndex = -1;
    private static int navigationHoverPortRoomIndex = -1;
    private static int navigationHoverPortNodeIndex = -1;
    private static string navigationHoverPortConnectionId = string.Empty;
    private static string navigationHoverConnectionId = string.Empty;

    internal static string SelectedConnectionId => selectedConnectionId;

    internal static void SelectConnection(string connectionId) =>
        selectedConnectionId = connectionId ?? string.Empty;

    internal static void ClearConnectionSelection()
    {
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
    }

    internal static void FocusRoom(int roomIndex)
    {
        focusRoomRequested = roomIndex;
        fitRequested = false;
    }

    internal static void ResetRetainedState()
    {
        localPositions.Clear();
        retainedVisibleRoomIds.Clear();
        retainedHoverRoomIds.Clear();
        connectionPathScratch.Clear();
        overlayLabelRects.Clear();
        localPositionRevision = 0L;
        synchronizedPositionRooms = null;
        hoverRoomLookup.Clear();
        hoverIndexedSnapshot = null;
        Array.Clear(geometryColorCacheValid, 0, geometryColorCacheValid.Length);

        region = string.Empty;
        pan = Num.Vector2.Zero;
        zoom = 1f;
        fitRequested = true;
        focusRoomRequested = -1;
        showConnections = true;
        showPortLabels = true;
        showSubregionLabels = true;
        for (int i = 0; i < layerVisible.Length; i++) layerVisible[i] = true;

        draggingRoom = -1;
        dragStartMouse = Num.Vector2.Zero;
        dragStartWorld = Num.Vector2.Zero;
        linkingRoom = -1;
        linkingNode = -1;
        linkDirection = WorldConnectionDirection.Bidirectional;
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
        navigationHoverRoomIndex = -1;
        navigationHoverPortRoomIndex = -1;
        navigationHoverPortNodeIndex = -1;
        navigationHoverPortConnectionId = string.Empty;
        navigationHoverConnectionId = string.Empty;

        WorldMapRetainedV2Runtime.ResetRetainedState();
    }

    internal static void Draw(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot?.Available != true)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("世界地图不可用。", "World Map unavailable."), true);
            return;
        }

        Array.Clear(geometryColorCacheValid, 0, geometryColorCacheValid.Length);

        SynchronizeRegion(snapshot);
        SynchronizePositions(snapshot);
        SynchronizeLinkState(snapshot);
        SynchronizeConnectionSelection(snapshot);

        DrawToolbar(snapshot);
        ImGui.Spacing();
        DrawCanvas(snapshot);
    }

    private static void DrawToolbar(EditorMapPresentationSnapshot snapshot)
    {
        float available = ImGui.GetContentRegionAvail().X;
        bool compact = available < 760f;

        ImGui.TextDisabled(snapshot.RegionName + " | " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine();
        ImGui.TextDisabled("| " + Math.Round(zoom * 100f) + "%");

        if (!compact) ImGui.SameLine(0f, 16f);
        else ImGui.Spacing();

        DrawCompactCheckbox(DevToolUiSettings.T("连接", "Links"), "WorldMapLinks", ref showConnections);
        ImGui.SameLine();
        WorldMapPipeLayers.DrawToolbarControls(ref showPortLabels, ref linkingRoom, ref linkingNode);
        ImGui.SameLine();
        DrawCompactCheckbox(DevToolUiSettings.T("子区域", "Subregions"), "WorldMapSubregions", ref showSubregionLabels);

        if (!compact) ImGui.SameLine(0f, 16f);
        else ImGui.Spacing();

        for (int i = 0; i < layerVisible.Length; i++)
        {
            bool value = layerVisible[i];
            if (ImGui.Checkbox(MapRoomLayer.Label(i) + "##WorldMapLayer" + i, ref value))
            {
                layerVisible[i] = value;
                fitRequested = true;
            }
            if (i < layerVisible.Length - 1) ImGui.SameLine();
        }

        ImGui.SameLine(0f, 18f);
        DevToolWidgets.MutedText(DevToolUiSettings.T("连接方向", "Link direction"));
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.Bidirectional, "Both", "Both");
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.AToB, "A > B", "AToB");
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.BToA, "A < B", "BToA");

        if (linkingRoom >= 0)
        {
            EditorMapRoomSnapshot source = FindRoom(snapshot, linkingRoom);
            if (DevToolWidgets.SameLineIfFits(120f, 4f))
                ImGui.TextDisabled("| " + (source?.Name ?? linkingRoom.ToString()) + ":" + linkingNode + " " + DirectionGlyph(linkDirection) + " ...");
        }

        WorldMapPlayerLocator.DrawToolbar(snapshot);
        WorldMapRetainedV2Runtime.DrawToolbarDiagnostics();
    }

    private static void DrawCompactCheckbox(string label, string id, ref bool value) =>
        ImGui.Checkbox(label + "##" + id, ref value);

    private static void DrawDirectionButton(WorldConnectionDirection direction, string label, string id)
    {
        if (DevToolWidgets.ActionButton(
                label,
                "WorldMapDirection" + id,
                linkDirection == direction ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            linkDirection = direction;
    }

    private static void DrawCanvas(EditorMapPresentationSnapshot snapshot)
    {
        UpdateActiveDragBeforeDraw();
        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 80f || canvasSize.Y < 80f) return;

        ImGui.InvisibleButton("##WorldMapCanvasInput", canvasSize);
        bool canvasHovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();

        if (focusRoomRequested >= 0)
        {
            int roomIndex = focusRoomRequested;
            focusRoomRequested = -1;
            fitRequested = false;
            FocusRoomOnCanvas(snapshot, canvasSize, roomIndex);
        }
        else if (fitRequested)
        {
            Fit(snapshot, canvasSize);
            fitRequested = false;
        }

        bool viewportInteraction = false;
        if (canvasHovered && Math.Abs(io.MouseWheel) > 0.0001f && linkingRoom < 0)
        {
            float oldZoom = zoom;
            float next = Math.Max(MinZoom, Math.Min(MaxZoom, zoom * (io.MouseWheel > 0f ? 1.12f : 0.89f)));
            Num.Vector2 mouseInCanvas = io.MousePos - canvasMin;
            Num.Vector2 worldAtMouse = (mouseInCanvas - pan) / oldZoom;
            zoom = next;
            pan = mouseInCanvas - worldAtMouse * zoom;
            viewportInteraction = true;
        }

        if (canvasHovered && linkingRoom < 0 &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
        {
            pan += io.MouseDelta;
            viewportInteraction = true;
        }

        // Interaction classes are published before any room/shortcut presentation work so the
        // current frame, not only the next one, receives the correct background budget.
        WorldMapInteractionKind interactionKind = WorldMapInteractionKind.None;
        if (viewportInteraction) interactionKind |= WorldMapInteractionKind.Viewport;
        if (draggingRoom >= 0) interactionKind |= WorldMapInteractionKind.RoomDrag;
        if (linkingRoom >= 0) interactionKind |= WorldMapInteractionKind.Linking;
        if (interactionKind != WorldMapInteractionKind.None)
            WorldMapBackgroundBudget.NoteInteraction(interactionKind);

        WorldMapRetainedV2Runtime.Synchronize(
            snapshot,
            localPositions,
            localPositionRevision,
            draggingRoom,
            CurrentLayerMask(),
            showConnections,
            new WorldMapViewTransform(canvasMin, canvasSize, pan, zoom));

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        bool renderChannels = WorldMapRenderOrder.BeginCanvas(draw, snapshot);
        bool canvasClip = WorldMapPresentationCorrectness.BeginCanvasClip(draw, snapshot, canvasMin, canvasSize);
        Num.Vector2 canvasMax = canvasMin + canvasSize;
        draw.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.ChildBg));
        draw.AddRect(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.Border));
        DrawGrid(draw, canvasMin, canvasSize);
        bool retainedRoomsPresented =
            WorldMapRetainedV2Runtime.TryPresentSurface(draw, canvasMin, canvasMax);

        EditorMapRoomSnapshot hoveredRoom;
        ExitPortHit hoveredPort;

        if (!viewportInteraction)
        {
            hoveredRoom =
                canvasHovered
                    ? FindHoveredRoom(
                        snapshot,
                        canvasMin,
                        canvasSize,
                        io.MousePos)
                    : null;
            hoveredPort =
                canvasHovered
                    ? FindHoveredExitPort(
                        snapshot,
                        canvasMin,
                        canvasSize,
                        io.MousePos,
                        hoveredRoom)
                    : null;

            navigationHoverRoomIndex =
                hoveredRoom?.RoomIndex ??
                -1;
            navigationHoverPortRoomIndex =
                hoveredPort?.Room?.RoomIndex ??
                -1;
            navigationHoverPortNodeIndex =
                hoveredPort?.Node?.NodeIndex ??
                -1;
            navigationHoverPortConnectionId =
                hoveredPort?.Connection?.ConnectionId ??
                string.Empty;
        }
        else
        {
            hoveredRoom =
                navigationHoverRoomIndex >= 0
                    ? FindRoom(
                        snapshot,
                        navigationHoverRoomIndex)
                    : null;
            hoveredPort =
                ResolveNavigationHoverPort(
                    snapshot,
                    canvasMin);
        }

        bool retainedConnectionsPresented =
            retainedRoomsPresented &&
            showConnections;

        EdgeHit hoveredEdge = null;

        if (!viewportInteraction)
        {
            hoveredConnectionId = string.Empty;

            if (canvasHovered &&
                hoveredPort == null &&
                showConnections)
            {
                float retainedScreenDistanceSq = float.MaxValue;
                if (retainedConnectionsPresented)
                {
                    float safeZoom = Math.Max(0.0001f, zoom);
                    Num.Vector2 worldPoint =
                        (io.MousePos - canvasMin - pan) / safeZoom;
                    float worldRadius = 12f / safeZoom;
                    if (WorldMapRetainedV2Runtime.TryHitConnection(
                            worldPoint,
                            worldRadius,
                            out string retainedConnectionId,
                            out float retainedWorldDistanceSq))
                    {
                        hoveredConnectionId = retainedConnectionId;
                        retainedScreenDistanceSq =
                            retainedWorldDistanceSq *
                            safeZoom *
                            safeZoom;
                    }
                }

                hoveredEdge = FindHoveredEdge(
                    snapshot,
                    canvasMin,
                    canvasSize,
                    io.MousePos,
                    skipRetainedRoutes: retainedConnectionsPresented);

                if (hoveredEdge != null &&
                    hoveredEdge.DistanceSq < retainedScreenDistanceSq)
                {
                    hoveredConnectionId = string.Empty;
                }
                else if (!string.IsNullOrEmpty(hoveredConnectionId))
                {
                    hoveredEdge = null;
                }
            }

            navigationHoverConnectionId =
                hoveredPort?.Connection?.ConnectionId ??
                hoveredConnectionId ??
                string.Empty;
        }
        else
        {
            hoveredConnectionId =
                navigationHoverConnectionId;
        }

        DrawRooms(
            draw,
            snapshot,
            canvasMin,
            canvasSize,
            hoveredRoom,
            hoveredPort,
            viewportInteraction,
            retainedRoomsPresented);
        if (showConnections)
        {
            DrawConnections(
                draw,
                snapshot,
                canvasMin,
                canvasSize,
                skipRetainedRoutes: retainedConnectionsPresented);

            if (!retainedConnectionsPresented)
            {
                DrawFallbackCrossingSemantics(
                    draw,
                    snapshot,
                    canvasMin,
                    canvasSize);
            }

            DrawConnectionFocusOverlays(
                draw,
                snapshot,
                canvasMin,
                canvasSize,
                hoveredPort);
        }

        WorldMapRenderOrder.UseOverlay(draw);

        if (retainedConnectionsPresented &&
            canvasHovered &&
            hoveredPort == null &&
            !string.IsNullOrEmpty(hoveredConnectionId))
            DrawRetainedConnectionTooltip(snapshot, hoveredConnectionId);

        if (!viewportInteraction)
            HandleInteraction(
                snapshot,
                canvasHovered,
                io,
                hoveredRoom,
                hoveredPort,
                hoveredEdge,
                retainedConnectionsPresented);
        DrawLinkPreview(draw, snapshot, canvasMin, io.MousePos, hoveredPort);
        HandleDelete(snapshot);
        if (WorldMapExactShortcuts.AfterCanvas(snapshot, selectedConnectionId))
            selectedConnectionId = string.Empty;
        WorldMapPlayerLocator.DrawCanvas(
            snapshot,
            canvasMin,
            canvasMax,
            pan,
            zoom,
            localPositions,
            layerVisible);
        WorldMapPresentationCorrectness.EndCanvasClip(draw, canvasClip);
        WorldMapRenderOrder.EndCanvas(draw, renderChannels);
    }

    private static void UpdateActiveDragBeforeDraw()
    {
        if (draggingRoom < 0 || !ImGui.IsMouseDown(ImGuiMouseButton.Left) || zoom <= 0.0001f)
            return;

        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        SetLocalPosition(
            draggingRoom,
            dragStartWorld + (mouse - dragStartMouse) / zoom);
    }

    private static void DrawGrid(ImDrawListPtr draw, Num.Vector2 canvasMin, Num.Vector2 canvasSize)
    {
        float grid = 64f * zoom;
        if (grid < 16f) grid *= 4f;
        uint color = ImGui.GetColorU32(ImGuiCol.Border);
        float startX = PositiveModulo(pan.X, grid);
        float startY = PositiveModulo(pan.Y, grid);
        for (float x = startX; x < canvasSize.X; x += grid)
            draw.AddLine(canvasMin + new Num.Vector2(x, 0f), canvasMin + new Num.Vector2(x, canvasSize.Y), color);
        for (float y = startY; y < canvasSize.Y; y += grid)
            draw.AddLine(canvasMin + new Num.Vector2(0f, y), canvasMin + new Num.Vector2(canvasSize.X, y), color);
    }

    private static void DrawRooms(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapRoomSnapshot hoveredRoom,
        ExitPortHit hoveredPort,
        bool fastNavigation,
        bool retainedRoomsPresented)
    {
        bool useSpatial =
            retainedRoomsPresented &&
            WorldMapRetainedV2Runtime.QueryVisibleRooms(
                CurrentLayerMask(),
                retainedVisibleRoomIds);

        if (useSpatial)
        {
            for (int i = 0; i < retainedVisibleRoomIds.Count; i++)
            {
                EditorMapRoomSnapshot room = FindRoom(snapshot, retainedVisibleRoomIds[i]);
                if (room == null) continue;
                DrawRoomEntry(
                    draw,
                    snapshot,
                    canvasMin,
                    canvasSize,
                    hoveredRoom,
                    fastNavigation,
                    retainedRoomsPresented,
                    room);
            }
        }
        else
        {
            retainedVisibleRoomIds.Clear();
            EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null || !IsLayerVisible(room.Layer)) continue;
                DrawRoomEntry(
                    draw,
                    snapshot,
                    canvasMin,
                    canvasSize,
                    hoveredRoom,
                    fastNavigation,
                    retainedRoomsPresented,
                    room);
            }
        }

        // Navigation must preserve the map's semantic overlay. Hiding room/pipe labels while the
        // middle/right mouse button is held makes the map visually jump and removes exactly the
        // information the user is panning to inspect. Keep these overlays visible, but switch exit
        // sockets to a presentation-only path during navigation so expensive endpoint/link hover
        // resolution is still skipped.
        IReadOnlyList<int> overlayRooms =
            useSpatial ? retainedVisibleRoomIds : null;

        PrepareOverlayLabelLayout(
            snapshot,
            canvasMin,
            canvasSize,
            hoveredRoom,
            overlayRooms);

        DrawExitPorts(
            draw,
            snapshot,
            canvasMin,
            canvasSize,
            hoveredRoom,
            hoveredPort,
            overlayRooms,
            presentationOnly: fastNavigation);
        DrawCreatureShortcuts(
            draw,
            snapshot,
            canvasMin,
            canvasSize,
            overlayRooms);
    }

    private static void DrawRoomEntry(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapRoomSnapshot hoveredRoom,
        bool fastNavigation,
        bool retainedRoomsPresented,
        EditorMapRoomSnapshot room)
    {
        if (!IsLayerVisible(room.Layer)) return;

        EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
        GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
        if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 48f)) return;

        bool selected = room.RoomIndex == snapshot.SelectedRoomIndex;
        bool hovered = ReferenceEquals(room, hoveredRoom);
        if (!retainedRoomsPresented)
        {
            // Never replace a room thumbnail with the old flat navigation placeholder just because
            // the retained surface misses a frame during middle/right-button panning. That fallback
            // made every room flash into a pale rectangle as soon as viewport interaction began.
            //
            // Keep the same detailed raster fallback for both idle and navigation frames. The
            // expensive interaction affordances (ports, creature holes, labels) are still suppressed
            // by fastNavigation, so a transient retained-surface miss stays visually stable without
            // bringing back the old full-map interaction cost.
            DrawRoomGeometry(draw, room, visual, min, selected, hovered);
        }
        else if (selected || hovered || room.CurrentRoom)
        {
            DrawRetainedRoomOutline(draw, room, min, max, selected, hovered);
        }

        // Labels are navigation content. Keep them visible while panning; DrawRoomLabel already
        // applies the zoom LOD threshold so this does not turn low-zoom navigation into a text wall.
        DrawRoomLabel(draw, room, min, max, selected, hovered);
    }

    private static void DrawRetainedRoomOutline(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 roomMin,
        Num.Vector2 roomMax,
        bool selected,
        bool hovered)
    {
        WorldMapRenderOrder.UseOverlay(draw);

        uint outline = ImGui.GetColorU32(
            selected ? ImGuiCol.ButtonActive :
            room.CurrentRoom ? ImGuiCol.Header :
            hovered ? ImGuiCol.ButtonHovered :
            ImGuiCol.Border);

        float thickness =
            selected ? 2.4f :
            room.CurrentRoom ? 1.8f :
            1.35f;

        float pad = selected ? 1.5f : 0.75f;
        draw.AddRect(
            roomMin - new Num.Vector2(pad, pad),
            roomMax + new Num.Vector2(pad, pad),
            outline,
            Math.Max(1f, 3f * zoom),
            ImDrawFlags.None,
            thickness);
    }

    private static void DrawRoomNavigationLod(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 roomMin,
        Num.Vector2 roomMax,
        bool selected)
    {
        WorldMapRenderOrder.UseBase(draw);
        // Phase 0 visual continuity rule: navigation/zoom may simplify detail, but it must never
        // replace the room with a near-black placeholder. The retained V2 renderer will preserve the
        // committed thumbnail itself; until then use the same visible Air tone as the detailed map.
        uint fill = selected
            ? ImGui.GetColorU32(ImGuiCol.Button)
            : room.CurrentRoom
                ? ImGui.GetColorU32(ImGuiCol.Header)
                : GeometryColor(EditorMapGeometryKind.Air);
        uint outline = ImGui.GetColorU32(
            selected ? ImGuiCol.ButtonActive :
            room.CurrentRoom ? ImGuiCol.Header :
            room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Border);

        float rounding = Math.Max(1f, 2.2f * zoom);
        draw.AddRectFilled(roomMin, roomMax, fill, rounding);
        draw.AddRect(
            roomMin,
            roomMax,
            outline,
            rounding,
            ImDrawFlags.None,
            selected || room.CurrentRoom ? 2f : 1f);
    }

    private static void DrawRoomGeometry(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered)
    {
        WorldMapRenderOrder.UseBase(draw);

        int pushedStyleColors = WorldMapThumbnailVisibility.PushRoomStyle();
        try
        {
            float scale = TileDisplaySize * zoom;
            float width = Math.Max(1f, visual.WidthTiles) * scale;
            float height = Math.Max(1f, visual.HeightTiles) * scale;
            Num.Vector2 roomMax = roomMin + new Num.Vector2(width, height);
            uint outline = ImGui.GetColorU32(
                selected ? ImGuiCol.ButtonActive :
                room.CurrentRoom ? ImGuiCol.Header :
                hovered ? ImGuiCol.ButtonHovered :
                room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Border);

            bool lowLod = zoom < 0.48f;
            bool highLod = zoom >= 0.82f;

            draw.PushClipRect(roomMin, roomMax, true);
            try
            {
                if (!visual.DetailedRasterAvailable)
                {
                    uint fill = selected
                        ? ImGui.GetColorU32(ImGuiCol.Button)
                        : GeometryColor(EditorMapGeometryKind.Air);
                    draw.AddRectFilled(roomMin, roomMax, fill, Math.Max(1f, 3f * zoom));
                }

                if (visual.DetailedRasterAvailable)
                {
                    EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();

                    // MapTex represents Air as row runs too. Fill it once for the whole room and skip
                    // thousands of redundant Air rectangles; all non-Air terrain is then painted above it.
                    bool hasAir = false;
                    for (int i = 0; i < runs.Length; i++)
                    {
                        if (runs[i].Kind != EditorMapGeometryKind.Air) continue;
                        hasAir = true;
                        break;
                    }
                    if (hasAir)
                        draw.AddRectFilled(roomMin, roomMax, GeometryColor(EditorMapGeometryKind.Air));

                    for (int i = 0; i < runs.Length; i++)
                    {
                        EditorMapRectSnapshot run = runs[i];
                        if (run.Kind == EditorMapGeometryKind.Air) continue;
                        if (lowLod && run.Kind == EditorMapGeometryKind.Water) continue;
                        Num.Vector2 a = LocalToScreen(roomMin, visual, run.X, run.Y + run.Height);
                        Num.Vector2 b = LocalToScreen(roomMin, visual, run.X + run.Width, run.Y);
                        draw.AddRectFilled(Num.Vector2.Min(a, b), Num.Vector2.Max(a, b), GeometryColor(run.Kind));
                    }
                }

                if (visual.Curves != null)
                {
                    for (int i = 0; i < visual.Curves.Length; i++)
                        DrawCurve(draw, visual, roomMin, visual.Curves[i], highLod);
                }
            }
            finally
            {
                draw.PopClipRect();
            }

            draw.AddRect(roomMin, roomMax, outline, Math.Max(1f, 3f * zoom), ImDrawFlags.None, selected ? 2.2f : 1f);
        }
        finally
        {
            WorldMapThumbnailVisibility.PopRoomStyle(pushedStyleColors);
        }
    }

    private static void DrawCurve(
        ImDrawListPtr draw,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        EditorMapPolylineSnapshot curve,
        bool highLod)
    {
        EditorMapPointSnapshot[] points = curve?.Points ?? Array.Empty<EditorMapPointSnapshot>();
        if (points.Length < 2) return;

        uint color = GeometryColor(curve.Kind);
        int surfaceCount = curve.Closed && points.Length >= 4 ? points.Length - 2 : points.Length;
        for (int i = 0; i < surfaceCount - 1; i++)
        {
            Num.Vector2 a = LocalToScreen(roomMin, visual, points[i].X, points[i].Y);
            Num.Vector2 b = LocalToScreen(roomMin, visual, points[i + 1].X, points[i + 1].Y);
            draw.AddLine(a, b, color, curve.Kind == EditorMapGeometryKind.QuicksandMaterial ? 2.2f : 1.6f);
        }

        if (!curve.Closed || surfaceCount < 2) return;
        float bottomY = points[points.Length - 1].Y;
        Num.Vector2 bottomA = LocalToScreen(roomMin, visual, points[0].X, bottomY);
        Num.Vector2 bottomB = LocalToScreen(roomMin, visual, points[surfaceCount - 1].X, bottomY);
        draw.AddLine(bottomA, bottomB, color, 1f);

        if (!highLod && curve.Kind != EditorMapGeometryKind.QuicksandMaterial) return;
        int stride = curve.Kind == EditorMapGeometryKind.QuicksandMaterial ? 2 : 4;
        for (int i = 0; i < surfaceCount; i += stride)
        {
            Num.Vector2 top = LocalToScreen(roomMin, visual, points[i].X, points[i].Y);
            Num.Vector2 bottom = LocalToScreen(roomMin, visual, points[i].X, bottomY);
            draw.AddLine(top, bottom, color, curve.Kind == EditorMapGeometryKind.QuicksandMaterial ? 1.2f : 0.7f);
        }
    }

    private static uint GeometryColor(EditorMapGeometryKind kind)
    {
        int index = (int)kind;
        if (index >= 0 && index < geometryColorCache.Length && geometryColorCacheValid[index])
            return geometryColorCache[index];

        uint fallback = kind switch
        {
            EditorMapGeometryKind.Air => ImGui.GetColorU32(new Num.Vector4(0.58f, 0.59f, 0.60f, 1.00f)),
            EditorMapGeometryKind.BackWall => ImGui.GetColorU32(new Num.Vector4(0.47f, 0.48f, 0.49f, 1.00f)),
            EditorMapGeometryKind.Solid => ImGui.GetColorU32(new Num.Vector4(0.29f, 0.30f, 0.31f, 1.00f)),
            EditorMapGeometryKind.Structure => ImGui.GetColorU32(new Num.Vector4(0.58f, 0.31f, 0.31f, 1.00f)),
            EditorMapGeometryKind.Shortcut => ImGui.GetColorU32(new Num.Vector4(0.84f, 0.85f, 0.84f, 1.00f)),
            EditorMapGeometryKind.Transport => ImGui.GetColorU32(new Num.Vector4(0.72f, 0.20f, 0.28f, 1.00f)),
            EditorMapGeometryKind.Water => ImGui.GetColorU32(new Num.Vector4(0.12f, 0.34f, 0.78f, 0.24f)),
            // Authored curved terrain uses the same semantic colors as ordinary terrain.
            EditorMapGeometryKind.LocalTerrain => ImGui.GetColorU32(new Num.Vector4(0.58f, 0.31f, 0.31f, 1.00f)),
            EditorMapGeometryKind.CurvedSlope => ImGui.GetColorU32(new Num.Vector4(0.29f, 0.30f, 0.31f, 1.00f)),
            EditorMapGeometryKind.QuicksandMaterial => ImGui.GetColorU32(ImGuiCol.ButtonHovered),
            EditorMapGeometryKind.QuicksandBody => ImGui.GetColorU32(ImGuiCol.Separator),
            _ => ImGui.GetColorU32(ImGuiCol.Border)
        };
        uint resolved = WorldMapThumbnailVisibility.ResolveGeometryColor(kind, fallback);

        if (index >= 0 && index < geometryColorCache.Length)
        {
            geometryColorCache[index] = resolved;
            geometryColorCacheValid[index] = true;
        }
        return resolved;
    }

    private static uint ShortcutGold(bool bright) =>
        ImGui.GetColorU32(bright
            ? new Num.Vector4(1.00f, 0.82f, 0.30f, 1.00f)
            : new Num.Vector4(0.86f, 0.62f, 0.17f, 1.00f));

    private static uint ShortcutGoldDark() =>
        ImGui.GetColorU32(new Num.Vector4(0.34f, 0.22f, 0.055f, 1.00f));

    private static uint CreatureShortcutGreen(bool bright) =>
        ImGui.GetColorU32(bright
            ? new Num.Vector4(0.32f, 1.00f, 0.46f, 1.00f)
            : new Num.Vector4(0.10f, 0.72f, 0.28f, 1.00f));

    private static uint CreatureShortcutGreenDark() =>
        ImGui.GetColorU32(new Num.Vector4(0.025f, 0.24f, 0.08f, 1.00f));

    private static uint ConnectionColor(WorldConnectionDirection direction) =>
        ImGui.GetColorU32(direction == WorldConnectionDirection.Bidirectional
            ? new Num.Vector4(0.83f, 0.67f, 0.40f, 1.00f)
            : new Num.Vector4(0.92f, 0.94f, 0.97f, 1.00f));

    private static void DrawRoomLabel(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 min,
        Num.Vector2 max,
        bool selected,
        bool hovered)
    {
        WorldMapRenderOrder.UseOverlay(draw);
        if (zoom < 0.34f && !selected && !hovered) return;
        uint text = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Text);
        string name = room.Name;
        Num.Vector2 nameSize = ImGui.CalcTextSize(name);
        Num.Vector2 label = new Num.Vector2((min.X + max.X - nameSize.X) * 0.5f, min.Y - nameSize.Y - 3f);
        draw.AddText(label, text, name);

        if (!showSubregionLabels || zoom < 0.75f || string.IsNullOrEmpty(room.Subregion)) return;
        string meta = MapRoomLayer.Label(room.Layer) + " | " + room.Subregion;
        if (room.OffScreenDen) meta += " | DEN";
        Num.Vector2 metaSize = ImGui.CalcTextSize(meta);
        draw.AddText(
            new Num.Vector2((min.X + max.X - metaSize.X) * 0.5f, max.Y + 2f),
            ImGui.GetColorU32(ImGuiCol.TextDisabled),
            meta);
    }

    private static void DrawConnections(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        bool skipRetainedRoutes)
    {
        WorldMapRenderOrder.UseConnections(draw);
        EditorMapConnectionSnapshot[] connections =
            snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        Num.Vector2 canvasMax = canvasMin + canvasSize;

        directionMarkerOccluders.Clear();
        foreach (EditorMapRoomSnapshot room in snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>())
        {
            if (room == null || !IsLayerVisible(room.Layer)) continue;
            GetRoomRect(room, WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex), canvasMin,
                out Num.Vector2 min, out Num.Vector2 max);
            directionMarkerOccluders.Add(new Num.Vector4(min.X, min.Y, max.X, max.Y));
        }

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null)
                continue;

            bool retainedOnSurface = skipRetainedRoutes &&
                WorldMapRetainedV2Runtime.IsConnectionRetainedOnSurface(
                    connection.ConnectionId);

            if (!BuildImmediateConnectionPath(
                    snapshot,
                    connection,
                    canvasMin,
                    connectionPathScratch,
                    out bool usedRetainedRoute))
                continue;

            // Never suppress the immediate stroke unless the marker/path also came from the exact
            // retained route currently used for hit testing. During a route-index/surface handoff,
            // falling back to the legacy path while hiding its stroke would put the direction glyph
            // on a different line from the visible GPU route.
            bool retained = retainedOnSurface && usedRetainedRoute;

            if (!PathNearCanvas(
                    connectionPathScratch,
                    canvasMin,
                    canvasMax,
                    42f))
                continue;

            uint core =
                connection.Ambiguous
                    ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                    : ConnectionColor(connection.Direction);
            uint shadow =
                ImGui.GetColorU32(ImGuiCol.WindowBg);

            float coreThickness =
                connection.Direction ==
                WorldConnectionDirection.Bidirectional
                    ? 1.9f
                    : 1.8f;
            float shadowThickness =
                coreThickness + 2f;

            if (!retained) DrawConnectionPathStroke(
                draw,
                connectionPathScratch,
                shadow,
                core,
                shadowThickness,
                coreThickness,
                connection.Ambiguous);

            WorldMapConnectionDrawing.DrawDirectionMarker(draw, connectionPathScratch, canvasMin, canvasMax,
                directionMarkerOccluders, connection.Direction, 0xFFBFE5FA);

            if (connection.Ambiguous &&
                TryPointOnPath(
                    connectionPathScratch,
                    0.5f,
                    out Num.Vector2 labelPoint,
                    out Num.Vector2 _))
            {
                draw.AddText(
                    labelPoint + new Num.Vector2(8f, -20f),
                    core,
                    "?");
            }
        }
    }

    private static void DrawFallbackCrossingSemantics(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        if (snapshot == null)
            return;

        EditorMapConnectionSnapshot[] connections =
            snapshot.Connections ??
            Array.Empty<EditorMapConnectionSnapshot>();
        Num.Vector2 canvasMax =
            canvasMin +
            canvasSize;

        for (int c = 0;
             c < connections.Length;
             c++)
        {
            EditorMapConnectionSnapshot overConnection =
                connections[c];

            if (overConnection == null ||
                string.IsNullOrEmpty(
                    overConnection.ConnectionId) ||
                !WorldMapRetainedV2Runtime.TryGetConnectionCrossings(
                    overConnection.ConnectionId,
                    out WorldMapCrossingMark[] marks) ||
                marks == null)
                continue;

            for (int i = 0;
                 i < marks.Length;
                 i++)
            {
                WorldMapCrossingMark mark =
                    marks[i];

                // Crossing snapshots are indexed under both routes. Render only from the over-route
                // entry so every semantic crossing is emitted exactly once.
                if (!string.Equals(
                        mark.OverRouteId,
                        overConnection.ConnectionId,
                        StringComparison.Ordinal))
                    continue;

                EditorMapConnectionSnapshot underConnection =
                    FindConnection(
                        snapshot,
                        mark.UnderRouteId);

                if (underConnection == null ||
                    !ConnectionLayersVisible(
                        snapshot,
                        overConnection) ||
                    !ConnectionLayersVisible(
                        snapshot,
                        underConnection))
                    continue;

                Num.Vector2 point =
                    ToScreen(
                        canvasMin,
                        mark.Point);

                if (point.X < canvasMin.X - 32f ||
                    point.Y < canvasMin.Y - 32f ||
                    point.X > canvasMax.X + 32f ||
                    point.Y > canvasMax.Y + 32f)
                    continue;

                Num.Vector2 tangent =
                    mark.Tangent;
                float tangentLength =
                    tangent.Length();
                if (tangentLength <= 0.001f)
                    continue;

                tangent /=
                    tangentLength;
                Num.Vector2 normal =
                    new(
                        -tangent.Y,
                        tangent.X);

                float radius =
                    Math.Max(
                        2.5f,
                        (mark.Dense ? 5.2f : 6.4f) *
                        zoom);
                float rise =
                    Math.Max(
                        1.8f,
                        (mark.Dense ? 3.7f : 4.8f) *
                        zoom);
                float underGap =
                    Math.Max(
                        3.2f,
                        (mark.Dense ? 4.8f : 6.4f) *
                        zoom);

                uint mask =
                    ImGui.GetColorU32(
                        ImGuiCol.WindowBg);
                uint bridgeCore =
                    overConnection.Ambiguous
                        ? ImGui.GetColorU32(
                            ImGuiCol.TextDisabled)
                        : ConnectionColor(
                            overConnection.Direction);
                float bridgeCoreThickness =
                    overConnection.Direction ==
                    WorldConnectionDirection.Bidirectional
                        ? 2.5f
                        : 2.35f;
                float bridgeShadowThickness =
                    bridgeCoreThickness +
                    3.4f;

                float underCoreThickness =
                    underConnection.Direction ==
                    WorldConnectionDirection.Bidirectional
                        ? 2.5f
                        : 2.35f;
                float underShadowThickness =
                    underCoreThickness +
                    3.4f;

                // Explicitly break the under-route, then erase the straight over-route span before
                // drawing the arc. This mirrors the retained GPU presentation when the surface is
                // temporarily unavailable during pan/zoom.
                draw.AddLine(
                    point -
                        normal *
                        underGap,
                    point +
                        normal *
                        underGap,
                    mask,
                    underShadowThickness +
                    2f);

                draw.AddLine(
                    point -
                        tangent *
                        (radius + 3f),
                    point +
                        tangent *
                        (radius + 3f),
                    mask,
                    bridgeShadowThickness +
                    2f);

                int arcSegments =
                    mark.Dense ? 3 : 6;
                Num.Vector2 previous =
                    point -
                    tangent *
                    radius;

                for (int segment = 1;
                     segment <= arcSegments;
                     segment++)
                {
                    float t =
                        segment /
                        (float)arcSegments;
                    float along =
                        (-1f + t * 2f) *
                        radius;
                    float lift =
                        (float)Math.Sin(
                            Math.PI * t) *
                        rise;

                    Num.Vector2 current =
                        point +
                        tangent *
                        along +
                        normal *
                        lift;

                    draw.AddLine(
                        previous,
                        current,
                        mask,
                        bridgeShadowThickness);

                    draw.AddLine(
                        previous,
                        current,
                        bridgeCore,
                        bridgeCoreThickness);

                    previous =
                        current;
                }
            }
        }
    }

    private static void DrawConnectionFocusOverlays(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        ExitPortHit hoveredPort)
    {
        if (!showConnections || snapshot == null)
            return;

        string hoverId =
            !string.IsNullOrEmpty(hoveredConnectionId)
                ? hoveredConnectionId
                : hoveredPort?.Connection?.ConnectionId ??
                  string.Empty;

        if (!string.IsNullOrEmpty(selectedConnectionId))
        {
            DrawConnectionFocusOverlay(
                draw,
                snapshot,
                canvasMin,
                canvasSize,
                selectedConnectionId,
                selected: true);
        }

        if (!string.IsNullOrEmpty(hoverId) &&
            !string.Equals(
                hoverId,
                selectedConnectionId,
                StringComparison.Ordinal))
        {
            DrawConnectionFocusOverlay(
                draw,
                snapshot,
                canvasMin,
                canvasSize,
                hoverId,
                selected: false);
        }
    }

    private static void DrawConnectionFocusOverlay(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        string connectionId,
        bool selected)
    {
        EditorMapConnectionSnapshot connection =
            FindConnection(
                snapshot,
                connectionId);
        if (connection == null)
            return;

        EditorMapRoomSnapshot fromRoom =
            FindRoom(
                snapshot,
                connection.FromRoomIndex);
        EditorMapRoomSnapshot toRoom =
            FindRoom(
                snapshot,
                connection.ToRoomIndex);
        if (fromRoom == null ||
            toRoom == null ||
            !IsLayerVisible(fromRoom.Layer) ||
            !IsLayerVisible(toRoom.Layer) ||
            !BuildImmediateConnectionPath(
                snapshot,
                connection,
                canvasMin,
                connectionPathScratch))
            return;

        Num.Vector2 canvasMax =
            canvasMin + canvasSize;
        if (!PathNearCanvas(
                connectionPathScratch,
                canvasMin,
                canvasMax,
                48f))
            return;

        // The broad opaque isolation stroke hides only routes immediately under/alongside the
        // focused connection. It gives local visual separation without requiring a full retained
        // surface re-render or global alpha update on every hover frame.
        uint isolation =
            ImGui.GetColorU32(ImGuiCol.ChildBg);
        uint core =
            connection.Ambiguous
                ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                : ConnectionColor(connection.Direction);

        float coreThickness =
            selected ? 2.6f : 2.2f;
        float isolationThickness =
            selected ? 5.2f : 4.4f;

        DrawConnectionPathStroke(
            draw,
            connectionPathScratch,
            isolation,
            core,
            isolationThickness,
            coreThickness,
            connection.Ambiguous);

        WorldMapConnectionDrawing.DrawDirectionMarker(draw, connectionPathScratch, canvasMin, canvasMax,
            directionMarkerOccluders, connection.Direction, 0xFFEAF4FF);

        DrawFocusedCrossingSemantics(
            draw,
            snapshot,
            canvasMin,
            canvasSize,
            connection,
            selected,
            isolation,
            core,
            isolationThickness,
            coreThickness);
    }

    private static void DrawFocusedCrossingSemantics(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapConnectionSnapshot focusedConnection,
        bool selected,
        uint focusIsolation,
        uint focusCore,
        float focusIsolationThickness,
        float focusCoreThickness)
    {
        if (focusedConnection == null ||
            !WorldMapRetainedV2Runtime.TryGetConnectionCrossings(
                focusedConnection.ConnectionId,
                out WorldMapCrossingMark[] marks) ||
            marks == null ||
            marks.Length == 0)
            return;

        Num.Vector2 canvasMax =
            canvasMin + canvasSize;

        for (int i = 0; i < marks.Length; i++)
        {
            WorldMapCrossingMark mark =
                marks[i];

            EditorMapConnectionSnapshot overConnection =
                FindConnection(
                    snapshot,
                    mark.OverRouteId);

            if (overConnection == null ||
                !ConnectionLayersVisible(
                    snapshot,
                    overConnection))
                continue;

            Num.Vector2 point =
                ToScreen(
                    canvasMin,
                    mark.Point);

            if (point.X < canvasMin.X - 32f ||
                point.Y < canvasMin.Y - 32f ||
                point.X > canvasMax.X + 32f ||
                point.Y > canvasMax.Y + 32f)
                continue;

            Num.Vector2 tangent =
                mark.Tangent;
            float tangentLength =
                tangent.Length();
            if (tangentLength <= 0.001f)
                continue;

            tangent /= tangentLength;
            Num.Vector2 normal =
                new(
                    -tangent.Y,
                    tangent.X);

            bool focusedIsOver =
                string.Equals(
                    mark.OverRouteId,
                    focusedConnection.ConnectionId,
                    StringComparison.Ordinal);

            uint bridgeCore =
                focusedIsOver
                    ? focusCore
                    : overConnection.Ambiguous
                        ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                        : ConnectionColor(
                            overConnection.Direction);

            uint bridgeShadow =
                focusedIsOver
                    ? focusIsolation
                    : ImGui.GetColorU32(
                        ImGuiCol.WindowBg);

            float bridgeCoreThickness =
                focusedIsOver
                    ? focusCoreThickness
                    : overConnection.Direction ==
                      WorldConnectionDirection.Bidirectional
                        ? 2.5f
                        : 2.35f;

            float bridgeShadowThickness =
                focusedIsOver
                    ? focusIsolationThickness
                    : bridgeCoreThickness + 3.4f;

            float radius =
                (mark.Dense ? 5.2f : 6.4f) *
                zoom;
            float rise =
                (mark.Dense ? 3.7f : 4.8f) *
                zoom;

            radius =
                Math.Max(
                    2.5f,
                    radius);
            rise =
                Math.Max(
                    1.8f,
                    rise);

            // Mirror the retained crossing grammar in the interaction overlay: first carve a
            // perpendicular gap through the under-route, then remove the straight over-route span
            // that is replaced by the bridge arc. This keeps hover/selection from turning a clear
            // crossover back into a false junction.
            uint crossingMask =
                ImGui.GetColorU32(
                    ImGuiCol.WindowBg);
            float underGap =
                Math.Max(
                    3.2f,
                    (mark.Dense ? 4.8f : 6.4f) *
                    zoom);

            draw.AddLine(
                point -
                    normal *
                    underGap,
                point +
                    normal *
                    underGap,
                crossingMask,
                Math.Max(
                    5.2f,
                    focusIsolationThickness));

            draw.AddLine(
                point -
                    tangent *
                    (radius + 3f),
                point +
                    tangent *
                    (radius + 3f),
                focusIsolation,
                focusIsolationThickness + 2f);

            int arcSegments =
                mark.Dense ? 3 : 6;
            Num.Vector2 previous =
                point -
                tangent * radius;

            for (int segment = 1;
                 segment <= arcSegments;
                 segment++)
            {
                float t =
                    segment /
                    (float)arcSegments;
                float along =
                    (-1f + t * 2f) *
                    radius;
                float lift =
                    (float)Math.Sin(
                        Math.PI * t) *
                    rise;

                Num.Vector2 current =
                    point +
                    tangent * along +
                    normal * lift;

                draw.AddLine(
                    previous,
                    current,
                    bridgeShadow,
                    bridgeShadowThickness);

                draw.AddLine(
                    previous,
                    current,
                    bridgeCore,
                    bridgeCoreThickness);

                previous = current;
            }
        }
    }

    private static bool ConnectionLayersVisible(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection)
    {
        if (snapshot == null ||
            connection == null)
            return false;

        EditorMapRoomSnapshot from =
            FindRoom(
                snapshot,
                connection.FromRoomIndex);
        EditorMapRoomSnapshot to =
            FindRoom(
                snapshot,
                connection.ToRoomIndex);

        return from != null &&
               to != null &&
               IsLayerVisible(from.Layer) &&
               IsLayerVisible(to.Layer);
    }

    private static void DrawExitPorts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapRoomSnapshot hoveredRoom,
        ExitPortHit hoveredPort,
        IReadOnlyList<int> candidateRooms,
        bool presentationOnly = false)
    {
        if (!WorldMapPipeLayers.RoomPipesVisible) return;

        EditorMapRoomSnapshot[] rooms =
            candidateRooms == null
                ? snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>()
                : null;
        int count = candidateRooms?.Count ?? rooms.Length;
        bool linking = linkingRoom >= 0;

        string hoveredLinkId =
            !string.IsNullOrEmpty(hoveredConnectionId)
                ? hoveredConnectionId
                : hoveredPort?.Connection?.ConnectionId ??
                  string.Empty;

        for (int i = 0; i < count; i++)
        {
            EditorMapRoomSnapshot room = candidateRooms != null
                ? FindRoom(snapshot, candidateRooms[i])
                : rooms[i];
            if (room == null || !IsLayerVisible(room.Layer)) continue;

            EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 42f)) continue;

            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (!node.Exit) continue;

                EditorMapConnectionSnapshot endpointConnection =
                    presentationOnly
                        ? null
                        : FindConnectionAtEndpoint(
                            snapshot,
                            room.RoomIndex,
                            node.NodeIndex);
                bool free =
                    presentationOnly
                        ? node.ConnectedRoomIndex < 0
                        : IsEndpointFree(
                            snapshot,
                            room.RoomIndex,
                            node);
                bool connected =
                    presentationOnly
                        ? node.ConnectedRoomIndex >= 0
                        : endpointConnection != null ||
                          node.ConnectedRoomIndex >= 0;

                Num.Vector2 point = EndpointPosition(room, node.NodeIndex, canvasMin);
                bool source =
                    !presentationOnly &&
                    room.RoomIndex == linkingRoom &&
                    node.NodeIndex == linkingNode;
                bool hovered =
                    hoveredPort != null &&
                    hoveredPort.Room.RoomIndex == room.RoomIndex &&
                    hoveredPort.Node.NodeIndex == node.NodeIndex;
                bool validTarget =
                    !presentationOnly &&
                    linking &&
                    free &&
                    room.RoomIndex != linkingRoom;
                bool selectedLink =
                    !presentationOnly &&
                    endpointConnection != null &&
                    string.Equals(
                        selectedConnectionId,
                        endpointConnection.ConnectionId,
                        StringComparison.Ordinal);
                bool hoveredLink =
                    !presentationOnly &&
                    endpointConnection != null &&
                    !string.IsNullOrEmpty(hoveredLinkId) &&
                    string.Equals(
                        hoveredLinkId,
                        endpointConnection.ConnectionId,
                        StringComparison.Ordinal);

                bool emphasized =
                    source ||
                    hovered ||
                    validTarget ||
                    selectedLink ||
                    hoveredLink;
                uint color = ShortcutGold(connected || emphasized);
                uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
                DrawShortcutSocket(draw, point, shadow, color, connected, emphasized);

                if (showPortLabels && (zoom >= 0.48f || emphasized || room.RoomIndex == snapshot.SelectedRoomIndex))
                {
                    string label = node.NodeIndex.ToString();
                    Num.Vector2 labelSize = ImGui.CalcTextSize(label);

                    // Keep the label out of the terminal stub. The previous left/right placement put
                    // the number box directly on top of the connection line, so dense exits looked
                    // like labels were part of the topology. Place labels tangentially to the room
                    // edge instead; the route owns the outward normal.
                    Num.Vector2 pad = new(3f, 1.5f);
                    Num.Vector2 labelPos =
                        PortLabelPosition(
                            point,
                            min,
                            max,
                            labelSize,
                            pad,
                            node.NodeIndex,
                            emphasized);
                    ReserveOverlayLabelRect(
                        labelPos,
                        labelSize,
                        pad);
                    draw.AddRectFilled(labelPos - pad, labelPos + labelSize + pad, shadow, 3f);
                    draw.AddRect(labelPos - pad, labelPos + labelSize + pad, ShortcutGold(false), 3f, ImDrawFlags.None, 1f);
                    draw.AddText(labelPos, ShortcutGold(true), label);
                }
            }
        }
    }

    private static void PrepareOverlayLabelLayout(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapRoomSnapshot hoveredRoom,
        IReadOnlyList<int> candidateRooms)
    {
        overlayLabelRects.Clear();

        if (snapshot == null)
            return;

        EditorMapRoomSnapshot[] rooms =
            candidateRooms == null
                ? snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>()
                : null;
        int count =
            candidateRooms?.Count ??
            rooms.Length;

        for (int i = 0; i < count; i++)
        {
            EditorMapRoomSnapshot room =
                candidateRooms != null
                    ? FindRoom(
                        snapshot,
                        candidateRooms[i])
                    : rooms[i];

            if (room == null ||
                !IsLayerVisible(
                    room.Layer))
                continue;

            EditorMapRoomVisualSnapshot visual =
                WorldMapPresentationIndex.GetRoomVisual(
                    room.RoomIndex);
            GetRoomRect(
                room,
                visual,
                canvasMin,
                out Num.Vector2 min,
                out Num.Vector2 max);

            if (!Intersects(
                    min,
                    max,
                    canvasMin,
                    canvasMin + canvasSize,
                    48f))
                continue;

            bool selected =
                room.RoomIndex ==
                snapshot.SelectedRoomIndex;
            bool hovered =
                ReferenceEquals(
                    room,
                    hoveredRoom);

            if (zoom >= 0.34f ||
                selected ||
                hovered)
            {
                Num.Vector2 size =
                    ImGui.CalcTextSize(
                        room.Name);
                Num.Vector2 pos =
                    new(
                        (min.X +
                         max.X -
                         size.X) *
                        0.5f,
                        min.Y -
                        size.Y -
                        3f);

                ReserveOverlayLabelRect(
                    pos,
                    size,
                    new Num.Vector2(
                        2f,
                        1f));
            }

            if (!showSubregionLabels ||
                zoom < 0.75f ||
                string.IsNullOrEmpty(
                    room.Subregion))
                continue;

            string meta =
                MapRoomLayer.Label(
                    room.Layer) +
                " | " +
                room.Subregion;
            if (room.OffScreenDen)
                meta += " | DEN";

            Num.Vector2 metaSize =
                ImGui.CalcTextSize(
                    meta);
            Num.Vector2 metaPos =
                new(
                    (min.X +
                     max.X -
                     metaSize.X) *
                    0.5f,
                    max.Y + 2f);

            ReserveOverlayLabelRect(
                metaPos,
                metaSize,
                new Num.Vector2(
                    2f,
                    1f));
        }
    }

    private static Num.Vector2 PortLabelPosition(
        Num.Vector2 point,
        Num.Vector2 roomMin,
        Num.Vector2 roomMax,
        Num.Vector2 labelSize,
        Num.Vector2 pad,
        int nodeIndex,
        bool emphasized)
    {
        float left =
            Math.Abs(
                point.X -
                roomMin.X);
        float right =
            Math.Abs(
                roomMax.X -
                point.X);
        float top =
            Math.Abs(
                point.Y -
                roomMin.Y);
        float bottom =
            Math.Abs(
                roomMax.Y -
                point.Y);
        float nearest =
            Math.Min(
                Math.Min(
                    left,
                    right),
                Math.Min(
                    top,
                    bottom));

        bool verticalSide =
            nearest == left ||
            nearest == right;
        float gap =
            emphasized
                ? 9f
                : 7f;
        float primarySign =
            (nodeIndex & 1) != 0
                ? 1f
                : -1f;

        for (int attempt = 0;
             attempt < 6;
             attempt++)
        {
            float sign =
                (attempt & 1) == 0
                    ? primarySign
                    : -primarySign;
            int ring =
                attempt / 2;
            float extra =
                ring *
                10f;

            Num.Vector2 candidate;

            if (verticalSide)
            {
                float centerY =
                    point.Y +
                    sign *
                    (labelSize.Y *
                     0.5f +
                     gap +
                     extra);
                candidate =
                    new Num.Vector2(
                        point.X -
                        labelSize.X *
                        0.5f,
                        centerY -
                        labelSize.Y *
                        0.5f);
            }
            else
            {
                float centerX =
                    point.X +
                    sign *
                    (labelSize.X *
                     0.5f +
                     gap +
                     extra);
                candidate =
                    new Num.Vector2(
                        centerX -
                        labelSize.X *
                        0.5f,
                        point.Y -
                        labelSize.Y *
                        0.5f);
            }

            if (!OverlayLabelRectOccupied(
                    candidate,
                    labelSize,
                    pad))
                return candidate;
        }

        // Deterministic far fallback: never place the label back on the terminal line merely
        // because the local cluster is crowded.
        float fallbackOffset =
            gap +
            30f;

        if (verticalSide)
        {
            return new Num.Vector2(
                point.X -
                labelSize.X *
                0.5f,
                point.Y +
                primarySign *
                fallbackOffset -
                labelSize.Y *
                0.5f);
        }

        return new Num.Vector2(
            point.X +
            primarySign *
            fallbackOffset -
            labelSize.X *
            0.5f,
            point.Y -
            labelSize.Y *
            0.5f);
    }

    private static Num.Vector2 PointLabelPosition(
        Num.Vector2 point,
        Num.Vector2 roomMin,
        Num.Vector2 roomMax,
        Num.Vector2 labelSize,
        Num.Vector2 pad,
        int nodeIndex)
    {
        bool preferLeft =
            point.X <=
            (roomMin.X +
             roomMax.X) *
            0.5f;
        bool preferUp =
            point.Y <=
            (roomMin.Y +
             roomMax.Y) *
            0.5f;

        float baseGap =
            13f +
            ((nodeIndex & 1) != 0
                ? 2f
                : 0f);

        for (int ring = 0;
             ring < 2;
             ring++)
        {
            float gap =
                baseGap +
                ring *
                9f;

            for (int d = 0;
                 d < 4;
                 d++)
            {
                Num.Vector2 direction;
                switch (d)
                {
                    case 0:
                        direction =
                            preferLeft
                                ? new Num.Vector2(-1f, 0f)
                                : new Num.Vector2(1f, 0f);
                        break;
                    case 1:
                        direction =
                            preferLeft
                                ? new Num.Vector2(1f, 0f)
                                : new Num.Vector2(-1f, 0f);
                        break;
                    case 2:
                        direction =
                            preferUp
                                ? new Num.Vector2(0f, -1f)
                                : new Num.Vector2(0f, 1f);
                        break;
                    default:
                        direction =
                            preferUp
                                ? new Num.Vector2(0f, 1f)
                                : new Num.Vector2(0f, -1f);
                        break;
                }
                Num.Vector2 center =
                    point +
                    new Num.Vector2(
                        direction.X *
                        (gap +
                         labelSize.X *
                         0.5f),
                        direction.Y *
                        (gap +
                         labelSize.Y *
                         0.5f));
                Num.Vector2 candidate =
                    center -
                    labelSize *
                    0.5f;

                if (!OverlayLabelRectOccupied(
                        candidate,
                        labelSize,
                        pad))
                    return candidate;
            }
        }

        return point +
               new Num.Vector2(
                   preferLeft
                       ? -labelSize.X - 31f
                       : 31f,
                   -labelSize.Y *
                   0.5f);
    }

    private static bool OverlayLabelRectOccupied(
        Num.Vector2 position,
        Num.Vector2 size,
        Num.Vector2 pad)
    {
        Num.Vector4 rect =
            OverlayLabelRect(
                position,
                size,
                pad);

        for (int i = 0;
             i < overlayLabelRects.Count;
             i++)
        {
            Num.Vector4 occupied =
                overlayLabelRects[i];

            if (rect.Z <
                    occupied.X ||
                rect.X >
                    occupied.Z ||
                rect.W <
                    occupied.Y ||
                rect.Y >
                    occupied.W)
                continue;

            return true;
        }

        return false;
    }

    private static void ReserveOverlayLabelRect(
        Num.Vector2 position,
        Num.Vector2 size,
        Num.Vector2 pad)
    {
        overlayLabelRects.Add(
            OverlayLabelRect(
                position,
                size,
                pad));
    }

    private static Num.Vector4 OverlayLabelRect(
        Num.Vector2 position,
        Num.Vector2 size,
        Num.Vector2 pad)
    {
        const float separation = 2f;

        return new Num.Vector4(
            position.X -
            pad.X -
            separation,
            position.Y -
            pad.Y -
            separation,
            position.X +
            size.X +
            pad.X +
            separation,
            position.Y +
            size.Y +
            pad.Y +
            separation);
    }

    private static void DrawCreatureShortcuts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        IReadOnlyList<int> candidateRooms)
    {
        if (!WorldMapPipeLayers.CreaturePipesVisible) return;

        EditorMapRoomSnapshot[] rooms =
            candidateRooms == null
                ? snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>()
                : null;
        int count = candidateRooms?.Count ?? rooms.Length;

        uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
        uint labelBorder = ImGui.GetColorU32(new Num.Vector4(0.10f, 0.72f, 0.28f, 1f));
        uint labelText = ImGui.GetColorU32(new Num.Vector4(0.32f, 1.00f, 0.46f, 1f));

        for (int i = 0; i < count; i++)
        {
            EditorMapRoomSnapshot room = candidateRooms != null
                ? FindRoom(snapshot, candidateRooms[i])
                : rooms[i];
            if (room == null || !IsLayerVisible(room.Layer)) continue;

            WorldMapShortcutPresentation.ShortcutMarker[] holes =
                WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex);
            if (holes == null || holes.Length == 0) continue;

            EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 32f)) continue;

            for (int h = 0; h < holes.Length; h++)
            {
                WorldMapShortcutPresentation.ShortcutMarker hole = holes[h];
                Num.Vector2 point = LocalToScreen(min, visual, hole.X, hole.Y);
                DrawCreatureShortcutSocket(draw, point, shadow);

                if (hole.NodeIndex < 0 || (zoom < 0.48f && room.RoomIndex != snapshot.SelectedRoomIndex))
                    continue;

                string label = hole.NodeIndex.ToString();
                Num.Vector2 labelSize = ImGui.CalcTextSize(label);
                Num.Vector2 pad = new(4f, 2f);
                Num.Vector2 labelPos =
                    PointLabelPosition(
                        point,
                        min,
                        max,
                        labelSize,
                        pad,
                        hole.NodeIndex);
                ReserveOverlayLabelRect(
                    labelPos,
                    labelSize,
                    pad);
                draw.AddRectFilled(labelPos - pad, labelPos + labelSize + pad, shadow, 3f);
                draw.AddRect(labelPos - pad, labelPos + labelSize + pad, labelBorder, 3f, ImDrawFlags.None, 1f);
                draw.AddText(labelPos, labelText, label);
            }
        }
    }

    private static void HandleInteraction(
        EditorMapPresentationSnapshot snapshot,
        bool canvasHovered,
        ImGuiIOPtr io,
        EditorMapRoomSnapshot hoveredRoom,
        ExitPortHit hoveredPort,
        EdgeHit hoveredEdge,
        bool retainedConnectionsPresented)
    {
        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredPort != null)
            {
                SelectRoom(hoveredPort.Room.RoomIndex);
                draggingRoom = -1;
                selectedConnectionId = string.Empty;
                if (hoveredPort.Free && linkingRoom < 0)
                {
                    linkingRoom = hoveredPort.Room.RoomIndex;
                    linkingNode = hoveredPort.Node.NodeIndex;
                }
                else if (!hoveredPort.Free && hoveredPort.Connection != null)
                {
                    selectedConnectionId = hoveredPort.Connection.ConnectionId;
                }
            }
            else if (retainedConnectionsPresented && !string.IsNullOrEmpty(hoveredConnectionId))
            {
                selectedConnectionId = hoveredConnectionId;
                EditorMapConnectionSnapshot connection =
                    FindConnection(snapshot, hoveredConnectionId);
                if (connection != null)
                    SelectRoom(connection.FromRoomIndex);
                draggingRoom = -1;
            }
            else if (hoveredEdge?.Connection != null && showConnections)
            {
                selectedConnectionId = hoveredEdge.Connection.ConnectionId;
                SelectRoom(hoveredEdge.Connection.FromRoomIndex);
                draggingRoom = -1;
            }
            else if (linkingRoom < 0 && hoveredRoom != null)
            {
                selectedConnectionId = string.Empty;
                SelectRoom(hoveredRoom.RoomIndex);
                draggingRoom = hoveredRoom.RoomIndex;
                dragStartMouse = io.MousePos;
                dragStartWorld = GetPosition(hoveredRoom);
            }
            else if (linkingRoom < 0)
            {
                selectedConnectionId = string.Empty;
                SelectRoom(-1);
                draggingRoom = -1;
            }
        }

        if (linkingRoom >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (hoveredPort != null && hoveredPort.Free && hoveredPort.Room.RoomIndex != linkingRoom)
                CompleteLink(snapshot, hoveredPort);
            CancelLink();
        }
        if (linkingRoom >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) CancelLink();

        if (draggingRoom >= 0)
        {
            EditorMapRoomSnapshot dragged = FindRoom(snapshot, draggingRoom);
            if (dragged != null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                SetLocalPosition(
                    draggingRoom,
                    dragStartWorld + (io.MousePos - dragStartMouse) / zoom);
            else
            {
                if (dragged != null && localPositions.TryGetValue(draggingRoom, out Num.Vector2 final))
                    SetPosition(draggingRoom, final);
                draggingRoom = -1;
            }
        }
    }

    private static void DrawRetainedConnectionTooltip(
        EditorMapPresentationSnapshot snapshot,
        string connectionId)
    {
        EditorMapConnectionSnapshot connection =
            FindConnection(snapshot, connectionId);
        if (connection == null) return;

        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        if (a == null || b == null) return;

        string direction = connection.Direction switch
        {
            WorldConnectionDirection.AToB => "A > B",
            WorldConnectionDirection.BToA => "A < B",
            _ => "Both"
        };

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(
            a.Name + ":" + connection.FromNodeIndex +
            "  " + direction + "  " +
            b.Name + ":" +
            (connection.ToNodeIndex >= 0
                ? connection.ToNodeIndex.ToString()
                : "?"));

        DrawRetainedWorldToken(
            snapshot.RegionName,
            a.Name,
            connection.FromNodeIndex);
        if (connection.ToNodeIndex >= 0)
            DrawRetainedWorldToken(
                snapshot.RegionName,
                b.Name,
                connection.ToNodeIndex);

        if (connection.Ambiguous)
            ImGui.TextDisabled(
                DevToolUiSettings.T(
                    "目标出口不明确",
                    "Ambiguous target exit"));
        ImGui.EndTooltip();
    }

    private static void DrawRetainedWorldToken(
        string regionName,
        string roomName,
        int nodeIndex)
    {
        if (!WorldTextRegistry.TryGetConnectionEndpoint(
                regionName,
                roomName,
                nodeIndex,
                out string destinationRoom,
                out int destinationNode))
            return;

        string token = destinationNode >= 0
            ? "<" + destinationNode + ">" + destinationRoom
            : destinationRoom;
        ImGui.TextDisabled(
            roomName + ":" + nodeIndex + " -> " + token);
    }

    private static void DrawLinkPreview(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 mouse,
        ExitPortHit hoveredPort)
    {
        if (linkingRoom < 0 || linkingNode < 0) return;
        EditorMapRoomSnapshot sourceRoom = FindRoom(snapshot, linkingRoom);
        if (sourceRoom == null) return;
        Num.Vector2 source = EndpointPosition(sourceRoom, linkingNode, canvasMin);
        bool validTarget = hoveredPort != null && hoveredPort.Free && hoveredPort.Room.RoomIndex != linkingRoom;
        Num.Vector2 target = validTarget ? hoveredPort.Position : mouse;
        uint color = ConnectionColor(linkDirection);
        uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
        DrawConnectionStroke(draw, source, target, shadow, color, 6.5f, 2.5f, linkDirection, false);
        DrawShortcutSocket(draw, source, shadow, ShortcutGold(true), true, true);
        if (validTarget)
            DrawShortcutSocket(draw, target, shadow, ShortcutGold(true), false, true);
    }

    private static EditorMapRoomSnapshot FindHoveredRoom(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse)
    {
        float safeZoom = Math.Max(0.0001f, zoom);
        Num.Vector2 mapPoint = (mouse - canvasMin - pan) / safeZoom;
        int currentLayerMask = CurrentLayerMask();
        if (WorldMapRetainedV2Runtime.TryHitRoom(
                mapPoint,
                currentLayerMask,
                out int retainedRoomIndex))
        {
            EnsureHoverRoomLookup(snapshot);
            return hoverRoomLookup.TryGetValue(
                retainedRoomIndex,
                out EditorMapRoomSnapshot retainedRoom)
                ? retainedRoom
                : null;
        }

        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = rooms.Length - 1; i >= 0; i--)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;
            EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 8f)) continue;
            if (Contains(min, max, mouse)) return room;
        }
        return null;
    }

    private static void EnsureHoverRoomLookup(EditorMapPresentationSnapshot snapshot)
    {
        if (ReferenceEquals(hoverIndexedSnapshot, snapshot)) return;
        hoverIndexedSnapshot = snapshot;
        hoverRoomLookup.Clear();

        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room != null) hoverRoomLookup[room.RoomIndex] = room;
        }
    }

    private static ExitPortHit ResolveNavigationHoverPort(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin)
    {
        if (navigationHoverPortRoomIndex < 0 ||
            navigationHoverPortNodeIndex < 0)
            return null;

        EditorMapRoomSnapshot room =
            FindRoom(
                snapshot,
                navigationHoverPortRoomIndex);
        if (room == null)
            return null;

        EditorMapRoomNodeSnapshot[] nodes =
            room.Nodes ??
            Array.Empty<EditorMapRoomNodeSnapshot>();
        EditorMapRoomNodeSnapshot node =
            null;

        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i]?.NodeIndex !=
                navigationHoverPortNodeIndex)
                continue;

            node =
                nodes[i];
            break;
        }

        if (node == null ||
            !node.Exit)
            return null;

        EditorMapConnectionSnapshot connection =
            string.IsNullOrEmpty(
                navigationHoverPortConnectionId)
                ? null
                : FindConnection(
                    snapshot,
                    navigationHoverPortConnectionId);

        return new ExitPortHit
        {
            Room = room,
            Node = node,
            Position = EndpointPosition(
                room,
                node.NodeIndex,
                canvasMin),
            Free = node.ConnectedRoomIndex < 0,
            Connection = connection
        };
    }

    private static ExitPortHit FindHoveredExitPort(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse,
        EditorMapRoomSnapshot hoveredRoom)
    {
        float safeZoom = Math.Max(0.0001f, zoom);
        Num.Vector2 worldPoint = (mouse - canvasMin - pan) / safeZoom;
        float worldRadius = 36f / safeZoom;
        Num.Vector2 radius = new(worldRadius, worldRadius);

        bool useSpatial = WorldMapRetainedV2Runtime.QueryRooms(
            worldPoint - radius,
            worldPoint + radius,
            CurrentLayerMask(),
            retainedHoverRoomIds);

        EditorMapRoomSnapshot[] rooms =
            useSpatial
                ? null
                : snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        int count = useSpatial ? retainedHoverRoomIds.Count : rooms.Length;

        ExitPortHit best = null;
        float bestDistanceSq = 400f;
        for (int i = 0; i < count; i++)
        {
            EditorMapRoomSnapshot room = useSpatial
                ? FindRoom(snapshot, retainedHoverRoomIds[i])
                : rooms[i];
            if (room == null || !IsLayerVisible(room.Layer)) continue;
            EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 36f)) continue;

            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (!node.Exit) continue;

                EditorMapConnectionSnapshot endpointConnection = FindConnectionAtEndpoint(snapshot, room.RoomIndex, node.NodeIndex);
                bool free = IsEndpointFree(snapshot, room.RoomIndex, node);
                Num.Vector2 point = EndpointPosition(room, node.NodeIndex, canvasMin);
                float distanceSq = Num.Vector2.DistanceSquared(point, mouse);
                if (distanceSq > bestDistanceSq) continue;
                bestDistanceSq = distanceSq;
                best = new ExitPortHit
                {
                    Room = room,
                    Node = node,
                    Position = point,
                    Free = free,
                    Connection = endpointConnection
                };
            }
        }
        return best;
    }

    private static EdgeHit FindHoveredEdge(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse,
        bool skipRetainedRoutes)
    {
        if (!showConnections) return null;
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        EdgeHit best = null;
        float thresholdSq = 169f;
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null)
                continue;

            if (skipRetainedRoutes &&
                WorldMapRetainedV2Runtime.IsConnectionRetainedOnSurface(
                    connection.ConnectionId) &&
                WorldMapRetainedV2Runtime.TryGetConnectionRoutePoints(
                    connection.ConnectionId,
                    out Num.Vector2[] retainedHitRoute) &&
                retainedHitRoute != null &&
                retainedHitRoute.Length >= 2)
            {
                // The retained hit index and the visible retained surface now refer to the same
                // route geometry, so the immediate hit path would only duplicate work. During the
                // brief surface/index handoff, fall through to the immediate path instead of making
                // the connection temporarily unhoverable.
                continue;
            }

            if (!BuildImmediateConnectionPath(
                    snapshot,
                    connection,
                    canvasMin,
                    connectionPathScratch))
                continue;

            if (!PathNearCanvas(
                    connectionPathScratch,
                    canvasMin,
                    canvasMin + canvasSize,
                    30f))
                continue;

            float distanceSq =
                DistanceToPathSquared(
                    mouse,
                    connectionPathScratch);
            if (distanceSq > thresholdSq ||
                best != null && distanceSq >= best.DistanceSq)
                continue;

            best = new EdgeHit
            {
                Connection = connection,
                DistanceSq = distanceSq
            };
        }
        return best;
    }

    private static bool BuildImmediateConnectionPath(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        List<Num.Vector2> output) =>
        BuildImmediateConnectionPath(
            snapshot,
            connection,
            canvasMin,
            output,
            out _);

    private static bool BuildImmediateConnectionPath(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        List<Num.Vector2> output,
        out bool usedRetainedRoute)
    {
        usedRetainedRoute = false;
        output.Clear();
        if (connection == null ||
            connection.FromNodeIndex < 0 ||
            connection.ToNodeIndex < 0)
            return false;

        EditorMapRoomSnapshot roomA =
            FindRoom(
                snapshot,
                connection.FromRoomIndex);
        EditorMapRoomSnapshot roomB =
            FindRoom(
                snapshot,
                connection.ToRoomIndex);

        if (roomA == null ||
            roomB == null ||
            !IsLayerVisible(roomA.Layer) ||
            !IsLayerVisible(roomB.Layer))
            return false;

        if (WorldMapRetainedV2Runtime.TryGetConnectionRoutePoints(
                connection.ConnectionId,
                out Num.Vector2[] retainedPoints) &&
            retainedPoints != null &&
            retainedPoints.Length >= 2)
        {
            for (int i = 0; i < retainedPoints.Length; i++)
                AppendDistinct(
                    output,
                    ToScreen(canvasMin, retainedPoints[i]));
            usedRetainedRoute = output.Count >= 2;
            return usedRetainedRoute;
        }

        Num.Vector2 a =
            EndpointPosition(
                roomA,
                connection.FromNodeIndex,
                canvasMin);
        Num.Vector2 b =
            connection.ToNodeIndex >= 0
                ? EndpointPosition(
                    roomB,
                    connection.ToNodeIndex,
                    canvasMin)
                : RoomCenter(roomB, canvasMin);

        EditorMapRoomVisualSnapshot visualA =
            WorldMapPresentationIndex.GetRoomVisual(roomA.RoomIndex);
        EditorMapRoomVisualSnapshot visualB =
            WorldMapPresentationIndex.GetRoomVisual(roomB.RoomIndex);
        GetRoomRect(
            roomA,
            visualA,
            canvasMin,
            out Num.Vector2 aMin,
            out Num.Vector2 aMax);
        GetRoomRect(
            roomB,
            visualB,
            canvasMin,
            out Num.Vector2 bMin,
            out Num.Vector2 bMax);

        Num.Vector2 dirA =
            InferScreenPortDirection(a, aMin, aMax);
        Num.Vector2 dirB =
            InferScreenPortDirection(b, bMin, bMax);

        AppendDistinct(output, a);

        if (Math.Abs(a.X - b.X) <= 0.5f ||
            Math.Abs(a.Y - b.Y) <= 0.5f)
        {
            AppendDistinct(output, b);
            return true;
        }

        bool aHorizontal = Math.Abs(dirA.X) > 0.5f;
        bool bHorizontal = Math.Abs(dirB.X) > 0.5f;

        if (aHorizontal && bHorizontal)
        {
            float midX = (a.X + b.X) * 0.5f;
            AppendDistinct(
                output,
                new Num.Vector2(midX, a.Y));
            AppendDistinct(
                output,
                new Num.Vector2(midX, b.Y));
        }
        else if (!aHorizontal && !bHorizontal)
        {
            float midY = (a.Y + b.Y) * 0.5f;
            AppendDistinct(
                output,
                new Num.Vector2(a.X, midY));
            AppendDistinct(
                output,
                new Num.Vector2(b.X, midY));
        }
        else if (aHorizontal)
        {
            AppendDistinct(
                output,
                new Num.Vector2(b.X, a.Y));
        }
        else
        {
            AppendDistinct(
                output,
                new Num.Vector2(a.X, b.Y));
        }

        AppendDistinct(output, b);
        return output.Count >= 2;
    }

    private static Num.Vector2 InferScreenPortDirection(
        Num.Vector2 point,
        Num.Vector2 roomMin,
        Num.Vector2 roomMax)
    {
        float left = Math.Abs(point.X - roomMin.X);
        float right = Math.Abs(roomMax.X - point.X);
        float top = Math.Abs(point.Y - roomMin.Y);
        float bottom = Math.Abs(roomMax.Y - point.Y);
        float best =
            Math.Min(
                Math.Min(left, right),
                Math.Min(top, bottom));

        if (best == left)
            return new Num.Vector2(-1f, 0f);
        if (best == right)
            return new Num.Vector2(1f, 0f);
        if (best == top)
            return new Num.Vector2(0f, -1f);
        return new Num.Vector2(0f, 1f);
    }

    private static void AppendDistinct(
        List<Num.Vector2> points,
        Num.Vector2 point)
    {
        if (points.Count == 0 ||
            Num.Vector2.DistanceSquared(
                points[points.Count - 1],
                point) >= 0.25f)
            points.Add(point);
    }

    private static bool PathNearCanvas(
        IReadOnlyList<Num.Vector2> points,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax,
        float margin)
    {
        if (points == null || points.Count < 2)
            return false;

        for (int i = 0; i < points.Count - 1; i++)
        {
            if (SegmentNearCanvas(
                    points[i],
                    points[i + 1],
                    canvasMin,
                    canvasMax,
                    margin))
                return true;
        }

        return false;
    }

    private static float DistanceToPathSquared(
        Num.Vector2 point,
        IReadOnlyList<Num.Vector2> points)
    {
        float best = float.MaxValue;
        if (points == null)
            return best;

        for (int i = 0; i < points.Count - 1; i++)
        {
            float distance =
                DistanceToSegmentSquared(
                    point,
                    points[i],
                    points[i + 1]);
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private static bool TryConnectionSegment(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        out Num.Vector2 a,
        out Num.Vector2 b)
    {
        a = Num.Vector2.Zero;
        b = Num.Vector2.Zero;
        if (connection == null || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            return false;

        EditorMapRoomSnapshot roomA = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot roomB = FindRoom(snapshot, connection.ToRoomIndex);
        if (roomA == null || roomB == null || !IsLayerVisible(roomA.Layer) || !IsLayerVisible(roomB.Layer)) return false;
        a = EndpointPosition(roomA, connection.FromNodeIndex, canvasMin);
        b = connection.ToNodeIndex >= 0
            ? EndpointPosition(roomB, connection.ToNodeIndex, canvasMin)
            : RoomCenter(roomB, canvasMin);
        return true;
    }

    private static Num.Vector2 EndpointPosition(EditorMapRoomSnapshot room, int nodeIndex, Num.Vector2 canvasMin)
    {
        EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
        Num.Vector2 roomMin = ToScreen(canvasMin, GetPosition(room));

        if (WorldMapShortcutPresentation.TryGetExitMouth(
                room.RoomIndex,
                nodeIndex,
                out WorldMapShortcutPresentation.ShortcutMarker mouth))
            return LocalToScreen(roomMin, visual, mouth.X, mouth.Y);

        EditorMapNodeVisualSnapshot[] nodes = visual.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeIndex != nodeIndex) continue;
            return LocalToScreen(roomMin, visual, nodes[i].X, nodes[i].Y);
        }

        EditorMapRoomNodeSnapshot[] roomNodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        int ordinal = 0;
        int exits = 0;
        for (int i = 0; i < roomNodes.Length; i++)
        {
            if (!roomNodes[i].Exit) continue;
            if (roomNodes[i].NodeIndex == nodeIndex) ordinal = exits;
            exits++;
        }
        GetRoomRect(room, visual, canvasMin, out Num.Vector2 fallbackMin, out Num.Vector2 roomMax);
        if (exits <= 0) return (fallbackMin + roomMax) * 0.5f;
        bool right = ordinal % 2 == 0;
        int row = ordinal / 2;
        int rows = right ? (exits + 1) / 2 : exits / 2;
        float t = (row + 1f) / (rows + 1f);
        return new Num.Vector2(right ? roomMax.X : fallbackMin.X, fallbackMin.Y + (roomMax.Y - fallbackMin.Y) * t);
    }

    private static Num.Vector2 LocalToScreen(
        Num.Vector2 roomMin,
        EditorMapRoomVisualSnapshot visual,
        float tileX,
        float tileY)
    {
        float scale = TileDisplaySize * zoom;
        return new Num.Vector2(
            roomMin.X + tileX * scale,
            roomMin.Y + (visual.HeightTiles - tileY) * scale);
    }

    private static Num.Vector2 RoomCenter(EditorMapRoomSnapshot room, Num.Vector2 canvasMin)
    {
        EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
        GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
        return (min + max) * 0.5f;
    }

    private static void GetRoomRect(
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 canvasMin,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        min = ToScreen(canvasMin, GetPosition(room));
        float scale = TileDisplaySize * zoom;
        max = min + new Num.Vector2(
            Math.Max(1f, visual.WidthTiles) * scale,
            Math.Max(1f, visual.HeightTiles) * scale);
    }

    private static void CompleteLink(EditorMapPresentationSnapshot snapshot, ExitPortHit target)
    {
        EditorMapRoomSnapshot source = FindRoom(snapshot, linkingRoom);
        if (source == null || target?.Room == null || target.Node == null) return;
        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.CreateConnection,
            region: snapshot.RegionName,
            roomA: source.Name,
            nodeA: linkingNode,
            roomB: target.Room.Name,
            nodeB: target.Node.NodeIndex,
            direction: linkDirection));
    }

    private static void HandleDelete(EditorMapPresentationSnapshot snapshot)
    {
        if (!showConnections || string.IsNullOrEmpty(selectedConnectionId)) return;
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput || !ImGui.IsKeyPressed(ImGuiKey.Delete)) return;
        EditorMapConnectionSnapshot connection = FindConnection(snapshot, selectedConnectionId);
        if (connection == null || connection.Ambiguous || connection.ToNodeIndex < 0) return;
        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        if (a == null || b == null) return;

        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.DeleteConnection,
            region: snapshot.RegionName,
            edgeId: ExplicitEdgeId(connection.ConnectionId),
            roomA: a.Name,
            nodeA: connection.FromNodeIndex,
            roomB: b.Name,
            nodeB: connection.ToNodeIndex));
        selectedConnectionId = string.Empty;
        EditorShortcutFeedback.PublishCustom(
            "已删除地图连接",
            "World Map connection deleted",
            "Delete",
            true,
            EditorShortcutFeedbackVisual.Delete);
    }

    private static bool IsEndpointFree(EditorMapPresentationSnapshot snapshot, int roomIndex, EditorMapRoomNodeSnapshot node) =>
        WorldMapPresentationIndex.IsEndpointFree(snapshot, roomIndex, node);

    private static EditorMapConnectionSnapshot FindConnectionAtEndpoint(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex) =>
        WorldMapPresentationIndex.FindConnectionAtEndpoint(snapshot, roomIndex, nodeIndex);

    private static void DrawShortcutSocket(
        ImDrawListPtr draw,
        Num.Vector2 point,
        uint shadow,
        uint color,
        bool connected,
        bool emphasized)
    {
        float iconScale = zoom < 0.30f ? 0.92f : 1f;
        float half = (emphasized ? 10.2f : connected ? 9.2f : 8.4f) * iconScale;
        float halo = half + 3.3f * iconScale;
        float rounding = Math.Max(2.5f, 3.6f * iconScale);
        Num.Vector2 haloSize = new(halo, halo);
        Num.Vector2 bodySize = new(half, half);

        draw.AddRectFilled(point - haloSize, point + haloSize, shadow, rounding + 1.5f);
        uint fill = connected || emphasized ? color : ShortcutGoldDark();
        draw.AddRectFilled(point - bodySize, point + bodySize, fill, rounding);
        draw.AddRect(point - bodySize, point + bodySize, color, rounding, ImDrawFlags.None, Math.Max(2f, 2.4f * iconScale));

        float innerHalf = half - 2.7f * iconScale;
        Num.Vector2 inner = new(innerHalf, innerHalf);
        draw.AddRect(point - inner, point + inner, ShortcutGold(false), Math.Max(1.5f, rounding - 1f), ImDrawFlags.None, Math.Max(1f, 1.2f * iconScale));

        float holeHalf = (connected ? 3.7f : 3.25f) * iconScale;
        Num.Vector2 hole = new(holeHalf, holeHalf);
        draw.AddRectFilled(point - hole, point + hole, shadow, Math.Max(1f, 1.8f * iconScale));

        float notchHalf = Math.Max(1f, 1.35f * iconScale);
        float notchDepth = Math.Max(2.7f, 3.7f * iconScale);
        draw.AddRectFilled(
            new Num.Vector2(point.X - notchHalf, point.Y - half - 0.5f),
            new Num.Vector2(point.X + notchHalf, point.Y - half + notchDepth),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X - notchHalf, point.Y + half - notchDepth),
            new Num.Vector2(point.X + notchHalf, point.Y + half + 0.5f),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X - half - 0.5f, point.Y - notchHalf),
            new Num.Vector2(point.X - half + notchDepth, point.Y + notchHalf),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X + half - notchDepth, point.Y - notchHalf),
            new Num.Vector2(point.X + half + 0.5f, point.Y + notchHalf),
            shadow);

        if (emphasized)
            draw.AddRect(point - haloSize, point + haloSize, ShortcutGold(true), rounding + 1.5f, ImDrawFlags.None, Math.Max(1.6f, 2f * iconScale));
    }

    private static void DrawCreatureShortcutSocket(ImDrawListPtr draw, Num.Vector2 point, uint shadow)
    {
        float iconScale = zoom < 0.30f ? 0.90f : 1f;
        float half = 7.8f * iconScale;
        float halo = half + 2.8f * iconScale;
        float rounding = Math.Max(2.2f, 3.2f * iconScale);
        Num.Vector2 haloSize = new(halo, halo);
        Num.Vector2 bodySize = new(half, half);

        uint bright = CreatureShortcutGreen(true);
        uint mid = CreatureShortcutGreen(false);
        uint dark = CreatureShortcutGreenDark();

        draw.AddRectFilled(point - haloSize, point + haloSize, shadow, rounding + 1.2f);
        draw.AddRectFilled(point - bodySize, point + bodySize, dark, rounding);
        draw.AddRect(point - bodySize, point + bodySize, bright, rounding, ImDrawFlags.None, Math.Max(2f, 2.3f * iconScale));

        float innerHalf = half - 2.5f * iconScale;
        Num.Vector2 inner = new(innerHalf, innerHalf);
        draw.AddRect(point - inner, point + inner, mid, Math.Max(1.2f, rounding - 1f), ImDrawFlags.None, Math.Max(1f, 1.2f * iconScale));

        float holeRadius = 2.8f * iconScale;
        draw.AddCircleFilled(point, holeRadius, shadow, 12);

        float notchHalf = Math.Max(0.9f, 1.15f * iconScale);
        float notchDepth = Math.Max(2.4f, 3.2f * iconScale);
        draw.AddRectFilled(
            new Num.Vector2(point.X - notchHalf, point.Y - half - 0.5f),
            new Num.Vector2(point.X + notchHalf, point.Y - half + notchDepth),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X - notchHalf, point.Y + half - notchDepth),
            new Num.Vector2(point.X + notchHalf, point.Y + half + 0.5f),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X - half - 0.5f, point.Y - notchHalf),
            new Num.Vector2(point.X - half + notchDepth, point.Y + notchHalf),
            shadow);
        draw.AddRectFilled(
            new Num.Vector2(point.X + half - notchDepth, point.Y - notchHalf),
            new Num.Vector2(point.X + half + 0.5f, point.Y + notchHalf),
            shadow);
    }

    private static void DrawConnectionPathStroke(
        ImDrawListPtr draw,
        IReadOnlyList<Num.Vector2> points,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        bool dashed)
    {
        if (points == null || points.Count < 2) return;
        WorldMapConnectionDrawing.RoundCorners(points, roundedConnectionScratch,
            WorldMapConnectionDrawing.CornerRadius * zoom);
        for (int i = 0; i + 1 < roundedConnectionScratch.Count; i++)
        {
            Num.Vector2 a = roundedConnectionScratch[i], b = roundedConnectionScratch[i + 1];
            if (dashed)
            {
                DrawDashedLine(draw, a, b, shadow, shadowThickness, 10f, 6f);
                DrawDashedLine(draw, a, b, core, coreThickness, 10f, 6f);
            }
            else
            {
                draw.AddLine(a, b, shadow, shadowThickness);
                draw.AddLine(a, b, core, coreThickness);
                draw.AddLine(a, b, 0xC8B5E2F8, Math.Max(0.7f, coreThickness * 0.32f));
            }
        }
    }
    private static bool TryPointOnPath(
        IReadOnlyList<Num.Vector2> points,
        float fraction,
        out Num.Vector2 point,
        out Num.Vector2 tangent)
    {
        point = Num.Vector2.Zero;
        tangent = Num.Vector2.Zero;
        if (points == null || points.Count < 2)
            return false;

        float total = PathLength(points);
        if (total <= 0.001f)
            return false;

        float target =
            total * Math.Max(0f, Math.Min(1f, fraction));
        float walked = 0f;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Num.Vector2 a = points[i];
            Num.Vector2 b = points[i + 1];
            Num.Vector2 delta = b - a;
            float length = delta.Length();
            if (length <= 0.001f)
                continue;

            if (walked + length >= target)
            {
                float t =
                    (target - walked) / length;
                point =
                    Num.Vector2.Lerp(a, b, t);
                tangent =
                    delta / length;
                return true;
            }

            walked += length;
        }

        Num.Vector2 last =
            points[points.Count - 1];
        Num.Vector2 before =
            points[points.Count - 2];
        Num.Vector2 finalDelta =
            last - before;
        float finalLength =
            finalDelta.Length();
        if (finalLength <= 0.001f)
            return false;

        point = last;
        tangent = finalDelta / finalLength;
        return true;
    }

    private static float PathLength(
        IReadOnlyList<Num.Vector2> points)
    {
        float total = 0f;
        if (points == null)
            return total;

        for (int i = 0; i < points.Count - 1; i++)
            total +=
                Num.Vector2.Distance(
                    points[i],
                    points[i + 1]);
        return total;
    }

    private static void DrawConnectionStroke(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        WorldConnectionDirection direction,
        bool dashed)
    {
        if (WorldMapPresentationCorrectness.TryDrawBidirectionalStroke(
                draw, a, b, shadow, core, shadowThickness, coreThickness, direction, dashed))
            return;

        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length <= 0.001f) return;

        if (dashed)
        {
            DrawDashedLine(draw, a, b, shadow, shadowThickness, 10f, 6f);
            DrawDashedLine(draw, a, b, core, coreThickness, 10f, 6f);
            if (length >= 25f) DrawDirectionArrows(draw, a, b, direction, shadow, core, coreThickness);
            return;
        }

        draw.AddLine(a, b, shadow, shadowThickness);
        draw.AddLine(a, b, core, coreThickness);
        if (length >= 25f) DrawDirectionArrows(draw, a, b, direction, shadow, core, coreThickness);
    }

    private static void DrawDashedLine(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint color,
        float thickness,
        float dash,
        float gap)
    {
        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length <= 0.001f) return;
        Num.Vector2 direction = delta / length;
        float step = Math.Max(1f, dash + gap);
        for (float distance = 0f; distance < length; distance += step)
        {
            float end = Math.Min(length, distance + dash);
            draw.AddLine(a + direction * distance, a + direction * end, color, thickness);
        }
    }

    private static void DrawDirectionArrows(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        WorldConnectionDirection direction,
        uint shadow,
        uint core,
        float coreThickness)
    {
        Num.Vector2 forward = b - a;
        float size = Math.Min(forward.Length() * 0.22f, Math.Max(6.5f, Math.Min(8.0f, 5.7f + coreThickness * 0.55f)));
        switch (direction)
        {
            case WorldConnectionDirection.AToB:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.58f), forward, shadow, core, size);
                break;
            case WorldConnectionDirection.BToA:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.42f), -forward, shadow, core, size);
                break;
            default:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.35f), -forward, shadow, core, size);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.65f), forward, shadow, core, size);
                break;
        }
    }

    private static void DrawArrowHead(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 direction,
        uint shadow,
        uint core,
        float size)
    {
        float length = direction.Length();
        if (length <= 0.001f) return;
        Num.Vector2 forward = direction / length;
        Num.Vector2 normal = new(-forward.Y, forward.X);
        DrawArrowTriangle(draw, tip, forward, normal, shadow, size + 2.5f);
        DrawArrowTriangle(draw, tip, forward, normal, core, size);
    }

    private static void DrawArrowTriangle(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 forward,
        Num.Vector2 normal,
        uint color,
        float size)
    {
        // Kept under the old helper name to avoid duplicating call sites: visually this is now a
        // compact open chevron, not a filled triangle. Direction remains readable without hiding
        // the route lane, endpoint code or a neighbouring connection.
        Num.Vector2 baseCenter =
            tip -
            forward *
            (size * 0.72f);
        float wing =
            size * 0.42f;
        float thickness =
            Math.Max(
                1.0f,
                size * 0.16f);

        draw.AddLine(
            baseCenter + normal * wing,
            tip,
            color,
            thickness);
        draw.AddLine(
            baseCenter - normal * wing,
            tip,
            color,
            thickness);
    }

    private static void SynchronizeRegion(EditorMapPresentationSnapshot snapshot)
    {
        string next = snapshot.RegionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;
        region = next;
        synchronizedPositionRooms = null;
        retainedVisibleRoomIds.Clear();
        if (localPositions.Count > 0)
        {
            localPositions.Clear();
            unchecked { localPositionRevision++; }
        }
        pan = Num.Vector2.Zero;
        zoom = 1f;
        fitRequested = true;
        draggingRoom = -1;
        linkingRoom = -1;
        linkingNode = -1;
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
    }

    private static void SynchronizePositions(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (ReferenceEquals(synchronizedPositionRooms, rooms))
            return;

        synchronizedPositionRooms = rooms;
        HashSet<int> alive = new();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null) continue;

            alive.Add(room.RoomIndex);
            if (room.RoomIndex == draggingRoom) continue;
            SetLocalPosition(room.RoomIndex, new Num.Vector2(room.X, room.Y));
        }

        if (localPositions.Count == alive.Count) return;

        List<int> remove = new();
        foreach (int key in localPositions.Keys)
            if (!alive.Contains(key)) remove.Add(key);

        if (remove.Count == 0) return;
        for (int i = 0; i < remove.Count; i++)
            localPositions.Remove(remove[i]);
        unchecked { localPositionRevision++; }
    }

    private static bool SetLocalPosition(int roomIndex, Num.Vector2 position)
    {
        if (localPositions.TryGetValue(roomIndex, out Num.Vector2 current) &&
            Num.Vector2.DistanceSquared(current, position) <= 0.0001f)
            return false;

        localPositions[roomIndex] = position;
        unchecked { localPositionRevision++; }
        return true;
    }

    private static void SynchronizeLinkState(EditorMapPresentationSnapshot snapshot)
    {
        if (linkingRoom < 0) return;
        EditorMapRoomSnapshot room = FindRoom(snapshot, linkingRoom);
        EditorMapRoomNodeSnapshot node = FindNode(room, linkingNode);
        if (room == null || node == null || !IsEndpointFree(snapshot, linkingRoom, node)) CancelLink();
    }

    private static void SynchronizeConnectionSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(selectedConnectionId) && FindConnection(snapshot, selectedConnectionId) == null)
            selectedConnectionId = string.Empty;
    }

    private static void FocusRoomOnCanvas(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasSize,
        int roomIndex)
    {
        EditorMapRoomSnapshot room = FindRoom(snapshot, roomIndex);
        if (room == null)
            return;

        if (room.Layer >= 0 && room.Layer < layerVisible.Length)
            layerVisible[room.Layer] = true;

        EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
        Num.Vector2 position = GetPosition(room);
        Num.Vector2 size = new(
            Math.Max(1f, visual.WidthTiles) * TileDisplaySize,
            Math.Max(1f, visual.HeightTiles) * TileDisplaySize);
        Num.Vector2 center = position + size * 0.5f;

        // Preserve the developer's zoom level; double-click is navigation, not a zoom reset.
        pan = canvasSize * 0.5f - center * zoom;
    }

    private static void Fit(EditorMapPresentationSnapshot snapshot, Num.Vector2 canvasSize)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        bool found = false;
        Num.Vector2 min = new(float.MaxValue, float.MaxValue);
        Num.Vector2 max = new(float.MinValue, float.MinValue);
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;
            EditorMapRoomVisualSnapshot visual = WorldMapPresentationIndex.GetRoomVisual(room.RoomIndex);
            Num.Vector2 p = GetPosition(room);
            Num.Vector2 size = new(
                Math.Max(1f, visual.WidthTiles) * TileDisplaySize,
                Math.Max(1f, visual.HeightTiles) * TileDisplaySize);
            min = Num.Vector2.Min(min, p);
            max = Num.Vector2.Max(max, p + size);
            found = true;
        }

        if (!found)
        {
            zoom = 1f;
            pan = canvasSize * 0.5f;
            return;
        }

        Num.Vector2 span = Num.Vector2.Max(max - min, new Num.Vector2(1f, 1f));
        float availableX = Math.Max(100f, canvasSize.X - 90f);
        float availableY = Math.Max(100f, canvasSize.Y - 90f);
        zoom = Math.Max(MinZoom, Math.Min(2.25f, Math.Min(availableX / span.X, availableY / span.Y)));
        Num.Vector2 center = (min + max) * 0.5f;
        pan = canvasSize * 0.5f - center * zoom;
    }

    private static Num.Vector2 GetPosition(EditorMapRoomSnapshot room)
    {
        if (room != null && localPositions.TryGetValue(room.RoomIndex, out Num.Vector2 value)) return value;
        return room == null ? Num.Vector2.Zero : new Num.Vector2(room.X, room.Y);
    }

    private static Num.Vector2 ToScreen(Num.Vector2 canvasMin, Num.Vector2 world) =>
        canvasMin + pan + world * zoom;

    private static void SelectRoom(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

    private static void SetPosition(int roomIndex, Num.Vector2 position) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomPosition,
            roomIndex: roomIndex,
            value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: position.X, y: position.Y)));

    private static void CancelLink()
    {
        linkingRoom = -1;
        linkingNode = -1;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex) =>
        WorldMapPresentationIndex.FindRoom(snapshot, roomIndex);

    private static EditorMapRoomNodeSnapshot FindNode(EditorMapRoomSnapshot room, int nodeIndex)
    {
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].NodeIndex == nodeIndex) return nodes[i];
        return null;
    }

    private static EditorMapConnectionSnapshot FindConnection(EditorMapPresentationSnapshot snapshot, string id) =>
        WorldMapPresentationIndex.FindConnection(snapshot, id);

    private static bool IsLayerVisible(int layer) =>
        layer >= 0 && layer < layerVisible.Length ? layerVisible[layer] : true;

    private static int CurrentLayerMask()
    {
        int mask = 0;
        for (int i = 0; i < layerVisible.Length; i++)
            if (layerVisible[i]) mask |= 1 << i;
        return mask;
    }

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => DevToolGlyphs.ArrowRight,
        WorldConnectionDirection.BToA => DevToolGlyphs.ArrowLeft,
        _ => DevToolGlyphs.ArrowBoth
    };

    private static string ExplicitEdgeId(string connectionId)
    {
        const string prefix = "explicit:";
        return connectionId != null && connectionId.StartsWith(prefix, StringComparison.Ordinal)
            ? connectionId.Substring(prefix.Length)
            : string.Empty;
    }

    private static bool Contains(Num.Vector2 min, Num.Vector2 max, Num.Vector2 point) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private static bool Intersects(
        Num.Vector2 min,
        Num.Vector2 max,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax,
        float margin) =>
        max.X >= canvasMin.X - margin && min.X <= canvasMax.X + margin &&
        max.Y >= canvasMin.Y - margin && min.Y <= canvasMax.Y + margin;

    private static bool SegmentNearCanvas(
        Num.Vector2 a,
        Num.Vector2 b,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax,
        float margin)
    {
        Num.Vector2 min = Num.Vector2.Min(a, b);
        Num.Vector2 max = Num.Vector2.Max(a, b);
        return Intersects(min, max, canvasMin, canvasMax, margin);
    }

    private static float DistanceToSegmentSquared(Num.Vector2 p, Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq <= 0.0001f) return Num.Vector2.DistanceSquared(p, a);
        float t = Math.Max(0f, Math.Min(1f, Num.Vector2.Dot(p - a, ab) / lengthSq));
        return Num.Vector2.DistanceSquared(p, a + ab * t);
    }

    private static float PositiveModulo(float value, float modulus)
    {
        if (modulus <= 0f) return 0f;
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }
}

internal static class WorldMapThumbnailVisibility
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("World Map thumbnails use direct zoom-independent contrast styling; no self-detour attached.");
    }

    internal static void Disable() => enabled = false;

    internal static int PushRoomStyle()
    {
        if (!enabled) return 0;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(0.35f, 0.36f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Num.Vector4(0.62f, 0.64f, 0.66f, 0.95f));
        return 2;
    }

    internal static void PopRoomStyle(int count)
    {
        if (count > 0) ImGui.PopStyleColor(count);
    }

    internal static uint ResolveGeometryColor(EditorMapGeometryKind kind, uint fallback)
    {
        if (!enabled) return fallback;

        return kind switch
        {
            EditorMapGeometryKind.Air =>
                ImGui.GetColorU32(new Num.Vector4(0.68f, 0.69f, 0.70f, 1.00f)),
            EditorMapGeometryKind.BackWall =>
                ImGui.GetColorU32(new Num.Vector4(0.56f, 0.57f, 0.58f, 1.00f)),
            EditorMapGeometryKind.Solid =>
                ImGui.GetColorU32(new Num.Vector4(0.41f, 0.42f, 0.43f, 1.00f)),
            EditorMapGeometryKind.Structure =>
                ImGui.GetColorU32(new Num.Vector4(0.66f, 0.35f, 0.35f, 1.00f)),
            _ => fallback
        };
    }
}

internal static class WorldMapPipeLayers
{
    private static bool enabled;
    private static bool roomPipesVisible = true;
    private static bool creaturePipesVisible = true;

    internal static bool RoomPipesVisible => !enabled || roomPipesVisible;
    internal static bool CreaturePipesVisible => !enabled || creaturePipesVisible;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        roomPipesVisible = true;
        creaturePipesVisible = true;
        logger?.LogInfo("World Map room/creature pipe layers use direct view controls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        roomPipesVisible = true;
        creaturePipesVisible = true;
    }

    internal static void DrawToolbarControls(ref bool portLabels, ref int linkingRoom, ref int linkingNode)
    {
        if (!enabled) return;

        bool roomVisible = roomPipesVisible;
        if (ImGui.Checkbox(
                DevToolUiSettings.T("房间管道", "Room pipes") + "##WorldMapRoomPipes",
                ref roomVisible))
        {
            bool wasVisible = roomPipesVisible;
            roomPipesVisible = roomVisible;
            if (wasVisible && !roomPipesVisible)
            {
                linkingRoom = -1;
                linkingNode = -1;
            }
        }

        portLabels = roomPipesVisible;

        ImGui.SameLine();
        bool creatureVisible = creaturePipesVisible;
        if (ImGui.Checkbox(
                DevToolUiSettings.T("生物管道", "Creature pipes") + "##WorldMapCreaturePipes",
                ref creatureVisible))
            creaturePipesVisible = creatureVisible;
    }
}

internal static class WorldMapRenderOrder
{
    private const int BaseChannel = 0;
    private const int ConnectionChannel = 1;
    private const int OverlayChannel = 2;
    private const int ChannelCount = 3;

    private static ManualLogSource log;
    private static bool enabled;
    private static bool channelsActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("World Map render order uses direct draw-channel calls: rooms < connections < overlays; no self-detour attached.");
    }

    internal static void Disable()
    {
        channelsActive = false;
        enabled = false;
        log = null;
    }

    internal static bool BeginCanvas(ImDrawListPtr draw, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true || channelsActive)
            return false;

        try
        {
            draw.ChannelsSplit(ChannelCount);
            channelsActive = true;
            draw.ChannelsSetCurrent(BaseChannel);
            return true;
        }
        catch (Exception error)
        {
            channelsActive = false;
            log?.LogDebug("World Map channel split failed: " + error.Message);
            return false;
        }
    }

    internal static void EndCanvas(ImDrawListPtr draw, bool split)
    {
        if (!split) return;
        channelsActive = false;
        try
        {
            draw.ChannelsSetCurrent(BaseChannel);
            draw.ChannelsMerge();
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map channel merge failed: " + error.Message);
        }
    }

    internal static void UseBase(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(BaseChannel);
    }

    internal static void UseConnections(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(ConnectionChannel);
    }

    internal static void UseOverlay(ImDrawListPtr draw)
    {
        if (channelsActive) draw.ChannelsSetCurrent(OverlayChannel);
    }
}

internal static class WorldMapPresentationCorrectness
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("World Map canvas correctness service enabled for clipping and connection stroke presentation.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static bool BeginCanvasClip(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        if (!enabled || snapshot?.Available != true || canvasSize.X < 1f || canvasSize.Y < 1f)
            return false;

        try
        {
            draw.PushClipRect(canvasMin, canvasMin + canvasSize, true);
            return true;
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map canvas clip push failed: " + error.Message);
            return false;
        }
    }

    internal static void EndCanvasClip(ImDrawListPtr draw, bool pushed)
    {
        if (!pushed) return;
        try { draw.PopClipRect(); }
        catch (Exception error) { log?.LogDebug("World Map canvas clip pop failed: " + error.Message); }
    }

    internal static bool TryDrawBidirectionalStroke(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        WorldConnectionDirection direction,
        bool dashed)
    {
        if (!enabled || dashed || direction != WorldConnectionDirection.Bidirectional)
            return false;

        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length <= 0.001f) return true;

        // Bidirectional links use the same single stroke as routed links. Opposing arrowheads carry
        // the semantics; parallel rails looked like duplicated/overlapping connections at a glance.
        draw.AddLine(a, b, shadow, shadowThickness);
        draw.AddLine(a, b, core, coreThickness);
        if (length >= 25f)
        {
            float arrowSize = Math.Min(
                length * 0.22f,
                Math.Max(6.5f, Math.Min(8.0f, 5.7f + coreThickness * 0.55f)));
            Num.Vector2 forward = delta / length;
            DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.35f), -forward, shadow, core, arrowSize);
            DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.65f), forward, shadow, core, arrowSize);
        }
        return true;
    }

    private static void DrawArrowHead(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 direction,
        uint shadow,
        uint core,
        float size)
    {
        float length = direction.Length();
        if (length <= 0.001f) return;
        Num.Vector2 forward = direction / length;
        Num.Vector2 normal = new(-forward.Y, forward.X);
        DrawArrowTriangle(draw, tip, forward, normal, shadow, size + 2.4f);
        DrawArrowTriangle(draw, tip, forward, normal, core, size);
    }

    private static void DrawArrowTriangle(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 forward,
        Num.Vector2 normal,
        uint color,
        float size)
    {
        Num.Vector2 baseCenter =
            tip -
            forward *
            (size * 0.72f);
        float wing =
            size * 0.42f;
        float thickness =
            Math.Max(
                1.0f,
                size * 0.16f);

        draw.AddLine(
            baseCenter + normal * wing,
            tip,
            color,
            thickness);
        draw.AddLine(
            baseCenter - normal * wing,
            tip,
            color,
            thickness);
    }
}
