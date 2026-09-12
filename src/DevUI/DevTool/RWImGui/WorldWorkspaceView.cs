using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Unified region workspace. Map layout and world topology intentionally use different canvases:
/// MapEditorView edits MapPage Dev Position, while WorldGraphView edits only graph presentation
/// and endpoint-to-endpoint world connections.
/// </summary>
internal static class WorldWorkspaceView
{
    private enum WorkspaceMode
    {
        MapLayout,
        WorldGraph,
        WorldData,
        Subregions,
        Validation
    }

    private enum ExplorerMode
    {
        Rooms,
        Subregions,
        Connections,
        Issues
    }

    private enum SelectionKind
    {
        Region,
        Room,
        Connection,
        Subregion
    }

    private sealed class SubregionSummary
    {
        internal string Name = string.Empty;
        internal int Count;
        internal int FirstRoom = -1;
    }

    private sealed class WorldIssue
    {
        internal int RoomIndex = -1;
        internal string Title = string.Empty;
        internal string Detail = string.Empty;
    }

    private static WorkspaceMode workspaceMode = WorkspaceMode.MapLayout;
    private static ExplorerMode explorerMode = ExplorerMode.Rooms;
    private static SelectionKind selectionKind = SelectionKind.Region;
    private static string search = string.Empty;
    private static string selectedSubregion = string.Empty;
    private static string selectedConnectionId = string.Empty;

    private static float explorerWidth = 270f;
    private static float inspectorWidth = 350f;
    private static bool draggingExplorerSplitter;
    private static bool draggingInspectorSplitter;

    private static int inspectorRoom = -1;
    private static Num.Vector2 inspectorPosition;
    private static string inspectorSubregion = string.Empty;

    private static string mappingConnectionId = string.Empty;
    private static int mappingTargetNode = -1;
    private static WorldConnectionDirection mappingDirection = WorldConnectionDirection.Bidirectional;

    internal static void Draw(EditorPresentationSnapshot editor, Num.Vector2 display)
    {
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;

        float defaultX = 188f;
        float defaultY = 8f;
        float defaultWidth = Math.Max(720f, display.X - defaultX - 10f);
        float defaultHeight = Math.Max(430f, display.Y - defaultY - 10f);

        ImGui.SetNextWindowPos(new Num.Vector2(defaultX, defaultY), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, defaultHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(700f, 420f),
            new Num.Vector2(Math.Max(700f, display.X - 8f), Math.Max(420f, display.Y - 8f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("世界工作区###DevToolWorldWorkspace", "World Workspace###DevToolWorldWorkspace"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("WorldWorkspace");
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("当前区域地图不可用。", "The current region map is unavailable."),
                true);
            ImGui.End();
            return;
        }

        WorldTopologyRegistry.EnsureLoaded();
        SynchronizeSelection(snapshot);
        DrawToolbar(editor, snapshot);
        ImGui.Separator();

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float statusHeight = Math.Max(28f, ImGui.GetFrameHeight() + 8f);
        float bodyHeight = Math.Max(180f, available.Y - statusHeight);
        if (ImGui.BeginChild("##WorldWorkspaceBody", new Num.Vector2(0f, bodyHeight), ImGuiChildFlags.None))
            DrawBody(editor, snapshot);
        ImGui.EndChild();

        ImGui.Separator();
        DrawStatus(snapshot);
        ImGui.End();
    }

    private static void DrawToolbar(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.RegionName);
        ImGui.SameLine();
        DevToolWidgets.MutedText(
            "· " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
        ImGui.SameLine(0f, 18f);

        DrawWorkspaceModeButton(WorkspaceMode.MapLayout, DevToolUiSettings.T("地图布局", "Map Layout"));
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.WorldGraph, DevToolUiSettings.T("世界拓扑", "World Graph"));
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.WorldData, DevToolUiSettings.T("世界数据", "World Data"));
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.Subregions, DevToolUiSettings.T("子区域", "Subregions"));
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.Validation, DevToolUiSettings.T("验证", "Validation"));

        ImGui.SameLine(0f, 18f);
        bool anyDirty = WorldWorkspaceDataView.HasDirtyData || WorldTopologyRegistry.Dirty || WorldTextRegistry.Dirty;
        if (DevToolWidgets.ActionButton(
                anyDirty ? DevToolUiSettings.T("保存修改", "Save Changes") : DevToolUiSettings.T("保存", "Save"),
                "WorldWorkspaceSave",
                anyDirty ? DevToolButtonTone.Primary : DevToolButtonTone.Normal))
        {
            Send(EditorUiCommandKind.Save);
            if (WorldWorkspaceDataView.HasDirtyData) WorldWorkspaceDataView.SaveDirty();
            if (WorldTextRegistry.Dirty) WorldTextRegistry.Save();
            if (WorldTopologyRegistry.Dirty) WorldTopologyRegistry.Save();
        }

        ImGui.SameLine();
        if (!editor.CanUndo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("撤销", "Undo"),
                "WorldWorkspaceUndo",
                DevToolButtonTone.Normal))
            Send(EditorUiCommandKind.Undo);
        if (!editor.CanUndo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!editor.CanRedo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重做", "Redo"),
                "WorldWorkspaceRedo",
                DevToolButtonTone.Normal))
            Send(EditorUiCommandKind.Redo);
        if (!editor.CanRedo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("专注", "Focus"),
                "WorldWorkspaceFocus",
                DevToolButtonTone.Subtle))
            Send(EditorUiCommandKind.ToggleFocus);
    }

    private static void DrawWorkspaceModeButton(WorkspaceMode mode, string label)
    {
        if (!DevToolWidgets.ActionButton(
                label,
                "WorldWorkspaceMode" + mode,
                workspaceMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            return;

        workspaceMode = mode;
        if (mode != WorkspaceMode.WorldGraph)
            WorldGraphView.ClearConnectionSelection();
    }

    private static void DrawBody(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot)
    {
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        bool showExplorer = editor.BrowserOpen;
        bool showInspector = editor.InspectorOpen;
        float splitter = Math.Max(8f, ImGui.GetStyle().ItemSpacing.X);
        float reserved = (showExplorer ? splitter : 0f) + (showInspector ? splitter : 0f);

        float minCenter = 360f;
        float maxSideSpace = Math.Max(0f, available.X - minCenter - reserved);
        float left = showExplorer ? Math.Max(170f, Math.Min(explorerWidth, maxSideSpace * 0.48f)) : 0f;
        float right = showInspector ? Math.Max(230f, Math.Min(inspectorWidth, Math.Max(0f, maxSideSpace - left))) : 0f;

        if (showExplorer && showInspector && left + right > maxSideSpace)
        {
            float overflow = left + right - maxSideSpace;
            float leftGive = Math.Min(Math.Max(0f, left - 170f), overflow * 0.5f);
            left -= leftGive;
            overflow -= leftGive;
            right -= Math.Min(Math.Max(0f, right - 230f), overflow);
        }

        float center = Math.Max(120f, available.X - left - right - reserved);

        if (showExplorer)
        {
            if (ImGui.BeginChild("##WorldExplorer", new Num.Vector2(left, available.Y), ImGuiChildFlags.Borders))
                DrawExplorer(snapshot);
            ImGui.EndChild();
            ImGui.SameLine(0f, 0f);
            DrawSplitter(
                "##WorldExplorerSplitter",
                ref explorerWidth,
                ref draggingExplorerSplitter,
                +1f,
                available.Y,
                170f,
                460f);
            ImGui.SameLine(0f, 0f);
        }

        if (ImGui.BeginChild("##WorldCanvas", new Num.Vector2(center, available.Y), ImGuiChildFlags.Borders))
            DrawCenter(snapshot);
        ImGui.EndChild();

        SynchronizeGraphSelection(snapshot);

        if (showInspector)
        {
            ImGui.SameLine(0f, 0f);
            DrawSplitter(
                "##WorldInspectorSplitter",
                ref inspectorWidth,
                ref draggingInspectorSplitter,
                -1f,
                available.Y,
                230f,
                600f);
            ImGui.SameLine(0f, 0f);
            if (ImGui.BeginChild("##WorldInspector", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
                DrawInspector(snapshot);
            ImGui.EndChild();
        }
    }

    private static void DrawSplitter(
        string id,
        ref float width,
        ref bool dragging,
        float direction,
        float height,
        float min,
        float max)
    {
        float splitterWidth = Math.Max(8f, ImGui.GetStyle().ItemSpacing.X);
        ImGui.InvisibleButton(id, new Num.Vector2(splitterWidth, height));
        bool hovered = ImGui.IsItemHovered();
        if (!dragging && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) dragging = true;
        if (dragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) dragging = false;
        if (dragging)
            width = Math.Max(min, Math.Min(max, width + ImGui.GetIO().MouseDelta.X * direction));

        Num.Vector2 minPos = ImGui.GetItemRectMin();
        Num.Vector2 maxPos = ImGui.GetItemRectMax();
        float x = (minPos.X + maxPos.X) * 0.5f;
        uint color = ImGui.GetColorU32(hovered || dragging ? ImGuiCol.HeaderActive : ImGuiCol.Separator);
        ImGui.GetWindowDrawList().AddLine(
            new Num.Vector2(x, minPos.Y),
            new Num.Vector2(x, maxPos.Y),
            color,
            dragging ? 3f : 1f);
    }

    private static void DrawExplorer(EditorMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("世界浏览器", "WORLD EXPLORER"));
        DrawExplorerModeButton(ExplorerMode.Rooms, DevToolUiSettings.T("房间", "Rooms"));
        ImGui.SameLine();
        DrawExplorerModeButton(ExplorerMode.Subregions, DevToolUiSettings.T("子区域", "Subregions"));
        ImGui.SameLine();
        DrawExplorerModeButton(ExplorerMode.Connections, DevToolUiSettings.T("连接", "Connections"));
        ImGui.SameLine();
        DrawExplorerModeButton(ExplorerMode.Issues, DevToolUiSettings.T("问题", "Issues"));

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("搜索##WorldExplorerSearch", "Search##WorldExplorerSearch"),
            ref search,
            192);
        ImGui.Separator();

        switch (explorerMode)
        {
            case ExplorerMode.Rooms:
                DrawRoomExplorer(snapshot);
                break;
            case ExplorerMode.Subregions:
                DrawSubregionExplorer(snapshot);
                break;
            case ExplorerMode.Connections:
                DrawConnectionExplorer(snapshot);
                break;
            case ExplorerMode.Issues:
                DrawIssueExplorer(snapshot);
                break;
        }
    }

    private static void DrawExplorerModeButton(ExplorerMode mode, string label)
    {
        if (DevToolWidgets.ActionButton(
                label,
                "WorldExplorerMode" + mode,
                explorerMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            explorerMode = mode;
    }

    private static void DrawRoomExplorer(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        int visible = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!Matches(room.Name, room.Subregion, "L" + room.Layer)) continue;
            visible++;

            string marker = room.CurrentRoom ? "● " : room.OffScreenDen ? "◆ " : "  ";
            string label = marker + room.Name + "  L" + room.Layer;
            if (!string.IsNullOrEmpty(room.Subregion)) label += "  ·  " + room.Subregion;
            if (!ImGui.Selectable(label + "##WorldRoom" + room.RoomIndex, room.RoomIndex == snapshot.SelectedRoomIndex))
                continue;

            SelectRoom(room.RoomIndex);
            selectionKind = SelectionKind.Room;
            ClearConnectionSelection();
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的房间。", "No matching rooms."), true);
    }

    private static void DrawSubregionExplorer(EditorMapPresentationSnapshot snapshot)
    {
        List<SubregionSummary> summaries = BuildSubregions(snapshot);
        int visible = 0;
        for (int i = 0; i < summaries.Count; i++)
        {
            SubregionSummary summary = summaries[i];
            if (!Matches(summary.Name, summary.Count.ToString())) continue;
            visible++;
            bool selected = selectionKind == SelectionKind.Subregion &&
                            string.Equals(selectedSubregion, summary.Name, StringComparison.Ordinal);
            if (!ImGui.Selectable(summary.Name + "  ·  " + summary.Count + "##WorldSubregion" + i, selected))
                continue;

            selectedSubregion = summary.Name;
            selectionKind = SelectionKind.Subregion;
            ClearConnectionSelection();
            if (summary.FirstRoom >= 0) SelectRoom(summary.FirstRoom);
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的子区域。", "No matching subregions."), true);
    }

    private static void DrawConnectionExplorer(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        int visible = 0;
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            string label = ConnectionLabel(snapshot, connection);
            if (!Matches(label, connection.ConnectionId)) continue;
            visible++;

            bool selected = selectionKind == SelectionKind.Connection &&
                            string.Equals(selectedConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            if (ImGui.Selectable(label + "##WorldConnection" + i, selected))
                SelectConnection(connection);

            if (connection.Ambiguous)
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T(
                        "目标出口不唯一，需要指定端点。",
                        "Target exit is ambiguous; choose an endpoint."),
                    true);
        }

        if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的连接。", "No matching connections."), true);
    }

    private static void DrawIssueExplorer(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> issues = BuildIssues(snapshot);
        int visible = 0;
        for (int i = 0; i < issues.Count; i++)
        {
            WorldIssue issue = issues[i];
            if (!Matches(issue.Title, issue.Detail)) continue;
            visible++;
            if (ImGui.Selectable("⚠ " + issue.Title + "##WorldIssue" + i, false) && issue.RoomIndex >= 0)
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                SelectRoom(issue.RoomIndex);
            }
            if (!string.IsNullOrEmpty(issue.Detail))
                DevToolWidgets.MutedText(issue.Detail, true);
        }

        if (issues.Count == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("当前检查没有发现问题。", "No issues found by the current validation."), true);
        else if (visible == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的问题。", "No matching issues."), true);
    }

    private static void DrawCenter(EditorMapPresentationSnapshot snapshot)
    {
        string title = workspaceMode switch
        {
            WorkspaceMode.MapLayout => DevToolUiSettings.T("地图布局", "MAP LAYOUT"),
            WorkspaceMode.WorldGraph => DevToolUiSettings.T("世界拓扑", "WORLD GRAPH"),
            WorkspaceMode.WorldData => DevToolUiSettings.T("世界数据", "WORLD DATA"),
            WorkspaceMode.Subregions => DevToolUiSettings.T("子区域", "SUBREGIONS"),
            _ => DevToolUiSettings.T("区域验证", "REGION VALIDATION")
        };
        DevToolWidgets.PaneTitle(title);

        switch (workspaceMode)
        {
            case WorkspaceMode.MapLayout:
                MapEditorView.DrawEmbeddedCanvas(snapshot);
                break;

            case WorkspaceMode.WorldGraph:
                DrawWorldGraphHeader(snapshot);
                WorldGraphView.Draw(snapshot);
                break;

            case WorkspaceMode.WorldData:
                WorldWorkspaceDataView.Draw(snapshot);
                break;

            case WorkspaceMode.Subregions:
                DrawSubregionSummary(snapshot);
                break;

            case WorkspaceMode.Validation:
                DrawValidationCenter(snapshot);
                break;
        }
    }

    private static void DrawWorldGraphHeader(EditorMapPresentationSnapshot snapshot)
    {
        WorldTextRegistry.EnsureLoaded(snapshot.RegionName);
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "World Graph 使用独立布局，不会修改地图 Dev Position。拖动空闲 Exit 建立连接；↔/→/← 决定新连接方向；点击连线后 Delete 可断开。",
            "World Graph has its own layout and never changes map Dev Position. Drag free Exits to link them; ↔/→/← sets direction; click an edge and press Delete to disconnect."), true);

        if (!string.IsNullOrEmpty(WorldTopologyRegistry.LoadError))
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                DevToolUiSettings.T("WorldTopology 加载异常", "WorldTopology load error"));
            DevToolWidgets.MutedText(WorldTopologyRegistry.LoadError, true);
        }

        if (!string.IsNullOrEmpty(WorldTextRegistry.LoadError))
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                DevToolUiSettings.T("world.txt 编辑异常", "world.txt authoring error"));
            DevToolWidgets.MutedText(WorldTextRegistry.LoadError, true);
        }
        ImGui.Spacing();
    }

    private static void DrawSubregionSummary(EditorMapPresentationSnapshot snapshot)
    {
        List<SubregionSummary> summaries = BuildSubregions(snapshot);
        for (int i = 0; i < summaries.Count; i++)
        {
            SubregionSummary summary = summaries[i];
            if (!ImGui.Selectable(
                    summary.Name + "  ·  " + summary.Count + DevToolUiSettings.T(" 个房间", " rooms") +
                    "##CenterSubregion" + i))
                continue;

            selectedSubregion = summary.Name;
            selectionKind = SelectionKind.Subregion;
            ClearConnectionSelection();
            if (summary.FirstRoom >= 0) SelectRoom(summary.FirstRoom);
        }
    }

    private static void DrawValidationCenter(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> issues = BuildIssues(snapshot);
        if (issues.Count == 0)
        {
            ImGui.TextUnformatted(DevToolUiSettings.T("验证通过", "Validation passed"));
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "检查覆盖未分配子区域、孤立房间、重复链接歧义、无效端点，以及 WorldTopology 方向与实际 world.txt 连接不一致。",
                "Checks cover unassigned subregions, isolated rooms, ambiguous repeated links, invalid endpoints, and direction mismatches between WorldTopology and world.txt."), true);
            return;
        }

        ImGui.TextUnformatted(
            DevToolUiSettings.T("发现 ", "Found ") + issues.Count + DevToolUiSettings.T(" 个问题", " issue(s)"));
        ImGui.Separator();
        for (int i = 0; i < issues.Count; i++)
        {
            WorldIssue issue = issues[i];
            if (ImGui.Selectable("⚠ " + issue.Title + "##CenterIssue" + i) && issue.RoomIndex >= 0)
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                SelectRoom(issue.RoomIndex);
            }
            DevToolWidgets.MutedText(issue.Detail, true);
        }
    }

    private static void DrawInspector(EditorMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("检查器", "INSPECTOR"));

        if (selectionKind == SelectionKind.Connection && !string.IsNullOrEmpty(selectedConnectionId))
        {
            EditorMapConnectionSnapshot connection = FindConnection(snapshot, selectedConnectionId);
            if (connection != null)
            {
                DrawConnectionInspector(snapshot, connection);
                return;
            }
        }

        if (selectionKind == SelectionKind.Subregion && !string.IsNullOrEmpty(selectedSubregion))
        {
            DrawSubregionInspector(snapshot);
            return;
        }

        EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
        if (room != null)
        {
            selectionKind = SelectionKind.Room;
            DrawRoomInspector(snapshot, room);
        }
        else
        {
            selectionKind = SelectionKind.Region;
            DrawRegionInspector(snapshot);
        }
    }

    private static void DrawRegionInspector(EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.RegionName);
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("房间", "Rooms"), (snapshot.Rooms?.Length ?? 0).ToString());
        DrawMetric(DevToolUiSettings.T("连接", "Connections"), (snapshot.Connections?.Length ?? 0).ToString());
        DrawMetric(DevToolUiSettings.T("子区域", "Subregions"), BuildSubregions(snapshot).Count.ToString());
        DrawMetric(DevToolUiSettings.T("问题", "Issues"), BuildIssues(snapshot).Count.ToString());

        if (WorldTopologyRegistry.Dirty)
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "WorldTopology.json 有未保存修改。",
                "WorldTopology.json has unsaved changes."), true);
        if (WorldTextRegistry.Dirty)
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "world.txt 有未保存修改。",
                "world.txt has unsaved changes."), true);
    }

    private static void DrawRoomInspector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        SynchronizeRoomInspector(room);
        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled("L" + room.Layer);
        if (room.CurrentRoom) DevToolWidgets.MutedText(DevToolUiSettings.T("当前镜头房间", "Current camera room"));
        if (room.OffScreenDen) DevToolWidgets.MutedText(DevToolUiSettings.T("屏幕外巢穴", "Off-screen den"));
        if (room.Disabled) DevToolWidgets.MutedText(DevToolUiSettings.T("地图输出隐藏", "Hidden from map output"));
        ImGui.Separator();

        if (workspaceMode == WorkspaceMode.MapLayout || workspaceMode == WorkspaceMode.Subregions)
            DrawRoomMapProperties(room);
        else
        {
            DrawMetric(DevToolUiSettings.T("子区域", "Subregion"),
                string.IsNullOrWhiteSpace(room.Subregion) ? DevToolUiSettings.T("未分配", "Unassigned") : room.Subregion);
            DrawMetric(DevToolUiSettings.T("Exit 数", "Exits"), CountExitNodes(room).ToString());
        }

        ImGui.Spacing();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("世界连接", "WORLD CONNECTIONS"));
        DrawRoomConnections(snapshot, room.RoomIndex);

        ImGui.Spacing();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("节点", "NODES"));
        DrawRoomNodes(snapshot, room);
        DrawTopologyCommandStatus();
    }

    private static void SynchronizeRoomInspector(EditorMapRoomSnapshot room)
    {
        if (inspectorRoom != room.RoomIndex)
        {
            inspectorRoom = room.RoomIndex;
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
            return;
        }

        if (!ImGui.IsAnyItemActive())
        {
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
        }
    }

    private static void DrawRoomMapProperties(EditorMapRoomSnapshot room)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("地图", "MAP"));
        Num.Vector2 position = inspectorPosition;
        bool positionChanged = ImGui.InputFloat2(
            DevToolUiSettings.T("位置##WorldRoomPosition", "Position##WorldRoomPosition"),
            ref position,
            "%.1f");
        inspectorPosition = position;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SetPosition(room.RoomIndex, position);
        else if (!positionChanged && !ImGui.IsItemActive())
            inspectorPosition = new Num.Vector2(room.X, room.Y);

        int layer = room.Layer;
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("图层##WorldRoomLayer", "Layer##WorldRoomLayer"),
                "L" + layer))
        {
            for (int i = 0; i < 3; i++)
            {
                bool selected = layer == i;
                if (ImGui.Selectable("L" + i + "##WorldRoomLayer" + i, selected))
                    MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                        MapEditorCommandKind.SetRoomLayer,
                        roomIndex: room.RoomIndex,
                        value: new EditorPropertyValue(EditorPropertyKind.Integer, integer: i)));
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DevToolWidgets.MutedText(DevToolUiSettings.T("子区域", "Subregion"));
        ImGui.SetNextItemWidth(-1f);
        string subregion = inspectorSubregion;
        bool changed = ImGui.InputText("##WorldRoomSubregion", ref subregion, 128);
        inspectorSubregion = subregion;
        if (ImGui.IsItemDeactivatedAfterEdit())
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                MapEditorCommandKind.SetRoomSubregion,
                roomIndex: room.RoomIndex,
                text: subregion));
        else if (!changed && !ImGui.IsAnyItemActive())
            inspectorSubregion = room.Subregion ?? string.Empty;
    }

    private static void DrawRoomConnections(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        int count = 0;
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection.FromRoomIndex != roomIndex && connection.ToRoomIndex != roomIndex) continue;
            count++;
            if (ImGui.Selectable(ConnectionLabel(snapshot, connection) + "##RoomConnection" + i))
                SelectConnection(connection);
        }

        if (count == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有区域内连接。", "No in-region connections."));
    }

    private static void DrawRoomNodes(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        if (nodes.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有节点数据。", "No node data."));
            return;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            string target = node.ConnectedRoomIndex >= 0
                ? FindRoom(snapshot, node.ConnectedRoomIndex)?.Name ?? node.ConnectedRoomIndex.ToString()
                : DevToolUiSettings.T("未连接", "Disconnected");
            string line = node.NodeIndex + " · " + node.Type;
            if (node.Exit) line += "  →  " + target;
            ImGui.TextUnformatted(line);
        }

        if (workspaceMode == WorkspaceMode.WorldGraph)
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "创建连接请直接从 World Graph 的空闲 Exit 拖到目标 Exit。",
                "Create links by dragging a free Exit directly onto the target Exit in World Graph."), true);
    }

    private static void DrawConnectionInspector(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection)
    {
        ImGui.TextUnformatted(DevToolUiSettings.T("连接", "CONNECTION"));
        ImGui.Separator();
        ImGui.TextUnformatted(ConnectionLabel(snapshot, connection));
        ImGui.Spacing();

        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        DrawMetric(
            DevToolUiSettings.T("端点 A", "Endpoint A"),
            (a?.Name ?? connection.FromRoomIndex.ToString()) + ":" + connection.FromNodeIndex);
        DrawMetric(
            DevToolUiSettings.T("端点 B", "Endpoint B"),
            (b?.Name ?? connection.ToRoomIndex.ToString()) + ":" + NodeText(connection.ToNodeIndex));
        DrawMetric(DevToolUiSettings.T("方向", "Direction"), DirectionGlyph(connection.Direction));
        DrawMetric(
            DevToolUiSettings.T("来源", "Source"),
            connection.Explicit ? "WorldTopology.json" : "World / AbstractRoom");

        if (connection.Ambiguous)
            DrawAmbiguousConnectionEditor(snapshot, connection, a, b);
        else if (connection.Explicit)
            DrawExplicitConnectionEditor(snapshot, connection, a, b);
        else
            DrawVanillaConnectionEditor(snapshot, connection, a, b);

        DrawTopologyCommandStatus();
    }

    private static void DrawAmbiguousConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        ImGui.Spacing();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("指定目标端点", "RESOLVE TARGET ENDPOINT"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "重复房间链接只有目标房间名，原版无法知道应该进入哪个 Exit。选择目标 Exit 后会写入精确端点映射。",
            "Repeated room links only contain the target room name, so vanilla cannot identify the target Exit. Choose it to create an exact endpoint mapping."), true);

        if (a == null || b == null) return;
        if (!string.Equals(mappingConnectionId, connection.ConnectionId, StringComparison.Ordinal))
        {
            mappingConnectionId = connection.ConnectionId;
            mappingTargetNode = FirstCandidateTargetNode(snapshot, connection, b);
            mappingDirection = WorldConnectionDirection.Bidirectional;
        }

        List<int> candidates = CandidateTargetNodes(snapshot, connection, b);
        if (candidates.Count == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "没有可用目标 Exit。检查另一端是否真的指回源房间，或端点是否已被其它显式连接占用。",
                "No target Exit is available. Check that the other side points back to the source room and that the endpoint is not already owned."), true);
            return;
        }

        if (!candidates.Contains(mappingTargetNode)) mappingTargetNode = candidates[0];
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("目标出口##TopologyTargetExit", "Target Exit##TopologyTargetExit"),
                "Exit " + mappingTargetNode))
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                int node = candidates[i];
                bool selected = mappingTargetNode == node;
                if (ImGui.Selectable("Exit " + node + "##TargetExit" + node, selected))
                    mappingTargetNode = node;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DevToolWidgets.MutedText(DevToolUiSettings.T("方向", "Direction"));
        DrawMappingDirectionButtons();
        ImGui.Spacing();
        ImGui.TextUnformatted(
            a.Name + ":" + connection.FromNodeIndex + " " +
            DirectionGlyph(mappingDirection) + " " + b.Name + ":" + mappingTargetNode);

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("建立精确映射", "Create Exact Mapping"),
                "CreateTopologyMapping",
                DevToolButtonTone.Primary))
        {
            WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
                WorldTopologyCommandKind.AddExplicitMapping,
                region: snapshot.RegionName,
                roomA: a.Name,
                nodeA: connection.FromNodeIndex,
                roomB: b.Name,
                nodeB: mappingTargetNode,
                direction: mappingDirection));
        }
    }

    private static void DrawMappingDirectionButtons()
    {
        if (DirectionButton("↔", "MappingBoth", mappingDirection == WorldConnectionDirection.Bidirectional))
            mappingDirection = WorldConnectionDirection.Bidirectional;
        ImGui.SameLine();
        if (DirectionButton("→", "MappingAToB", mappingDirection == WorldConnectionDirection.AToB))
            mappingDirection = WorldConnectionDirection.AToB;
        ImGui.SameLine();
        if (DirectionButton("←", "MappingBToA", mappingDirection == WorldConnectionDirection.BToA))
            mappingDirection = WorldConnectionDirection.BToA;
    }

    private static void DrawExplicitConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        ImGui.Spacing();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("连接方向", "CONNECTION DIRECTION"));
        string edgeId = ExplicitEdgeId(connection.ConnectionId);
        if (edgeId.Length == 0) return;

        if (DirectionButton("↔", "ExplicitBoth", connection.Direction == WorldConnectionDirection.Bidirectional))
            QueueDirection(snapshot.RegionName, edgeId, WorldConnectionDirection.Bidirectional);
        ImGui.SameLine();
        if (DirectionButton("→", "ExplicitAToB", connection.Direction == WorldConnectionDirection.AToB))
            QueueDirection(snapshot.RegionName, edgeId, WorldConnectionDirection.AToB);
        ImGui.SameLine();
        if (DirectionButton("←", "ExplicitBToA", connection.Direction == WorldConnectionDirection.BToA))
            QueueDirection(snapshot.RegionName, edgeId, WorldConnectionDirection.BToA);

        DrawDisconnectButton(snapshot, connection, a, b, edgeId);
    }

    private static void DrawVanillaConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        if (a == null || b == null || connection.ToNodeIndex < 0) return;
        ImGui.Spacing();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("精确方向", "EXACT DIRECTION"));
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "普通原版连接当前是双向的。点击方向会把它升级为精确端点连接，再同步 world.txt 的单/双向关系。",
            "This vanilla link is bidirectional. Choosing a direction upgrades it to an exact endpoint edge and synchronizes world.txt."), true);

        if (DirectionButton("↔", "VanillaBoth", false))
            QueueExactUpgrade(snapshot, connection, a, b, WorldConnectionDirection.Bidirectional);
        ImGui.SameLine();
        if (DirectionButton("→", "VanillaAToB", false))
            QueueExactUpgrade(snapshot, connection, a, b, WorldConnectionDirection.AToB);
        ImGui.SameLine();
        if (DirectionButton("←", "VanillaBToA", false))
            QueueExactUpgrade(snapshot, connection, a, b, WorldConnectionDirection.BToA);

        DrawDisconnectButton(snapshot, connection, a, b, string.Empty);
    }

    private static void QueueExactUpgrade(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b,
        WorldConnectionDirection direction)
    {
        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.AddExplicitMapping,
            region: snapshot.RegionName,
            roomA: a.Name,
            nodeA: connection.FromNodeIndex,
            roomB: b.Name,
            nodeB: connection.ToNodeIndex,
            direction: direction));
    }

    private static void DrawDisconnectButton(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b,
        string edgeId)
    {
        if (a == null || b == null || connection.ToNodeIndex < 0) return;
        ImGui.Spacing();
        if (!DevToolWidgets.ActionButton(
                DevToolUiSettings.T("断开连接", "Disconnect"),
                "DisconnectWorldConnection",
                DevToolButtonTone.Danger))
            return;

        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.DeleteConnection,
            region: snapshot.RegionName,
            edgeId: edgeId,
            roomA: a.Name,
            nodeA: connection.FromNodeIndex,
            roomB: b.Name,
            nodeB: connection.ToNodeIndex));
        ClearConnectionSelection();
    }

    private static bool DirectionButton(string glyph, string id, bool selected) =>
        DevToolWidgets.ActionButton(
            glyph,
            "WorldDirection" + id,
            selected ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle);

    private static void QueueDirection(
        string region,
        string edgeId,
        WorldConnectionDirection direction)
    {
        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.SetDirection,
            region: region,
            edgeId: edgeId,
            direction: direction));
    }

    private static void DrawTopologyCommandStatus()
    {
        if (string.IsNullOrEmpty(WorldTopologyCommandQueue.LastStatus)) return;
        ImGui.Spacing();
        if (WorldTopologyCommandQueue.LastSucceeded)
            DevToolWidgets.MutedText(WorldTopologyCommandQueue.LastStatus, true);
        else
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                WorldTopologyCommandQueue.LastStatus);
    }

    private static List<int> CandidateTargetNodes(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot targetRoom)
    {
        List<int> result = new();
        EditorMapRoomNodeSnapshot[] nodes = targetRoom?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            if (!node.Exit || node.ConnectedRoomIndex != connection.FromRoomIndex) continue;
            if (EndpointOwnedByExplicitEdge(snapshot, targetRoom.RoomIndex, node.NodeIndex)) continue;
            result.Add(node.NodeIndex);
        }
        return result;
    }

    private static int FirstCandidateTargetNode(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot targetRoom)
    {
        List<int> candidates = CandidateTargetNodes(snapshot, connection, targetRoom);
        return candidates.Count > 0 ? candidates[0] : -1;
    }

    private static bool EndpointOwnedByExplicitEdge(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (!connection.Explicit) continue;
            if ((connection.FromRoomIndex == roomIndex && connection.FromNodeIndex == nodeIndex) ||
                (connection.ToRoomIndex == roomIndex && connection.ToNodeIndex == nodeIndex))
                return true;
        }
        return false;
    }

    private static void DrawSubregionInspector(EditorMapPresentationSnapshot snapshot)
    {
        int count = 0;
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (string.Equals(NormalizeSubregion(rooms[i].Subregion), selectedSubregion, StringComparison.Ordinal))
                count++;

        ImGui.TextUnformatted(selectedSubregion);
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("房间", "Rooms"), count.ToString());
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "批量分配和多选会在后续 WE 批量编辑阶段加入。",
            "Batch assignment and multi-selection will be added in the WE batch-editing stage."), true);
    }

    private static void DrawStatus(EditorMapPresentationSnapshot snapshot)
    {
        string selection;
        if (selectionKind == SelectionKind.Connection && !string.IsNullOrEmpty(selectedConnectionId))
        {
            EditorMapConnectionSnapshot connection = FindConnection(snapshot, selectedConnectionId);
            selection = connection == null ? snapshot.RegionName : ConnectionLabel(snapshot, connection);
        }
        else if (selectionKind == SelectionKind.Subregion && !string.IsNullOrEmpty(selectedSubregion))
        {
            selection = selectedSubregion;
        }
        else
        {
            EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
            selection = room == null
                ? snapshot.RegionName
                : room.Name + " · L" + room.Layer +
                  (string.IsNullOrEmpty(room.Subregion) ? string.Empty : " · " + room.Subregion);
        }

        string mode = workspaceMode switch
        {
            WorkspaceMode.MapLayout => DevToolUiSettings.T("地图布局", "Map Layout"),
            WorkspaceMode.WorldGraph => DevToolUiSettings.T("世界拓扑", "World Graph"),
            WorkspaceMode.WorldData => DevToolUiSettings.T("世界数据", "World Data"),
            WorkspaceMode.Subregions => DevToolUiSettings.T("子区域", "Subregions"),
            _ => DevToolUiSettings.T("验证", "Validation")
        };

        string dirty = string.Empty;
        if (WorldTextRegistry.Dirty) dirty += DevToolUiSettings.T(" · world.txt 未保存", " · world.txt dirty");
        if (WorldTopologyRegistry.Dirty) dirty += DevToolUiSettings.T(" · 拓扑未保存", " · topology dirty");
        if (WorldWorkspaceDataView.HasDirtyData) dirty += DevToolUiSettings.T(" · 世界数据未保存", " · world data dirty");

        ImGui.TextDisabled(
            selection + "   ·   " + mode + "   ·   " +
            (snapshot.Connections?.Length ?? 0) + DevToolUiSettings.T(" 条连接", " connections") + dirty);
    }

    private static List<SubregionSummary> BuildSubregions(EditorMapPresentationSnapshot snapshot)
    {
        Dictionary<string, SubregionSummary> map = new(StringComparer.Ordinal);
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            string key = NormalizeSubregion(rooms[i].Subregion);
            if (!map.TryGetValue(key, out SubregionSummary summary))
            {
                summary = new SubregionSummary { Name = key, FirstRoom = rooms[i].RoomIndex };
                map.Add(key, summary);
            }
            summary.Count++;
        }

        List<SubregionSummary> result = new(map.Values);
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static List<WorldIssue> BuildIssues(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> result = new();
        Dictionary<int, int> degree = new();
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            Increment(degree, connection.FromRoomIndex);
            Increment(degree, connection.ToRoomIndex);

            if (connection.Ambiguous)
            {
                result.Add(new WorldIssue
                {
                    RoomIndex = connection.FromRoomIndex,
                    Title = DevToolUiSettings.T("出口映射不明确：", "Ambiguous exit mapping: ") + ConnectionLabel(snapshot, connection),
                    Detail = DevToolUiSettings.T(
                        "重复房间连接需要指定精确目标 Exit。",
                        "Repeated room links require an exact target Exit.")
                });
            }

            if (connection.Explicit)
                AddDirectionConsistencyIssue(snapshot, connection, result);
        }

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (string.IsNullOrWhiteSpace(room.Subregion))
            {
                result.Add(new WorldIssue
                {
                    RoomIndex = room.RoomIndex,
                    Title = DevToolUiSettings.T("未分配子区域：", "No subregion: ") + room.Name,
                    Detail = DevToolUiSettings.T(
                        "该房间当前没有 subregionName。",
                        "This room currently has no subregionName.")
                });
            }

            if (!room.OffScreenDen && (!degree.TryGetValue(room.RoomIndex, out int d) || d == 0))
            {
                result.Add(new WorldIssue
                {
                    RoomIndex = room.RoomIndex,
                    Title = DevToolUiSettings.T("孤立房间：", "Isolated room: ") + room.Name,
                    Detail = DevToolUiSettings.T(
                        "区域内没有可见连接。",
                        "No in-region connection is visible for this room.")
                });
            }
        }

        List<WorldTopologyIssue> topologyIssues = WorldTopologyRegistry.ValidateRegion(
            snapshot.RegionName,
            roomName => FindRoom(snapshot, roomName) != null,
            (roomName, nodeIndex) => FindNode(FindRoom(snapshot, roomName), nodeIndex)?.Exit == true);

        for (int i = 0; i < topologyIssues.Count; i++)
        {
            WorldTopologyIssue issue = topologyIssues[i];
            result.Add(new WorldIssue
            {
                RoomIndex = -1,
                Title = "WorldTopology · " + issue.Kind,
                Detail = issue.Message
            });
        }

        return result;
    }

    private static void AddDirectionConsistencyIssue(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        List<WorldIssue> issues)
    {
        if (connection.ToNodeIndex < 0) return;
        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        EditorMapRoomNodeSnapshot nodeA = FindNode(a, connection.FromNodeIndex);
        EditorMapRoomNodeSnapshot nodeB = FindNode(b, connection.ToNodeIndex);
        if (a == null || b == null || nodeA == null || nodeB == null) return;

        bool expectAtoB = connection.Direction != WorldConnectionDirection.BToA;
        bool expectBtoA = connection.Direction != WorldConnectionDirection.AToB;
        bool actualAtoB = nodeA.ConnectedRoomIndex == b.RoomIndex;
        bool actualBtoA = nodeB.ConnectedRoomIndex == a.RoomIndex;
        if (expectAtoB == actualAtoB && expectBtoA == actualBtoA) return;

        issues.Add(new WorldIssue
        {
            RoomIndex = a.RoomIndex,
            Title = DevToolUiSettings.T("连接方向与 world.txt 不一致：", "Direction mismatch with world.txt: ") +
                    ConnectionLabel(snapshot, connection),
            Detail = DevToolUiSettings.T(
                "WorldTopology 箭头和 AbstractRoom/world.txt 的实际出向关系不同。请重新设置方向或修复 world.txt。",
                "The WorldTopology arrow differs from the actual AbstractRoom/world.txt outgoing relation. Reapply the direction or repair world.txt.")
        });
    }

    private static void Increment(Dictionary<int, int> values, int key)
    {
        values.TryGetValue(key, out int value);
        values[key] = value + 1;
    }

    private static int CountExitNodes(EditorMapRoomSnapshot room)
    {
        int count = 0;
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].Exit) count++;
        return count;
    }

    private static void DrawMetric(string key, string value)
    {
        DevToolWidgets.MutedText(key);
        ImGui.SameLine(Math.Max(120f, ImGui.GetWindowWidth() * 0.48f));
        ImGui.TextUnformatted(value ?? string.Empty);
    }

    private static void SynchronizeSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (selectionKind == SelectionKind.Connection && FindConnection(snapshot, selectedConnectionId) == null)
            ClearConnectionSelection();

        if (selectionKind == SelectionKind.Room && FindRoom(snapshot, snapshot.SelectedRoomIndex) == null)
            selectionKind = SelectionKind.Region;
    }

    private static void SynchronizeGraphSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (workspaceMode != WorkspaceMode.WorldGraph) return;
        string graphConnection = WorldGraphView.SelectedConnectionId;
        if (string.IsNullOrEmpty(graphConnection)) return;
        if (FindConnection(snapshot, graphConnection) == null) return;
        if (string.Equals(selectedConnectionId, graphConnection, StringComparison.Ordinal) &&
            selectionKind == SelectionKind.Connection)
            return;

        selectedConnectionId = graphConnection;
        selectionKind = SelectionKind.Connection;
    }

    private static void SelectConnection(EditorMapConnectionSnapshot connection)
    {
        if (connection == null) return;
        selectedConnectionId = connection.ConnectionId;
        selectionKind = SelectionKind.Connection;
        WorldGraphView.SelectConnection(connection.ConnectionId);
        SelectRoom(connection.FromRoomIndex);
        if (!string.Equals(mappingConnectionId, selectedConnectionId, StringComparison.Ordinal))
        {
            mappingConnectionId = string.Empty;
            mappingTargetNode = -1;
            mappingDirection = WorldConnectionDirection.Bidirectional;
        }
    }

    private static void SelectRoom(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

    private static void SetPosition(int roomIndex, Num.Vector2 position) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomPosition,
            roomIndex: roomIndex,
            value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: position.X, y: position.Y)));

    private static void ClearConnectionSelection()
    {
        selectedConnectionId = string.Empty;
        mappingConnectionId = string.Empty;
        mappingTargetNode = -1;
        mappingDirection = WorldConnectionDirection.Bidirectional;
        WorldGraphView.ClearConnectionSelection();
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, string roomName)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (string.Equals(rooms[i].Name, roomName, StringComparison.OrdinalIgnoreCase)) return rooms[i];
        return null;
    }

    private static EditorMapRoomNodeSnapshot FindNode(EditorMapRoomSnapshot room, int nodeIndex)
    {
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].NodeIndex == nodeIndex) return nodes[i];
        return null;
    }

    private static EditorMapConnectionSnapshot FindConnection(
        EditorMapPresentationSnapshot snapshot,
        string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
            if (string.Equals(connections[i].ConnectionId, id, StringComparison.Ordinal)) return connections[i];
        return null;
    }

    private static string ConnectionLabel(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection)
    {
        string a = (FindRoom(snapshot, connection.FromRoomIndex)?.Name ?? connection.FromRoomIndex.ToString()) +
                   ":" + connection.FromNodeIndex;
        string b = (FindRoom(snapshot, connection.ToRoomIndex)?.Name ?? connection.ToRoomIndex.ToString()) +
                   ":" + NodeText(connection.ToNodeIndex);
        return a + " " + DirectionGlyph(connection.Direction) + " " + b;
    }

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "→",
        WorldConnectionDirection.BToA => "←",
        _ => "↔"
    };

    private static string NodeText(int node) => node < 0 ? "?" : node.ToString();

    private static string ExplicitEdgeId(string connectionId)
    {
        const string prefix = "explicit:";
        return connectionId != null && connectionId.StartsWith(prefix, StringComparison.Ordinal)
            ? connectionId.Substring(prefix.Length)
            : string.Empty;
    }

    private static string NormalizeSubregion(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? DevToolUiSettings.T("未分配", "Unassigned")
            : value.Trim();

    private static bool Matches(params string[] values)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        string query = search.Trim();
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrEmpty(values[i]) &&
                values[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static void Send(EditorUiCommandKind kind) =>
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(kind));
}
