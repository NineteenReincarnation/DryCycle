using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Dedicated endpoint graph editor for world topology.
/// Graph positions are intentionally independent from MapPage Dev Position.
/// </summary>
internal static class WorldGraphView
{
    private sealed class ExitPortHit
    {
        internal EditorMapRoomSnapshot Room;
        internal EditorMapRoomNodeSnapshot Node;
        internal Num.Vector2 Position;
        internal bool Free;
    }

    private sealed class EdgeHit
    {
        internal EditorMapConnectionSnapshot Connection;
        internal float DistanceSq;
    }

    private static readonly Dictionary<int, Num.Vector2> positions = new();
    private static string region = string.Empty;
    private static Num.Vector2 pan;
    private static float zoom = 1f;
    private static bool fitRequested = true;
    private static int draggingRoom = -1;
    private static Num.Vector2 dragStartMouse;
    private static Num.Vector2 dragStartWorld;

    private static int linkingRoom = -1;
    private static int linkingNode = -1;
    private static WorldConnectionDirection linkDirection = WorldConnectionDirection.Bidirectional;

    private static string selectedConnectionId = string.Empty;
    private static string hoveredConnectionId = string.Empty;

    internal static string SelectedConnectionId => selectedConnectionId;

    internal static void ClearConnectionSelection()
    {
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
    }

    internal static void SelectConnection(string connectionId)
    {
        selectedConnectionId = connectionId ?? string.Empty;
    }

    internal static void Draw(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot?.Available != true)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("世界拓扑不可用。", "World graph unavailable."), true);
            return;
        }

        SynchronizeRegion(snapshot);
        DrawToolbar(snapshot);
        ImGui.Spacing();
        DrawCanvas(snapshot);
    }

    private static void DrawToolbar(EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextDisabled(snapshot.RegionName + " · " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine();
        ImGui.TextDisabled("· " + Math.Round(zoom * 100f) + "%");
        ImGui.SameLine();

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("适配", "Fit"),
                "WorldGraphFit",
                DevToolButtonTone.Subtle))
            fitRequested = true;

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("100%", "WorldGraphZoom100", DevToolButtonTone.Subtle))
            zoom = 1f;

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重置布局", "Reset Layout"),
                "WorldGraphResetLayout",
                DevToolButtonTone.Subtle))
        {
            ResetPositionsFromMap(snapshot);
            fitRequested = true;
        }

        ImGui.SameLine(0f, 18f);
        DevToolWidgets.MutedText(DevToolUiSettings.T("新连接", "New link"));
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.Bidirectional, "↔", "Both");
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.AToB, "→", "AToB");
        ImGui.SameLine();
        DrawDirectionButton(WorldConnectionDirection.BToA, "←", "BToA");

        if (linkingRoom >= 0)
        {
            EditorMapRoomSnapshot source = FindRoom(snapshot, linkingRoom);
            ImGui.SameLine(0f, 16f);
            ImGui.TextDisabled(
                "· " + (source?.Name ?? linkingRoom.ToString()) + ":" + linkingNode + " " + DirectionGlyph(linkDirection) + " …");
        }
    }

    private static void DrawDirectionButton(
        WorldConnectionDirection direction,
        string glyph,
        string id)
    {
        if (DevToolWidgets.ActionButton(
                glyph,
                "WorldGraphDirection" + id,
                linkDirection == direction ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            linkDirection = direction;
    }

    private static void DrawCanvas(EditorMapPresentationSnapshot snapshot)
    {
        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 80f || canvasSize.Y < 80f) return;

        ImGui.InvisibleButton("##WorldGraphCanvasInput", canvasSize);
        bool canvasHovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();

        SynchronizePositions(snapshot);
        SynchronizeLinkState(snapshot);
        SynchronizeConnectionSelection(snapshot);

        if (fitRequested)
        {
            Fit(snapshot, canvasSize);
            fitRequested = false;
        }

        if (canvasHovered && Math.Abs(io.MouseWheel) > 0.0001f && linkingRoom < 0)
        {
            float oldZoom = zoom;
            float nextZoom = Math.Max(0.22f, Math.Min(3.25f, zoom * (io.MouseWheel > 0f ? 1.12f : 0.89f)));
            Num.Vector2 mouseInCanvas = io.MousePos - canvasMin;
            Num.Vector2 worldAtMouse = (mouseInCanvas - pan) / oldZoom;
            zoom = nextZoom;
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

        ExitPortHit hoveredPort = canvasHovered
            ? FindHoveredExitPort(snapshot, canvasMin, io.MousePos)
            : null;
        EdgeHit hoveredEdge = canvasHovered && hoveredPort == null
            ? FindHoveredEdge(snapshot, canvasMin, io.MousePos)
            : null;
        hoveredConnectionId = hoveredEdge?.Connection?.ConnectionId ?? string.Empty;

        DrawConnections(draw, snapshot, canvasMin);
        DrawRooms(draw, snapshot, canvasMin, canvasHovered, io, hoveredPort, hoveredEdge);
        HandleDelete(snapshot);
    }

    private static void DrawGrid(ImDrawListPtr draw, Num.Vector2 canvasMin, Num.Vector2 canvasSize)
    {
        float grid = 72f * zoom;
        if (grid < 18f) grid *= 4f;
        uint color = ImGui.GetColorU32(ImGuiCol.Border);
        float startX = PositiveModulo(pan.X, grid);
        float startY = PositiveModulo(pan.Y, grid);

        for (float x = startX; x < canvasSize.X; x += grid)
            draw.AddLine(canvasMin + new Num.Vector2(x, 0f), canvasMin + new Num.Vector2(x, canvasSize.Y), color);
        for (float y = startY; y < canvasSize.Y; y += grid)
            draw.AddLine(canvasMin + new Num.Vector2(0f, y), canvasMin + new Num.Vector2(canvasSize.X, y), color);
    }

    private static void DrawConnections(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (!TryConnectionSegment(snapshot, connection, canvasMin, out Num.Vector2 a, out Num.Vector2 b)) continue;

            bool selected = string.Equals(selectedConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            bool hovered = string.Equals(hoveredConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            uint color = ImGui.GetColorU32(
                selected ? ImGuiCol.ButtonActive :
                hovered ? ImGuiCol.ButtonHovered :
                connection.Ambiguous ? ImGuiCol.TextDisabled : ImGuiCol.TextDisabled);
            float thickness = selected ? 3.4f : hovered ? 3f : connection.Explicit ? 2.4f : 2f;
            draw.AddLine(a, b, color, thickness);

            string label = DirectionGlyph(connection.Direction) + (connection.Ambiguous ? " ?" : string.Empty);
            Num.Vector2 labelSize = ImGui.CalcTextSize(label);
            Num.Vector2 midpoint = (a + b) * 0.5f;
            Num.Vector2 labelMin = midpoint - labelSize * 0.5f - new Num.Vector2(4f, 2f);
            Num.Vector2 labelMax = midpoint + labelSize * 0.5f + new Num.Vector2(4f, 2f);
            draw.AddRectFilled(labelMin, labelMax, ImGui.GetColorU32(ImGuiCol.ChildBg), 3f);
            draw.AddText(midpoint - labelSize * 0.5f, color, label);
        }
    }

    private static void DrawRooms(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        bool canvasHovered,
        ImGuiIOPtr io,
        ExitPortHit hoveredPort,
        EdgeHit hoveredEdge)
    {
        EditorMapRoomSnapshot hoveredRoom = null;
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        Num.Vector2 size = NodeSize();

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            Num.Vector2 min = ToScreen(canvasMin, GetPosition(room));
            if (canvasHovered && Contains(min, min + size, io.MousePos))
                hoveredRoom = room;
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredPort != null)
            {
                SelectRoom(hoveredPort.Room.RoomIndex);
                draggingRoom = -1;
                if (hoveredPort.Free && linkingRoom < 0)
                {
                    linkingRoom = hoveredPort.Room.RoomIndex;
                    linkingNode = hoveredPort.Node.NodeIndex;
                }
            }
            else if (hoveredEdge?.Connection != null)
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

        if (linkingRoom >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            CancelLink();

        if (draggingRoom >= 0)
        {
            EditorMapRoomSnapshot dragged = FindRoom(snapshot, draggingRoom);
            if (dragged != null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                positions[draggingRoom] = dragStartWorld + (io.MousePos - dragStartMouse) / zoom;
            else
                draggingRoom = -1;
        }

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            Num.Vector2 min = ToScreen(canvasMin, GetPosition(room));
            Num.Vector2 max = min + size;
            bool hovered = ReferenceEquals(room, hoveredRoom);

            uint fill = ImGui.GetColorU32(
                room.RoomIndex == snapshot.SelectedRoomIndex ? ImGuiCol.ButtonActive :
                room.CurrentRoom ? ImGuiCol.Header :
                hovered ? ImGuiCol.ButtonHovered : ImGuiCol.FrameBg);
            uint border = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Border);
            uint text = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Text);

            draw.AddRectFilled(min, max, fill, 5f);
            draw.AddRect(min, max, border, 5f, ImDrawFlags.None,
                room.RoomIndex == snapshot.SelectedRoomIndex ? 2f : 1f);
            draw.AddText(min + new Num.Vector2(8f, 6f), text, room.Name);

            string meta = "L" + room.Layer;
            if (!string.IsNullOrEmpty(room.Subregion)) meta += " · " + room.Subregion;
            if (room.OffScreenDen) meta += " · DEN";
            draw.AddText(min + new Num.Vector2(8f, 23f), ImGui.GetColorU32(ImGuiCol.TextDisabled), meta);
        }

        DrawExitPorts(draw, snapshot, canvasMin, hoveredPort);
        DrawLinkPreview(draw, snapshot, canvasMin, io.MousePos, hoveredPort);
    }

    private static void DrawExitPorts(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        ExitPortHit hoveredPort)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        bool linking = linkingRoom >= 0;

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            bool showAll = linking || room.RoomIndex == snapshot.SelectedRoomIndex;
            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();

            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (!node.Exit) continue;

                Num.Vector2 point = EndpointPosition(room, node.NodeIndex, canvasMin);
                bool free = IsEndpointFree(snapshot, room.RoomIndex, node);
                bool source = room.RoomIndex == linkingRoom && node.NodeIndex == linkingNode;
                bool hovered = hoveredPort != null &&
                               hoveredPort.Room.RoomIndex == room.RoomIndex &&
                               hoveredPort.Node.NodeIndex == node.NodeIndex;

                uint color = ImGui.GetColorU32(
                    source ? ImGuiCol.ButtonActive :
                    hovered && free ? ImGuiCol.ButtonHovered :
                    free ? ImGuiCol.Text : ImGuiCol.TextDisabled);
                float radius = hovered || source ? 5.8f : 4.2f;
                draw.AddCircleFilled(point, radius, color);

                if (showAll || hovered)
                {
                    string label = node.NodeIndex.ToString();
                    Num.Vector2 labelSize = ImGui.CalcTextSize(label);
                    Num.Vector2 roomMin = ToScreen(canvasMin, GetPosition(room));
                    bool left = point.X <= roomMin.X + NodeSize().X * 0.5f;
                    float x = left ? point.X - labelSize.X - 8f : point.X + 8f;
                    draw.AddText(new Num.Vector2(x, point.Y - labelSize.Y * 0.5f), color, label);
                }
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
        Num.Vector2 target = hoveredPort != null && hoveredPort.Free && hoveredPort.Room.RoomIndex != linkingRoom
            ? hoveredPort.Position
            : mouse;
        uint color = ImGui.GetColorU32(ImGuiCol.ButtonHovered);
        draw.AddLine(source, target, color, 2.8f);

        string glyph = DirectionGlyph(linkDirection);
        Num.Vector2 size = ImGui.CalcTextSize(glyph);
        Num.Vector2 midpoint = (source + target) * 0.5f;
        draw.AddText(midpoint - size * 0.5f, color, glyph);
    }

    private static ExitPortHit FindHoveredExitPort(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 mouse)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        ExitPortHit best = null;
        float bestDistanceSq = 81f;

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (linkingRoom < 0 && room.RoomIndex != snapshot.SelectedRoomIndex) continue;

            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (!node.Exit) continue;
                Num.Vector2 point = EndpointPosition(room, node.NodeIndex, canvasMin);
                float distanceSq = Num.Vector2.DistanceSquared(point, mouse);
                if (distanceSq > bestDistanceSq) continue;

                bestDistanceSq = distanceSq;
                best = new ExitPortHit
                {
                    Room = room,
                    Node = node,
                    Position = point,
                    Free = IsEndpointFree(snapshot, room.RoomIndex, node)
                };
            }
        }

        return best;
    }

    private static EdgeHit FindHoveredEdge(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 mouse)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        EdgeHit best = null;
        float threshold = 8f;
        float thresholdSq = threshold * threshold;

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (!TryConnectionSegment(snapshot, connection, canvasMin, out Num.Vector2 a, out Num.Vector2 b)) continue;
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
        if (roomA == null || roomB == null) return false;

        a = EndpointPosition(roomA, connection.FromNodeIndex, canvasMin);
        b = connection.ToNodeIndex >= 0
            ? EndpointPosition(roomB, connection.ToNodeIndex, canvasMin)
            : ToScreen(canvasMin, GetPosition(roomB)) + NodeSize() * 0.5f;
        return true;
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
        if (string.IsNullOrEmpty(selectedConnectionId)) return;
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

    private static bool IsEndpointFree(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node)
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

    private static void SynchronizeRegion(EditorMapPresentationSnapshot snapshot)
    {
        string next = snapshot.RegionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;

        region = next;
        positions.Clear();
        pan = Num.Vector2.Zero;
        zoom = 1f;
        fitRequested = true;
        draggingRoom = -1;
        linkingRoom = -1;
        linkingNode = -1;
        linkDirection = WorldConnectionDirection.Bidirectional;
        selectedConnectionId = string.Empty;
        hoveredConnectionId = string.Empty;
        ResetPositionsFromMap(snapshot);
    }

    private static void SynchronizePositions(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        HashSet<int> alive = new();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            alive.Add(room.RoomIndex);
            if (!positions.ContainsKey(room.RoomIndex))
                positions[room.RoomIndex] = new Num.Vector2(room.X, room.Y);
        }

        if (positions.Count == alive.Count) return;
        List<int> remove = new();
        foreach (int key in positions.Keys)
            if (!alive.Contains(key)) remove.Add(key);
        for (int i = 0; i < remove.Count; i++) positions.Remove(remove[i]);
    }

    private static void SynchronizeLinkState(EditorMapPresentationSnapshot snapshot)
    {
        if (linkingRoom < 0) return;
        EditorMapRoomSnapshot room = FindRoom(snapshot, linkingRoom);
        EditorMapRoomNodeSnapshot node = FindNode(room, linkingNode);
        if (room == null || node == null || !IsEndpointFree(snapshot, linkingRoom, node))
            CancelLink();
    }

    private static void SynchronizeConnectionSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(selectedConnectionId) && FindConnection(snapshot, selectedConnectionId) == null)
            selectedConnectionId = string.Empty;
    }

    private static void ResetPositionsFromMap(EditorMapPresentationSnapshot snapshot)
    {
        positions.Clear();
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            positions[rooms[i].RoomIndex] = new Num.Vector2(rooms[i].X, rooms[i].Y);
    }

    private static void Fit(EditorMapPresentationSnapshot snapshot, Num.Vector2 canvasSize)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0)
        {
            zoom = 1f;
            pan = canvasSize * 0.5f;
            return;
        }

        Num.Vector2 min = new(float.MaxValue, float.MaxValue);
        Num.Vector2 max = new(float.MinValue, float.MinValue);
        Num.Vector2 size = BaseNodeSize();
        bool found = false;
        for (int i = 0; i < rooms.Length; i++)
        {
            Num.Vector2 p = GetPosition(rooms[i]);
            min = Num.Vector2.Min(min, p);
            max = Num.Vector2.Max(max, p + size);
            found = true;
        }

        if (!found) return;
        Num.Vector2 span = Num.Vector2.Max(max - min, Num.Vector2.One);
        float availableX = Math.Max(100f, canvasSize.X - 90f);
        float availableY = Math.Max(100f, canvasSize.Y - 90f);
        zoom = Math.Max(0.22f, Math.Min(2f, Math.Min(availableX / span.X, availableY / span.Y)));
        Num.Vector2 center = (min + max) * 0.5f;
        pan = canvasSize * 0.5f - center * zoom;
    }

    private static Num.Vector2 EndpointPosition(
        EditorMapRoomSnapshot room,
        int nodeIndex,
        Num.Vector2 canvasMin)
    {
        Num.Vector2 min = ToScreen(canvasMin, GetPosition(room));
        Num.Vector2 size = NodeSize();
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();

        int exitCount = 0;
        int ordinal = -1;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].Exit) continue;
            if (nodes[i].NodeIndex == nodeIndex) ordinal = exitCount;
            exitCount++;
        }

        if (ordinal < 0 || exitCount == 0) return min + size * 0.5f;

        bool right = ordinal % 2 == 0;
        int row = ordinal / 2;
        int rowsOnSide = right ? (exitCount + 1) / 2 : exitCount / 2;
        float t = (row + 1f) / (rowsOnSide + 1f);
        float y = min.Y + 6f + (size.Y - 12f) * t;
        float x = right ? min.X + size.X : min.X;
        return new Num.Vector2(x, y);
    }

    private static Num.Vector2 GetPosition(EditorMapRoomSnapshot room)
    {
        if (room != null && positions.TryGetValue(room.RoomIndex, out Num.Vector2 value)) return value;
        return room == null ? Num.Vector2.Zero : new Num.Vector2(room.X, room.Y);
    }

    private static Num.Vector2 ToScreen(Num.Vector2 canvasMin, Num.Vector2 world) =>
        canvasMin + pan + world * zoom;

    private static Num.Vector2 BaseNodeSize() => new(146f, 48f);

    private static Num.Vector2 NodeSize()
    {
        float visualScale = Math.Max(0.68f, Math.Min(1.25f, zoom));
        return new Num.Vector2(146f * visualScale, 48f);
    }

    private static void SelectRoom(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

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

    private static void CancelLink()
    {
        linkingRoom = -1;
        linkingNode = -1;
    }

    private static string ExplicitEdgeId(string connectionId)
    {
        const string prefix = "explicit:";
        return connectionId != null && connectionId.StartsWith(prefix, StringComparison.Ordinal)
            ? connectionId.Substring(prefix.Length)
            : string.Empty;
    }

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "→",
        WorldConnectionDirection.BToA => "←",
        _ => "↔"
    };

    private static float DistanceToSegmentSquared(Num.Vector2 point, Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq <= 0.0001f) return Num.Vector2.DistanceSquared(point, a);
        float t = Num.Vector2.Dot(point - a, ab) / lengthSq;
        t = Math.Max(0f, Math.Min(1f, t));
        Num.Vector2 closest = a + ab * t;
        return Num.Vector2.DistanceSquared(point, closest);
    }

    private static bool Contains(Num.Vector2 min, Num.Vector2 max, Num.Vector2 point) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private static float PositiveModulo(float value, float modulus)
    {
        if (modulus <= 0f) return 0f;
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }
}
