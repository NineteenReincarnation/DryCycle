using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Unified region workspace. The mapper sees one World Map where room placement and endpoint
/// topology are edited directly on the same geometry-aware canvas. The backend still keeps map
/// commands, world.txt authoring and endpoint topology as separate responsibilities.
/// </summary>
internal static class WorldWorkspaceView
{
    private enum WorkspaceMode
    {
        WorldMap,
        WorldData,
        Validation
    }

    private enum ExplorerMode
    {
        Subregions,
        Issues
    }

    private enum SelectionKind
    {
        Region,
        Room,
        Connection,
        Subregion
    }

    private sealed class RoomExplorerRow
    {
        internal EditorMapRoomSnapshot Room;
        internal string LayerToken = string.Empty;
    }

    private sealed class ConnectionExplorerRow
    {
        internal EditorMapConnectionSnapshot Connection;
        internal string Label = string.Empty;
    }

    private sealed class SubregionSummary
    {
        // Name is the authored subregion value. Empty means the room has no subregion and is
        // presented as the synthetic "None" group in the explorer.
        internal string Name = string.Empty;
        internal string DisplayName = string.Empty;
        internal int Count;
        internal int FirstRoom = -1;
        internal string CountText = string.Empty;
        internal string Label = string.Empty;
        internal readonly List<RoomExplorerRow> Rooms = new();
    }

    private sealed class WorldIssue
    {
        internal int RoomIndex = -1;
        internal string Title = string.Empty;
        internal string Detail = string.Empty;
        internal string Label = string.Empty;
    }

    private readonly struct EndpointKey : IEquatable<EndpointKey>
    {
        internal EndpointKey(int roomIndex, int nodeIndex)
        {
            RoomIndex = roomIndex;
            NodeIndex = nodeIndex;
        }

        private int RoomIndex { get; }
        private int NodeIndex { get; }

        public bool Equals(EndpointKey other) => RoomIndex == other.RoomIndex && NodeIndex == other.NodeIndex;
        public override bool Equals(object obj) => obj is EndpointKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                return RoomIndex * 397 ^ NodeIndex;
            }
        }
    }

    private static WorkspaceMode workspaceMode = WorkspaceMode.WorldMap;
    internal static int WorkspaceModeValue
    {
        get => (int)workspaceMode;
        set => workspaceMode = (WorkspaceMode)Math.Max(0, Math.Min(2, value));
    }

    private static ExplorerMode explorerMode = ExplorerMode.Subregions;
    private static SelectionKind selectionKind = SelectionKind.Region;
    private static string search = string.Empty;
    private static string selectedSubregion = string.Empty;
    private static string selectedConnectionId = string.Empty;
    private static int lastObservedRoomIndex = -1;

    private static float explorerWidth = 310f;
    private static float inspectorWidth = 344f;
    private static bool draggingExplorerSplitter;
    private static bool draggingInspectorSplitter;
    private static float explorerSplitterReveal;
    private static float inspectorSplitterReveal;

    private static int inspectorRoom = -1;
    private static string inspectorSubregion = string.Empty;
    private static string attractionSearch = string.Empty;
    private static string newAttraction = "Neutral";
    private static readonly string[] AttractionValues =
    {
        "Inherit",
        "Neutral",
        "Forbidden",
        "Avoid",
        "Like",
        "Stay"
    };

    private static string mappingConnectionId = string.Empty;
    private static int mappingTargetNode = -1;
    private static WorldConnectionDirection mappingDirection = WorldConnectionDirection.Bidirectional;
    private static readonly List<int> CandidatePreferredScratch = new();
    private static readonly List<int> CandidateFreeScratch = new();
    private static EditorMapConnectionSnapshot[] indexedExplicitEndpointConnections;
    private static readonly HashSet<EndpointKey> ExplicitEndpoints = new();

    private static EditorMapRoomSnapshot[] indexedRooms;
    private static readonly Dictionary<int, EditorMapRoomSnapshot> RoomsByIndex = new();
    private static readonly Dictionary<string, EditorMapRoomSnapshot> RoomsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private static EditorMapConnectionSnapshot[] indexedConnections;
    private static readonly Dictionary<string, EditorMapConnectionSnapshot> ConnectionsById =
        new(StringComparer.Ordinal);

    private static EditorMapRoomSnapshot[] projectedRoomRowsSource;
    private static RoomExplorerRow[] roomExplorerRows = Array.Empty<RoomExplorerRow>();

    private static EditorMapRoomSnapshot[] projectedConnectionRooms;
    private static EditorMapConnectionSnapshot[] projectedConnectionSource;
    private static ConnectionExplorerRow[] connectionExplorerRows = Array.Empty<ConnectionExplorerRow>();
    private static readonly Dictionary<string, string> ConnectionLabels = new(StringComparer.Ordinal);

    private static EditorMapRoomSnapshot[] projectedSubregionRooms;
    private static bool projectedSubregionChinese;
    private static readonly Dictionary<string, SubregionSummary> SubregionMap = new(StringComparer.Ordinal);
    private static readonly List<SubregionSummary> SubregionSummaries = new();

    private static EditorMapRoomSnapshot[] projectedIssueRooms;
    private static EditorMapConnectionSnapshot[] projectedIssueConnections;
    private static string projectedIssueRegion = string.Empty;
    private static int projectedIssueTopologyRevision = -1;
    private static bool projectedIssueChinese;
    private static readonly Dictionary<int, int> IssueDegree = new();
    private static readonly List<WorldIssue> WorldIssues = new();

    private static string observedSearch = null;
    private static string normalizedSearch = string.Empty;

    private static int toolbarRoomCount = -1;
    private static bool toolbarRoomCountChinese;
    private static string toolbarRoomCountText = string.Empty;

    private static EditorMapRoomSnapshot[] statusRooms;
    private static EditorMapConnectionSnapshot[] statusConnections;
    private static SelectionKind statusSelectionKind;
    private static int statusSelectedRoomIndex = int.MinValue;
    private static string statusConnectionId = null;
    private static string statusSubregion = null;
    private static WorkspaceMode statusWorkspaceMode;
    private static bool statusWorldTextDirty;
    private static bool statusTopologyDirty;
    private static bool statusWorldDataDirty;
    private static bool statusPropertiesDirty;
    private static bool statusChinese;
    private static string statusText = string.Empty;

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
        EnsureRoomIndex(snapshot);
        EnsureConnectionIndex(snapshot);
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

    internal static void ResetRetainedState()
    {
        indexedRooms = null;
        RoomsByIndex.Clear();
        RoomsByName.Clear();
        indexedConnections = null;
        ConnectionsById.Clear();
        indexedExplicitEndpointConnections = null;
        ExplicitEndpoints.Clear();
        CandidatePreferredScratch.Clear();
        CandidateFreeScratch.Clear();

        projectedRoomRowsSource = null;
        roomExplorerRows = Array.Empty<RoomExplorerRow>();
        projectedConnectionRooms = null;
        projectedConnectionSource = null;
        connectionExplorerRows = Array.Empty<ConnectionExplorerRow>();
        ConnectionLabels.Clear();
        projectedSubregionRooms = null;
        projectedSubregionChinese = false;
        SubregionMap.Clear();
        SubregionSummaries.Clear();
        projectedIssueRooms = null;
        projectedIssueConnections = null;
        projectedIssueRegion = string.Empty;
        projectedIssueTopologyRevision = -1;
        projectedIssueChinese = false;
        IssueDegree.Clear();
        WorldIssues.Clear();

        observedSearch = null;
        normalizedSearch = string.Empty;
        toolbarRoomCount = -1;
        toolbarRoomCountChinese = false;
        toolbarRoomCountText = string.Empty;
        statusRooms = null;
        statusConnections = null;
        statusSelectionKind = default;
        statusSelectedRoomIndex = int.MinValue;
        statusConnectionId = null;
        statusSubregion = null;
        statusWorkspaceMode = default;
        statusWorldTextDirty = false;
        statusTopologyDirty = false;
        statusWorldDataDirty = false;
        statusPropertiesDirty = false;
        statusChinese = false;
        statusText = string.Empty;

        explorerMode = ExplorerMode.Subregions;
        selectionKind = SelectionKind.Region;
        selectedSubregion = string.Empty;
        selectedConnectionId = string.Empty;
        lastObservedRoomIndex = -1;
        inspectorRoom = -1;
        inspectorSubregion = string.Empty;
        attractionSearch = string.Empty;
        newAttraction = "Neutral";
        mappingConnectionId = string.Empty;
        mappingTargetNode = -1;
        mappingDirection = WorldConnectionDirection.Bidirectional;
        draggingExplorerSplitter = false;
        draggingInspectorSplitter = false;
        explorerSplitterReveal = 0f;
        inspectorSplitterReveal = 0f;
        WorldMapView.ResetRetainedState();
    }

    private static void DrawToolbar(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.RegionName);
        ImGui.SameLine();
        DevToolWidgets.MutedText(GetToolbarRoomCount(snapshot.Rooms?.Length ?? 0));
        ImGui.SameLine(0f, 18f);

        DrawWorkspaceModeButton(WorkspaceMode.WorldMap, DevToolUiSettings.T("世界地图", "World Map"), "WorldWorkspaceModeMap");
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.WorldData, DevToolUiSettings.T("世界数据", "World Data"), "WorldWorkspaceModeData");
        ImGui.SameLine();
        DrawWorkspaceModeButton(WorkspaceMode.Validation, DevToolUiSettings.T("验证", "Validation"), "WorldWorkspaceModeValidation");

        ImGui.SameLine(0f, 18f);
        bool anyDirty = WorldWorkspaceDataView.HasDirtyData || WorldTopologyRegistry.Dirty ||
                        WorldTextRegistry.Dirty || WorldRoomAttractionRegistry.Dirty;
        if (DevToolWidgets.ActionButton(
                anyDirty ? DevToolUiSettings.T("保存修改", "Save Changes") : DevToolUiSettings.T("保存", "Save"),
                "WorldWorkspaceSave",
                anyDirty ? DevToolButtonTone.Primary : DevToolButtonTone.Normal))
        {
            // Save is a queued Core command. Do not persist individual files here: doing so races the
            // game-thread command and makes the toolbar behave differently from Ctrl/Cmd+S.
            Send(EditorUiCommandKind.Save);
        }

        ImGui.SameLine();
        if (!editor.CanUndo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("撤销", "Undo"), "WorldWorkspaceUndo"))
            Send(EditorUiCommandKind.Undo);
        if (!editor.CanUndo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!editor.CanRedo) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("重做", "Redo"), "WorldWorkspaceRedo"))
            Send(EditorUiCommandKind.Redo);
        if (!editor.CanRedo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("专注", "Focus"), "WorldWorkspaceFocus", DevToolButtonTone.Subtle))
            Send(EditorUiCommandKind.ToggleFocus);

        PlayerMapWorkspaceIntegration.DrawToolbar(editor, snapshot);
    }

    private static string GetToolbarRoomCount(int count)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (toolbarRoomCount == count && toolbarRoomCountChinese == chinese && toolbarRoomCountText.Length > 0)
            return toolbarRoomCountText;
        toolbarRoomCount = count;
        toolbarRoomCountChinese = chinese;
        toolbarRoomCountText = chinese ? "· " + count + " 个房间" : "· " + count + " rooms";
        return toolbarRoomCountText;
    }

    private static void DrawWorkspaceModeButton(WorkspaceMode mode, string label, string id)
    {
        if (DevToolWidgets.ActionButton(
                label,
                id,
                workspaceMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            PlayerMapWorkspaceIntegration.ExitSpecialView();
            workspaceMode = mode;
        }
    }

    private static void DrawBody(EditorPresentationSnapshot editor, EditorMapPresentationSnapshot snapshot)
    {
        if (PlayerMapWorkspaceIntegration.DrawBodyIfActive(editor, snapshot))
            return;

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        bool showExplorer = editor.BrowserOpen;
        bool showInspector = editor.InspectorOpen;
        float splitter = GetSplitterInteractionWidth();
        float reserved = (showExplorer ? splitter : 0f) + (showInspector ? splitter : 0f);
        float minCenter = 400f;
        float maxSideSpace = Math.Max(0f, available.X - minCenter - reserved);
        float left = showExplorer ? Math.Max(260f, Math.Min(explorerWidth, maxSideSpace * 0.46f)) : 0f;
        WorldInspectorReadability.NormalizeInspectorWidth(ref inspectorWidth);
        float right = showInspector ? Math.Max(238f, Math.Min(inspectorWidth, Math.Max(0f, maxSideSpace - left))) : 0f;

        if (!showExplorer)
        {
            draggingExplorerSplitter = false;
            explorerSplitterReveal = 0f;
        }
        if (!showInspector)
        {
            draggingInspectorSplitter = false;
            inspectorSplitterReveal = 0f;
        }

        if (showExplorer && showInspector && left + right > maxSideSpace)
        {
            float overflow = left + right - maxSideSpace;
            float leftGive = Math.Min(Math.Max(0f, left - 260f), overflow * 0.5f);
            left -= leftGive;
            overflow -= leftGive;
            right -= Math.Min(Math.Max(0f, right - 238f), overflow);
        }

        float center = Math.Max(160f, available.X - left - right - reserved);
        if (showExplorer)
        {
            if (ImGui.BeginChild("##WorldExplorer", new Num.Vector2(left, available.Y), ImGuiChildFlags.Borders))
            {
                DrawExplorer(snapshot);
                ScopedScrollChrome.Draw("WorldExplorer");
            }
            ImGui.EndChild();
            ImGui.SameLine(0f, 0f);
            DrawSplitter(
                "##WorldExplorerSplitter",
                ref explorerWidth,
                ref draggingExplorerSplitter,
                ref explorerSplitterReveal,
                +1f,
                available.Y,
                260f,
                450f);
            ImGui.SameLine(0f, 0f);
        }

        if (ImGui.BeginChild("##WorldCanvas", new Num.Vector2(center, available.Y), ImGuiChildFlags.Borders))
        {
            DrawCenter(snapshot);
            ScopedScrollChrome.Draw("WorldCenter");
        }
        ImGui.EndChild();
        SynchronizeCanvasSelection(snapshot);

        if (showInspector)
        {
            ImGui.SameLine(0f, 0f);
            DrawSplitter(
                "##WorldInspectorSplitter",
                ref inspectorWidth,
                ref draggingInspectorSplitter,
                ref inspectorSplitterReveal,
                -1f,
                available.Y,
                238f,
                590f);
            ImGui.SameLine(0f, 0f);
            if (ImGui.BeginChild("##WorldInspector", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                DrawInspector(snapshot);
                ScopedScrollChrome.Draw("WorldInspector", pruneAfter: true);
            }
            ImGui.EndChild();
        }
    }

    private static float GetSplitterInteractionWidth()
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        return Math.Max(
            18f,
            style.FramePadding.X * 2f + 8f);
    }

    private static void DrawSplitter(
        string id,
        ref float width,
        ref bool dragging,
        ref float reveal,
        float direction,
        float height,
        float min,
        float max)
    {
        float interactionWidth =
            GetSplitterInteractionWidth();

        ImGui.InvisibleButton(
            id,
            new Num.Vector2(
                interactionWidth,
                height));

        bool hovered =
            ImGui.IsItemHovered();
        if (hovered || dragging)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (!dragging &&
            hovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            dragging = true;

        if (dragging &&
            !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            dragging = false;

        ImGuiIOPtr io = ImGui.GetIO();
        if (dragging)
        {
            width =
                Math.Max(
                    min,
                    Math.Min(
                        max,
                        width +
                        io.MouseDelta.X *
                        direction));
        }

        // Stable frames stop changing state once the spring has converged. No allocations, render
        // targets, background sampling or blur passes are involved.
        float target =
            dragging
                ? 1f
                : hovered
                    ? 0.76f
                    : 0f;
        float delta =
            target - reveal;
        if (Math.Abs(delta) > 0.001f)
        {
            float dt =
                Math.Max(
                    0.001f,
                    Math.Min(
                        0.05f,
                        io.DeltaTime));
            float response =
                1f -
                (float)Math.Exp(
                    -18f * dt);
            reveal += delta * response;
        }
        else
        {
            reveal = target;
        }

        Num.Vector2 minPos =
            ImGui.GetItemRectMin();
        Num.Vector2 maxPos =
            ImGui.GetItemRectMax();
        float x =
            (minPos.X + maxPos.X) * 0.5f;

        DrawGlassSplitter(
            ImGui.GetWindowDrawList(),
            x,
            minPos.Y,
            maxPos.Y,
            reveal,
            dragging);
    }

    private static void DrawGlassSplitter(
        ImDrawListPtr draw,
        float x,
        float top,
        float bottom,
        float reveal,
        bool dragging)
    {
        float eased =
            reveal * reveal *
            (3f - 2f * reveal);

        float y0 = top + 2f;
        float y1 = bottom - 2f;

        // Stable-frame fast path: the normal resting splitter is one line and one style lookup.
        // All "glass" composition work exists only while hover/drag animation is visible.
        if (!dragging && eased <= 0.002f)
        {
            draw.AddLine(
                new Num.Vector2(x, y0),
                new Num.Vector2(x, y1),
                ImGui.GetColorU32(ImGuiCol.Separator),
                1.25f);
            return;
        }

        float glassWidth =
            1.5f +
            eased *
            (dragging ? 10.5f : 7.5f);
        float half =
            glassWidth * 0.5f;
        float rounding =
            Math.Max(
                1f,
                half);

        Num.Vector4 accent =
            ImGui.GetStyle().Colors[(int)(dragging
                    ? ImGuiCol.HeaderActive
                    : ImGuiCol.HeaderHovered)];
        Num.Vector4 background =
            ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        Num.Vector4 text =
            ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        float glassAlpha =
            0.10f +
            eased * 0.30f;
        Num.Vector4 glass =
            new(
                accent.X * 0.62f +
                background.X * 0.38f,
                accent.Y * 0.62f +
                background.Y * 0.38f,
                accent.Z * 0.62f +
                background.Z * 0.38f,
                glassAlpha);

        Num.Vector4 shadow =
            new(
                0f,
                0f,
                0f,
                0.05f +
                eased * 0.13f);
        Num.Vector4 highlight =
            new(
                text.X,
                text.Y,
                text.Z,
                0.05f +
                eased * 0.19f);
        Num.Vector4 core =
            new(
                accent.X,
                accent.Y,
                accent.Z,
                0.48f +
                eased * 0.42f);
        if (eased > 0.002f)
        {
            // Layered translucent surfaces mimic Apple's frosted-glass depth without performing
            // an actual blur pass. This keeps the splitter GPU-cheap and independent of the map
            // RenderTexture pipeline.
            draw.AddRectFilled(
                new Num.Vector2(
                    x - half - 1f,
                    y0 + 1f),
                new Num.Vector2(
                    x + half + 1f,
                    y1 + 1f),
                ImGui.GetColorU32(shadow),
                rounding + 1f);

            draw.AddRectFilled(
                new Num.Vector2(
                    x - half,
                    y0),
                new Num.Vector2(
                    x + half,
                    y1),
                ImGui.GetColorU32(glass),
                rounding);

            float highlightX =
                x - Math.Max(
                    0.5f,
                    half * 0.36f);
            draw.AddLine(
                new Num.Vector2(
                    highlightX,
                    y0 + rounding),
                new Num.Vector2(
                    highlightX,
                    y1 - rounding),
                ImGui.GetColorU32(highlight),
                1f);
        }

        float coreThickness =
            1.25f +
            eased * 1.65f;
        draw.AddLine(
            new Num.Vector2(x, y0),
            new Num.Vector2(x, y1),
            ImGui.GetColorU32(core),
            coreThickness);
    }

    private static void DrawExplorer(EditorMapPresentationSnapshot snapshot)
    {
        DevToolWidgets.PaneTitle(DevToolUiSettings.T("世界浏览器", "WORLD EXPLORER"));
        DrawExplorerModeButton(ExplorerMode.Subregions, DevToolUiSettings.T("子区域", "SubRegions"), "WorldExplorerModeSubregions");
        ImGui.SameLine();
        DrawExplorerModeButton(ExplorerMode.Issues, DevToolUiSettings.T("问题", "Issues"), "WorldExplorerModeIssues");

        ImGui.Spacing();
        string explorerSearchLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("搜索", "Search"),
                "WorldExplorerSearch",
                minimumControlWidth: 180f);
        ImGui.InputText(explorerSearchLabel, ref search, 192);
        ImGui.Separator();

        switch (explorerMode)
        {
            case ExplorerMode.Subregions:
                DrawSubregionExplorer(snapshot);
                break;
            case ExplorerMode.Issues:
                DrawIssueExplorer(snapshot);
                break;
        }
    }

    private static void DrawExplorerModeButton(ExplorerMode mode, string label, string id)
    {
        if (DevToolWidgets.ActionButton(label, id, explorerMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            explorerMode = mode;
    }

    private static void DrawRoomExplorer(EditorMapPresentationSnapshot snapshot)
    {
        EnsureRoomExplorerRows(snapshot);
        int visible = 0;
        for (int i = 0; i < roomExplorerRows.Length; i++)
        {
            RoomExplorerRow row = roomExplorerRows[i];
            EditorMapRoomSnapshot room = row.Room;
            if (!RoomMatchesSearch(row)) continue;
            visible++;

            string status = WorldRoomStatusText(room);
            string detail = WorldRoomDetailText(room);
            bool clicked = DevToolRoomExplorerEntry.Draw(
                "WorldRoom:" + room.RoomIndex,
                room.Name,
                row.LayerToken,
                status,
                detail,
                WorldRoomStatusColor(room),
                selectionKind == SelectionKind.Room && room.RoomIndex == snapshot.SelectedRoomIndex,
                out bool doubleClicked,
                WorldRoomTooltip(room));
            if (!clicked && !doubleClicked) continue;

            selectionKind = SelectionKind.Room;
            selectedSubregion = string.Empty;
            ClearConnectionSelection();
            lastObservedRoomIndex = room.RoomIndex;
            SelectRoom(room.RoomIndex);
            if (doubleClicked)
                ActivateCurrentRoomFromExplorer(room.RoomIndex);
        }
        if (visible == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的房间。", "No matching rooms."), true);
    }

    private static void EnsureRoomExplorerRows(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (ReferenceEquals(projectedRoomRowsSource, rooms)) return;

        RoomExplorerRow[] rows = new RoomExplorerRow[rooms.Length];
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            rows[i] = new RoomExplorerRow
            {
                Room = room,
                LayerToken = MapRoomLayer.Label(room.Layer)
            };
        }
        projectedRoomRowsSource = rooms;
        roomExplorerRows = rows;
    }

    private static bool RoomMatchesSearch(RoomExplorerRow row)
    {
        EditorMapRoomSnapshot room = row.Room;
        string status = WorldRoomStatusText(room);
        string detail = WorldRoomDetailText(room);
        return Matches(room.Name, room.Subregion, row.LayerToken) || Matches(status, detail);
    }

    private static string WorldRoomStatusText(EditorMapRoomSnapshot room)
    {
        if (room.CurrentRoom)
            return DevToolUiSettings.T("当前房间", "Current Room");
        if (room.OffScreenDen)
            return DevToolUiSettings.T("离屏巢穴", "Off-screen Den");
        if (room.Disabled)
            return DevToolUiSettings.T("已隐藏", "Hidden");
        return DevToolUiSettings.T("地图房间", "Map Room");
    }

    private static string WorldRoomDetailText(EditorMapRoomSnapshot room) =>
        string.IsNullOrWhiteSpace(room.Subregion)
            ? DevToolUiSettings.T("未分配子区域", "No subregion")
            : room.Subregion;

    private static string WorldRoomTooltip(EditorMapRoomSnapshot room)
    {
        if (room.OffScreenDen)
            return DevToolUiSettings.T(
                "离屏巢穴只参与世界拓扑，不作为普通房间缩略图输出。",
                "Off-screen dens participate in world topology but are not rendered as normal room thumbnails.");
        if (room.Disabled)
            return DevToolUiSettings.T("这个房间当前从地图输出中隐藏。", "This room is currently hidden from map output.");
        return null;
    }

    private static uint WorldRoomStatusColor(EditorMapRoomSnapshot room)
    {
        if (room.Disabled)
            return ImGui.GetColorU32(ImGuiCol.TextDisabled);
        if (room.CurrentRoom)
            return ImGui.GetColorU32(ImGuiCol.HeaderActive);

        Num.Vector4 color = room.OffScreenDen
            ? new Num.Vector4(0.92f, 0.72f, 0.34f, 1f)
            : new Num.Vector4(0.48f, 0.76f, 0.62f, 1f);
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static void DrawSubregionExplorer(EditorMapPresentationSnapshot snapshot)
    {
        List<SubregionSummary> summaries = GetSubregions(snapshot);
        bool searching = SearchQuery().Length > 0;
        int visibleGroups = 0;

        for (int i = 0; i < summaries.Count; i++)
        {
            SubregionSummary summary = summaries[i];
            bool groupMatches = Matches(summary.DisplayName, summary.CountText);
            int matchingRooms = 0;
            for (int r = 0; r < summary.Rooms.Count; r++)
                if (RoomMatchesSearch(summary.Rooms[r])) matchingRooms++;

            if (!groupMatches && matchingRooms == 0) continue;
            visibleGroups++;

            bool selected = selectionKind == SelectionKind.Subregion &&
                            string.Equals(selectedSubregion, summary.Name, StringComparison.Ordinal);
            ImGui.PushID("Subregion:" + (summary.Name.Length == 0 ? "<None>" : summary.Name));
            if (searching) ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
            if (selected) flags |= ImGuiTreeNodeFlags.Selected;

            string treeLabel = BuildResponsiveSubregionLabel(summary, out bool labelClipped);
            bool open = ImGui.TreeNodeEx(treeLabel, flags);
            bool groupHovered = ImGui.IsItemHovered();

            if (groupHovered && labelClipped)
                DevToolTooltip.Show(summary.DisplayName + " · " + summary.CountText);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                selectedSubregion = summary.Name;
                selectionKind = SelectionKind.Subregion;
                ClearConnectionSelection();
            }

            if (open)
            {
                int visibleRooms = 0;
                bool showWholeGroup = groupMatches;
                for (int r = 0; r < summary.Rooms.Count; r++)
                {
                    RoomExplorerRow row = summary.Rooms[r];
                    if (!showWholeGroup && !RoomMatchesSearch(row)) continue;

                    EditorMapRoomSnapshot room = row.Room;
                    visibleRooms++;
                    bool clicked = DevToolRoomExplorerEntry.Draw(
                        "WorldSubregionRoom:" + room.RoomIndex,
                        room.Name,
                        row.LayerToken,
                        WorldRoomStatusText(room),
                        WorldRoomDetailText(room),
                        WorldRoomStatusColor(room),
                        selectionKind == SelectionKind.Room && room.RoomIndex == snapshot.SelectedRoomIndex,
                        out bool doubleClicked,
                        WorldRoomTooltip(room));
                    if (!clicked && !doubleClicked) continue;

                    selectionKind = SelectionKind.Room;
                    selectedSubregion = string.Empty;
                    ClearConnectionSelection();
                    lastObservedRoomIndex = room.RoomIndex;
                    SelectRoom(room.RoomIndex);
                    if (doubleClicked)
                        ActivateCurrentRoomFromExplorer(room.RoomIndex);
                }

                if (visibleRooms == 0)
                    DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的房间。", "No matching rooms."), true);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        if (visibleGroups == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的子区域或房间。", "No matching subregions or rooms."), true);
    }

    private static string BuildResponsiveSubregionLabel(SubregionSummary summary, out bool clipped)
    {
        string suffix = "  ·  " + summary.CountText;
        float available = Math.Max(0f, ImGui.GetContentRegionAvail().X);
        ImGuiStylePtr style = ImGui.GetStyle();

        // TreeNode owns the arrow/bullet area inside the same content width. Reserve it explicitly,
        // then always preserve the room count and ellipsize only the authored subregion name.
        float treeChrome = ImGui.GetFrameHeight() + style.FramePadding.X * 2f + style.ItemSpacing.X;
        float suffixWidth = ImGui.CalcTextSize(suffix).X;
        float nameWidth = Math.Max(0f, available - treeChrome - suffixWidth - 4f);
        string visibleName = DevToolResponsiveText.Ellipsize(summary.DisplayName, nameWidth, out clipped);

        if (visibleName.Length == 0)
        {
            // Extremely narrow panels still show a meaningful row instead of drawing text under
            // the right edge. The full name remains available through the hover tooltip.
            visibleName = "…";
            clipped = true;
        }

        return visibleName + suffix;
    }

    private static void DrawConnectionExplorer(EditorMapPresentationSnapshot snapshot)
    {
        EnsureConnectionRows(snapshot);
        int visible = 0;
        for (int i = 0; i < connectionExplorerRows.Length; i++)
        {
            ConnectionExplorerRow row = connectionExplorerRows[i];
            EditorMapConnectionSnapshot connection = row.Connection;
            if (!Matches(row.Label, connection.ConnectionId)) continue;
            visible++;
            bool selected = selectionKind == SelectionKind.Connection &&
                            string.Equals(selectedConnectionId, connection.ConnectionId, StringComparison.Ordinal);
            ImGui.PushID(i);
            bool clicked = ImGui.Selectable(row.Label, selected);
            ImGui.PopID();
            if (clicked) SelectConnection(connection);
            if (connection.Ambiguous)
                DevToolWidgets.MutedText(DevToolUiSettings.T("目标 Exit 不唯一。", "Target Exit is ambiguous."), true);
        }
        if (visible == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的连接。", "No matching links."), true);
    }

    private static void EnsureConnectionRows(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        if (ReferenceEquals(projectedConnectionRooms, rooms) && ReferenceEquals(projectedConnectionSource, connections))
            return;

        EnsureRoomIndex(snapshot);
        ConnectionLabels.Clear();
        ConnectionExplorerRow[] rows = new ConnectionExplorerRow[connections.Length];
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            string label = BuildConnectionLabel(connection);
            rows[i] = new ConnectionExplorerRow { Connection = connection, Label = label };
            if (!string.IsNullOrEmpty(connection.ConnectionId))
                ConnectionLabels[connection.ConnectionId] = label;
        }
        projectedConnectionRooms = rooms;
        projectedConnectionSource = connections;
        connectionExplorerRows = rows;
    }

    private static void DrawIssueExplorer(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> issues = GetIssues(snapshot);
        int visible = 0;
        for (int i = 0; i < issues.Count; i++)
        {
            WorldIssue issue = issues[i];
            if (!Matches(issue.Title, issue.Detail)) continue;
            visible++;
            ImGui.PushID(i);
            bool clicked = ImGui.Selectable(issue.Label, false);
            ImGui.PopID();
            if (clicked && issue.RoomIndex >= 0)
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                lastObservedRoomIndex = issue.RoomIndex;
                SelectRoom(issue.RoomIndex);
            }
            if (!string.IsNullOrEmpty(issue.Detail)) DevToolWidgets.MutedText(issue.Detail, true);
        }
        if (issues.Count == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有发现问题。", "No issues found."), true);
        else if (visible == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的问题。", "No matching issues."), true);
    }

    private static void DrawCenter(EditorMapPresentationSnapshot snapshot)
    {
        switch (workspaceMode)
        {
            case WorkspaceMode.WorldMap:
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("世界地图", "WORLD MAP"));
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T(
                        "拖房间调整地图位置；拖 Exit 创建连接；点击连接后可改 ↔ / → / ←。房间缩略图包含 tile、曲面地形和 DryCycle 自定义地形。",
                        "Drag rooms to arrange the map; drag Exits to link rooms; select a link to edit ↔ / → / ←. Room thumbnails include tiles, curved terrain and DryCycle custom terrain."),
                    true);
                WorldMapView.Draw(snapshot);
                break;
            case WorkspaceMode.WorldData:
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("世界数据", "WORLD DATA"));
                WorldWorkspaceDataView.Draw(snapshot);
                break;
            case WorkspaceMode.Validation:
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("区域验证", "REGION VALIDATION"));
                DrawValidationCenter(snapshot);
                break;
        }
    }

    private static void DrawValidationCenter(EditorMapPresentationSnapshot snapshot)
    {
        List<WorldIssue> issues = GetIssues(snapshot);
        if (issues.Count == 0)
        {
            ImGui.TextUnformatted(DevToolUiSettings.T("验证通过", "Validation passed"));
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "当前检查覆盖孤立房间、未分配子区域、重复链接歧义、端点冲突和箭头与 world.txt 实际方向不一致。",
                    "Checks cover isolated rooms, missing subregions, repeated-link ambiguity, endpoint conflicts and direction mismatches with world.txt."),
                true);
            return;
        }

        ImGui.TextUnformatted(DevToolUiSettings.IsChinese ? "发现 " + issues.Count + " 个问题" : "Found " + issues.Count + " issue(s)");
        ImGui.Separator();
        for (int i = 0; i < issues.Count; i++)
        {
            WorldIssue issue = issues[i];
            ImGui.PushID(i);
            bool clicked = ImGui.Selectable(issue.Label);
            ImGui.PopID();
            if (clicked && issue.RoomIndex >= 0)
            {
                selectionKind = SelectionKind.Room;
                ClearConnectionSelection();
                lastObservedRoomIndex = issue.RoomIndex;
                SelectRoom(issue.RoomIndex);
            }
            DevToolWidgets.MutedText(issue.Detail, true);
        }
    }

    private static void DrawInspector(EditorMapPresentationSnapshot snapshot)
    {
        using (WorldInspectorReadability.Enter())
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

        if (selectionKind == SelectionKind.Subregion)
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
    }

    private static void DrawRegionInspector(EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextUnformatted(snapshot.RegionName);
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("房间", "Rooms"), (snapshot.Rooms?.Length ?? 0).ToString());
        DrawMetric(DevToolUiSettings.T("连接", "Links"), (snapshot.Connections?.Length ?? 0).ToString());
        DrawMetric(DevToolUiSettings.T("子区域", "Subregions"), GetSubregions(snapshot).Count.ToString());
        DrawMetric(DevToolUiSettings.T("问题", "Issues"), GetIssues(snapshot).Count.ToString());
        DrawDirtyHints();
    }

    private static void DrawRoomInspector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        if (inspectorRoom != room.RoomIndex)
        {
            inspectorRoom = room.RoomIndex;
            inspectorSubregion = room.Subregion ?? string.Empty;
        }
        else if (!ImGui.IsAnyItemActive())
        {
            inspectorSubregion = room.Subregion ?? string.Empty;
        }

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(MapRoomLayer.Label(room.Layer));
        if (room.CurrentRoom) DevToolWidgets.MutedText(DevToolUiSettings.T("当前镜头房间", "Current camera room"));
        if (room.OffScreenDen) DevToolWidgets.MutedText(DevToolUiSettings.T("屏幕外巢穴", "Off-screen den"));
        if (room.Disabled) DevToolWidgets.MutedText(DevToolUiSettings.T("地图输出隐藏", "Hidden from map output"));
        ImGui.Separator();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("地图", "MAP"));
        DrawRoomLayerButtons(room);

        if (!WorldSubregionSelector.DrawField(snapshot, room))
        {
            string subregionLabel =
                WorldInspectorReadability.PrepareField(
                    DevToolUiSettings.T("子区域", "Subregion"),
                    "WorldRoomSubregion");
            string subregion = inspectorSubregion;
            bool changed =
                ImGui.InputText(
                    subregionLabel,
                    ref subregion,
                    128);
            inspectorSubregion = subregion;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                MapEditorCommandQueue.Enqueue(
                    new MapEditorCommand(
                        MapEditorCommandKind.SetRoomSubregion,
                        roomIndex: room.RoomIndex,
                        text: subregion));
            }
            else if (!changed &&
                     !ImGui.IsAnyItemActive())
            {
                inspectorSubregion =
                    room.Subregion ??
                    string.Empty;
            }
        }

        WorldCreatureSpawnInspector.DrawIntegrated(snapshot, room);
        DrawRoomAttractions(room);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("世界连接", "WORLD LINKS"));
        DrawRoomConnections(snapshot, room.RoomIndex);
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("节点", "NODES"));
        DrawRoomNodes(snapshot, room);
    }

    private static void DrawRoomAttractions(EditorMapRoomSnapshot room)
    {
        EditorSession session = DevToolSessionHub.Current;
        if (session == null || room == null) return;

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("生物吸引度", "CREATURE ATTRACTION"));

        if (!WorldRoomAttractionRegistry.EnsureLoaded(session))
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                WorldRoomAttractionRegistry.LoadError ?? DevToolUiSettings.T("Properties.txt 无法读取", "Properties.txt unavailable"));
            return;
        }

        string source = Path.GetFileName(WorldRoomAttractionRegistry.LoadedPath ?? string.Empty);
        if (!string.IsNullOrEmpty(source))
            DevToolWidgets.MutedText(DevToolUiSettings.T("来源：", "Source: ") + source, true);

        WorldRoomAttractionEntry[] overrides =
            WorldRoomAttractionRegistry.GetRoomOverrides(session, room.RoomIndex);
        HashSet<string> overridden = new(StringComparer.Ordinal);

        if (overrides.Length == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "当前房间没有显式覆盖，全部继承区域默认值。",
                    "No explicit room overrides; all creatures inherit region defaults."),
                true);
        }
        else
        {
            for (int i = 0; i < overrides.Length; i++)
            {
                WorldRoomAttractionEntry entry = overrides[i];
                overridden.Add(entry.CreatureId);
                ImGui.PushID("RoomAttr:" + entry.CreatureId);
                ImGui.TextUnformatted(entry.CreatureId);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##Value", entry.Attraction))
                {
                    for (int v = 0; v < AttractionValues.Length; v++)
                    {
                        string value = AttractionValues[v];
                        bool selected = string.Equals(entry.Attraction, value, StringComparison.Ordinal);
                        if (ImGui.Selectable(
                                DevToolUiSettings.T(
                                    value == "Inherit" ? "继承" : value,
                                    value),
                                selected))
                        {
                            QueueRoomAttraction(room.RoomIndex, entry.CreatureId, value);
                        }
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("添加覆盖", "Add override"));
        string attractionSearchLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("搜索生物", "Search creature"),
                "RoomAttrSearch");
        ImGui.InputText(
            attractionSearchLabel,
            ref attractionSearch,
            128);

        string attractionValueLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("新值", "New value"),
                "RoomAttrNewValue");
        if (ImGui.BeginCombo(
                attractionValueLabel,
                newAttraction))
        {
            for (int v = 1; v < AttractionValues.Length; v++)
            {
                string value = AttractionValues[v];
                bool selected = string.Equals(newAttraction, value, StringComparison.Ordinal);
                if (ImGui.Selectable(value, selected))
                    newAttraction = value;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var registered = ExtEnum<CreatureTemplate.Type>.values?.entries;
        if (registered == null || registered.Count == 0)
            return;

        if (ImGui.BeginChild("##RoomAttrCreatureList", new Num.Vector2(0f, 132f), ImGuiChildFlags.Borders))
        {
            string filter = (attractionSearch ?? string.Empty).Trim();
            int shown = 0;
            for (int i = 0; i < registered.Count; i++)
            {
                string creatureId = registered[i];
                if (string.IsNullOrWhiteSpace(creatureId) || overridden.Contains(creatureId))
                    continue;
                if (filter.Length > 0 &&
                    creatureId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string effective = WorldRoomAttractionRegistry.GetEffective(
                    session,
                    room.RoomIndex,
                    creatureId);
                string label = creatureId +
                               (string.IsNullOrEmpty(effective)
                                   ? string.Empty
                                   : DevToolUiSettings.T(" · 继承=", " · inherited=") + effective);
                if (ImGui.Selectable(label + "##AddRoomAttr" + i))
                    QueueRoomAttraction(room.RoomIndex, creatureId, newAttraction);

                shown++;
            }

            if (shown == 0)
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T("没有匹配的未覆盖生物。", "No matching creature without an override."),
                    true);
        }
        ImGui.EndChild();
    }

    private static void QueueRoomAttraction(
        int roomIndex,
        string creatureId,
        string attraction)
    {
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomAttraction,
            roomIndex: roomIndex,
            text: attraction,
            key: creatureId));
    }

    private static void DrawRoomConnections(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EnsureConnectionRows(snapshot);
        int count = 0;
        for (int i = 0; i < connectionExplorerRows.Length; i++)
        {
            ConnectionExplorerRow row = connectionExplorerRows[i];
            EditorMapConnectionSnapshot connection = row.Connection;
            if (connection.FromRoomIndex != roomIndex && connection.ToRoomIndex != roomIndex) continue;
            count++;
            ImGui.PushID(i);
            bool clicked = ImGui.Selectable(row.Label);
            ImGui.PopID();
            if (clicked) SelectConnection(connection);
        }
        if (count == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有区域内连接。", "No in-region links."));
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
            if (node.Exit) line += "  ->  " + target;
            ImGui.TextUnformatted(line);
        }
    }

    private static void DrawConnectionInspector(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection)
    {
        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        ImGui.TextUnformatted(ConnectionLabel(snapshot, connection));
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("端点 A", "Endpoint A"), (a?.Name ?? connection.FromRoomIndex.ToString()) + ":" + NodeText(connection.FromNodeIndex));
        DrawMetric(DevToolUiSettings.T("端点 B", "Endpoint B"), (b?.Name ?? connection.ToRoomIndex.ToString()) + ":" + NodeText(connection.ToNodeIndex));
        DrawMetric(DevToolUiSettings.T("方向", "Direction"), DirectionGlyph(connection.Direction));
        DrawMetric(DevToolUiSettings.T("来源", "Source"), connection.Explicit ? "WorldTopology.json" : "world.txt");

        bool validA = FindNode(a, connection.FromNodeIndex)?.Exit == true;
        bool validB = FindNode(b, connection.ToNodeIndex)?.Exit == true;
        if (connection.Explicit && (!validA || !validB))
            DrawBrokenExplicitConnectionEditor(snapshot, connection);
        else if (connection.Ambiguous)
            DrawAmbiguousConnectionEditor(snapshot, connection, a, b);
        else if (connection.Explicit)
            DrawExplicitConnectionEditor(snapshot, connection, a, b);
        else
            DrawVanillaConnectionEditor(snapshot, connection, a, b);

        DrawTopologyCommandStatus();
    }

    private static void DrawBrokenExplicitConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("损坏的精确映射", "BROKEN EXACT MAPPING"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "WorldTopology.json 中保存的节点已经不存在或不是 Exit。可以移除这条损坏映射，让编辑器重新以 world.txt 为准解析连接。",
                "The saved WorldTopology.json endpoint no longer exists or is not an Exit. Remove the broken mapping to rebuild the link from world.txt."),
            true);

        string edgeId = ExplicitEdgeId(connection.ConnectionId);
        if (string.IsNullOrEmpty(edgeId)) return;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("移除损坏映射并重新解析", "Remove Broken Mapping and Re-resolve"),
                "RemoveBrokenTopologyMapping",
                DevToolButtonTone.Danger))
        {
            WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
                WorldTopologyCommandKind.RemoveExplicitMapping,
                region: snapshot.RegionName,
                edgeId: edgeId));
            ClearConnectionSelection();
        }
    }

    private static void DrawAmbiguousConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("指定目标 Exit", "RESOLVE TARGET EXIT"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "无法自动确定目标出口时仍可手动修复。优先列出已经指回源房间的 Exit，同时也允许选择当前空闲的 Exit；不会覆盖已经连接到第三个房间的出口。",
                "If the target Exit cannot be resolved automatically, repair it manually. Exits already pointing back are listed first, followed by free Exits; endpoints connected to a third room are never overwritten."),
            true);
        if (a == null || b == null || connection.FromNodeIndex < 0) return;

        if (!string.Equals(mappingConnectionId, connection.ConnectionId, StringComparison.Ordinal))
        {
            mappingConnectionId = connection.ConnectionId;
            mappingTargetNode = FirstCandidateTargetNode(snapshot, connection, b);
            mappingDirection = WorldConnectionDirection.Bidirectional;
        }

        List<int> candidates = CandidateTargetNodes(snapshot, connection, b);
        if (candidates.Count == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "目标房间没有可安全使用的 Exit。请先断开冲突出口，再回来修复这条连接。",
                    "The target room has no safe Exit available. Disconnect a conflicting endpoint first, then repair this link."),
                true);
            return;
        }
        if (!candidates.Contains(mappingTargetNode)) mappingTargetNode = candidates[0];

        string targetExitLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("目标出口", "Target Exit"),
                "TopologyTargetExit");
        string preview = "Exit " + mappingTargetNode;
        EditorMapRoomNodeSnapshot selectedNode = FindNode(b, mappingTargetNode);
        if (selectedNode?.ConnectedRoomIndex < 0) preview += DevToolUiSettings.T("（空闲）", " (free)");
        if (ImGui.BeginCombo(targetExitLabel, preview))
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                int node = candidates[i];
                EditorMapRoomNodeSnapshot candidate = FindNode(b, node);
                bool free = candidate?.ConnectedRoomIndex < 0;
                string label = "Exit " + node + (free ? DevToolUiSettings.T("（空闲）", " (free)") : string.Empty);
                bool selected = mappingTargetNode == node;
                ImGui.PushID(node);
                bool clicked = ImGui.Selectable(label, selected);
                ImGui.PopID();
                if (clicked) mappingTargetNode = node;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DrawMappingDirectionButtons();
        ImGui.Spacing();
        ImGui.TextUnformatted(a.Name + ":" + connection.FromNodeIndex + " " + DirectionGlyph(mappingDirection) + " " + b.Name + ":" + mappingTargetNode);
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("修复并建立精确映射", "Repair Exact Mapping"), "CreateTopologyMapping", DevToolButtonTone.Primary))
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

    private static void DrawExplicitConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("连接方向", "DIRECTION"));
        string edgeId = ExplicitEdgeId(connection.ConnectionId);
        DrawExistingDirectionButtons(snapshot.RegionName, edgeId, connection.Direction);
        DrawDisconnectButton(snapshot, connection, a, b, edgeId);
    }

    private static void DrawVanillaConnectionEditor(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        EditorMapRoomSnapshot a,
        EditorMapRoomSnapshot b)
    {
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("精确端点", "EXACT ENDPOINTS"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "这条原版连接当前可唯一解析。建立精确映射后即可安全改成单向，并避免以后出现同房间多链接歧义。",
                "This vanilla link is currently unambiguous. Promote it to an exact mapping to safely use one-way direction or future repeated room links."),
            true);
        if (a == null || b == null || connection.ToNodeIndex < 0) return;

        if (!string.Equals(mappingConnectionId, connection.ConnectionId, StringComparison.Ordinal))
        {
            mappingConnectionId = connection.ConnectionId;
            mappingDirection = WorldConnectionDirection.Bidirectional;
        }
        DrawMappingDirectionButtons();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("升级为精确连接", "Promote to Exact Link"), "PromoteVanillaLink", DevToolButtonTone.Primary))
        {
            WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
                WorldTopologyCommandKind.AddExplicitMapping,
                region: snapshot.RegionName,
                roomA: a.Name,
                nodeA: connection.FromNodeIndex,
                roomB: b.Name,
                nodeB: connection.ToNodeIndex,
                direction: mappingDirection));
        }
        DrawDisconnectButton(snapshot, connection, a, b, string.Empty);
    }

    private static void DrawExistingDirectionButtons(string regionName, string edgeId, WorldConnectionDirection current)
    {
        if (DirectionButton("↔", "WorldDirectionBoth", current == WorldConnectionDirection.Bidirectional))
            QueueDirection(regionName, edgeId, WorldConnectionDirection.Bidirectional);
        ImGui.SameLine();
        if (DirectionButton("→", "WorldDirectionAToB", current == WorldConnectionDirection.AToB))
            QueueDirection(regionName, edgeId, WorldConnectionDirection.AToB);
        ImGui.SameLine();
        if (DirectionButton("←", "WorldDirectionBToA", current == WorldConnectionDirection.BToA))
            QueueDirection(regionName, edgeId, WorldConnectionDirection.BToA);
    }

    private static void DrawMappingDirectionButtons()
    {
        if (DirectionButton("↔", "WorldDirectionNewBoth", mappingDirection == WorldConnectionDirection.Bidirectional))
            mappingDirection = WorldConnectionDirection.Bidirectional;
        ImGui.SameLine();
        if (DirectionButton("→", "WorldDirectionNewAToB", mappingDirection == WorldConnectionDirection.AToB))
            mappingDirection = WorldConnectionDirection.AToB;
        ImGui.SameLine();
        if (DirectionButton("←", "WorldDirectionNewBToA", mappingDirection == WorldConnectionDirection.BToA))
            mappingDirection = WorldConnectionDirection.BToA;
    }

    private static bool DirectionButton(string glyph, string id, bool selected) =>
        DevToolWidgets.ActionButton(glyph, id, selected ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle);

    private static void QueueDirection(string regionName, string edgeId, WorldConnectionDirection direction)
    {
        if (string.IsNullOrEmpty(edgeId)) return;
        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.SetDirection,
            region: regionName,
            edgeId: edgeId,
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
        if (!DevToolWidgets.ActionButton(DevToolUiSettings.T("断开连接", "Disconnect"), "DisconnectWorldLink", DevToolButtonTone.Danger)) return;
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

    private static void DrawTopologyCommandStatus()
    {
        if (string.IsNullOrEmpty(WorldTopologyCommandQueue.LastStatus)) return;
        ImGui.Spacing();
        if (WorldTopologyCommandQueue.LastSucceeded)
            DevToolWidgets.MutedText(WorldTopologyCommandQueue.LastStatus, true);
        else
            ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), WorldTopologyCommandQueue.LastStatus);
    }

    private static void DrawSubregionInspector(EditorMapPresentationSnapshot snapshot)
    {
        List<SubregionSummary> summaries = GetSubregions(snapshot);
        SubregionSummary match = null;
        for (int i = 0; i < summaries.Count; i++)
            if (string.Equals(summaries[i].Name, selectedSubregion, StringComparison.Ordinal)) match = summaries[i];

        ImGui.TextUnformatted(match?.DisplayName ?? SubregionDisplayName(selectedSubregion));
        ImGui.Separator();
        DrawMetric(DevToolUiSettings.T("房间", "Rooms"), match?.CountText ?? "0");
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "子区域现在直接作为世界地图的筛选与房间属性，不再占用独立顶层页面。后续多选会在这里提供批量分配。",
                "Subregions now live inside the World Map workflow instead of a separate top-level page. Batch assignment will attach to multi-selection here."),
            true);
    }

    private static void DrawDirtyHints()
    {
        if (WorldTextRegistry.Dirty) DevToolWidgets.MutedText(DevToolUiSettings.T("world.txt · 未保存", "world.txt · unsaved"));
        if (WorldTopologyRegistry.Dirty) DevToolWidgets.MutedText(DevToolUiSettings.T("WorldTopology.json · 未保存", "WorldTopology.json · unsaved"));
        if (WorldWorkspaceDataView.HasDirtyData) DevToolWidgets.MutedText(DevToolUiSettings.T("世界数据 · 未保存", "World data · unsaved"));
        if (WorldRoomAttractionRegistry.Dirty) DevToolWidgets.MutedText(DevToolUiSettings.T("Properties.txt · 未保存", "Properties.txt · unsaved"));
    }

    private static void DrawStatus(EditorMapPresentationSnapshot snapshot)
    {
        ImGui.TextDisabled(GetStatusText(snapshot));
    }

    private static string GetStatusText(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        bool worldTextDirty = WorldTextRegistry.Dirty;
        bool topologyDirty = WorldTopologyRegistry.Dirty;
        bool worldDataDirty = WorldWorkspaceDataView.HasDirtyData;
        bool propertiesDirty = WorldRoomAttractionRegistry.Dirty;
        bool chinese = DevToolUiSettings.IsChinese;

        if (ReferenceEquals(statusRooms, rooms) && ReferenceEquals(statusConnections, connections) &&
            statusSelectionKind == selectionKind && statusSelectedRoomIndex == snapshot.SelectedRoomIndex &&
            string.Equals(statusConnectionId, selectedConnectionId, StringComparison.Ordinal) &&
            string.Equals(statusSubregion, selectedSubregion, StringComparison.Ordinal) &&
            statusWorkspaceMode == workspaceMode && statusWorldTextDirty == worldTextDirty &&
            statusTopologyDirty == topologyDirty && statusWorldDataDirty == worldDataDirty &&
            statusPropertiesDirty == propertiesDirty &&
            statusChinese == chinese && statusText.Length > 0)
            return statusText;

        string selection;
        if (selectionKind == SelectionKind.Connection && !string.IsNullOrEmpty(selectedConnectionId))
            selection = FindConnection(snapshot, selectedConnectionId) is { } connection
                ? ConnectionLabel(snapshot, connection)
                : snapshot.RegionName;
        else if (selectionKind == SelectionKind.Subregion)
            selection = SubregionDisplayName(selectedSubregion);
        else
        {
            EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
            selection = room == null
                ? snapshot.RegionName
                : room.Name + " · " + MapRoomLayer.Label(room.Layer) +
                  (string.IsNullOrEmpty(room.Subregion) ? string.Empty : " · " + room.Subregion);
        }

        string mode = workspaceMode switch
        {
            WorkspaceMode.WorldMap => DevToolUiSettings.T("世界地图", "World Map"),
            WorkspaceMode.WorldData => DevToolUiSettings.T("世界数据", "World Data"),
            _ => DevToolUiSettings.T("验证", "Validation")
        };
        string dirty = string.Empty;
        if (worldTextDirty) dirty += DevToolUiSettings.T(" · world.txt 未保存", " · world.txt dirty");
        if (topologyDirty) dirty += DevToolUiSettings.T(" · 拓扑未保存", " · topology dirty");
        if (worldDataDirty) dirty += DevToolUiSettings.T(" · 世界数据未保存", " · world data dirty");
        if (propertiesDirty) dirty += DevToolUiSettings.T(" · Properties 未保存", " · Properties dirty");
        statusText = selection + "   ·   " + mode + "   ·   " + connections.Length +
                     DevToolUiSettings.T(" 条连接", " links") + dirty;

        statusRooms = rooms;
        statusConnections = connections;
        statusSelectionKind = selectionKind;
        statusSelectedRoomIndex = snapshot.SelectedRoomIndex;
        statusConnectionId = selectedConnectionId;
        statusSubregion = selectedSubregion;
        statusWorkspaceMode = workspaceMode;
        statusWorldTextDirty = worldTextDirty;
        statusTopologyDirty = topologyDirty;
        statusWorldDataDirty = worldDataDirty;
        statusPropertiesDirty = propertiesDirty;
        statusChinese = chinese;
        return statusText;
    }

    private static List<SubregionSummary> GetSubregions(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedSubregionRooms, rooms) && projectedSubregionChinese == chinese)
            return SubregionSummaries;

        EnsureRoomExplorerRows(snapshot);
        SubregionMap.Clear();
        SubregionSummaries.Clear();
        for (int i = 0; i < roomExplorerRows.Length; i++)
        {
            RoomExplorerRow row = roomExplorerRows[i];
            EditorMapRoomSnapshot room = row.Room;
            string key = NormalizeSubregion(room.Subregion);
            if (!SubregionMap.TryGetValue(key, out SubregionSummary summary))
            {
                summary = new SubregionSummary
                {
                    Name = key,
                    DisplayName = SubregionDisplayName(key),
                    FirstRoom = room.RoomIndex
                };
                SubregionMap.Add(key, summary);
                SubregionSummaries.Add(summary);
            }
            summary.Count++;
            summary.Rooms.Add(row);
        }
        SubregionSummaries.Sort(CompareSubregions);
        for (int i = 0; i < SubregionSummaries.Count; i++)
        {
            SubregionSummary summary = SubregionSummaries[i];
            summary.CountText = summary.Count.ToString();
            summary.Label = summary.DisplayName + "  ·  " + summary.Count;
        }
        projectedSubregionRooms = rooms;
        projectedSubregionChinese = chinese;
        return SubregionSummaries;
    }

    private static int CompareSubregions(SubregionSummary a, SubregionSummary b)
    {
        bool aNone = a.Name.Length == 0;
        bool bNone = b.Name.Length == 0;
        if (aNone != bNone) return aNone ? -1 : 1;
        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<WorldIssue> GetIssues(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        string region = snapshot.RegionName ?? string.Empty;
        int topologyRevision = WorldTopologyRegistry.Revision;
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedIssueRooms, rooms) &&
            ReferenceEquals(projectedIssueConnections, connections) &&
            string.Equals(projectedIssueRegion, region, StringComparison.Ordinal) &&
            projectedIssueTopologyRevision == topologyRevision &&
            projectedIssueChinese == chinese)
            return WorldIssues;

        EnsureRoomIndex(snapshot);
        EnsureConnectionRows(snapshot);
        WorldIssues.Clear();
        IssueDegree.Clear();

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            Increment(IssueDegree, connection.FromRoomIndex);
            Increment(IssueDegree, connection.ToRoomIndex);
            if (connection.Ambiguous)
            {
                AddIssue(
                    connection.FromRoomIndex,
                    DevToolUiSettings.T("出口映射不明确：", "Ambiguous exit mapping: ") + ConnectionLabel(snapshot, connection),
                    DevToolUiSettings.T("重复房间链接需要指定精确目标 Exit。", "Repeated room links require an exact target Exit."));
            }
            if (connection.Explicit && !DirectionMatchesWorld(snapshot, connection))
            {
                AddIssue(
                    connection.FromRoomIndex,
                    DevToolUiSettings.T("连接方向与 world.txt 不一致：", "Direction disagrees with world.txt: ") + ConnectionLabel(snapshot, connection),
                    DevToolUiSettings.T("箭头、AbstractRoom.connections 与 sidecar 必须表达同一方向。", "Arrow direction, AbstractRoom.connections and the sidecar must describe the same route."));
            }
        }

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (string.IsNullOrWhiteSpace(room.Subregion))
            {
                AddIssue(
                    room.RoomIndex,
                    DevToolUiSettings.T("未分配子区域：", "No subregion: ") + room.Name,
                    DevToolUiSettings.T("该房间当前没有 subregionName。", "This room has no subregionName."));
            }
            if (!room.OffScreenDen && (!IssueDegree.TryGetValue(room.RoomIndex, out int d) || d == 0))
            {
                AddIssue(
                    room.RoomIndex,
                    DevToolUiSettings.T("孤立房间：", "Isolated room: ") + room.Name,
                    DevToolUiSettings.T("区域内没有可见连接。", "No in-region link is visible for this room."));
            }
        }

        List<WorldTopologyIssue> topologyIssues = WorldTopologyRegistry.ValidateRegion(
            region,
            roomName => FindRoom(snapshot, roomName) != null,
            (roomName, nodeIndex) => FindNode(FindRoom(snapshot, roomName), nodeIndex)?.Exit == true);
        for (int i = 0; i < topologyIssues.Count; i++)
        {
            AddIssue(-1, "WorldTopology · " + topologyIssues[i].Kind, topologyIssues[i].Message);
        }

        projectedIssueRooms = rooms;
        projectedIssueConnections = connections;
        projectedIssueRegion = region;
        projectedIssueTopologyRevision = topologyRevision;
        projectedIssueChinese = chinese;
        return WorldIssues;
    }

    private static void AddIssue(int roomIndex, string title, string detail)
    {
        WorldIssues.Add(new WorldIssue
        {
            RoomIndex = roomIndex,
            Title = title ?? string.Empty,
            Detail = detail ?? string.Empty,
            Label = "⚠ " + (title ?? string.Empty)
        });
    }

    private static bool DirectionMatchesWorld(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection)
    {
        if (connection.ToNodeIndex < 0) return false;
        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        EditorMapRoomNodeSnapshot nodeA = FindNode(a, connection.FromNodeIndex);
        EditorMapRoomNodeSnapshot nodeB = FindNode(b, connection.ToNodeIndex);
        if (a == null || b == null || nodeA == null || nodeB == null) return false;
        bool aToB = nodeA.ConnectedRoomIndex == b.RoomIndex;
        bool bToA = nodeB.ConnectedRoomIndex == a.RoomIndex;
        return connection.Direction switch
        {
            WorldConnectionDirection.AToB => aToB && !bToA,
            WorldConnectionDirection.BToA => !aToB && bToA,
            _ => aToB && bToA
        };
    }

    private static List<int> CandidateTargetNodes(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection, EditorMapRoomSnapshot targetRoom)
    {
        CandidatePreferredScratch.Clear();
        CandidateFreeScratch.Clear();
        EnsureExplicitEndpointIndex(snapshot);

        EditorMapRoomNodeSnapshot[] nodes = targetRoom?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            if (!node.Exit) continue;
            if (ExplicitEndpoints.Contains(new EndpointKey(targetRoom.RoomIndex, node.NodeIndex))) continue;

            if (node.ConnectedRoomIndex == connection.FromRoomIndex)
                CandidatePreferredScratch.Add(node.NodeIndex);
            else if (node.ConnectedRoomIndex < 0)
                CandidateFreeScratch.Add(node.NodeIndex);
        }

        CandidatePreferredScratch.AddRange(CandidateFreeScratch);
        return CandidatePreferredScratch;
    }

    private static int FirstCandidateTargetNode(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection, EditorMapRoomSnapshot targetRoom)
    {
        List<int> candidates = CandidateTargetNodes(snapshot, connection, targetRoom);
        return candidates.Count > 0 ? candidates[0] : -1;
    }

    private static void EnsureExplicitEndpointIndex(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        if (ReferenceEquals(indexedExplicitEndpointConnections, connections)) return;

        ExplicitEndpoints.Clear();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null || !connection.Explicit) continue;
            if (connection.FromRoomIndex >= 0 && connection.FromNodeIndex >= 0)
                ExplicitEndpoints.Add(new EndpointKey(connection.FromRoomIndex, connection.FromNodeIndex));
            if (connection.ToRoomIndex >= 0 && connection.ToNodeIndex >= 0)
                ExplicitEndpoints.Add(new EndpointKey(connection.ToRoomIndex, connection.ToNodeIndex));
        }
        indexedExplicitEndpointConnections = connections;
    }

    private static void SynchronizeSelection(EditorMapPresentationSnapshot snapshot)
    {
        if (selectionKind == SelectionKind.Connection && FindConnection(snapshot, selectedConnectionId) == null)
            ClearConnectionSelection();
        if (lastObservedRoomIndex < 0) lastObservedRoomIndex = snapshot.SelectedRoomIndex;
    }

    private static void SynchronizeCanvasSelection(EditorMapPresentationSnapshot snapshot)
    {
        string mapConnection = WorldMapView.SelectedConnectionId;
        if (!string.IsNullOrEmpty(mapConnection))
        {
            if (!string.Equals(selectedConnectionId, mapConnection, StringComparison.Ordinal))
            {
                selectedConnectionId = mapConnection;
                selectionKind = SelectionKind.Connection;
                EditorMapConnectionSnapshot connection = FindConnection(snapshot, mapConnection);
                if (connection != null) lastObservedRoomIndex = connection.FromRoomIndex;
            }
            return;
        }

        if (snapshot.SelectedRoomIndex != lastObservedRoomIndex)
        {
            lastObservedRoomIndex = snapshot.SelectedRoomIndex;
            selectedConnectionId = string.Empty;
            selectedSubregion = string.Empty;
            selectionKind = snapshot.SelectedRoomIndex >= 0 ? SelectionKind.Room : SelectionKind.Region;
        }
    }

    internal static void SelectConnection(EditorMapConnectionSnapshot connection)
    {
        if (connection == null) return;
        selectedConnectionId = connection.ConnectionId;
        selectionKind = SelectionKind.Connection;
        selectedSubregion = string.Empty;
        lastObservedRoomIndex = connection.FromRoomIndex;
        WorldMapView.SelectConnection(connection.ConnectionId);
        SelectRoom(connection.FromRoomIndex);
        if (!string.Equals(mappingConnectionId, selectedConnectionId, StringComparison.Ordinal))
        {
            mappingConnectionId = string.Empty;
            mappingTargetNode = -1;
            mappingDirection = WorldConnectionDirection.Bidirectional;
        }
    }

    private static void ClearConnectionSelection()
    {
        selectedConnectionId = string.Empty;
        mappingConnectionId = string.Empty;
        mappingTargetNode = -1;
        mappingDirection = WorldConnectionDirection.Bidirectional;
        WorldMapView.ClearConnectionSelection();
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

    private static void SelectRoom(int roomIndex) =>
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex));

    private static void ActivateCurrentRoomFromExplorer(int roomIndex)
    {
        MapEditorCommandQueue.Enqueue(
            new MapEditorCommand(
                MapEditorCommandKind.SwitchCurrentRoom,
                roomIndex));
        WorldMapView.FocusRoom(roomIndex);
    }

    private static void DrawRoomLayerButtons(EditorMapRoomSnapshot room)
    {
        if (room == null)
            return;

        ImGuiStylePtr style = ImGui.GetStyle();
        float spacing = Math.Max(4f, style.ItemSpacing.X);
        float available = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float buttonWidth = Math.Max(42f, (available - spacing * (MapRoomLayer.Count - 1)) / MapRoomLayer.Count);

        for (int layer = 0; layer < MapRoomLayer.Count; layer++)
        {
            bool selected = room.Layer == layer;
            ImGui.PushID("WorldRoomLayer_" + layer);

            if (selected)
            {
                Num.Vector4 selectedColor =
                    ImGui.GetStyle().Colors[(int)ImGuiCol.HeaderActive];
                Num.Vector4 hoveredColor =
                    ImGui.GetStyle().Colors[(int)ImGuiCol.HeaderHovered];
                ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoveredColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, selectedColor);
            }

            bool clicked =
                ImGui.Button(
                    MapRoomLayer.Label(layer),
                    new Num.Vector2(buttonWidth, 0f));

            if (selected)
                ImGui.PopStyleColor(3);

            ImGui.PopID();

            if (clicked && !selected)
            {
                MapEditorCommandQueue.Enqueue(
                    new MapEditorCommand(
                        MapEditorCommandKind.SetRoomLayer,
                        roomIndex: room.RoomIndex,
                        value: new EditorPropertyValue(
                            EditorPropertyKind.Integer,
                            integer: layer)));
            }

            if (layer < MapRoomLayer.Count - 1)
                ImGui.SameLine(0f, spacing);
        }
    }

    private static void EnsureRoomIndex(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (ReferenceEquals(indexedRooms, rooms)) return;
        RoomsByIndex.Clear();
        RoomsByName.Clear();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null) continue;
            RoomsByIndex[room.RoomIndex] = room;
            if (!string.IsNullOrEmpty(room.Name)) RoomsByName[room.Name] = room;
        }
        indexedRooms = rooms;
    }

    private static void EnsureConnectionIndex(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        if (ReferenceEquals(indexedConnections, connections)) return;
        ConnectionsById.Clear();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null || string.IsNullOrEmpty(connection.ConnectionId)) continue;
            ConnectionsById[connection.ConnectionId] = connection;
        }
        indexedConnections = connections;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EnsureRoomIndex(snapshot);
        RoomsByIndex.TryGetValue(roomIndex, out EditorMapRoomSnapshot room);
        return room;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, string roomName)
    {
        if (string.IsNullOrEmpty(roomName)) return null;
        EnsureRoomIndex(snapshot);
        RoomsByName.TryGetValue(roomName, out EditorMapRoomSnapshot room);
        return room;
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
        EnsureConnectionIndex(snapshot);
        ConnectionsById.TryGetValue(id, out EditorMapConnectionSnapshot connection);
        return connection;
    }

    private static string ConnectionLabel(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection)
    {
        if (connection == null) return string.Empty;
        EnsureConnectionRows(snapshot);
        if (!string.IsNullOrEmpty(connection.ConnectionId) &&
            ConnectionLabels.TryGetValue(connection.ConnectionId, out string label))
            return label;
        return BuildConnectionLabel(connection);
    }

    private static string BuildConnectionLabel(EditorMapConnectionSnapshot connection)
    {
        string a = (RoomsByIndex.TryGetValue(connection.FromRoomIndex, out EditorMapRoomSnapshot from)
                ? from.Name
                : connection.FromRoomIndex.ToString()) + ":" + NodeText(connection.FromNodeIndex);
        string b = (RoomsByIndex.TryGetValue(connection.ToRoomIndex, out EditorMapRoomSnapshot to)
                ? to.Name
                : connection.ToRoomIndex.ToString()) + ":" + NodeText(connection.ToNodeIndex);
        return a + " " + DirectionGlyph(connection.Direction) + " " + b;
    }

    private static string DirectionGlyph(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "->",
        WorldConnectionDirection.BToA => "<-",
        _ => "<->"
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
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string SubregionDisplayName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "None" : value.Trim();

    private static string SearchQuery()
    {
        if (string.Equals(observedSearch, search, StringComparison.Ordinal)) return normalizedSearch;
        observedSearch = search;
        normalizedSearch = search?.Trim() ?? string.Empty;
        return normalizedSearch;
    }

    private static bool Matches(string value)
    {
        string query = SearchQuery();
        return query.Length == 0 || Contains(value, query);
    }

    private static bool Matches(string first, string second)
    {
        string query = SearchQuery();
        return query.Length == 0 || Contains(first, query) || Contains(second, query);
    }

    private static bool Matches(string first, string second, string third)
    {
        string query = SearchQuery();
        return query.Length == 0 || Contains(first, query) || Contains(second, query) || Contains(third, query);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void Send(EditorUiCommandKind kind) => EditorUiCommandQueue.Enqueue(new EditorUiCommand(kind));
}
