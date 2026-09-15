using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevToolOverlay
{
    private sealed class CategoryRun
    {
        internal string Category;
        internal int Start;
        internal int Count;
    }

    private sealed class ObjectLibraryRow
    {
        internal EditorObjectTypeSnapshot Item;
        internal string Category;
        internal string DisplayName;
        internal string Tooltip;
    }

    private sealed class ObjectLibraryGroup
    {
        internal string Source;
        internal DevToolSourceMark SourceMark;
        internal readonly List<ObjectLibraryRow> Rows = new();
        internal readonly List<CategoryRun> CategoryRuns = new();
    }

    private sealed class SceneObjectRow
    {
        internal EditorObjectSnapshot Item;
        internal string Category;
        internal string Label;
        internal string Tooltip;
    }

    private sealed class SceneObjectGroup
    {
        internal string Source;
        internal DevToolSourceMark SourceMark;
        internal readonly List<SceneObjectRow> Rows = new();
        internal readonly List<CategoryRun> CategoryRuns = new();
    }

    private static string objectSearch = string.Empty;
    private static string sceneSearch = string.Empty;
    private static bool sceneTab;
    private static bool lanceDebugPage;
    private static int sceneSelectionAnchor = -1;
    private static float browserInspectorSplit = 0.40f;
    private static bool browserInspectorSplitterDragging;
    private const float BrowserPaneFontScale = 1.22f;

    private static EditorObjectTypeSnapshot[] projectedObjectLibrary;
    private static string projectedObjectSearch = string.Empty;
    private static bool projectedObjectChinese;
    private static readonly Dictionary<string, ObjectLibraryGroup> ObjectLibraryGroupsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ObjectLibraryGroup> ObjectLibraryGroups = new();
    private static int objectLibraryMatchCount;
    private static string observedObjectSearch;
    private static string normalizedObjectSearch = string.Empty;

    private static EditorObjectTypeSnapshot[] indexedMetadataLibrary;
    private static readonly Dictionary<string, EditorObjectTypeSnapshot> MetadataByType =
        new(StringComparer.Ordinal);

    private static EditorObjectSnapshot[] projectedSceneObjects;
    private static EditorObjectTypeSnapshot[] projectedSceneLibrary;
    private static string projectedSceneSearch = string.Empty;
    private static bool projectedSceneChinese;
    private static readonly Dictionary<string, SceneObjectGroup> SceneGroupsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<SceneObjectGroup> SceneGroups = new();
    private static int sceneMatchCount;
    private static string observedSceneSearch;
    private static string normalizedSceneSearch = string.Empty;

    private static int sceneStatusObjectCount = -1;
    private static int sceneStatusSelectionCount = -1;
    private static bool sceneStatusChinese;
    private static string sceneStatusText = string.Empty;

    private static string placementLabelType = string.Empty;
    private static bool placementLabelChinese;
    private static string placementLabelText = string.Empty;

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1366f;
        if (display.Y < 1f) display.Y = 768f;

        // The LanceScavenger debugger is a true New-UI workspace. Do not render the normal page
        // workspace under it; only the shared editor chrome remains visible.
        if (!lanceDebugPage)
        {
            // Map no longer opens an independent floating graph on top of the Browser/Inspector panel.
            // In normal mode it gets one coherent World Workspace below. Focus mode intentionally
            // keeps only the graph itself.
            if (snapshot.FocusMode && snapshot.ToolMode == EditorToolMode.Map)
                DrawMapCanvas(snapshot, display);
            else if (snapshot.ToolMode == EditorToolMode.Dialog)
                DrawDialogPreview(snapshot, display);
            else if (snapshot.ToolMode == EditorToolMode.Relationships)
                DrawRelationshipMatrix(snapshot, display);
        }

        // The shared Control Center is part of the editor chrome, not a page-specific panel.
        // Keep it alive while switching into Map/World so the developer's current view does not
        // disappear merely because the active DevInterface page changed.
        ControlCenterWindow.Draw(snapshot, display);

        if (!snapshot.FocusMode)
            DrawActivityBar(snapshot, display);

        if (lanceDebugPage)
        {
            // Entering diagnostics must not leave Objects placement armed underneath the debug
            // workspace, otherwise a click outside an ImGui window could place an object.
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
            LanceScavengerDebugView.Draw(snapshot, display);
        }
        else
        {
            LanceScavengerDebugView.StopCapture();
            if (!snapshot.FocusMode)
            {
                if (snapshot.ToolMode == EditorToolMode.Map)
                    WorldWorkspaceView.Draw(snapshot, display);
                else if (snapshot.BrowserOpen || snapshot.InspectorOpen)
                    DrawBrowserInspectorPanel(snapshot, display);
            }
            HandlePlacement(snapshot, display, io);
        }
    }

    internal static void ResetRetainedState()
    {
        foreach (ObjectLibraryGroup group in ObjectLibraryGroupsBySource.Values)
        {
            group.Rows.Clear();
            group.CategoryRuns.Clear();
        }
        ObjectLibraryGroupsBySource.Clear();
        ObjectLibraryGroups.Clear();
        projectedObjectLibrary = null;
        projectedObjectSearch = string.Empty;
        projectedObjectChinese = false;
        objectLibraryMatchCount = 0;
        observedObjectSearch = null;
        normalizedObjectSearch = string.Empty;

        indexedMetadataLibrary = null;
        MetadataByType.Clear();

        foreach (SceneObjectGroup group in SceneGroupsBySource.Values)
        {
            group.Rows.Clear();
            group.CategoryRuns.Clear();
        }
        SceneGroupsBySource.Clear();
        SceneGroups.Clear();
        projectedSceneObjects = null;
        projectedSceneLibrary = null;
        projectedSceneSearch = string.Empty;
        projectedSceneChinese = false;
        sceneMatchCount = 0;
        observedSceneSearch = null;
        normalizedSceneSearch = string.Empty;

        sceneSelectionAnchor = -1;
        sceneStatusObjectCount = -1;
        sceneStatusSelectionCount = -1;
        sceneStatusChinese = false;
        sceneStatusText = string.Empty;
        placementLabelType = string.Empty;
        placementLabelText = string.Empty;
        browserInspectorSplitterDragging = false;
        lanceDebugPage = false;
        LanceScavengerDebugView.StopCapture();
    }

    private static void DrawMapCanvas(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        GetCentralWorkspaceRect(display, out Num.Vector2 pos, out Num.Vector2 size);
        MapEditorView.DrawCanvas(MapEditorPresentationHub.Current, pos, size);
    }

    private static void DrawDialogPreview(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        GetCentralWorkspaceRect(display, out Num.Vector2 pos, out Num.Vector2 size);
        DialogEditorView.DrawPreview(DialogEditorPresentationHub.Current, pos, size);
    }

    private static void DrawRelationshipMatrix(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        GetCentralWorkspaceRect(display, out Num.Vector2 pos, out Num.Vector2 size);
        RelationshipEditorView.DrawMatrix(RelationshipEditorPresentationHub.Current, pos, size);
    }

    private static void GetCentralWorkspaceRect(Num.Vector2 display, out Num.Vector2 pos, out Num.Vector2 size)
    {
        float width = Math.Min(900f, Math.Max(520f, display.X * 0.58f));
        float height = Math.Min(620f, Math.Max(360f, display.Y * 0.64f));
        pos = new Num.Vector2(
            Math.Max(180f, (display.X - width) * 0.5f),
            Math.Max(88f, (display.Y - height) * 0.5f));
        size = new Num.Vector2(width, height);
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        string roomLabel = DevToolUiSettings.T("房间", "Room");
        string objectsLabel = DevToolUiSettings.T("物件", "Objects");
        string soundLabel = DevToolUiSettings.T("声音", "Sound");
        string triggersLabel = DevToolUiSettings.T("触发器", "Triggers");
        string mapLabel = DevToolUiSettings.T("地图", "Map");
        string dialogLabel = DevToolUiSettings.T("对话", "Dialog");
        string relationshipsLabel = DevToolUiSettings.T("关系", "Relationships");
        string debugLabel = DevToolUiSettings.T("调试", "Debug");

        float widest = ImGui.CalcTextSize(relationshipsLabel).X;
        widest = Math.Max(widest, ImGui.CalcTextSize(triggersLabel).X);
        widest = Math.Max(widest, ImGui.CalcTextSize(objectsLabel).X);
        widest = Math.Max(widest, ImGui.CalcTextSize(debugLabel).X);
        float defaultWidth = Math.Min(380f, Math.Max(190f, widest + 64f));
        float defaultHeight = Math.Min(Math.Max(500f, 460f * Math.Max(1f, DevToolUiSettings.UiScale)), Math.Max(260f, display.Y - 32f));

        ImGui.SetNextWindowPos(new Num.Vector2(8f, 120f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, defaultHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(170f, 300f),
            new Num.Vector2(Math.Min(520f, Math.Max(170f, display.X - 16f)), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(DevToolUiSettings.T("工具###DevToolActivity", "Tools###DevToolActivity"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Tools");

        DrawModeButton(roomLabel, DevToolUiSettings.T("房间设置", "Room settings"), "DevToolModeRoom", EditorToolMode.Room, snapshot.ToolMode);
        DrawModeButton(objectsLabel, DevToolUiSettings.T("物件", "Objects"), "DevToolModeObjects", EditorToolMode.Objects, snapshot.ToolMode);
        DrawModeButton(soundLabel, DevToolUiSettings.T("声音", "Sound"), "DevToolModeSound", EditorToolMode.Sound, snapshot.ToolMode);
        DrawModeButton(triggersLabel, DevToolUiSettings.T("触发器", "Triggers"), "DevToolModeTriggers", EditorToolMode.Triggers, snapshot.ToolMode);
        DrawModeButton(mapLabel, DevToolUiSettings.T("地图", "Map"), "DevToolModeMap", EditorToolMode.Map, snapshot.ToolMode);
        DrawModeButton(dialogLabel, DevToolUiSettings.T("对话", "Dialog"), "DevToolModeDialog", EditorToolMode.Dialog, snapshot.ToolMode);
        DrawModeButton(relationshipsLabel, DevToolUiSettings.T("关系", "Relationships"), "DevToolModeRelationships", EditorToolMode.Relationships, snapshot.ToolMode);

        if (DevToolWidgets.NavItem(debugLabel, "DevToolLanceScavengerDebug", lanceDebugPage))
        {
            lanceDebugPage = true;
            if (snapshot.PlacementActive)
                Send(EditorUiCommandKind.CancelPlacement);
        }
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "长枪拾荒者：实时状态、瞄准质量、38帧架枪历史、路径阻断与反扫诊断",
                "Lance Scavenger: live state, aim quality, 38-frame brace history, path blocks and counter-sweep diagnostics"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (lanceDebugPage)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T(
                "调试页使用独立工作区。选择上方任一常规工具即可返回。",
                "Debug uses its own workspace. Select any normal tool above to return."));
            ImGui.End();
            return;
        }

        string browserLabel = snapshot.BrowserOpen
            ? DevToolUiSettings.T("隐藏浏览器", "Hide Browser")
            : DevToolUiSettings.T("显示浏览器", "Show Browser");
        if (DevToolWidgets.ActionButton(browserLabel, "DevToolToggleBrowser", DevToolButtonTone.Subtle, true))
            Send(EditorUiCommandKind.ToggleBrowser);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏左栏浏览器", "Toggle left Browser pane"));

        string inspectorLabel = snapshot.InspectorOpen
            ? DevToolUiSettings.T("隐藏检查器", "Hide Inspector")
            : DevToolUiSettings.T("显示检查器", "Show Inspector");
        if (DevToolWidgets.ActionButton(inspectorLabel, "DevToolToggleInspector", DevToolButtonTone.Subtle, true))
            Send(EditorUiCommandKind.ToggleInspector);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏右栏检查器", "Toggle right Inspector pane"));
        ImGui.End();
    }

    private static void DrawModeButton(string label, string tooltip, string id, EditorToolMode mode, EditorToolMode current)
    {
        if (DevToolWidgets.NavItem(label, id, !lanceDebugPage && current == mode))
        {
            lanceDebugPage = false;
            LanceScavengerDebugView.StopCapture();
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SetToolMode, mode: mode));
        }
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(tooltip);
    }

    private static void DrawMapWorkspacePanel(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        // Kept as a compatibility shim for older callers while the World Workspace becomes the
        // single region-level editor surface.
        WorldWorkspaceView.Draw(snapshot, display);
    }

    private static void DrawBrowserInspectorPanel(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        float defaultWidth = Math.Min(Math.Max(760f * Math.Min(1.4f, scale), display.X * 0.58f), Math.Max(520f, display.X - 80f));
        float defaultHeight = Math.Min(Math.Max(440f * Math.Min(1.25f, scale), display.Y * 0.52f), Math.Max(320f, display.Y - 120f));
        Num.Vector2 defaultPos = new(
            Math.Max(80f, (display.X - defaultWidth) * 0.58f),
            Math.Max(92f, (display.Y - defaultHeight) * 0.50f));

        ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, defaultHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(520f, 300f),
            new Num.Vector2(Math.Max(520f, display.X - 16f), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(DevToolUiSettings.T("编辑面板###DevToolBrowserInspector", "Editor Panel###DevToolBrowserInspector"), ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("BrowserInspector");

        bool objectNoSelection = snapshot.ToolMode == EditorToolMode.Objects && snapshot.Inspector?.HasSelection != true;
        bool browser = snapshot.BrowserOpen;
        bool inspector = snapshot.InspectorOpen && !objectNoSelection;

        if (objectNoSelection && !browser && snapshot.InspectorOpen)
            browser = true;

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f)
        {
            ImGui.End();
            return;
        }

        if (browser && inspector)
        {
            float splitterWidth = Math.Max(12f, 8f * Math.Min(1.5f, scale));
            float minLeft = Math.Min(available.X * 0.25f, Math.Max(100f, 120f * Math.Min(1f, scale)));
            float minRight = Math.Min(available.X * 0.55f, Math.Max(180f, 220f * Math.Min(1.25f, scale)));
            float usable = Math.Max(1f, available.X - splitterWidth);
            float maxLeft = Math.Max(minLeft, usable - minRight);
            float leftWidth = Math.Max(minLeft, Math.Min(usable * browserInspectorSplit, maxLeft));

            if (ImGui.BeginChild("##DevToolBrowserPane", new Num.Vector2(leftWidth, available.Y), ImGuiChildFlags.Borders))
            {
                ImGui.SetWindowFontScale(BrowserPaneFontScale);
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("浏览器", "BROWSER"), BrowserPaneFontScale);
                DrawBrowserContents(snapshot);
            }
            ImGui.EndChild();

            ImGui.SameLine(0f, 0f);
            ImGui.InvisibleButton("##DevToolBrowserInspectorSplitter", new Num.Vector2(splitterWidth, available.Y));
            bool splitterHovered = ImGui.IsItemHovered();
            if (!browserInspectorSplitterDragging && splitterHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                browserInspectorSplitterDragging = true;
            if (browserInspectorSplitterDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                browserInspectorSplitterDragging = false;

            Num.Vector2 splitMin = ImGui.GetItemRectMin();
            Num.Vector2 splitMax = ImGui.GetItemRectMax();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float lineX = (splitMin.X + splitMax.X) * 0.5f;
            uint lineColor = ImGui.GetColorU32(splitterHovered || browserInspectorSplitterDragging
                ? ImGuiCol.HeaderActive
                : ImGuiCol.Separator);
            draw.AddLine(
                new Num.Vector2(lineX, splitMin.Y),
                new Num.Vector2(lineX, splitMax.Y),
                lineColor,
                browserInspectorSplitterDragging ? 3f : 1.5f);

            if (browserInspectorSplitterDragging && usable > 1f)
            {
                float nextLeft = leftWidth + ImGui.GetIO().MouseDelta.X;
                nextLeft = Math.Max(minLeft, Math.Min(nextLeft, maxLeft));
                browserInspectorSplit = nextLeft / usable;
            }

            ImGui.SameLine(0f, 0f);
            if (ImGui.BeginChild("##DevToolInspectorPane", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("检查器", "INSPECTOR"));
                DrawInspectorContents(snapshot);
            }
            ImGui.EndChild();
        }
        else if (browser)
        {
            browserInspectorSplitterDragging = false;
            if (ImGui.BeginChild("##DevToolBrowserPaneFull", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                ImGui.SetWindowFontScale(BrowserPaneFontScale);
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("浏览器", "BROWSER"), BrowserPaneFontScale);
                DrawBrowserContents(snapshot);
            }
            ImGui.EndChild();
        }
        else if (inspector)
        {
            browserInspectorSplitterDragging = false;
            if (ImGui.BeginChild("##DevToolInspectorPaneFull", new Num.Vector2(0f, available.Y), ImGuiChildFlags.Borders))
            {
                DevToolWidgets.PaneTitle(DevToolUiSettings.T("检查器", "INSPECTOR"));
                DrawInspectorContents(snapshot);
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private static void DrawBrowserContents(EditorPresentationSnapshot snapshot)
    {
        if (snapshot.ToolMode == EditorToolMode.Room)
            RoomSettingsView.DrawBrowser(RoomEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Sound)
            SoundEditorView.DrawBrowser(SoundEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Triggers)
            TriggerEditorView.DrawBrowser(TriggerEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Map)
            MapEditorView.DrawBrowser(MapEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Dialog)
            DialogEditorView.DrawBrowser(DialogEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Relationships)
            RelationshipEditorView.DrawBrowser(RelationshipEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Objects)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("资源库", "Library"),
                    "ObjectsLibraryTab",
                    sceneTab ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
                sceneTab = false;
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("场景", "Scene"),
                    "ObjectsSceneTab",
                    sceneTab ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
                sceneTab = true;
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (sceneTab) DrawSceneObjectList(snapshot);
            else DrawObjectLibrary(snapshot);
        }
        else
            ImGui.TextDisabled(DevToolUiSettings.T("当前工具不可用。", "Tools unavailable."));
    }

    private static void DrawInspectorContents(EditorPresentationSnapshot snapshot)
    {
        if (snapshot.ToolMode == EditorToolMode.Room)
        {
            RoomSettingsView.DrawInspector(RoomEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, DevToolUiSettings.T("用于尚未迁移的模板、地形或自定义房间设置控件。", "Fallback for template, terrain or custom RoomSettings controls not migrated yet."));
        }
        else if (snapshot.ToolMode == EditorToolMode.Objects)
            ObjectInspectorView.Draw(snapshot.Inspector);
        else if (snapshot.ToolMode == EditorToolMode.Sound)
        {
            SoundEditorView.DrawInspector(SoundEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, DevToolUiSettings.T("用于未迁移的自定义声音页面控件。", "Fallback for custom SoundPage controls or mod-added sound tooling not migrated yet."));
        }
        else if (snapshot.ToolMode == EditorToolMode.Triggers)
        {
            TriggerEditorView.DrawInspector(TriggerEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, DevToolUiSettings.T("用于新检查器无法表达的自定义触发器/事件控件。", "Fallback for custom Trigger/TriggeredEvent controls not represented by the native inspector."));
        }
        else if (snapshot.ToolMode == EditorToolMode.Map)
        {
            // MapPage extensions are exposed through the World Workspace compatibility surface.
            // Do not ask users to reopen the old DevUI for the Map workflow.
            MapEditorView.DrawInspector(MapEditorPresentationHub.Current);
        }
        else if (snapshot.ToolMode == EditorToolMode.Dialog)
            DialogEditorView.DrawInspector(DialogEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Relationships)
        {
            RelationshipEditorView.DrawInspector(RelationshipEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, DevToolUiSettings.T("用于矩阵编辑器尚未表达的关系页面扩展。", "Fallback for custom RelationshipPage extensions not represented by the matrix editor."));
        }
        else
            ImGui.TextDisabled(DevToolUiSettings.T("当前检查器不可用。", "Inspector unavailable."));
    }

    private static void DrawObjectLibrary(EditorPresentationSnapshot snapshot)
    {
        DevToolWidgets.MutedText(DevToolUiSettings.T("搜索", "Search"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##DevToolObjectSearch", ref objectSearch, 128);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "普通搜索会匹配名称、类型、来源和类别。\n高级：@来源   #标签   :类别",
                "Search matches name, type, source and category.\nAdvanced: @source   #tag   :category"));

        if (snapshot.PlacementActive)
        {
            ImGui.Spacing();
            DevToolWidgets.SectionHeader(DevToolUiSettings.T("放置", "PLACEMENT"), BrowserPaneFontScale);
            ImGui.Text(GetPlacementLabel(snapshot.PlacementType));
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("取消放置", "Cancel placement"),
                    "CancelObjectPlacement",
                    DevToolButtonTone.Subtle))
                Send(EditorUiCommandKind.CancelPlacement);
        }

        ImGui.Spacing();
        EnsureObjectLibraryProjection(snapshot);
        for (int sourceIndex = 0; sourceIndex < ObjectLibraryGroups.Count; sourceIndex++)
        {
            ObjectLibraryGroup group = ObjectLibraryGroups[sourceIndex];
            DevToolWidgets.SourceHeader(group.SourceMark, 1.42f * BrowserPaneFontScale, BrowserPaneFontScale);

            List<ObjectLibraryRow> rows = group.Rows;
            List<CategoryRun> categoryRuns = group.CategoryRuns;
            for (int categoryIndex = 0; categoryIndex < categoryRuns.Count; categoryIndex++)
            {
                CategoryRun run = categoryRuns[categoryIndex];
                if (categoryIndex > 0) ImGui.Spacing();
                DevToolWidgets.MutedText(run.Category);

                using DevToolListClipper clipper = new(run.Count);
                while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
                {
                    for (int localIndex = firstVisible; localIndex < lastVisibleExclusive; localIndex++)
                    {
                        ObjectLibraryRow row = rows[run.Start + localIndex];
                        EditorObjectTypeSnapshot item = row.Item;
                        bool selected = snapshot.PlacementActive &&
                                        string.Equals(snapshot.PlacementType, item.Type, StringComparison.Ordinal);
                        ImGui.PushID(item.Type ?? string.Empty);
                        bool clicked = ImGui.Selectable(row.DisplayName, selected);
                        ImGui.PopID();
                        if (clicked)
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.BeginPlacement, text: item.Type));
                        if (ImGui.IsItemHovered()) DevToolTooltip.Show(row.Tooltip);
                    }
                }
            }
        }

        if (objectLibraryMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的物件。", "No matching objects."));
    }

    private static void EnsureObjectLibraryProjection(EditorPresentationSnapshot snapshot)
    {
        EditorObjectTypeSnapshot[] library = snapshot.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        string normalizedSearch = NormalizeObjectSearch();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedObjectLibrary, library) &&
            string.Equals(projectedObjectSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedObjectChinese == chinese)
            return;

        foreach (ObjectLibraryGroup cached in ObjectLibraryGroupsBySource.Values)
        {
            cached.Rows.Clear();
            cached.CategoryRuns.Clear();
        }
        ObjectLibraryGroups.Clear();
        objectLibraryMatchCount = 0;

        char searchMode = '\0';
        string needle = normalizedSearch;
        if (normalizedSearch.Length > 0 &&
            (normalizedSearch[0] == '@' || normalizedSearch[0] == ':' || normalizedSearch[0] == '#'))
        {
            searchMode = normalizedSearch[0];
            needle = normalizedSearch.Substring(1);
        }

        for (int i = 0; i < library.Length; i++)
        {
            EditorObjectTypeSnapshot item = library[i];
            if (item == null || !MatchesLibrary(item, normalizedSearch, searchMode, needle)) continue;

            string source = string.IsNullOrWhiteSpace(item.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : item.Source;
            string category = string.IsNullOrWhiteSpace(item.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : item.Category;
            string displayName = item.DisplayName ?? string.Empty;

            if (!ObjectLibraryGroupsBySource.TryGetValue(source, out ObjectLibraryGroup group))
            {
                group = new ObjectLibraryGroup
                {
                    Source = source,
                    SourceMark = DevToolSourcePresentation.FromLabel(source)
                };
                ObjectLibraryGroupsBySource[source] = group;
            }
            if (group.Rows.Count == 0)
                ObjectLibraryGroups.Add(group);

            int rowIndex = group.Rows.Count;
            if (group.CategoryRuns.Count == 0 ||
                !string.Equals(group.CategoryRuns[group.CategoryRuns.Count - 1].Category, category, StringComparison.Ordinal))
            {
                group.CategoryRuns.Add(new CategoryRun
                {
                    Category = category,
                    Start = rowIndex,
                    Count = 1
                });
            }
            else
            {
                group.CategoryRuns[group.CategoryRuns.Count - 1].Count++;
            }

            group.Rows.Add(new ObjectLibraryRow
            {
                Item = item,
                Category = category,
                DisplayName = displayName,
                Tooltip = (item.Source ?? string.Empty) + " · " + item.Type
            });
            objectLibraryMatchCount++;
        }

        projectedObjectLibrary = library;
        projectedObjectSearch = normalizedSearch;
        projectedObjectChinese = chinese;
    }

    private static bool MatchesLibrary(
        EditorObjectTypeSnapshot item,
        string query,
        char searchMode,
        string needle)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (searchMode == '@') return Contains(item.Source, needle);
        if (searchMode == ':') return Contains(item.Category, needle);
        if (searchMode == '#')
        {
            string[] tags = item.Tags ?? Array.Empty<string>();
            for (int i = 0; i < tags.Length; i++)
                if (Contains(tags[i], needle)) return true;
            return false;
        }

        return Contains(item.DisplayName, query) || Contains(item.Type, query) ||
               Contains(item.Category, query) || Contains(item.Source, query) ||
               Fuzzy(item.DisplayName, query) || Fuzzy(item.Type, query);
    }

    private static void DrawSceneObjectList(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        int selectedCount = snapshot.Inspector?.SelectionCount ?? 0;
        if (sceneSelectionAnchor >= objects.Length)
            sceneSelectionAnchor = -1;

        DevToolWidgets.MutedText(GetSceneStatusText(objects.Length, selectedCount));

        if (selectedCount > 0)
        {
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("复制", "Duplicate"),
                    "SceneSelectionDuplicate",
                    DevToolButtonTone.Normal))
                Send(EditorUiCommandKind.DuplicateSelection);
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除", "Delete"),
                    "SceneSelectionDelete",
                    DevToolButtonTone.Danger))
                Send(EditorUiCommandKind.DeleteSelection);
        }

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("搜索场景物件", "Search scene objects"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##DevToolSceneSearch", ref sceneSearch, 128);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureSceneProjection(snapshot, objects);
        ImGuiIOPtr io = ImGui.GetIO();
        for (int sourceIndex = 0; sourceIndex < SceneGroups.Count; sourceIndex++)
        {
            SceneObjectGroup group = SceneGroups[sourceIndex];
            DevToolWidgets.SourceHeader(group.SourceMark, 1.34f * BrowserPaneFontScale, BrowserPaneFontScale);

            List<SceneObjectRow> rows = group.Rows;
            List<CategoryRun> categoryRuns = group.CategoryRuns;
            for (int categoryIndex = 0; categoryIndex < categoryRuns.Count; categoryIndex++)
            {
                CategoryRun run = categoryRuns[categoryIndex];
                DevToolWidgets.MutedText(run.Category);

                using DevToolListClipper clipper = new(run.Count);
                while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
                {
                    for (int localIndex = firstVisible; localIndex < lastVisibleExclusive; localIndex++)
                    {
                        SceneObjectRow row = rows[run.Start + localIndex];
                        EditorObjectSnapshot item = row.Item;

                        ImGui.PushID(item.Index);
                        bool clicked = ImGui.Selectable(row.Label, item.Selected);
                        ImGui.PopID();
                        if (!clicked)
                        {
                            if (ImGui.IsItemHovered())
                                DevToolTooltip.Show(row.Tooltip);
                            continue;
                        }

                        if (io.KeyShift && sceneSelectionAnchor >= 0)
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                                EditorUiCommandKind.SelectObjectRange,
                                index: item.Index,
                                secondaryIndex: sceneSelectionAnchor,
                                flag: io.KeyCtrl));
                        }
                        else if (io.KeyCtrl)
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.ToggleObjectSelection, item.Index));
                            sceneSelectionAnchor = item.Index;
                        }
                        else
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, item.Index));
                            sceneSelectionAnchor = item.Index;
                        }
                    }
                }
            }
        }

        if (sceneMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景物件。", "No matching scene objects."));
    }

    private static void EnsureSceneProjection(EditorPresentationSnapshot snapshot, EditorObjectSnapshot[] objects)
    {
        EditorObjectTypeSnapshot[] library = snapshot.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        string normalizedSearch = NormalizeSceneSearch();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedSceneObjects, objects) &&
            ReferenceEquals(projectedSceneLibrary, library) &&
            string.Equals(projectedSceneSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedSceneChinese == chinese)
            return;

        EnsureMetadataIndex(library);
        foreach (SceneObjectGroup cached in SceneGroupsBySource.Values)
        {
            cached.Rows.Clear();
            cached.CategoryRuns.Clear();
        }
        SceneGroups.Clear();
        sceneMatchCount = 0;

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;
            MetadataByType.TryGetValue(item.Type ?? string.Empty, out EditorObjectTypeSnapshot metadata);
            if (!MatchesSceneObject(item, metadata, normalizedSearch)) continue;

            string source = string.IsNullOrWhiteSpace(metadata?.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : metadata.Source;
            string category = string.IsNullOrWhiteSpace(metadata?.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : metadata.Category;
            string displayName = string.IsNullOrWhiteSpace(metadata?.DisplayName)
                ? item.Type
                : metadata.DisplayName;

            if (!SceneGroupsBySource.TryGetValue(source, out SceneObjectGroup group))
            {
                group = new SceneObjectGroup
                {
                    Source = source,
                    SourceMark = DevToolSourcePresentation.FromLabel(source)
                };
                SceneGroupsBySource[source] = group;
            }
            if (group.Rows.Count == 0)
                SceneGroups.Add(group);

            int rowIndex = group.Rows.Count;
            if (group.CategoryRuns.Count == 0 ||
                !string.Equals(group.CategoryRuns[group.CategoryRuns.Count - 1].Category, category, StringComparison.Ordinal))
            {
                group.CategoryRuns.Add(new CategoryRun
                {
                    Category = category,
                    Start = rowIndex,
                    Count = 1
                });
            }
            else
            {
                group.CategoryRuns[group.CategoryRuns.Count - 1].Count++;
            }

            group.Rows.Add(new SceneObjectRow
            {
                Item = item,
                Category = category,
                Label = displayName + "  ·  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")",
                Tooltip = source + " · " + item.Type + " · " + category
            });
            sceneMatchCount++;
        }

        projectedSceneObjects = objects;
        projectedSceneLibrary = library;
        projectedSceneSearch = normalizedSearch;
        projectedSceneChinese = chinese;
    }

    private static void EnsureMetadataIndex(EditorObjectTypeSnapshot[] library)
    {
        if (ReferenceEquals(indexedMetadataLibrary, library)) return;
        MetadataByType.Clear();
        for (int i = 0; i < library.Length; i++)
        {
            EditorObjectTypeSnapshot metadata = library[i];
            if (metadata == null || string.IsNullOrEmpty(metadata.Type)) continue;
            MetadataByType[metadata.Type] = metadata;
        }
        indexedMetadataLibrary = library;
    }

    private static bool MatchesSceneObject(EditorObjectSnapshot item, EditorObjectTypeSnapshot metadata, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return Contains(item.Type, query) ||
               Contains(metadata?.DisplayName, query) ||
               Contains(metadata?.Source, query) ||
               Contains(metadata?.Category, query) ||
               Fuzzy(item.Type, query) ||
               Fuzzy(metadata?.DisplayName, query);
    }

    private static string GetSceneStatusText(int objectCount, int selectedCount)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (sceneStatusObjectCount == objectCount && sceneStatusSelectionCount == selectedCount &&
            sceneStatusChinese == chinese && sceneStatusText.Length > 0)
            return sceneStatusText;

        sceneStatusObjectCount = objectCount;
        sceneStatusSelectionCount = selectedCount;
        sceneStatusChinese = chinese;
        sceneStatusText = chinese
            ? $"已放置 {objectCount} 个物件 · 已选 {selectedCount}"
            : $"{objectCount} placed · {selectedCount} selected";
        return sceneStatusText;
    }

    private static string GetPlacementLabel(string type)
    {
        type ??= string.Empty;
        bool chinese = DevToolUiSettings.IsChinese;
        if (string.Equals(placementLabelType, type, StringComparison.Ordinal) &&
            placementLabelChinese == chinese && placementLabelText.Length > 0)
            return placementLabelText;

        placementLabelType = type;
        placementLabelChinese = chinese;
        placementLabelText = (chinese ? "正在放置 " : "Placing ") + type;
        return placementLabelText;
    }

    private static void DrawLegacyFallback(EditorPresentationSnapshot snapshot, string tooltip)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        bool legacyVisible = snapshot.Inspector?.LegacyUiVisible == true;
        string label = legacyVisible
            ? DevToolUiSettings.T("隐藏原版 DevUI", "Hide Original DevUI")
            : DevToolUiSettings.T("显示原版 DevUI", "Show Original DevUI");
        if (DevToolWidgets.ActionButton(label, "LegacyDevUI", DevToolButtonTone.Primary))
            Send(EditorUiCommandKind.ToggleLegacyUi);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(tooltip);
    }

    private static void HandlePlacement(EditorPresentationSnapshot snapshot, Num.Vector2 display, ImGuiIOPtr io)
    {
        if (!snapshot.PlacementActive || snapshot.ToolMode != EditorToolMode.Objects) return;
        bool overWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        if (!overWindow && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Send(EditorUiCommandKind.CancelPlacement);
            return;
        }
        if (!overWindow && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.PlaceObjectAtCursor, flag: io.KeyShift));

        Num.Vector2 mouse = io.MousePos;
        Num.Vector2 hintSize = new(260f, 52f);
        Num.Vector2 pos = new(
            Math.Min(Math.Max(8f, mouse.X + 18f), Math.Max(8f, display.X - hintSize.X - 8f)),
            Math.Min(Math.Max(8f, mouse.Y + 18f), Math.Max(8f, display.Y - hintSize.Y - 8f)));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(hintSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.PopupAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##DevToolPlacementHint", flags))
        {
            ImGui.Text(GetPlacementLabel(snapshot.PlacementType));
            ImGui.TextDisabled(io.KeyShift
                ? DevToolUiSettings.T("连续放置", "Continuous placement")
                : DevToolUiSettings.T("单次放置", "Single placement"));
        }
        ImGui.End();
    }

    private static string NormalizeObjectSearch()
    {
        if (string.Equals(observedObjectSearch, objectSearch, StringComparison.Ordinal))
            return normalizedObjectSearch;
        observedObjectSearch = objectSearch;
        normalizedObjectSearch = objectSearch?.Trim() ?? string.Empty;
        return normalizedObjectSearch;
    }

    private static string NormalizeSceneSearch()
    {
        if (string.Equals(observedSceneSearch, sceneSearch, StringComparison.Ordinal))
            return normalizedSceneSearch;
        observedSceneSearch = sceneSearch;
        normalizedSceneSearch = sceneSearch?.Trim() ?? string.Empty;
        return normalizedSceneSearch;
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Fuzzy(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static void Send(EditorUiCommandKind kind) =>
        EditorUiCommandQueue.Enqueue(new EditorUiCommand(kind));
}
