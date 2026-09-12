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
/// Region-level workspace for map layout, endpoint-based world topology and DryCycle world data.
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
    private static float inspectorWidth = 330f;
    private static bool draggingExplorerSplitter;
    private static bool draggingInspectorSplitter;

    private static int inspectorRoom = -1;
    private static Num.Vector2 inspectorPosition;
    private static string inspectorSubregion = string.Empty;

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
            DevToolWidgets.MutedText(DevToolUiSettings.T("当前区域地图不可用。", "The current region map is unavailable."), true);
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
        DevToolWidgets.MutedText("· " + (snapshot.Rooms?.Length ?? 0) + DevToolUiSettings.T(" 个房间", " rooms"));
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
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("保存", "Save"),
                "WorldWorkspaceSave",
                DevToolButtonTone.Primary))
        {
            Send(EditorUiCommandKind.Save);
            if (workspaceMode == WorkspaceMode.WorldData)
                WorldWorkspaceDataView.SaveDirty();
            if (WorldTopologyRegistry.Dirty)
                WorldTopologyRegistry.Save();
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
        if (DevToolWidgets.ActionButton(
                label,
                "WorldWorkspaceMode" + mode,
                workspaceMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            workspaceMode = mode;
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
        float right = showInspector ? Math.Max(220f, Math.Min(inspectorWidth, Math.Max(0f, maxSideSpace - left))) : 0f;

        if (showExplorer && showInspector && left + right > maxSideSpace)
        {
            float overflow = left + right - maxSideSpace;
            float leftGive = Math.Min(Math.Max(0f, left - 170f), overflow * 0.5f);
            left -= leftGive;
            overflow -= leftGive;
            right -= Math.Min(Math.Max(0f, right - 220f), overflow);
        }

        float center = Math.Max(120f, available.X - left - right - reserved);

        if (showExplorer)
        {
            if (ImGui.BeginChild("##WorldExplorer", new Num.Vector2(left, available.Y), ImGuiChildFlags.Borders))
                DrawExplorer(snapshot);
            ImGui.EndChild();
            ImGui.SameLine(0f, 0f);
            DrawSplitter("##WorldExplorerSplitter", ref explorerWidth, ref draggingExplorerSplitter, +1f, available.Y, 170f, 460f);
            ImGui.SameLine(0f, 0f);
        }

        if (ImGui.BeginChild("##WorldCanvas", new Num.Vector2(center, available.Y), ImGuiChildFlags.Borders))
            DrawCenter(snapshot);
        ImGui.EndChild();

        if (showInspector)
        {
            ImGui.SameLine(0f, 0f);
            DrawSplitter("##WorldInspectorSplitter", ref inspectorWidth, ref draggingInspectorSplitter, -1f, available.Y, 220f, 560f);
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
        ImGui.GetWindowDrawList().AddLine(new Num.Vector2(x, minPos.Y), new Num.Vector2(x, maxPos.Y), color, dragging ? 3f : 1f);
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
        ImGui.InputText(DevToolUiSettings.T("搜索##WorldExplorerSearch", "Search##WorldExplorerSearch"), ref search, 192);
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
            if (ImGui.Selectable(label + "##WorldRoom" + room.RoomIndex, room.RoomIndex == snapshot.SelectedRoomIndex))
            {
                SelectRoom(room.RoomIndex);
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
            }
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
            if (ImGui.Selectable(summary.Name + "  ·  " + summary.Count + "##WorldSubregion" + i, selected))
            {
                selectedSubregion = summary.Name;
                selectionKind = SelectionKind.Subregion;
                ClearConnectionSelection();
                if (summary.FirstRoom >= 0) SelectRoom(summary.FirstRoom);
            }
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
                DevToolWidgets.MutedText(DevToolUiSettings.T(
                    "目标出口不唯一，需要显式端点映射。",
                    "Target exit is ambiguous; explicit endpoint mapping is required."), true);
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
            if (ImGui.Selectable("⚠ " + issue.Title + "##WorldIssue" + i, false))
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                if (issue.RoomIndex >= 0) SelectRoom(issue.RoomIndex);
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
                DevToolWidgets.MutedText(DevToolUiSettings.T(
                    "连接以房间出口为端点。↔ 表示双向，→ / ← 表示单向；同一对房间的多条连接会平行显示。",
                    "Connections are endpoint-based. ↔ is bidirectional, → / ← is one-way; multiple links between the same rooms are drawn in parallel."), true);
                MapEditorView.DrawEmbeddedCanvas(snapshot);
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

    private static void DrawSubregionSummary(EditorMapPresentationSnapshot snapshot)
    {
        List<SubregionSummary> summaries = BuildSubregions(snapshot);
        for (int i = 0; i < summaries.Count; i++)
        {
            SubregionSummary summary = summaries[i];
            if (ImGui.Selectable(summary.Name + "  ·  " + summary.Count + DevToolUiSettings.T(" 个房间", " rooms") + "##CenterSubregion" + i))
            {
                selectedSubregion = summary.Name;
                selectionKind = SelectionKind.Subregion;
                if (summary.FirstRoom >= 0) SelectRoom(summary.FirstRoom);
            }
        }
    }

    private static void DrawValidationCenter(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> issues = BuildIssues(snapshot);
        if (issues.Count == 0)
        {
            ImGui.TextUnformatted(DevToolUiSettings.T("验证通过", "Validation passed"));
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "当前检查覆盖未分配子区域、孤立房间、无法唯一解析的出口连接和拓扑 sidecar 结构冲突。",
                "Checks currently cover unassigned subregions, isolated rooms, ambiguous exit routing and topology sidecar conflicts."), true);
            return;
        }

        ImGui.TextUnformatted(DevToolUiSettings.T("发现 ", "Found ") + issues.Count + DevToolUiSettings.T(" 个问题", " issue(s)"));
        ImGui.Separator();
        for (int i = 0; i < issues.Count; i++)
        {
            WorldIssue issue = issues[i];
            if (ImGui.Selectable("⚠ " + issue.Title + "##CenterIssue" + i))
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                if (issue.RoomIndex >= 0) SelectRoom(issue.RoomIndex);
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
            DevToolWidgets.MutedText(DevToolUiSettings.T("WorldTopology.json 有未保存修改。", "WorldTopology.json has unsaved changes."), true);
    }

    private static void DrawRoomInspector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
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

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled("L" + room.Layer);
        if (room.CurrentRoom) DevToolWidgets.MutedText(DevToolUiSettings.T("当前镜头房间", "Current camera room"));
        if (room.OffScreenDen) DevToolWidgets.MutedText(DevToolUiSettings.T("屏幕外巢穴", "Off-screen den"));
        if (room.Disabled) DevToolWidgets.MutedText(DevToolUiSettings.T("地图输出隐藏", "Hidden from map output"));
        ImGui.Separator();

        DevToolWidgets.MutedText(DevToolUiSettings.T("地图", "MAP"));
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

        ImGui.Spacing();
        ImGui.Separator();
        DevToolWidgets.MutedText(DevToolUiSettings.T("世界连接", "WORLD CONNECTIONS"));
        DrawRoomConnections(snapshot, room.RoomIndex);
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
        DrawMetric(
            DevToolUiSettings.T("方向", "Direction"),
            DirectionGlyph(connection.Direction));
        DrawMetric(
            DevToolUiSettings.T("来源", "Source"),
            connection.Explicit ? "WorldTopology.json" : "World / AbstractRoom");

        if (connection.Ambiguous)
        {
            ImGui.Spacing();
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "原版数据只记录目标房间，无法判断应该从目标房间的哪个出口进入。请为这组重复房间连接建立显式端点映射。",
                "Vanilla data records only the destination room, so the target exit cannot be determined. Create an explicit endpoint mapping for this repeated room link."), true);
        }

        if (connection.Explicit && connection.ConnectionId.StartsWith("explicit:", StringComparison.Ordinal))
        {
            ImGui.Spacing();
            DevToolWidgets.SectionHeader(DevToolUiSettings.T("连接方向", "CONNECTION DIRECTION"));
            string edgeId = connection.ConnectionId.Substring("explicit:".Length);
            DrawDirectionButton(snapshot.RegionName, edgeId, connection.Direction, WorldConnectionDirection.Bidirectional, "↔", "Both");
            ImGui.SameLine();
            DrawDirectionButton(snapshot.RegionName, edgeId, connection.Direction, WorldConnectionDirection.AToB, "→", "AToB");
            ImGui.SameLine();
            DrawDirectionButton(snapshot.RegionName, edgeId, connection.Direction, WorldConnectionDirection.BToA, "←", "BToA");
        }
    }

    private static void DrawDirectionButton(
        string region,
        string edgeId,
        WorldConnectionDirection current,
        WorldConnectionDirection candidate,
        string glyph,
        string id)
    {
        if (DevToolWidgets.ActionButton(
                glyph,
                "WorldDirection" + id,
                current == candidate ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            WorldTopologyRegistry.SetDirection(region, edgeId, candidate);
        }
    }

    private static void DrawSubregionInspector(EditorMapPresentationSnapshot snapshot)
    {
        int count = 0;
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (string.Equals(NormalizeSubregion(rooms[i].Subregion), selectedSubregion, StringComparison.Ordinal)) count++;

        ImGui.TextUnformatted(selectedSubregion);
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("房间", "Rooms"), count.ToString());
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "后续在这里继续加入批量分配、颜色和显示顺序。",
            "Batch assignment, color and display order can be added here next."), true);
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
            selection = room == null ? snapshot.RegionName : room.Name + " · L" + room.Layer +
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
        if (workspaceMode == WorkspaceMode.WorldData && WorldWorkspaceDataView.HasDirtyData)
            dirty += DevToolUiSettings.T("   ·   世界数据未保存", "   ·   world data dirty");
        if (WorldTopologyRegistry.Dirty)
            dirty += DevToolUiSettings.T("   ·   拓扑未保存", "   ·   topology dirty");

        ImGui.TextDisabled(selection + "   ·   " + mode + "   ·   " +
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
            Increment(degree, connections[i].FromRoomIndex);
            Increment(degree, connections[i].ToRoomIndex);
            if (connections[i].Ambiguous)
            {
                EditorMapRoomSnapshot room = FindRoom(snapshot, connections[i].FromRoomIndex);
                result.Add(new WorldIssue
                {
                    RoomIndex = connections[i].FromRoomIndex,
                    Title = DevToolUiSettings.T("出口连接不明确：", "Ambiguous exit link: ") +
                            ConnectionLabel(snapshot, connections[i]),
                    Detail = DevToolUiSettings.T(
                        "同一对房间存在重复连接，原版 ExitIndex 只能找到第一个目标出口。",
                        "The room pair is repeated and vanilla ExitIndex can only find the first target exit.")
                });
            }
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
                    Detail = DevToolUiSettings.T("该房间当前没有 subregionName。", "This room currently has no subregionName.")
                });
            }

            if (!room.OffScreenDen && (!degree.TryGetValue(room.RoomIndex, out int d) || d == 0))
            {
                result.Add(new WorldIssue
                {
                    RoomIndex = room.RoomIndex,
                    Title = DevToolUiSettings.T("孤立房间：", "Isolated room: ") + room.Name,
                    Detail = DevToolUiSettings.T("区域内没有可见连接。", "No in-region connection is visible for this room.")
                });
            }
        }

        List<WorldTopologyIssue> topologyIssues = WorldTopologyRegistry.ValidateRegion(
            snapshot.RegionName,
            roomName => FindRoomByName(snapshot, roomName) != null);
        for (int i = 0; i < topologyIssues.Count; i++)
        {
            result.Add(new WorldIssue
            {
                Title = DevToolUiSettings.T("拓扑数据：", "Topology: ") + topologyIssues[i].Kind,
                Detail = topologyIssues[i].Message
            });
        }

        return result;
    }

    private static void Increment(Dictionary<int, int> values, int key)
    {
        values.TryGetValue(key, out int value);
        values[key] = value + 1;
    }

    private static void DrawMetric(string key, string value)
    {
        DevToolWidgets.MutedText(key);
        ImGui.SameLine(Math.Max(120f, ImGui.GetWindowWidth() * 0.48f));
        ImGui.TextUnformatted(value ?? string.Empty);
    }

    private static void SynchronizeSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (selectionKind == SelectionKind.Connection &&
            FindConnection(snapshot, selectedConnectionId) == null)
        {
            ClearConnectionSelection();
            selectionKind = snapshot.SelectedRoomIndex >= 0 ? SelectionKind.Room : SelectionKind.Region;
        }

        if (selectionKind == SelectionKind.Room && FindRoom(snapshot, snapshot.SelectedRoomIndex) == null)
            selectionKind = SelectionKind.Region;
    }

    private static void SelectConnection(EditorMapConnectionSnapshot connection)
    {
        if (connection == null) return;
        selectedConnectionId = connection.ConnectionId ?? string.Empty;
        selectionKind = SelectionKind.Connection;
        SelectRoom(connection.FromRoomIndex);
    }

    private static void SelectRoom(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

    private static void SetPosition(int roomIndex, Num.Vector2 position) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomPosition,
            roomIndex: roomIndex,
            value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: position.X, y: position.Y)));

    private static void ClearConnectionSelection() => selectedConnectionId = string.Empty;

    private static EditorMapConnectionSnapshot FindConnection(
        EditorMapPresentationSnapshot snapshot,
        string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return null;
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
            if (string.Equals(connections[i].ConnectionId, connectionId, StringComparison.Ordinal)) return connections[i];
        return null;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static EditorMapRoomSnapshot FindRoomByName(EditorMapPresentationSnapshot snapshot, string roomName)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (string.Equals(rooms[i].Name, roomName, StringComparison.OrdinalIgnoreCase)) return rooms[i];
        return null;
    }

    private static string ConnectionLabel(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection)
    {
        string a = FindRoom(snapshot, connection.FromRoomIndex)?.Name ?? connection.FromRoomIndex.ToString();
        string b = FindRoom(snapshot, connection.ToRoomIndex)?.Name ?? connection.ToRoomIndex.ToString();
        return a + ":" + NodeText(connection.FromNodeIndex) + " " +
               DirectionGlyph(connection.Direction) + " " +
               b + ":" + NodeText(connection.ToNodeIndex);
    }

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "→",
        WorldConnectionDirection.BToA => "←",
        _ => "↔"
    };

    private static string NodeText(int node) => node < 0 ? "?" : node.ToString();

    private static string NormalizeSubregion(string value) =>
        string.IsNullOrWhiteSpace(value) ? DevToolUiSettings.T("未分配", "Unassigned") : value.Trim();

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
