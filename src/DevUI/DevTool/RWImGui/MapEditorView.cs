using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;
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
            DevToolWidgets.MutedText(DevToolUiSettings.T("地图编辑器不可用。", "Map editor unavailable."), true);
            return;
        }

        ImGui.TextDisabled(snapshot.RegionName + " · " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        string fitLabel = DevToolUiSettings.T("适配地图", "Fit Map");
        if (DevToolWidgets.ActionButton(fitLabel, "MapFit", DevToolButtonTone.Normal)) fitRequested = true;
        if (DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth("100%")))
        {
            if (DevToolWidgets.ActionButton("100%", "MapZoom100", DevToolButtonTone.Subtle)) zoom = 1f;
        }
        else if (DevToolWidgets.ActionButton("100%", "MapZoom100", DevToolButtonTone.Subtle))
        {
            zoom = 1f;
        }

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("图层", "LAYERS"));
        for (int i = 0; i < LayerVisible.Length; i++)
        {
            bool visible = LayerVisible[i];
            string layerLabel = "L" + i;
            if (ImGui.Checkbox(layerLabel + "##MapLayerFilter" + i, ref visible))
            {
                LayerVisible[i] = visible;
                fitRequested = true;
            }
            if (i < LayerVisible.Length - 1)
                DevToolWidgets.SameLineIfFits(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize("L" + (i + 1)).X);
        }

        ImGui.Spacing();
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索房间", "Search rooms"), "MapRoomSearch", ref search, 128);
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
            if (room.Disabled) label += DevToolUiSettings.T("  [隐藏]", "  [Hidden]");
            if (ImGui.Selectable(label + "##MapBrowserRoom" + room.RoomIndex, room.Selected))
                Select(room.RoomIndex);
        }
        if (matches == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的房间。", "No matching rooms."), true);
    }

    internal static void DrawInspector(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
        if (room == null)
        {
            inspectorRoom = -1;
            ImGui.TextWrapped(DevToolUiSettings.T("从图中或房间列表选择一个房间。", "Select a room from the graph or room list."));
            DrawGenericPageControls();
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
        ImGui.TextDisabled(DevToolUiSettings.T("房间索引 ", "Room index ") + room.RoomIndex);
        if (room.CurrentRoom) ImGui.TextDisabled(DevToolUiSettings.T("当前镜头房间", "Current camera room"));
        if (room.OffScreenDen) ImGui.TextDisabled(DevToolUiSettings.T("屏幕外巢穴", "Off-screen den"));
        if (room.Disabled) ImGui.TextDisabled(DevToolUiSettings.T("从地图输出中隐藏", "Hidden from map output"));
        ImGui.Separator();

        Num.Vector2 position = inspectorPosition;
        bool positionChanged = ImGui.InputFloat2(DevToolUiSettings.T("开发位置##MapInspectorPosition", "Dev position##MapInspectorPosition"), ref position, "%.1f");
        inspectorPosition = position;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SetPosition(room.RoomIndex, position);
        else if (!positionChanged && !ImGui.IsItemActive())
            inspectorPosition = new Num.Vector2(room.X, room.Y);

        int layer = room.Layer;
        if (ImGui.BeginCombo(DevToolUiSettings.T("图层##MapInspectorLayer", "Layer##MapInspectorLayer"), DevToolUiSettings.T("图层 ", "Layer ") + layer))
        {
            for (int i = 0; i < 3; i++)
            {
                bool selected = layer == i;
                if (ImGui.Selectable(DevToolUiSettings.T("图层 ", "Layer ") + i + "##MapLayer" + i, selected))
                    MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                        MapEditorCommandKind.SetRoomLayer,
                        roomIndex: room.RoomIndex,
                        value: new EditorPropertyValue(EditorPropertyKind.Integer, integer: i)));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        string subregion = inspectorSubregion;
        DevToolWidgets.MutedText(DevToolUiSettings.T("子区域", "Subregion"));
        ImGui.SetNextItemWidth(-1f);
        bool subregionChanged = ImGui.InputText("##MapInspectorSubregion", ref subregion, 128);
        inspectorSubregion = subregion;
        if (ImGui.IsItemDeactivatedAfterEdit())
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                MapEditorCommandKind.SetRoomSubregion,
                roomIndex: room.RoomIndex,
                text: subregion));
        else if (!subregionChanged && !ImGui.IsAnyItemActive())
            inspectorSubregion = room.Subregion ?? string.Empty;

        ImGui.Separator();
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "拖动中央图中的房间可直接修改 Dev Position；滚轮缩放，中键/右键拖动画布。",
            "Drag rooms in the center graph to edit Dev Position; wheel zooms and middle/right drag pans."), true);
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "Ctrl+S 使用 MapPage.SaveMapConfig() 保存地图配置。",
            "Ctrl+S saves through MapPage.SaveMapConfig()."), true);

        DrawGenericPageControls();
    }

    private static void DrawGenericPageControls()
    {
        UniversalDevUiPresentationSnapshot generic = UniversalDevUiPresentationHub.Current;
        if (generic?.Available != true) return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader(
                DevToolUiSettings.T("页面扩展控件##MapGenericPageControls", "PAGE EXTENSION CONTROLS##MapGenericPageControls")))
            return;

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "这里使用通用 DevInterface 协议镜像原版、RegionKit、DryCycle 和其他 Mod 注入的地图控件。",
            "Generic DevInterface protocols mirror vanilla, RegionKit, DryCycle and other mod-added Map controls here."), true);
        UniversalDevUiMirrorView.Draw(generic);
    }

    /// <summary>
    /// Standalone canvas retained for focus mode. Normal Map mode uses DrawEmbeddedCanvas inside
    /// the unified Browser | Map | Inspector workspace and therefore no longer opens a second
    /// floating Region Map window over the editor panel.
    /// </summary>
    internal static void DrawCanvas(EditorMapPresentationSnapshot snapshot, Num.Vector2 position, Num.Vector2 size)
    {
        if (!snapshot.Available || size.X < 120f || size.Y < 120f) return;

        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(420f, 300f), new Num.Vector2(4000f, 4000f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin(DevToolUiSettings.T("区域地图###DevToolMapCanvas", "Region Map###DevToolMapCanvas"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("MapCanvas");
        DrawCanvasSurface(snapshot, "##MapCanvasInput");
        ImGui.End();
    }

    internal static void DrawEmbeddedCanvas(EditorMapPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("地图编辑器不可用。", "Map editor unavailable."), true);
            return;
        }

        ImGui.TextDisabled(snapshot.RegionName + " · " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine();
        ImGui.TextDisabled("· " + Math.Round(zoom * 100f) + "%");
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("适配", "Fit"), "MapCanvasFit", DevToolButtonTone.Subtle))
            fitRequested = true;
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("100%", "MapCanvasZoom100", DevToolButtonTone.Subtle))
            zoom = 1f;

        ImGui.Spacing();
        DrawCanvasSurface(snapshot, "##MapEmbeddedCanvasInput");
    }

    private static void DrawCanvasSurface(EditorMapPresentationSnapshot snapshot, string inputId)
    {
        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 50f || canvasSize.Y < 50f)
            return;

        ImGui.InvisibleButton(inputId, canvasSize);
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

        if (hovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
            pan += io.MouseDelta;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 canvasMax = canvasMin + canvasSize;
        draw.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.ChildBg));
        draw.AddRect(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.Border));
        DrawGrid(draw, canvasMin, canvasSize);
        DrawConnections(draw, snapshot, canvasMin);
        DrawRooms(draw, snapshot, canvasMin, hovered, io);
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
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
            EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
            if (a == null || b == null || !IsLayerVisible(a.Layer) || !IsLayerVisible(b.Layer)) continue;

            Num.Vector2 pa = ToScreen(canvasMin, GetLocalPosition(a)) + NodeSize() * 0.5f;
            Num.Vector2 pb = ToScreen(canvasMin, GetLocalPosition(b)) + NodeSize() * 0.5f;
            Num.Vector2 delta = pb - pa;
            float length = delta.Length();
            Num.Vector2 normal = length > 0.001f
                ? new Num.Vector2(-delta.Y / length, delta.X / length)
                : Num.Vector2.Zero;

            int ordinal = 0;
            int total = 0;
            for (int j = 0; j < connections.Length; j++)
            {
                if (!SameRoomPair(connection, connections[j])) continue;
                if (j < i) ordinal++;
                total++;
            }

            float offsetAmount = (ordinal - (total - 1) * 0.5f) * 11f;
            Num.Vector2 offset = normal * offsetAmount;
            pa += offset;
            pb += offset;

            uint color = ImGui.GetColorU32(connection.Ambiguous ? ImGuiCol.TextDisabled : ImGuiCol.TextDisabled);
            draw.AddLine(pa, pb, color, connection.Explicit ? 2.4f : 2f);

            string arrow = DirectionGlyph(connection.Direction);
            if (connection.Ambiguous) arrow += " ?";
            Num.Vector2 labelSize = ImGui.CalcTextSize(arrow);
            Num.Vector2 midpoint = (pa + pb) * 0.5f;
            Num.Vector2 labelMin = midpoint - labelSize * 0.5f - new Num.Vector2(3f, 2f);
            Num.Vector2 labelMax = midpoint + labelSize * 0.5f + new Num.Vector2(3f, 2f);
            draw.AddRectFilled(labelMin, labelMax, ImGui.GetColorU32(ImGuiCol.ChildBg), 3f);
            draw.AddText(midpoint - labelSize * 0.5f, color, arrow);
        }
    }

    private static bool SameRoomPair(EditorMapConnectionSnapshot a, EditorMapConnectionSnapshot b) =>
        (a.FromRoomIndex == b.FromRoomIndex && a.ToRoomIndex == b.ToRoomIndex) ||
        (a.FromRoomIndex == b.ToRoomIndex && a.ToRoomIndex == b.FromRoomIndex);

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "→",
        WorldConnectionDirection.BToA => "←",
        _ => "↔"
    };

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
