using System;
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
    private static string objectSearch = string.Empty;
    private static bool sceneTab;
    private static int sceneSelectionAnchor = -1;
    private static float browserInspectorSplit = 0.40f;

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1366f;
        if (display.Y < 1f) display.Y = 768f;

        if (snapshot.ToolMode == EditorToolMode.Map)
            DrawMapCanvas(snapshot, display);
        else if (snapshot.ToolMode == EditorToolMode.Dialog)
            DrawDialogPreview(snapshot, display);
        else if (snapshot.ToolMode == EditorToolMode.Relationships)
            DrawRelationshipMatrix(snapshot, display);

        DrawTopBar(snapshot, display);
        if (!snapshot.FocusMode)
        {
            DrawActivityBar(snapshot, display);
            if (snapshot.BrowserOpen || snapshot.InspectorOpen)
                DrawBrowserInspectorPanel(snapshot, display);
            DrawStatusBar(snapshot, display);
        }

        HandlePlacement(snapshot, display, io);
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

    private static void DrawTopBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        string room = string.IsNullOrEmpty(snapshot.RoomName) ? snapshot.Document : snapshot.RoomName;
        string undo = string.IsNullOrEmpty(snapshot.UndoLabel)
            ? DevToolUiSettings.T("撤销", "Undo")
            : DevToolUiSettings.T("撤销 ", "Undo ") + snapshot.UndoLabel;
        string redo = string.IsNullOrEmpty(snapshot.RedoLabel)
            ? DevToolUiSettings.T("重做", "Redo")
            : DevToolUiSettings.T("重做 ", "Redo ") + snapshot.RedoLabel;
        float textWidth = ImGui.CalcTextSize(room + " · " + DevToolUiSettings.ToolMode(snapshot.ToolMode)).X;
        float preferredWidth = Math.Min(
            Math.Max(420f, display.X - 16f),
            Math.Max(520f, textWidth + ImGui.CalcTextSize(undo + redo).X + 320f));

        ImGui.SetNextWindowPos(new Num.Vector2(270f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(preferredWidth, 62f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(380f, 54f),
            new Num.Vector2(Math.Max(380f, display.X - 16f), 220f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin(DevToolUiSettings.T("命令###DevToolTopBar", "Commands###DevToolTopBar"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Commands");

        ImGui.TextDisabled(room);
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.Text(DevToolUiSettings.ToolMode(snapshot.ToolMode));

        if (snapshot.PlacementActive)
        {
            ImGui.SameLine(0f, 16f);
            ImGui.Text(DevToolUiSettings.T("放置：", "Place: ") + snapshot.PlacementType);
        }

        ImGui.SameLine(0f, 20f);
        if (ImGui.Button(DevToolUiSettings.T("保存  Ctrl+S", "Save  Ctrl+S"))) Send(EditorUiCommandKind.Save);

        ImGui.SameLine();
        bool undoDisabled = !snapshot.CanUndo;
        if (undoDisabled) ImGui.BeginDisabled();
        if (ImGui.Button(undo + "##DevToolUndo")) Send(EditorUiCommandKind.Undo);
        if (undoDisabled) ImGui.EndDisabled();

        ImGui.SameLine();
        bool redoDisabled = !snapshot.CanRedo;
        if (redoDisabled) ImGui.BeginDisabled();
        if (ImGui.Button(redo + "##DevToolRedo")) Send(EditorUiCommandKind.Redo);
        if (redoDisabled) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(DevToolUiSettings.T("专注  Tab", "Focus  Tab"))) Send(EditorUiCommandKind.ToggleFocus);
        ImGui.End();
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 120f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(62f, 350f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(60f, 220f),
            new Num.Vector2(190f, Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar;
        if (!ImGui.Begin(DevToolUiSettings.T("工具###DevToolActivity", "Tools###DevToolActivity"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Tools");

        DrawModeButton("R", DevToolUiSettings.T("房间", "Room"), EditorToolMode.Room, snapshot.ToolMode);
        DrawModeButton("O", DevToolUiSettings.T("物件", "Objects"), EditorToolMode.Objects, snapshot.ToolMode);
        DrawModeButton("S", DevToolUiSettings.T("声音", "Sound"), EditorToolMode.Sound, snapshot.ToolMode);
        DrawModeButton("T", DevToolUiSettings.T("触发器", "Triggers"), EditorToolMode.Triggers, snapshot.ToolMode);
        DrawModeButton("M", DevToolUiSettings.T("地图", "Map"), EditorToolMode.Map, snapshot.ToolMode);
        DrawModeButton("D", DevToolUiSettings.T("对话", "Dialog"), EditorToolMode.Dialog, snapshot.ToolMode);
        DrawModeButton("L", DevToolUiSettings.T("关系", "Relationships"), EditorToolMode.Relationships, snapshot.ToolMode);

        ImGui.Separator();
        if (ImGui.Button(snapshot.BrowserOpen ? "<" : ">", new Num.Vector2(28f, 0f)))
            Send(EditorUiCommandKind.ToggleBrowser);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏左栏浏览器 · Ctrl+B", "Toggle left Browser pane · Ctrl+B"));

        if (ImGui.Button(snapshot.InspectorOpen ? "I" : "i", new Num.Vector2(28f, 0f)))
            Send(EditorUiCommandKind.ToggleInspector);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(DevToolUiSettings.T("显示/隐藏右栏检查器 · Ctrl+I", "Toggle right Inspector pane · Ctrl+I"));
        ImGui.End();
    }

    private static void DrawModeButton(string text, string tooltip, EditorToolMode mode, EditorToolMode current)
    {
        if (current == mode) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button(text + "##DevToolMode" + mode, new Num.Vector2(28f, 28f)))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SetToolMode, mode: mode));
        if (current == mode) ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(tooltip);
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

        bool browser = snapshot.BrowserOpen;
        bool inspector = snapshot.InspectorOpen;
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        if (available.X < 1f || available.Y < 1f)
        {
            ImGui.End();
            return;
        }

        if (browser && inspector)
        {
            float splitterWidth = Math.Max(8f, 6f * scale);
            float minLeft = Math.Min(available.X * 0.45f, Math.Max(180f, 220f * Math.Min(1.4f, scale)));
            float minRight = Math.Min(available.X * 0.45f, Math.Max(220f, 260f * Math.Min(1.4f, scale)));
            float usable = Math.Max(1f, available.X - splitterWidth);
            float leftWidth = usable * browserInspectorSplit;
            leftWidth = Math.Max(minLeft, Math.Min(leftWidth, Math.Max(minLeft, usable - minRight)));
            browserInspectorSplit = Math.Max(0.18f, Math.Min(0.82f, leftWidth / usable));

            if (ImGui.BeginChild("##DevToolBrowserPane", new Num.Vector2(leftWidth, available.Y), true))
            {
                ImGui.TextDisabled(DevToolUiSettings.T("浏览器", "BROWSER"));
                ImGui.Separator();
                DrawBrowserContents(snapshot);
            }
            ImGui.EndChild();

            ImGui.SameLine(0f, 0f);
            ImGui.InvisibleButton("##DevToolBrowserInspectorSplitter", new Num.Vector2(splitterWidth, available.Y));
            Num.Vector2 splitMin = ImGui.GetItemRectMin();
            Num.Vector2 splitMax = ImGui.GetItemRectMax();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            float lineX = (splitMin.X + splitMax.X) * 0.5f;
            uint lineColor = ImGui.GetColorU32(ImGui.IsItemHovered() || ImGui.IsItemActive()
                ? ImGuiCol.HeaderActive
                : ImGuiCol.Separator);
            draw.AddLine(new Num.Vector2(lineX, splitMin.Y), new Num.Vector2(lineX, splitMax.Y), lineColor, ImGui.IsItemActive() ? 3f : 1.5f);
            if (ImGui.IsItemActive() && usable > 1f)
            {
                browserInspectorSplit += ImGui.GetIO().MouseDelta.X / usable;
                browserInspectorSplit = Math.Max(0.18f, Math.Min(0.82f, browserInspectorSplit));
            }

            ImGui.SameLine(0f, 0f);
            if (ImGui.BeginChild("##DevToolInspectorPane", new Num.Vector2(0f, available.Y), true))
            {
                ImGui.TextDisabled(DevToolUiSettings.T("检查器", "INSPECTOR"));
                ImGui.Separator();
                DrawInspectorContents(snapshot);
            }
            ImGui.EndChild();
        }
        else if (browser)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("浏览器", "BROWSER"));
            ImGui.Separator();
            DrawBrowserContents(snapshot);
        }
        else if (inspector)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("检查器", "INSPECTOR"));
            ImGui.Separator();
            DrawInspectorContents(snapshot);
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
            if (ImGui.Button(sceneTab ? DevToolUiSettings.T("资源库", "Library") : DevToolUiSettings.T("资源库*", "Library*"))) sceneTab = false;
            ImGui.SameLine();
            if (ImGui.Button(sceneTab ? DevToolUiSettings.T("场景*", "Scene*") : DevToolUiSettings.T("场景", "Scene"))) sceneTab = true;
            ImGui.Separator();
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
            MapEditorView.DrawInspector(MapEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, DevToolUiSettings.T("用于图编辑器尚未表达的原版或 Mod 地图控件。", "Fallback for vanilla or mod-added MapPage controls not represented by the graph editor."));
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
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(DevToolUiSettings.T("搜索##DevToolObjectSearch", "Search##DevToolObjectSearch"), ref objectSearch, 128);
        ImGui.TextDisabled("@source   #tag   :category");

        if (snapshot.PlacementActive)
        {
            ImGui.Separator();
            ImGui.Text(DevToolUiSettings.T("正在放置 ", "Placing ") + snapshot.PlacementType);
            ImGui.TextDisabled(DevToolUiSettings.T(
                "左键放置 · Shift 连续放置 · Esc/右键取消",
                "Left click room · Shift = repeat · Esc/right click = cancel"));
            if (ImGui.Button(DevToolUiSettings.T("取消放置", "Cancel placement"))) Send(EditorUiCommandKind.CancelPlacement);
        }

        ImGui.Separator();
        string lastCategory = null;
        int matches = 0;
        EditorObjectTypeSnapshot[] library = snapshot.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        for (int i = 0; i < library.Length; i++)
        {
            EditorObjectTypeSnapshot item = library[i];
            if (!Matches(item, objectSearch)) continue;
            matches++;
            if (!string.Equals(lastCategory, item.Category, StringComparison.Ordinal))
            {
                if (lastCategory != null) ImGui.Spacing();
                lastCategory = item.Category;
                ImGui.TextDisabled(lastCategory);
            }

            bool selected = snapshot.PlacementActive && string.Equals(snapshot.PlacementType, item.Type, StringComparison.Ordinal);
            if (ImGui.Selectable(item.DisplayName + "##PlaceObject" + item.Type, selected))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.BeginPlacement, text: item.Type));
            if (ImGui.IsItemHovered()) DevToolTooltip.Show(item.Source + " · " + item.Type);
        }
        if (matches == 0) ImGui.TextDisabled(DevToolUiSettings.T("没有匹配的物件。", "No matching objects."));
    }

    private static void DrawSceneObjectList(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        ImGui.TextDisabled(DevToolUiSettings.T($"已放置 {objects.Length} 个物件", $"{objects.Length} placed objects"));
        int selectedCount = snapshot.Inspector?.SelectionCount ?? 0;
        if (selectedCount > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(DevToolUiSettings.T("复制##SceneSelection", "Duplicate##SceneSelection"))) Send(EditorUiCommandKind.DuplicateSelection);
            ImGui.SameLine();
            if (ImGui.SmallButton(DevToolUiSettings.T("删除##SceneSelection", "Delete##SceneSelection"))) Send(EditorUiCommandKind.DeleteSelection);
        }

        ImGui.Separator();
        ImGuiIOPtr io = ImGui.GetIO();
        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            string label = item.Type + "  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")##SceneObject" + item.Index;
            if (!ImGui.Selectable(label, item.Selected)) continue;
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

    private static void DrawLegacyFallback(EditorPresentationSnapshot snapshot, string tooltip)
    {
        ImGui.Separator();
        bool legacyVisible = snapshot.Inspector?.LegacyUiVisible == true;
        if (ImGui.Button(legacyVisible
                ? DevToolUiSettings.T("隐藏原版 DevUI", "Hide Original DevUI")
                : DevToolUiSettings.T("显示原版 DevUI", "Show Original DevUI")))
            Send(EditorUiCommandKind.ToggleLegacyUi);
        if (ImGui.IsItemHovered()) DevToolTooltip.Show(tooltip);
    }

    private static void DrawStatusBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        string text = BuildStatusText(snapshot);
        float preferredWidth = Math.Min(
            Math.Max(220f, display.X - 16f),
            Math.Max(240f, ImGui.CalcTextSize(text).X + 32f));

        ImGui.SetNextWindowPos(new Num.Vector2(270f, Math.Max(8f, display.Y - 82f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(preferredWidth, 58f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(200f, 52f),
            new Num.Vector2(Math.Max(200f, display.X - 16f), 180f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin(DevToolUiSettings.T("状态###DevToolStatus", "Status###DevToolStatus"), flags))
        {
            FloatingWindowSnap.TrackCurrentWindow("Status");
            ImGui.TextDisabled(text);
        }
        ImGui.End();
    }

    private static string BuildStatusText(EditorPresentationSnapshot snapshot)
    {
        if (snapshot.ToolMode == EditorToolMode.Objects)
        {
            int selected = snapshot.Inspector?.SelectionCount ?? 0;
            string placement = snapshot.PlacementActive
                ? DevToolUiSettings.T("   ·   放置 ", "   ·   Placing ") + snapshot.PlacementType
                : string.Empty;
            return DevToolUiSettings.T("物件 ", "Objects ") + (snapshot.SceneObjects?.Length ?? 0) +
                   DevToolUiSettings.T("   ·   已选 ", "   ·   Selected ") + selected +
                   "   ·   " + snapshot.Document + placement;
        }

        if (snapshot.ToolMode == EditorToolMode.Sound)
        {
            EditorSoundPresentationSnapshot sound = SoundEditorPresentationHub.Current;
            return DevToolUiSettings.T("声音 ", "Sounds ") + (sound.Sounds?.Length ?? 0) + "   ·   " + snapshot.Document;
        }

        if (snapshot.ToolMode == EditorToolMode.Triggers)
        {
            EditorTriggerPresentationSnapshot trigger = TriggerEditorPresentationHub.Current;
            return DevToolUiSettings.T("触发器 ", "Triggers ") + (trigger.Triggers?.Length ?? 0) + "   ·   " + snapshot.Document;
        }

        if (snapshot.ToolMode == EditorToolMode.Map)
        {
            EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;
            return DevToolUiSettings.T("地图 ", "Map ") + (map.Rooms?.Length ?? 0) +
                   DevToolUiSettings.T(" 个房间   ·   ", " rooms   ·   ") + map.RegionName;
        }

        if (snapshot.ToolMode == EditorToolMode.Dialog)
        {
            EditorDialogPresentationSnapshot dialog = DialogEditorPresentationHub.Current;
            return DevToolUiSettings.T("对话 ", "Dialog ") + dialog.SelectedFileName + "   ·   " +
                   (dialog.Events?.Length ?? 0) + DevToolUiSettings.T(" 个事件", " events");
        }

        if (snapshot.ToolMode == EditorToolMode.Relationships)
        {
            EditorRelationshipPresentationSnapshot rel = RelationshipEditorPresentationHub.Current;
            return DevToolUiSettings.T("关系   ·   主体 ", "Relationships   ·   Primary ") +
                   rel.PrimaryCreature + "   ·   " + snapshot.Document;
        }

        return snapshot.Document + "   ·   " + DevToolUiSettings.ToolMode(snapshot.ToolMode);
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
            ImGui.Text(DevToolUiSettings.T("放置 ", "Place ") + snapshot.PlacementType);
            ImGui.TextDisabled(io.KeyShift
                ? DevToolUiSettings.T("点击 · 连续放置", "Click · continuous")
                : DevToolUiSettings.T("点击 · 单次   Shift · 连续", "Click · once   Shift · continuous"));
        }
        ImGui.End();
    }

    private static bool Matches(EditorObjectTypeSnapshot item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();
        if (query.StartsWith("@", StringComparison.Ordinal)) return Contains(item.Source, query.Substring(1));
        if (query.StartsWith(":", StringComparison.Ordinal)) return Contains(item.Category, query.Substring(1));
        if (query.StartsWith("#", StringComparison.Ordinal))
        {
            string needle = query.Substring(1);
            string[] tags = item.Tags ?? Array.Empty<string>();
            for (int i = 0; i < tags.Length; i++) if (Contains(tags[i], needle)) return true;
            return false;
        }
        return Contains(item.DisplayName, query) || Contains(item.Type, query) ||
               Contains(item.Category, query) || Contains(item.Source, query) ||
               Fuzzy(item.DisplayName, query) || Fuzzy(item.Type, query);
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
