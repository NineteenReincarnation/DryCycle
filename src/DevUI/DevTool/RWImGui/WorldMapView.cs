using System;
using System.Collections.Generic;
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
    private static readonly bool[] layerVisible = { true, true, true };

    private static string region = string.Empty;
    private static Num.Vector2 pan;
    private static float zoom = 1f;
    private static bool fitRequested = true;
    private static bool showTerrain = true;
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

    internal static string SelectedConnectionId => selectedConnectionId;

    internal static void SelectConnection(string connectionId) =>
        selectedConnectionId = connectionId ?? string.Empty;

    internal static void ClearConnectionSelection()
    {
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
    }

    internal static void Draw(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot?.Available != true)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("世界地图不可用。", "World Map unavailable."), true);
            return;
        }

        MapRoomGeometryPresentationHub.Prime(DevToolRuntime.ActiveSession);
        WorldMapShortcutPresentation.Prime(DevToolRuntime.ActiveSession, snapshot.SelectedRoomIndex);
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

        ImGui.TextDisabled(snapshot.RegionName + " · " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine();
        ImGui.TextDisabled("· " + Math.Round(zoom * 100f) + "%");
        ImGui.SameLine(0f, 14f);
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("适配", "Fit"), "WorldMapFit", DevToolButtonTone.Subtle))
            fitRequested = true;
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("100%", "WorldMapZoom100", DevToolButtonTone.Subtle))
            zoom = 1f;

        if (!compact) ImGui.SameLine(0f, 16f);
        else ImGui.Spacing();

        DrawCompactCheckbox(DevToolUiSettings.T("地形", "Terrain"), "WorldMapTerrain", ref showTerrain);
        ImGui.SameLine();
        DrawCompactCheckbox(DevToolUiSettings.T("连接", "Links"), "WorldMapLinks", ref showConnections);
        ImGui.SameLine();
        DrawCompactCheckbox(DevToolUiSettings.T("出口编号", "Exit IDs"), "WorldMapPortLabels", ref showPortLabels);
        ImGui.SameLine();
        DrawCompactCheckbox(DevToolUiSettings.T("子区域", "Subregions"), "WorldMapSubregions", ref showSubregionLabels);

        if (!compact) ImGui.SameLine(0f, 16f);
        else ImGui.Spacing();

        for (int i = 0; i < layerVisible.Length; i++)
        {
            bool value = layerVisible[i];
            if (ImGui.Checkbox("L" + i + "##WorldMapLayer" + i, ref value))
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
                ImGui.TextDisabled("· " + (source?.Name ?? linkingRoom.ToString()) + ":" + linkingNode + " " + DirectionGlyph(linkDirection) + " …");
        }
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
        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 80f || canvasSize.Y < 80f) return;

        ImGui.InvisibleButton("##WorldMapCanvasInput", canvasSize);
        bool canvasHovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();

        if (fitRequested)
        {
            Fit(snapshot, canvasSize);
            fitRequested = false;
        }

        if (canvasHovered && Math.Abs(io.MouseWheel) > 0.0001f && linkingRoom < 0)
        {
            float oldZoom = zoom;
            float next = Math.Max(MinZoom, Math.Min(MaxZoom, zoom * (io.MouseWheel > 0f ? 1.12f : 0.89f)));
            Num.Vector2 mouseInCanvas = io.MousePos - canvasMin;
            Num.Vector2 worldAtMouse = (mouseInCanvas - pan) / oldZoom;
            zoom = next;
            pan = mouseInCanvas - worldAtMouse * zoom;
        }

        if (canvasHovered && linkingRoom < 0 &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
            pan += io.MouseDelta;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 canvasMax = canvasMin + canvasSize;
        draw.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.ChildBg));
        draw.AddRect(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.Border));
        DrawGrid(draw, canvasMin, canvasSize);

        EditorMapRoomSnapshot hoveredRoom = canvasHovered
            ? FindHoveredRoom(snapshot, canvasMin, canvasSize, io.MousePos)
            : null;
        ExitPortHit hoveredPort = canvasHovered
            ? FindHoveredExitPort(snapshot, canvasMin, canvasSize, io.MousePos, hoveredRoom)
            : null;
        EdgeHit hoveredEdge = canvasHovered && hoveredPort == null
            ? FindHoveredEdge(snapshot, canvasMin, canvasSize, io.MousePos)
            : null;
        hoveredConnectionId = hoveredEdge?.Connection?.ConnectionId ?? string.Empty;

        if (showConnections) DrawConnections(draw, snapshot, canvasMin, canvasSize);
        DrawRooms(draw, snapshot, canvasMin, canvasSize, hoveredRoom, hoveredPort);
        HandleInteraction(snapshot, canvasHovered, io, hoveredRoom, hoveredPort, hoveredEdge);
        DrawLinkPreview(draw, snapshot, canvasMin, io.MousePos, hoveredPort);
        HandleDelete(snapshot);
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
        ExitPortHit hoveredPort)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 48f)) continue;

            bool selected = room.RoomIndex == snapshot.SelectedRoomIndex;
            bool hovered = ReferenceEquals(room, hoveredRoom);
            DrawRoomGeometry(draw, room, visual, min, selected, hovered);
            DrawRoomLabel(draw, room, min, max, selected, hovered);
        }

        DrawExitPorts(draw, snapshot, canvasMin, canvasSize, hoveredRoom, hoveredPort);
        DrawCreatureShortcuts(draw, snapshot, canvasMin, canvasSize);
    }

    private static void DrawRoomGeometry(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered)
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

        if (!visual.DetailedRasterAvailable)
        {
            uint fill = ImGui.GetColorU32(selected ? ImGuiCol.Button : ImGuiCol.FrameBg);
            draw.AddRectFilled(roomMin, roomMax, fill, Math.Max(1f, 3f * zoom));
        }

        if (showTerrain && visual.DetailedRasterAvailable)
        {
            EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
            for (int i = 0; i < runs.Length; i++)
            {
                EditorMapRectSnapshot run = runs[i];
                if (lowLod && run.Kind == EditorMapGeometryKind.Water) continue;
                if (!highLod && run.Kind == EditorMapGeometryKind.Detail && zoom < 0.32f) continue;
                Num.Vector2 a = LocalToScreen(roomMin, visual, run.X, run.Y + run.Height);
                Num.Vector2 b = LocalToScreen(roomMin, visual, run.X + run.Width, run.Y);
                draw.AddRectFilled(Num.Vector2.Min(a, b), Num.Vector2.Max(a, b), GeometryColor(run.Kind));
            }
        }

        if (showTerrain && visual.Curves != null)
        {
            for (int i = 0; i < visual.Curves.Length; i++)
                DrawCurve(draw, visual, roomMin, visual.Curves[i], highLod);
        }

        draw.AddRect(roomMin, roomMax, outline, Math.Max(1f, 3f * zoom), ImDrawFlags.None, selected ? 2.2f : 1f);
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
        return kind switch
        {
            EditorMapGeometryKind.Solid => ImGui.GetColorU32(ImGuiCol.TextDisabled),
            EditorMapGeometryKind.Detail => ImGui.GetColorU32(ImGuiCol.Border),
            EditorMapGeometryKind.Water => ImGui.GetColorU32(ImGuiCol.Header),
            EditorMapGeometryKind.QuicksandMaterial => ImGui.GetColorU32(ImGuiCol.ButtonHovered),
            EditorMapGeometryKind.QuicksandBody => ImGui.GetColorU32(ImGuiCol.Separator),
            EditorMapGeometryKind.CurvedSlope => ImGui.GetColorU32(ImGuiCol.Text),
            _ => ImGui.GetColorU32(ImGuiCol.Border)
        };
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
            ? new Num.Vector4(0.80f, 0.83f, 0.86f, 1.00f)
            : new Num.Vector4(0.96f, 0.69f, 0.25f, 1.00f));

    private static void DrawRoomLabel(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 min,
        Num.Vector2 max,
        bool selected,
        bool hovered)
    {
        if (zoom < 0.34f && !selected && !hovered) return;
        uint text = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Text);
        string name = room.Name;
        Num.Vector2 nameSize = ImGui.CalcTextSize(name);
        Num.Vector2 label = new Num.Vector2((min.X + max.X - nameSize.X) * 0.5f, min.Y - nameSize.Y - 3f);
        draw.AddText(label, text, name);

        if (!showSubregionLabels || zoom < 0.75f || string.IsNullOrEmpty(room.Subregion)) return;
        string meta = "L" + room.Layer + " · " + room.Subregion;
        if (room.OffScreenDen) meta += " · DEN";
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
        Num.Vector2 canvasSize)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (!TryConnectionSegment(snapshot, connection, canvasMin, out Num.Vector2 a, out Num.Vector2 b)) continue;
            if (!SegmentNearCanvas(a, b, canvasMin, canvasMin + canvasSize, 42f)) continue;

            bool selected = string.Equals(selectedConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            bool hovered = string.Equals(hoveredConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            uint core = selected
                ? ImGui.GetColorU32(ImGuiCol.ButtonActive)
                : hovered
                    ? ImGui.GetColorU32(ImGuiCol.ButtonHovered)
                    : connection.Ambiguous
                        ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                        : ConnectionColor(connection.Direction);
            uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);

            float coreThickness = selected ? 4.8f : hovered ? 4.2f : connection.Direction == WorldConnectionDirection.Bidirectional ? 3.4f : 3.2f;
            float shadowThickness = coreThickness + (selected || hovered ? 5.6f : 4.8f);
            DrawConnectionStroke(
                draw,
                a,
                b,
                shadow,
                core,
                shadowThickness,
                coreThickness,
                connection.Direction,
                connection.Ambiguous);

            if (!selected && !hovered && !connection.Ambiguous) continue;
            string glyph = DirectionLabel(connection.Direction) + (connection.Ambiguous ? " ?" : string.Empty);
            Num.Vector2 size = ImGui.CalcTextSize(glyph);
            Num.Vector2 mid = (a + b) * 0.5f;
            Num.Vector2 pad = new(6f, 3f);
            draw.AddRectFilled(mid - size * 0.5f - pad, mid + size * 0.5f + pad, shadow, 5f);
            draw.AddRect(mid - size * 0.5f - pad, mid + size * 0.5f + pad, core, 5f, ImDrawFlags.None, 1.4f);
            draw.AddText(mid - size * 0.5f, core, glyph);
        }
    }

    private static void DrawExitPorts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        EditorMapRoomSnapshot hoveredRoom,
        ExitPortHit hoveredPort)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        bool linking = linkingRoom >= 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 42f)) continue;

            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (!node.Exit) continue;

                EditorMapConnectionSnapshot endpointConnection = FindConnectionAtEndpoint(snapshot, room.RoomIndex, node.NodeIndex);
                bool free = IsEndpointFree(snapshot, room.RoomIndex, node);
                bool connected = endpointConnection != null || node.ConnectedRoomIndex >= 0;

                Num.Vector2 point = EndpointPosition(room, node.NodeIndex, canvasMin);
                bool source = room.RoomIndex == linkingRoom && node.NodeIndex == linkingNode;
                bool hovered = hoveredPort != null && hoveredPort.Room.RoomIndex == room.RoomIndex && hoveredPort.Node.NodeIndex == node.NodeIndex;
                bool validTarget = linking && free && room.RoomIndex != linkingRoom;
                bool selectedLink = endpointConnection != null && string.Equals(
                    selectedConnectionId,
                    endpointConnection.ConnectionId,
                    StringComparison.Ordinal);

                bool emphasized = source || hovered || validTarget || selectedLink;
                uint color = ShortcutGold(connected || emphasized);
                uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
                DrawShortcutSocket(draw, point, shadow, color, connected, emphasized);

                if (showPortLabels && (zoom >= 0.48f || emphasized || room.RoomIndex == snapshot.SelectedRoomIndex))
                {
                    string label = node.NodeIndex.ToString();
                    Num.Vector2 labelSize = ImGui.CalcTextSize(label);
                    bool left = point.X <= (min.X + max.X) * 0.5f;
                    float offset = emphasized ? 15f : 13f;
                    float x = left ? point.X - labelSize.X - offset : point.X + offset;
                    Num.Vector2 labelPos = new(x, point.Y - labelSize.Y * 0.5f);
                    Num.Vector2 pad = new(4f, 2f);
                    draw.AddRectFilled(labelPos - pad, labelPos + labelSize + pad, shadow, 3f);
                    draw.AddRect(labelPos - pad, labelPos + labelSize + pad, ShortcutGold(false), 3f, ImDrawFlags.None, 1f);
                    draw.AddText(labelPos, ShortcutGold(true), label);
                }
            }
        }
    }

    private static void DrawCreatureShortcuts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;

            WorldMapShortcutPresentation.ShortcutMarker[] holes =
                WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex);
            if (holes == null || holes.Length == 0) continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 32f)) continue;

            for (int h = 0; h < holes.Length; h++)
            {
                Num.Vector2 point = LocalToScreen(min, visual, holes[h].X, holes[h].Y);
                DrawCreatureShortcutSocket(draw, point, shadow);
            }
        }
    }

    private static void HandleInteraction(
        EditorMapPresentationSnapshot snapshot,
        bool canvasHovered,
        ImGuiIOPtr io,
        EditorMapRoomSnapshot hoveredRoom,
        ExitPortHit hoveredPort,
        EdgeHit hoveredEdge)
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
                localPositions[draggingRoom] = dragStartWorld + (io.MousePos - dragStartMouse) / zoom;
            else
            {
                if (dragged != null && localPositions.TryGetValue(draggingRoom, out Num.Vector2 final))
                    SetPosition(draggingRoom, final);
                draggingRoom = -1;
            }
        }
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
        uint color = ImGui.GetColorU32(ImGuiCol.ButtonHovered);
        uint shadow = ImGui.GetColorU32(ImGuiCol.WindowBg);
        DrawConnectionStroke(draw, source, target, shadow, color, 9f, 3.8f, linkDirection, false);
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
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = rooms.Length - 1; i >= 0; i--)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;
            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            GetRoomRect(room, visual, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (!Intersects(min, max, canvasMin, canvasMin + canvasSize, 8f)) continue;
            if (Contains(min, max, mouse)) return room;
        }
        return null;
    }

    private static ExitPortHit FindHoveredExitPort(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse,
        EditorMapRoomSnapshot hoveredRoom)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        ExitPortHit best = null;
        float bestDistanceSq = 400f;
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;
            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
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
        Num.Vector2 mouse)
    {
        if (!showConnections) return null;
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        EdgeHit best = null;
        float thresholdSq = 169f;
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (!TryConnectionSegment(snapshot, connection, canvasMin, out Num.Vector2 a, out Num.Vector2 b)) continue;
            if (!SegmentNearCanvas(a, b, canvasMin, canvasMin + canvasSize, 30f)) continue;
            float distanceSq = DistanceToSegmentSquared(mouse, a, b);
            if (distanceSq > thresholdSq || best != null && distanceSq >= best.DistanceSq) continue;
            best = new EdgeHit { Connection = connection, DistanceSq = distanceSq };
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
        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
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
        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
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
    }

    private static bool IsEndpointFree(EditorMapPresentationSnapshot snapshot, int roomIndex, EditorMapRoomNodeSnapshot node)
    {
        if (node == null || !node.Exit || node.ConnectedRoomIndex >= 0) return false;
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if ((connection.FromRoomIndex == roomIndex && connection.FromNodeIndex == node.NodeIndex) ||
                (connection.ToRoomIndex == roomIndex && connection.ToNodeIndex == node.NodeIndex))
                return false;
        }
        return true;
    }

    private static EditorMapConnectionSnapshot FindConnectionAtEndpoint(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex)
    {
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if ((connection.FromRoomIndex == roomIndex && connection.FromNodeIndex == nodeIndex) ||
                (connection.ToRoomIndex == roomIndex && connection.ToNodeIndex == nodeIndex))
                return connection;
        }
        return null;
    }

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

        if (direction == WorldConnectionDirection.Bidirectional && length >= 20f)
        {
            Num.Vector2 forward = delta / length;
            Num.Vector2 normal = new(-forward.Y, forward.X);
            float railOffset = Math.Max(2f, coreThickness * 0.72f);
            float railThickness = Math.Max(1.6f, coreThickness * 0.72f);
            draw.AddLine(a + normal * railOffset, b + normal * railOffset, core, railThickness);
            draw.AddLine(a - normal * railOffset, b - normal * railOffset, core, railThickness);

            if (length >= 34f)
            {
                float arrowSize = Math.Max(6.2f, Math.Min(9.2f, 6.2f + coreThickness * 0.55f));
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.62f) + normal * railOffset, delta, shadow, core, arrowSize);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.38f) - normal * railOffset, -delta, shadow, core, arrowSize);
            }
            return;
        }

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
        float size = Math.Max(6.2f, Math.Min(9.4f, 6.2f + coreThickness * 0.55f));
        switch (direction)
        {
            case WorldConnectionDirection.AToB:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.42f), forward, shadow, core, size);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.62f), forward, shadow, core, size);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.82f), forward, shadow, core, size);
                break;
            case WorldConnectionDirection.BToA:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.58f), -forward, shadow, core, size);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.38f), -forward, shadow, core, size);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.18f), -forward, shadow, core, size);
                break;
            default:
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.40f), forward, shadow, core, size * 0.92f);
                DrawArrowHead(draw, Num.Vector2.Lerp(a, b, 0.60f), -forward, shadow, core, size * 0.92f);
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
        Num.Vector2 baseCenter = tip - forward * size;
        float wing = size * 0.58f;
        draw.AddTriangleFilled(tip, baseCenter + normal * wing, baseCenter - normal * wing, color);
    }

    private static void SynchronizeRegion(EditorMapPresentationSnapshot snapshot)
    {
        string next = snapshot.RegionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;
        region = next;
        localPositions.Clear();
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
        HashSet<int> alive = new();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            alive.Add(room.RoomIndex);
            if (room.RoomIndex == draggingRoom) continue;
            localPositions[room.RoomIndex] = new Num.Vector2(room.X, room.Y);
        }
        if (localPositions.Count == alive.Count) return;
        List<int> remove = new();
        foreach (int key in localPositions.Keys)
            if (!alive.Contains(key)) remove.Add(key);
        for (int i = 0; i < remove.Count; i++) localPositions.Remove(remove[i]);
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
            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
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

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static EditorMapRoomNodeSnapshot FindNode(EditorMapRoomSnapshot room, int nodeIndex)
    {
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].NodeIndex == nodeIndex) return nodes[i];
        return null;
    }

    private static EditorMapConnectionSnapshot FindConnection(EditorMapPresentationSnapshot snapshot, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
            if (string.Equals(connections[i].ConnectionId, id, StringComparison.Ordinal)) return connections[i];
        return null;
    }

    private static bool IsLayerVisible(int layer) =>
        layer >= 0 && layer < layerVisible.Length ? layerVisible[layer] : true;

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "->",
        WorldConnectionDirection.BToA => "<-",
        _ => "<->"
    };

    private static string DirectionLabel(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "A > B",
        WorldConnectionDirection.BToA => "A < B",
        _ => "BOTH"
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
