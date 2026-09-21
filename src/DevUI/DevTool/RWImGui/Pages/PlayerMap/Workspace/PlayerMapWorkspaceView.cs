using System;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Rebuilt Canon / Player Map workspace. The frontend consumes immutable PlayerMap snapshots and
/// submits commands; it never calls vanilla MiniMap, RoomPanel.Update, or MapRenderOutput.
/// </summary>
internal static class PlayerMapWorkspaceView
{
    private const float MinZoom = 0.12f;
    private const float MaxZoom = 5f;
    private static readonly bool[] LayerVisible = { true, true, true };
    private static string search = string.Empty;
    private static Num.Vector2 pan;
    private static float zoom = 1f;
    private static bool fitRequested = true;
    private static bool showConnections = true;
    private static bool showRawOutput;
    private static int draggingRoom = -1;
    private static Num.Vector2 dragStartMouse;
    private static Vector2 dragStartPosition;
    private static Vector2 dragPreviewPosition;
    private static int inspectorRoom = -1;
    private static Num.Vector2 inspectorEffective;
    private static Num.Vector2 inspectorOffset;
    private static int selectedDefMaterial = -1;
    private static Num.Vector2 defA;
    private static Num.Vector2 defB;

    internal static Num.Vector2 Pan => pan;
    internal static float Zoom => zoom;
    internal static bool[] LayerVisibility => LayerVisible;
    internal static int SelectedDefMaterial
    {
        get => selectedDefMaterial;
        set => selectedDefMaterial = value;
    }
    internal static int DraggingRoom => draggingRoom;
    internal static Vector2 DragPreviewPosition => dragPreviewPosition;

    internal static void ResetRetainedState()
    {
        search = string.Empty;
        pan = default;
        zoom = 1f;
        fitRequested = true;
        showConnections = true;
        showRawOutput = false;
        LayerVisible[0] = true;
        LayerVisible[1] = true;
        LayerVisible[2] = true;
        draggingRoom = -1;
        inspectorRoom = -1;
        selectedDefMaterial = -1;
        inspectorEffective = default;
        inspectorOffset = default;
        defA = default;
        defB = default;
    }

    internal static void DrawBody(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot worldSnapshot)
    {
        PlayerMapPresentationSnapshot snapshot =
            PlayerMapWorkspaceRuntime.GetPresentation(DevToolRuntime.ActiveSession);
        if (snapshot?.Available != true)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("玩家地图工作区正在初始化。", "Player Map workspace is initializing."),
                true);
            return;
        }

        DrawToolbar(snapshot);
        ImGui.Separator();

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float left = Math.Max(280f, Math.Min(340f, available.X * 0.25f));
        float right = Math.Max(270f, Math.Min(355f, available.X * 0.30f));
        float center = Math.Max(260f, available.X - left - right - 16f);

        if (ImGui.BeginChild("##PlayerMapExplorer", new Num.Vector2(left, available.Y), ImGuiChildFlags.Borders))
            DrawExplorer(snapshot);
        ImGui.EndChild();
        ImGui.SameLine(0f, 8f);

        if (ImGui.BeginChild("##PlayerMapCanvas", new Num.Vector2(center, available.Y), ImGuiChildFlags.Borders))
            DrawCanvas(snapshot, worldSnapshot);
        ImGui.EndChild();
        ImGui.SameLine(0f, 8f);

        if (ImGui.BeginChild("##PlayerMapInspector", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            DrawInspector(snapshot);
        ImGui.EndChild();
    }

    private static void DrawToolbar(PlayerMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(DevToolUiSettings.T("玩家地图", "PLAYER MAP"));
        ImGui.SameLine();
        DevToolWidgets.MutedText(snapshot.RegionName + " · " + snapshot.Rooms.Length +
                                 DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine(0f, 16f);

        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("适配", "Fit"), "PlayerMapFit", DevToolButtonTone.Subtle))
            fitRequested = true;
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton("100%", "PlayerMap100", DevToolButtonTone.Subtle)) zoom = 1f;
        ImGui.SameLine(0f, 12f);

        for (int i = 0; i < 3; i++)
        {
            bool visible = LayerVisible[i];
            if (ImGui.Checkbox("L" + i + "##PlayerMapLayer" + i, ref visible))
            {
                LayerVisible[i] = visible;
                fitRequested = true;
            }
            if (i < 2) ImGui.SameLine();
        }

        ImGui.SameLine(0f, 12f);
        ImGui.Checkbox(DevToolUiSettings.T("连接##PlayerMapLinks", "Links##PlayerMapLinks"), ref showConnections);
        ImGui.SameLine(0f, 12f);
        bool raw = showRawOutput;
        if (ImGui.Checkbox(DevToolUiSettings.T("输出预览##PlayerMapRaw", "Output Preview##PlayerMapRaw"), ref raw))
            showRawOutput = raw;

        ImGui.SameLine(0f, 14f);
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("构建预览", "Build Preview"),
                "PlayerMapPreview",
                DevToolButtonTone.Normal))
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.BuildPreview));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "Render & Export",
                "PlayerMapExport",
                DevToolButtonTone.Primary))
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.RenderAndExport));

        PlayerMapLayoutAssist.DrawToolbar(snapshot);
        PlayerMapGroupLayerControls.DrawToolbar(snapshot);
        PlayerMapMigrationStreamView.DrawToolbar(snapshot);
    }

    private static void DrawExplorer(PlayerMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("房间", "ROOMS"));
        ImGui.TextDisabled(DevToolUiSettings.T("搜索房间", "Search rooms"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##PlayerMapSearch", ref search, 160);
        ImGui.Spacing();

        string normalized = (search ?? string.Empty).Trim();
        for (int i = 0; i < snapshot.Rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = snapshot.Rooms[i];
            if (!LayerVisible[Math.Max(0, Math.Min(2, room.Layer))]) continue;
            if (normalized.Length > 0 && room.Name.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) < 0) continue;

            string status = RoomStatusText(room);
            string placement = room.Disabled ? string.Empty : RoomPlacementText(room);
            string tooltip = room.Bake.Status == RoomMapBakeStatus.Failed
                ? room.Bake.Error
                : null;

            bool clicked = DevToolRoomExplorerEntry.Draw(
                room.Name,
                room.Name,
                "L" + room.Layer,
                status,
                placement,
                RoomStatusColor(room),
                room.Selected,
                tooltip);

            if (clicked)
                MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex: room.RoomIndex));
        }
    }

    private static string RoomStatusText(PlayerMapRoomSnapshot room)
    {
        if (room.Disabled)
            return DevToolUiSettings.T("已禁用", "Disabled");
        return room.Bake.Status switch
        {
            RoomMapBakeStatus.Ready => DevToolUiSettings.T("可用", "Ready"),
            RoomMapBakeStatus.Pending => DevToolUiSettings.T("准备中", "Preparing"),
            RoomMapBakeStatus.Failed => DevToolUiSettings.T("无法读取", "Unavailable"),
            _ => DevToolUiSettings.T("等待数据", "Waiting")
        };
    }

    private static string RoomPlacementText(PlayerMapRoomSnapshot room)
    {
        if (room.Mode == PlayerMapPlacementMode.Absolute)
            return DevToolUiSettings.T("独立位置", "Independent");
        return room.Offset.sqrMagnitude <= 0.0001f
            ? DevToolUiSettings.T("跟随世界布局", "World Layout")
            : DevToolUiSettings.T("跟随世界布局 + 偏移", "World Layout + Offset");
    }

    private static uint RoomStatusColor(PlayerMapRoomSnapshot room)
    {
        if (room.Disabled)
            return ImGui.GetColorU32(ImGuiCol.TextDisabled);
        Num.Vector4 color = room.Bake.Status switch
        {
            RoomMapBakeStatus.Ready => new Num.Vector4(0.45f, 0.82f, 0.50f, 1f),
            RoomMapBakeStatus.Pending => new Num.Vector4(0.92f, 0.72f, 0.34f, 1f),
            RoomMapBakeStatus.Failed => new Num.Vector4(0.92f, 0.38f, 0.34f, 1f),
            _ => new Num.Vector4(0.62f, 0.66f, 0.72f, 1f)
        };
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static void DrawCanvas(PlayerMapPresentationSnapshot snapshot, EditorMapPresentationSnapshot worldSnapshot)
    {
        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 80f || canvasSize.Y < 80f) return;
        ImGui.InvisibleButton("##PlayerMapCanvasInput", canvasSize);
        bool hovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 canvasMax = canvasMin + canvasSize;
        draw.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.ChildBg));
        draw.AddRect(canvasMin, canvasMax, ImGui.GetColorU32(ImGuiCol.Border));

        if (showRawOutput && snapshot.Preview.Available)
        {
            DrawRawPreview(draw, canvasMin, canvasSize, snapshot.Preview);
            return;
        }

        if (fitRequested)
        {
            Fit(snapshot, canvasSize);
            fitRequested = false;
        }

        if (hovered && Math.Abs(io.MouseWheel) > 0.0001f && draggingRoom < 0)
        {
            float oldZoom = zoom;
            float next = Math.Max(MinZoom, Math.Min(MaxZoom, zoom * (io.MouseWheel > 0f ? 1.12f : 0.89f)));
            Num.Vector2 mouse = io.MousePos - canvasMin;
            Num.Vector2 worldAtMouse = (mouse - pan) / oldZoom;
            zoom = next;
            pan = mouse - worldAtMouse * zoom;
        }
        if (hovered && draggingRoom < 0 &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right)))
            pan += io.MouseDelta;

        DrawGrid(draw, canvasMin, canvasSize);
        DrawDefMaterials(draw, snapshot, canvasMin);
        PlayerMapCanvasAuthoring.DrawCanvasOverlay(draw, snapshot, canvasMin);
        if (showConnections) DrawConnections(draw, snapshot, worldSnapshot, canvasMin);

        PlayerMapRoomSnapshot hoveredRoom = hovered ? HitRoom(snapshot, canvasMin, io.MousePos) : null;
        DrawRooms(draw, snapshot, canvasMin, hoveredRoom);
        HandleRoomInteraction(snapshot, hovered, hoveredRoom, canvasMin, io);
    }

    private static void DrawGrid(ImDrawListPtr draw, Num.Vector2 canvasMin, Num.Vector2 canvasSize)
    {
        float grid = 48f * zoom;
        if (grid < 14f) grid *= 4f;
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
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        for (int i = 0; i < snapshot.Rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = snapshot.Rooms[i];
            if (room.Disabled || !LayerVisible[Math.Max(0, Math.Min(2, room.Layer))]) continue;
            GetRoomRect(room, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            bool active = room.Selected || ReferenceEquals(room, hovered);

            if (room.Bake.Status == RoomMapBakeStatus.Ready && room.Bake.Runs.Length > 0)
            {
                float tile = PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom;
                for (int r = 0; r < room.Bake.Runs.Length; r++)
                {
                    RoomMapPreviewRun run = room.Bake.Runs[r];
                    // RoomMapBake uses Rain World's bottom-origin tile Y. ImGui uses top-origin
                    // screen Y, so mirror only the room-local row while leaving global placement intact.
                    float screenRow = room.Bake.Height - 1f - run.Y;
                    Num.Vector2 a = min + new Num.Vector2(run.X * tile, screenRow * tile);
                    Num.Vector2 b = a + new Num.Vector2(run.Length * tile, tile);
                    draw.AddRectFilled(a, b, PreviewColor(run.Kind, run.Water, room.Layer));
                }
            }
            else
            {
                draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));
            }

            uint outline = active ? ImGui.GetColorU32(ImGuiCol.HeaderActive) : ImGui.GetColorU32(ImGuiCol.Border);
            draw.AddRect(min, max, outline, 0f, ImDrawFlags.None, active ? 2.5f : 1f);
            if ((max - min).X > 42f && (max - min).Y > 18f)
                draw.AddText(min + new Num.Vector2(4f, 3f), ImGui.GetColorU32(ImGuiCol.Text), room.Name);
        }

        PlayerMapMultiSelection.DrawOverlay(draw, snapshot, canvasMin, hovered);
        PlayerMapLayoutAssist.DrawOverlay(draw, snapshot, canvasMin, hovered);
        PlayerMapLiveOverlapPreview.DrawOverlay(draw, snapshot, canvasMin, hovered);
        PlayerMapMigrationStreamView.DrawOverlay(draw, snapshot, canvasMin, hovered);
    }

    private static void DrawConnections(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        EditorMapPresentationSnapshot worldSnapshot,
        Num.Vector2 canvasMin)
    {
        if (PlayerMapMultiPipeConnections.Draw(draw, snapshot, worldSnapshot, canvasMin))
            return;
        if (worldSnapshot?.Connections == null) return;
        uint color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        for (int i = 0; i < worldSnapshot.Connections.Length; i++)
        {
            EditorMapConnectionSnapshot link = worldSnapshot.Connections[i];
            PlayerMapRoomSnapshot a = FindRoom(snapshot, link.FromRoomIndex);
            PlayerMapRoomSnapshot b = FindRoom(snapshot, link.ToRoomIndex);
            if (a == null || b == null || a.Disabled || b.Disabled ||
                !LayerVisible[Math.Max(0, Math.Min(2, a.Layer))] || !LayerVisible[Math.Max(0, Math.Min(2, b.Layer))]) continue;
            Num.Vector2 pa = canvasMin + pan + ToNum(DisplayPosition(a)) * zoom;
            Num.Vector2 pb = canvasMin + pan + ToNum(DisplayPosition(b)) * zoom;
            draw.AddLine(pa, pb, color, 1f);
        }
    }

    private static void DrawDefMaterials(ImDrawListPtr draw, PlayerMapPresentationSnapshot snapshot, Num.Vector2 canvasMin)
    {
        for (int i = 0; i < snapshot.DefaultMaterials.Length; i++)
        {
            PlayerMapDefMaterialSnapshot item = snapshot.DefaultMaterials[i];
            Num.Vector2 a = canvasMin + pan + ToNum(new Vector2(item.Left, item.Bottom)) * zoom;
            Num.Vector2 b = canvasMin + pan + ToNum(new Vector2(item.Right, item.Top)) * zoom;
            uint fill = item.Air ? 0x443366FFu : 0x44FF5533u;
            uint line = item.Id == selectedDefMaterial ? ImGui.GetColorU32(ImGuiCol.HeaderActive) : (item.Air ? 0xCC5577FFu : 0xCCFF7755u);
            draw.AddRectFilled(a, b, fill);
            draw.AddRect(a, b, line, 0f, ImDrawFlags.None, item.Id == selectedDefMaterial ? 2.5f : 1.5f);
        }
    }

    private static void HandleRoomInteraction(
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io)
    {
        PlayerMapGroupLayerControls.HandleShortcuts(snapshot, canvasHovered, io);
        if (PlayerMapCanvasAuthoring.OwnsCanvas)
            return;
        if (PlayerMapMultiSelection.HandleInteraction(snapshot, canvasHovered, hoveredRoom, canvasMin, io))
            return;

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hoveredRoom != null)
        {
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex: hoveredRoom.RoomIndex));
            draggingRoom = hoveredRoom.RoomIndex;
            dragStartMouse = io.MousePos;
            dragStartPosition = hoveredRoom.EffectivePosition;
            dragPreviewPosition = dragStartPosition;
        }

        if (draggingRoom >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            dragPreviewPosition = dragStartPosition + ToUnity((io.MousePos - dragStartMouse) / zoom);
        }
        else if (draggingRoom >= 0)
        {
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                PlayerMapCommandKind.SetEffectivePosition,
                roomIndex: draggingRoom,
                value: dragPreviewPosition));
            draggingRoom = -1;
        }
    }

    private static void DrawInspector(PlayerMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("玩家地图检查器", "PLAYER MAP INSPECTOR"));
        PlayerMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
        if (room != null)
            DrawRoomInspector(room);
        else
            DrawRegionInspector(snapshot);

        ImGui.Separator();
        DrawDefaultMaterialInspector(snapshot, room);
        PlayerMapCanvasAuthoring.DrawInspectorTools(snapshot, room);
        ImGui.Separator();
        PlayerMapRenderProgressView.Draw(snapshot);
        PlayerMapGroupPlacementControls.Draw(snapshot);
        PlayerMapGroupLayoutControls.Draw(snapshot);
        PlayerMapMigrationStreamView.DrawInspector(snapshot);
        PlayerMapPreflightPanel.Draw(snapshot);
    }

    private static void DrawRegionInspector(PlayerMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.RegionName);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "Player Map 默认由 World Layout ×1.5 派生；单房间拖动只记录 Offset，不会反向污染 World Layout。",
                "Player Map derives from World Layout ×1.5 by default; room drags store offsets without mutating World Layout."),
            true);
        DrawMetric("Rooms", snapshot.Rooms.Length.ToString());
        DrawMetric("Def_Mat", snapshot.DefaultMaterials.Length.ToString());
        DrawMetric("State", snapshot.Dirty ? "Modified" : "Saved");
    }

    private static void DrawRoomInspector(PlayerMapRoomSnapshot room)
    {
        if (inspectorRoom != room.RoomIndex)
        {
            inspectorRoom = room.RoomIndex;
            inspectorEffective = ToNum(room.EffectivePosition);
            inspectorOffset = ToNum(room.Offset);
        }
        else if (!ImGui.IsAnyItemActive())
        {
            inspectorEffective = ToNum(room.EffectivePosition);
            inspectorOffset = ToNum(room.Offset);
        }

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled("L" + room.Layer);
        ImGui.Separator();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("位置来源", "PLACEMENT"));
        bool derived = room.Mode == PlayerMapPlacementMode.Derived;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("跟随 World Layout", "Follow World Layout"),
                "PlayerMapDerived",
                derived ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.SetPlacementMode, roomIndex: room.RoomIndex, integer: 0));
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("独立", "Independent"),
                "PlayerMapAbsolute",
                !derived ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.SetPlacementMode, roomIndex: room.RoomIndex, integer: 1));

        DrawVectorMetric(DevToolUiSettings.T("World 位置", "World position"), room.WorldPosition);
        DrawVectorMetric(DevToolUiSettings.T("派生基底", "Derived base"), room.DerivedBasePosition);

        Num.Vector2 effective = inspectorEffective;
        bool effectiveChanged = ImGui.InputFloat2(
            DevToolUiSettings.T("最终位置##PlayerMapEffective", "Final position##PlayerMapEffective"),
            ref effective,
            "%.1f");
        inspectorEffective = effective;
        if (ImGui.IsItemDeactivatedAfterEdit())
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                PlayerMapCommandKind.SetEffectivePosition,
                roomIndex: room.RoomIndex,
                value: ToUnity(effective)));
        else if (!effectiveChanged && !ImGui.IsItemActive()) inspectorEffective = ToNum(room.EffectivePosition);

        if (derived)
        {
            Num.Vector2 offset = inspectorOffset;
            bool offsetChanged = ImGui.InputFloat2(
                DevToolUiSettings.T("微调 Offset##PlayerMapOffset", "Fine offset##PlayerMapOffset"),
                ref offset,
                "%.1f");
            inspectorOffset = offset;
            if (ImGui.IsItemDeactivatedAfterEdit())
                PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                    PlayerMapCommandKind.SetOffset,
                    roomIndex: room.RoomIndex,
                    value: ToUnity(offset)));
            else if (!offsetChanged && !ImGui.IsItemActive()) inspectorOffset = ToNum(room.Offset);

            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("清零 Offset", "Reset Offset"),
                    "PlayerMapResetOffset",
                    DevToolButtonTone.Subtle))
                PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.ResetOffset, roomIndex: room.RoomIndex));
        }

        int layer = room.Layer;
        if (ImGui.BeginCombo(DevToolUiSettings.T("地图图层##PlayerMapLayerCombo", "Map layer##PlayerMapLayerCombo"), "L" + layer))
        {
            for (int i = 0; i < 3; i++)
            {
                bool selected = i == layer;
                if (ImGui.Selectable("L" + i, selected))
                    PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.SetLayer, roomIndex: room.RoomIndex, integer: i));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DevToolWidgets.SectionHeader("ROOM BAKE");
        DrawMetric("Status", room.Bake.Status.ToString());
        DrawMetric("Size", room.Bake.Width + " × " + room.Bake.Height);
        if (!string.IsNullOrWhiteSpace(room.Bake.Error))
            DevToolWidgets.MutedText(room.Bake.Error, true);
    }

    private static void DrawDefaultMaterialInspector(PlayerMapPresentationSnapshot snapshot, PlayerMapRoomSnapshot room)
    {
        DevToolWidgets.SectionHeader("DEF_MAT");
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新建区域", "New Region"),
                "PlayerMapNewDef",
                DevToolButtonTone.Normal))
        {
            Vector2 center = room?.EffectivePosition ?? Vector2.zero;
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                PlayerMapCommandKind.CreateDefaultMaterial,
                value: center - new Vector2(45f, 30f),
                valueB: center + new Vector2(45f, 30f),
                flag: false));
        }

        for (int i = 0; i < snapshot.DefaultMaterials.Length; i++)
        {
            PlayerMapDefMaterialSnapshot item = snapshot.DefaultMaterials[i];
            bool selected = selectedDefMaterial == item.Id;
            ImGui.PushID(item.Id);
            if (ImGui.Selectable((item.Air ? "Air " : "Material ") + "#" + item.Id, selected))
            {
                selectedDefMaterial = item.Id;
                defA = ToNum(item.A);
                defB = ToNum(item.B);
            }
            ImGui.PopID();
        }

        PlayerMapDefMaterialSnapshot? current = FindDef(snapshot, selectedDefMaterial);
        if (!current.HasValue) return;
        PlayerMapDefMaterialSnapshot def = current.Value;
        if (!ImGui.IsAnyItemActive())
        {
            defA = ToNum(def.A);
            defB = ToNum(def.B);
        }

        Num.Vector2 a = defA;
        ImGui.InputFloat2("A##PlayerMapDefA", ref a, "%.1f");
        defA = a;
        bool commitA = ImGui.IsItemDeactivatedAfterEdit();
        Num.Vector2 b = defB;
        ImGui.InputFloat2("B##PlayerMapDefB", ref b, "%.1f");
        defB = b;
        bool commitB = ImGui.IsItemDeactivatedAfterEdit();
        if (commitA || commitB)
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                PlayerMapCommandKind.SetDefaultMaterialRect,
                integer: def.Id,
                value: ToUnity(defA),
                valueB: ToUnity(defB)));

        bool air = def.Air;
        if (ImGui.Checkbox(DevToolUiSettings.T("Air 区域", "Air region"), ref air))
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.SetDefaultMaterialAir, integer: def.Id, flag: air));
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("删除", "Delete"), "PlayerMapDeleteDef", DevToolButtonTone.Danger))
        {
            PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(PlayerMapCommandKind.DeleteDefaultMaterial, integer: def.Id));
            selectedDefMaterial = -1;
        }
    }

    internal static void DrawRenderReportBase(PlayerMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 状态", "RENDER STATUS"));
        PlayerMapRenderReport report = snapshot.RenderReport;
        if (!report.Attempted)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("尚未构建输出。Render 前会先做完整 Preflight。", "No output built yet. Render always runs a full preflight first."),
                true);
            return;
        }

        DrawMetric("Result", report.Success ? (report.Exported ? "Exported" : "Preview ready") : "Blocked / Failed");
        if (report.Width > 0 && report.Height > 0) DrawMetric("Output", report.Width + " × " + report.Height);
        if (report.IncludedRooms > 0) DrawMetric("Rooms", report.IncludedRooms.ToString());
        if (!string.IsNullOrWhiteSpace(report.OutputPath)) DevToolWidgets.MutedText(report.OutputPath, true);
        if (!string.IsNullOrWhiteSpace(report.Message)) DevToolWidgets.MutedText(report.Message, true);
        for (int i = 0; i < report.Errors.Length; i++)
            ImGui.TextWrapped("ERROR · " + report.Errors[i]);
        for (int i = 0; i < report.Warnings.Length; i++)
            ImGui.TextWrapped("WARN · " + report.Warnings[i]);
    }

    private static void DrawRawPreview(
        ImDrawListPtr draw,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        PlayerMapRenderPreview preview)
    {
        if (!preview.Available || preview.Width <= 0 || preview.Height <= 0) return;
        float scale = Math.Min((canvasSize.X - 24f) / preview.Width, (canvasSize.Y - 24f) / preview.Height);
        scale = Math.Max(0.02f, scale);
        Num.Vector2 size = new(preview.Width * scale, preview.Height * scale);
        Num.Vector2 origin = canvasMin + (canvasSize - size) * 0.5f;
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(ImGuiCol.Border));
        for (int i = 0; i < preview.Runs.Length; i++)
        {
            PlayerMapPreviewRun run = preview.Runs[i];
            Num.Vector2 a = origin + new Num.Vector2(run.X * scale, (preview.Height - run.Y - 1) * scale);
            Num.Vector2 b = a + new Num.Vector2(run.Length * scale, Math.Max(1f, scale));
            draw.AddRectFilled(a, b, Pack(run.Color));
        }
    }

    private static PlayerMapRoomSnapshot HitRoom(PlayerMapPresentationSnapshot snapshot, Num.Vector2 canvasMin, Num.Vector2 mouse)
    {
        for (int i = snapshot.Rooms.Length - 1; i >= 0; i--)
        {
            PlayerMapRoomSnapshot room = snapshot.Rooms[i];
            if (room.Disabled || !LayerVisible[Math.Max(0, Math.Min(2, room.Layer))]) continue;
            GetRoomRect(room, canvasMin, out Num.Vector2 min, out Num.Vector2 max);
            if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y) return room;
        }
        return null;
    }

    private static void GetRoomRect(PlayerMapRoomSnapshot room, Num.Vector2 canvasMin, out Num.Vector2 min, out Num.Vector2 max)
    {
        int width = Math.Max(1, room.Bake.Width);
        int height = Math.Max(1, room.Bake.Height);
        Vector2 position = DisplayPosition(room);
        Num.Vector2 center = canvasMin + pan + ToNum(position) * zoom;
        Num.Vector2 half = new(
            width * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f,
            height * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f);
        min = center - half;
        max = center + half;
    }

    private static Vector2 DisplayPosition(PlayerMapRoomSnapshot room) =>
        draggingRoom == room.RoomIndex ? dragPreviewPosition : room.EffectivePosition;

    private static void Fit(PlayerMapPresentationSnapshot snapshot, Num.Vector2 canvasSize)
    {
        bool any = false;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        for (int i = 0; i < snapshot.Rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = snapshot.Rooms[i];
            if (room.Disabled || !LayerVisible[Math.Max(0, Math.Min(2, room.Layer))]) continue;
            float width = Math.Max(1, room.Bake.Width) * PlayerMapCoordinateSystem.CanonPixelsPerTile;
            float height = Math.Max(1, room.Bake.Height) * PlayerMapCoordinateSystem.CanonPixelsPerTile;
            minX = Math.Min(minX, room.EffectivePosition.x - width * 0.5f);
            maxX = Math.Max(maxX, room.EffectivePosition.x + width * 0.5f);
            minY = Math.Min(minY, room.EffectivePosition.y - height * 0.5f);
            maxY = Math.Max(maxY, room.EffectivePosition.y + height * 0.5f);
            any = true;
        }
        if (!any)
        {
            zoom = 1f;
            pan = canvasSize * 0.5f;
            return;
        }
        float widthAll = Math.Max(1f, maxX - minX);
        float heightAll = Math.Max(1f, maxY - minY);
        zoom = Math.Max(MinZoom, Math.Min(MaxZoom,
            Math.Min((canvasSize.X - 36f) / widthAll, (canvasSize.Y - 36f) / heightAll)));
        Num.Vector2 center = new((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        pan = canvasSize * 0.5f - center * zoom;
    }

    private static PlayerMapRoomSnapshot FindRoom(PlayerMapPresentationSnapshot snapshot, int roomIndex)
    {
        if (snapshot?.Rooms == null) return null;
        for (int i = 0; i < snapshot.Rooms.Length; i++)
            if (snapshot.Rooms[i].RoomIndex == roomIndex) return snapshot.Rooms[i];
        return null;
    }

    private static PlayerMapDefMaterialSnapshot? FindDef(PlayerMapPresentationSnapshot snapshot, int id)
    {
        if (id < 0) return null;
        for (int i = 0; i < snapshot.DefaultMaterials.Length; i++)
            if (snapshot.DefaultMaterials[i].Id == id) return snapshot.DefaultMaterials[i];
        return null;
    }

    private static uint PreviewColor(RoomMapPixelKind kind, bool water, int layer)
    {
        Num.Vector4 c = kind switch
        {
            RoomMapPixelKind.Solid => new Num.Vector4(0.20f, 0.22f, 0.25f, 1f),
            RoomMapPixelKind.Structure => new Num.Vector4(0.55f, 0.30f, 0.28f, 1f),
            RoomMapPixelKind.BackWall => new Num.Vector4(0.38f, 0.40f, 0.43f, 1f),
            RoomMapPixelKind.RoomExit => new Num.Vector4(0.25f, 0.85f, 0.36f, 1f),
            RoomMapPixelKind.CreatureHole => new Num.Vector4(0.85f, 0.25f, 0.85f, 1f),
            RoomMapPixelKind.NormalShortcut => new Num.Vector4(0.85f, 0.85f, 0.85f, 1f),
            RoomMapPixelKind.NpcTransport => new Num.Vector4(0.75f, 0.28f, 0.20f, 1f),
            RoomMapPixelKind.RegionTransport => new Num.Vector4(0.12f, 0.12f, 0.12f, 1f),
            RoomMapPixelKind.UnknownShortcut => new Num.Vector4(0.35f, 0.35f, 0.35f, 1f),
            _ => new Num.Vector4(0.30f, 0.32f, 0.35f, 1f)
        };
        if (water)
        {
            c.X *= 0.72f;
            c.Y = c.Y * 0.72f + 0.08f;
            c.Z = Math.Min(1f, c.Z * 0.72f + 0.32f);
        }
        if (layer != 1)
        {
            c.X *= 0.78f;
            c.Y *= 0.78f;
            c.Z *= 0.78f;
        }
        return ImGui.ColorConvertFloat4ToU32(c);
    }

    private static uint Pack(Color32 color) =>
        (uint)(color.r | color.g << 8 | color.b << 16 | color.a << 24);

    private static void DrawMetric(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value ?? string.Empty);
    }

    private static void DrawVectorMetric(string label, Vector2 value) =>
        DrawMetric(label, value.x.ToString("0.##") + ", " + value.y.ToString("0.##"));

    private static float PositiveModulo(float value, float modulus)
    {
        if (modulus <= 0f) return 0f;
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private static Num.Vector2 ToNum(Vector2 value) => new(value.x, value.y);
    private static Vector2 ToUnity(Num.Vector2 value) => new(value.X, value.Y);
}

internal static class PlayerMapWorkspaceIntegration
{
    private static ManualLogSource log;
    private static bool enabled;
    private static bool playerMapActive;

    internal static bool Active => enabled && playerMapActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map integrated into World Workspace through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        playerMapActive = false;
        PlayerMapActivityGate.Reset();
        enabled = false;
        log = null;
    }

    internal static void DrawToolbar(
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        if (playerMapActive && WorldWorkspaceView.WorkspaceModeValue != 0)
        {
            playerMapActive = false;
            PlayerMapActivityGate.Reset();
        }

        ImGui.SameLine(0f, 8f);
        string label = playerMapActive
            ? DevToolUiSettings.T("返回世界地图", "Back to World Map")
            : DevToolUiSettings.T("玩家地图", "Player Map");
        if (DevToolWidgets.ActionButton(
                label,
                "WorldWorkspacePlayerMap",
                playerMapActive ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            playerMapActive = !playerMapActive;
            if (playerMapActive)
                WorldWorkspaceView.WorkspaceModeValue = 0;
            else
                PlayerMapActivityGate.Reset();
        }
    }

    internal static bool DrawBodyIfActive(
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || !playerMapActive)
            return false;

        PlayerMapActivityGate.MarkVisible();
        PlayerMapWorkspaceView.DrawBody(editor, snapshot);
        return true;
    }
}

internal static class PlayerMapPreflightPanel
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live preflight inspector enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
        PlayerMapPreflightSnapshot preflight = PlayerMapPreflightDiagnostics.Evaluate(snapshot, world);
        if (!preflight.Available) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 预检", "RENDER PREFLIGHT"));
        string state = preflight.CanRender
            ? DevToolUiSettings.T("可 Render", "Ready to Render")
            : DevToolUiSettings.T("需要处理", "Needs Attention");
        ImGui.TextUnformatted(state);

        DrawMetric("Rooms",
            preflight.ReadyRooms + " ready / " +
            preflight.PendingRooms + " pending / " +
            preflight.MissingRooms + " missing / " +
            preflight.FailedRooms + " failed");
        DrawMetric("Pipes",
            preflight.ExactConnections + " exact / " + preflight.AmbiguousConnections + " unresolved");
        if (preflight.InvalidEndpoints > 0)
            DrawMetric("Invalid endpoints", preflight.InvalidEndpoints.ToString());
        if (preflight.DuplicateEndpointClaims > 0)
            DrawMetric("Endpoint conflicts", preflight.DuplicateEndpointClaims.ToString());
        DrawMetric("Overlaps", preflight.OverlapPairs.ToString());
        if (preflight.Width > 0 && preflight.Height > 0)
            DrawMetric("Output", preflight.Width + " × " + preflight.Height);

        int errorLimit = Math.Min(6, preflight.Errors.Length);
        for (int i = 0; i < errorLimit; i++)
            ImGui.TextWrapped("ERROR · " + preflight.Errors[i]);
        if (preflight.Errors.Length > errorLimit)
            ImGui.TextDisabled("… +" + (preflight.Errors.Length - errorLimit) + " errors");

        int warningLimit = Math.Min(5, preflight.Warnings.Length);
        for (int i = 0; i < warningLimit; i++)
            ImGui.TextWrapped("WARN · " + preflight.Warnings[i]);
        if (preflight.Warnings.Length > warningLimit)
            ImGui.TextDisabled("… +" + (preflight.Warnings.Length - warningLimit) + " warnings");
    }

    private static void DrawMetric(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value ?? string.Empty);
    }

}

internal static class PlayerMapRenderProgressView
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live Render progress enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled)
        {
            PlayerMapWorkspaceView.DrawRenderReportBase(snapshot);
            return;
        }

        PlayerMapRenderProgressSnapshot render = PlayerMapRenderScheduler.Progress;
        if (render?.Running == true)
        {
            DrawRenderProgress(render);
            return;
        }

        PlayerMapRenderPreparationSnapshot preparation = PlayerMapRenderPreparationController.Progress;
        if (preparation?.Running == true)
        {
            DrawPreparationProgress(preparation);
            return;
        }

        PlayerMapWorkspaceView.DrawRenderReportBase(snapshot);
    }

    private static void DrawPreparationProgress(PlayerMapRenderPreparationSnapshot progress)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 进度", "RENDER PROGRESS"));
        ImGui.TextUnformatted(DevToolUiSettings.T("准备房间 Bake", "Preparing room bakes"));
        DrawProgressBar(progress.Progress);

        string percent = (progress.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string units = progress.ReadyRooms.ToString(CultureInfo.InvariantCulture) + " / " +
                       progress.TotalRooms.ToString(CultureInfo.InvariantCulture) + " rooms";
        ImGui.TextDisabled(percent + "  ·  " + units);
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            ImGui.TextWrapped(progress.Detail);

        if (progress.CanCancel && DevToolWidgets.ActionButton(
                DevToolUiSettings.T("取消 Render", "Cancel Render"),
                "PlayerMapCancelRenderPreparation",
                DevToolButtonTone.Danger))
            PlayerMapRenderPreparationController.RequestCancel();
    }

    private static void DrawRenderProgress(PlayerMapRenderProgressSnapshot progress)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 进度", "RENDER PROGRESS"));
        ImGui.TextUnformatted(progress.StageLabel ?? string.Empty);
        DrawProgressBar(progress.Progress);

        string percent = (progress.Progress * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string stagePercent = (progress.StageProgress * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        ImGui.TextDisabled(percent + "  ·  " + stagePercent + " " + DevToolUiSettings.T("阶段", "stage"));

        if (progress.TotalUnits > 1)
        {
            string units = progress.CompletedUnits.ToString("N0", CultureInfo.InvariantCulture) + " / " +
                           progress.TotalUnits.ToString("N0", CultureInfo.InvariantCulture);
            ImGui.TextDisabled(units);
        }
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            ImGui.TextWrapped(progress.Detail);

        if (progress.CanCancel)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("取消 Render", "Cancel Render"),
                    "PlayerMapCancelRender",
                    DevToolButtonTone.Danger))
                PlayerMapRenderScheduler.RequestCancel();
        }
        else
        {
            ImGui.TextDisabled(DevToolUiSettings.T("正在提交文件，已不可取消。", "Committing files; cancellation is disabled."));
        }
    }

    private static void DrawProgressBar(float fraction)
    {
        fraction = Math.Max(0f, Math.Min(1f, fraction));
        float width = Math.Max(80f, ImGui.GetContentRegionAvail().X);
        float height = Math.Max(8f, ImGui.GetFrameHeight() * 0.62f);
        Num.Vector2 min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##PlayerMapRenderProgressBar", new Num.Vector2(width, height));
        Num.Vector2 max = min + new Num.Vector2(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint bg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint fill = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        draw.AddRectFilled(min, max, bg, 2f);
        if (fraction > 0f)
        {
            Num.Vector2 fillMax = new(min.X + width * fraction, max.Y);
            draw.AddRectFilled(min, fillMax, fill, 2f);
        }
        draw.AddRect(min, max, border, 2f);
    }

}
