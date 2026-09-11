using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class MapEditorView
{
    private static readonly Dictionary<int, Num.Vector2> LocalPositions = new();
    private static string search = string.Empty;
    private static readonly bool[] LayerVisible = { true, true, true };
    private static Num.Vector2 pan;
    private static float zoom = 1f;
    private static bool fitRequested = true;
    private static int draggingRoom = -1;
    private static Num.Vector2 dragStartMouse;
    private static Num.Vector2 dragStartWorld;
    private static int inspectorRoom = -1;
    private static Num.Vector2 inspectorPosition;
    private static string inspectorSubregion = string.Empty;

    internal static void DrawBrowser(EditorMapPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Map editor unavailable.");
            return;
        }

        ImGui.TextDisabled(snapshot.RegionName + " · " + (snapshot.Rooms?.Length ?? 0) + " rooms");
        if (ImGui.Button("Fit Map")) fitRequested = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("100%")) zoom = 1f;

        ImGui.Separator();
        ImGui.TextDisabled("LAYERS");
        for (int i = 0; i < LayerVisible.Length; i++)
        {
            bool visible = LayerVisible[i];
            if (ImGui.Checkbox("L" + i + "##MapLayerFilter" + i, ref visible))
                LayerVisible[i] = visible;
            if (i < LayerVisible.Length - 1) ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search rooms##MapRoomSearch", ref search, 128);
        ImGui.Separator();

        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        int matches = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer) || !Matches(room, search)) continue;
            matches++;

            string label = room.Name + "  [L" + room.Layer + "]";
            if (!string.IsNullOrEmpty(room.Subregion)) label += "  " + room.Subregion;
            if (room.Disabled) label += "  [Hidden]";
            if (ImGui.Selectable(label + "##MapBrowserRoom" + room.RoomIndex, room.Selected))
                Select(room.RoomIndex);
        }
        if (matches == 0) ImGui.TextDisabled("No matching rooms.");
    }

    internal static void DrawInspector(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
        if (room == null)
        {
            inspectorRoom = -1;
            ImGui.TextDisabled("Select a room from the graph or room list.");
            return;
        }

        if (inspectorRoom != room.RoomIndex)
        {
            inspectorRoom = room.RoomIndex;
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
        }
        else if (!ImGui.IsAnyItemActive())
        {
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
        }

        ImGui.Text(room.Name);
        ImGui.TextDisabled("Room index " + room.RoomIndex);
        if (room.CurrentRoom) ImGui.TextDisabled("Current camera room");
        if (room.OffScreenDen) ImGui.TextDisabled("Off-screen den");
        if (room.Disabled) ImGui.TextDisabled("Hidden from map output");
        ImGui.Separator();

        Num.Vector2 position = inspectorPosition;
        bool positionChanged = ImGui.InputFloat2("Dev position##MapInspectorPosition", ref position, "%.1f");
        inspectorPosition = position;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SetPosition(room.RoomIndex, position);
        else if (!positionChanged && !ImGui.IsItemActive())
            inspectorPosition = new Num.Vector2(room.X, room.Y);

        int layer = room.Layer;
        if (ImGui.BeginCombo("Layer##MapInspectorLayer", "Layer " + layer))
        {
            for (int i = 0; i < 3; i++)
            {
                bool selected = layer == i;
                if (ImGui.Selectable("Layer " + i + "##MapLayer" + i, selected))
                    MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                        MapEditorCommandKind.SetRoomLayer,
                        roomIndex: room.RoomIndex,
                        value: new EditorPropertyValue(EditorPropertyKind.Integer, integer: i)));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        string subregion = inspectorSubregion;
        bool subregionChanged = ImGui.InputText("Subregion##MapInspectorSubregion", ref subregion, 128);
        inspectorSubregion = subregion;
        if (ImGui.IsItemDeactivatedAfterEdit())
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                MapEditorCommandKind.SetRoomSubregion,
                roomIndex: room.RoomIndex,
                text: subregion));
        else if (!subregionChanged && !ImGui.IsItemActive())
            inspectorSubregion = room.Subregion ?? string.Empty;

        ImGui.Separator();
        ImGui.TextDisabled("Connections are taken directly from World/AbstractRoom.");
        ImGui.TextDisabled("Map position edits are saved by Ctrl+S with MapPage.SaveMapConfig().");
    }

    internal static void DrawCanvas(EditorMapPresentationSnapshot snapshot, Num.Vector2 position, Num.Vector2 size)
    {
        if (!snapshot.Available || size.X < 120f || size.Y < 120f) return;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.98f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("Region Map###DevToolMapCanvas", flags))
        {
            ImGui.End();
            return;
        }

        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 50f || canvasSize.Y < 50f)
        {
            ImGui.End();
            return;
        }

        ImGui.InvisibleButton("##MapCanvasInput", canvasSize);
        bool hovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();

        SynchronizeLocalPositions(snapshot);
        if (fitRequested)
        {
            Fit(snapshot, canvasSize);
            fitRequested = false;
        }

        if (hovered && Math.Abs(io.MouseWheel) > 0.0001f)
        {
            float oldZoom = zoom;
            float nextZoom = Math.Max(0.2f, Math.Min(3.0f, zoom * (io.MouseWheel > 0f ? 1.12f : 0.89f)));
            Num.Vector2 mouseInCanvas = io.MousePos - canvasMin;
            Num.Vector2 worldAtMouse = (mouseInCanvas - pan) / oldZoom;
            zoom = nextZoom;
            pan = mouseInCanvas - worldAtMouse * zoom;
        }

        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            pan += io.MouseDelta;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        DrawGrid(draw, canvasMin, canvasSize);
        DrawConnections(draw, snapshot, canvasMin);
        DrawRooms(draw, snapshot, canvasMin, hovered, io);

        ImGui.End();
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

    private static void DrawConnections(ImDrawListPtr draw, EditorMapPresentationSnapshot snapshot, Num.Vector2 canvasMin)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        uint color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapRoomSnapshot a = FindRoom(snapshot, connections[i].FromRoomIndex);
            EditorMapRoomSnapshot b = FindRoom(snapshot, connections[i].ToRoomIndex);
            if (a == null || b == null || !IsLayerVisible(a.Layer) || !IsLayerVisible(b.Layer)) continue;

            Num.Vector2 pa = ToScreen(canvasMin, GetLocalPosition(a)) + NodeSize() * 0.5f;
            Num.Vector2 pb = ToScreen(canvasMin, GetLocalPosition(b)) + NodeSize() * 0.5f;
            draw.AddLine(pa, pb, color, 2f);
        }
    }

    private static void DrawRooms(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        bool canvasHovered,
        ImGuiIOPtr io)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapRoomSnapshot hoveredRoom = null;
        Num.Vector2 nodeSize = NodeSize();

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;
            Num.Vector2 world = GetLocalPosition(room);
            Num.Vector2 min = ToScreen(canvasMin, world);
            Num.Vector2 max = min + nodeSize;
            if (canvasHovered && Contains(min, max, io.MousePos)) hoveredRoom = room;
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredRoom != null)
            {
                Select(hoveredRoom.RoomIndex);
                draggingRoom = hoveredRoom.RoomIndex;
                dragStartMouse = io.MousePos;
                dragStartWorld = GetLocalPosition(hoveredRoom);
            }
            else
            {
                Select(-1);
                draggingRoom = -1;
            }
        }

        if (draggingRoom >= 0)
        {
            EditorMapRoomSnapshot dragged = FindRoom(snapshot, draggingRoom);
            if (dragged != null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                LocalPositions[draggingRoom] = dragStartWorld + (io.MousePos - dragStartMouse) / zoom;
            else
            {
                if (dragged != null && LocalPositions.TryGetValue(draggingRoom, out Num.Vector2 final))
                    SetPosition(draggingRoom, final);
                draggingRoom = -1;
            }
        }

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer)) continue;

            Num.Vector2 min = ToScreen(canvasMin, GetLocalPosition(room));
            Num.Vector2 max = min + nodeSize;
            bool hovered = ReferenceEquals(room, hoveredRoom);

            uint fill = ImGui.GetColorU32(room.Selected ? ImGuiCol.ButtonActive :
                room.CurrentRoom ? ImGuiCol.Header :
                hovered ? ImGuiCol.ButtonHovered : ImGuiCol.FrameBg);
            uint border = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Border);
            uint text = ImGui.GetColorU32(room.Disabled ? ImGuiCol.TextDisabled : ImGuiCol.Text);

            draw.AddRectFilled(min, max, fill, 4f);
            draw.AddRect(min, max, border, 4f, ImDrawFlags.None, room.Selected ? 2f : 1f);
            draw.AddText(min + new Num.Vector2(7f, 5f), text, room.Name);
            string meta = "L" + room.Layer;
            if (!string.IsNullOrEmpty(room.Subregion)) meta += " · " + room.Subregion;
            if (room.OffScreenDen) meta += " · DEN";
            draw.AddText(min + new Num.Vector2(7f, 20f), ImGui.GetColorU32(ImGuiCol.TextDisabled), meta);
        }
    }

    private static void SynchronizeLocalPositions(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        HashSet<int> alive = new();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            alive.Add(room.RoomIndex);
            if (room.RoomIndex == draggingRoom) continue;
            LocalPositions[room.RoomIndex] = new Num.Vector2(room.X, room.Y);
        }

        if (LocalPositions.Count == alive.Count) return;
        List<int> remove = new();
        foreach (int key in LocalPositions.Keys)
            if (!alive.Contains(key)) remove.Add(key);
        for (int i = 0; i < remove.Count; i++) LocalPositions.Remove(remove[i]);
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
            Num.Vector2 p = GetLocalPosition(room);
            min = Num.Vector2.Min(min, p);
            max = Num.Vector2.Max(max, p + new Num.Vector2(130f, 42f));
            found = true;
        }

        if (!found)
        {
            zoom = 1f;
            pan = canvasSize * 0.5f;
            return;
        }

        Num.Vector2 span = Num.Vector2.Max(max - min, new Num.Vector2(1f, 1f));
        float availableX = Math.Max(100f, canvasSize.X - 80f);
        float availableY = Math.Max(100f, canvasSize.Y - 80f);
        zoom = Math.Max(0.2f, Math.Min(2.0f, Math.Min(availableX / span.X, availableY / span.Y)));
        Num.Vector2 center = (min + max) * 0.5f;
        pan = canvasSize * 0.5f - center * zoom;
    }

    private static Num.Vector2 GetLocalPosition(EditorMapRoomSnapshot room)
    {
        if (room != null && LocalPositions.TryGetValue(room.RoomIndex, out Num.Vector2 value)) return value;
        return room == null ? Num.Vector2.Zero : new Num.Vector2(room.X, room.Y);
    }

    private static Num.Vector2 ToScreen(Num.Vector2 canvasMin, Num.Vector2 world) =>
        canvasMin + pan + world * zoom;

    private static Num.Vector2 NodeSize() => new(130f * Math.Max(0.65f, Math.Min(1.3f, zoom)), 42f);

    private static void Select(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

    private static void SetPosition(int roomIndex, Num.Vector2 position) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomPosition,
            roomIndex: roomIndex,
            value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: position.X, y: position.Y)));

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static bool IsLayerVisible(int layer) =>
        layer >= 0 && layer < LayerVisible.Length ? LayerVisible[layer] : true;

    private static bool Matches(EditorMapRoomSnapshot room, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        return Contains(room.Name, query) || Contains(room.Subregion, query) ||
               (query.StartsWith("L", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(query.Substring(1), out int layer) && room.Layer == layer);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Contains(Num.Vector2 min, Num.Vector2 max, Num.Vector2 point) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private static float PositiveModulo(float value, float modulus)
    {
        if (modulus <= 0f) return 0f;
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }
}
