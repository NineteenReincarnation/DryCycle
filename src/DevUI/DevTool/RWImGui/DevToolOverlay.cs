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

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1366f;
        if (display.Y < 1f) display.Y = 768f;

        // Map, Dialog and Relationships are true editor workspaces. Their windows now use
        // first-use defaults only; developers can move/resize them independently afterwards.
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
            if (snapshot.BrowserOpen) DrawBrowser(snapshot, display);
            if (snapshot.InspectorOpen) DrawInspector(snapshot, display);
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
        string undo = string.IsNullOrEmpty(snapshot.UndoLabel) ? "Undo" : "Undo " + snapshot.UndoLabel;
        string redo = string.IsNullOrEmpty(snapshot.RedoLabel) ? "Redo" : "Redo " + snapshot.RedoLabel;
        float textWidth = ImGui.CalcTextSize(room + " · " + snapshot.ToolMode).X;
        float preferredWidth = Math.Min(
            Math.Max(420f, display.X - 16f),
            Math.Max(500f, textWidth + ImGui.CalcTextSize(undo + redo).X + 300f));

        // The UI switch occupies the top-left by default. This command window starts to its
        // right, but only on first use; its saved/user placement is never overwritten later.
        ImGui.SetNextWindowPos(new Num.Vector2(168f, 8f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(preferredWidth, 62f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(360f, 54f),
            new Num.Vector2(Math.Max(360f, display.X - 16f), 220f));
        ImGui.SetNextWindowBgAlpha(0.95f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("Commands###DevToolTopBar", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled(room);
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.Text(snapshot.ToolMode.ToString());

        if (snapshot.PlacementActive)
        {
            ImGui.SameLine(0f, 16f);
            ImGui.Text("Place: " + snapshot.PlacementType);
        }

        ImGui.SameLine(0f, 20f);
        if (ImGui.Button("Save  Ctrl+S")) Send(EditorUiCommandKind.Save);

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
        if (ImGui.Button("Focus  Tab")) Send(EditorUiCommandKind.ToggleFocus);
        ImGui.End();
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 76f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(54f, 350f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(52f, 220f),
            new Num.Vector2(180f, Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar;
        if (!ImGui.Begin("Tools###DevToolActivity", flags))
        {
            ImGui.End();
            return;
        }

        DrawModeButton("R", "Room", EditorToolMode.Room, snapshot.ToolMode);
        DrawModeButton("O", "Objects", EditorToolMode.Objects, snapshot.ToolMode);
        DrawModeButton("S", "Sound", EditorToolMode.Sound, snapshot.ToolMode);
        DrawModeButton("T", "Triggers", EditorToolMode.Triggers, snapshot.ToolMode);
        DrawModeButton("M", "Map", EditorToolMode.Map, snapshot.ToolMode);
        DrawModeButton("D", "Dialog", EditorToolMode.Dialog, snapshot.ToolMode);
        DrawModeButton("L", "Relationships", EditorToolMode.Relationships, snapshot.ToolMode);

        ImGui.Separator();
        if (ImGui.Button(snapshot.BrowserOpen ? "<" : ">", new Num.Vector2(28f, 0f)))
            Send(EditorUiCommandKind.ToggleBrowser);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle browser · Ctrl+B");

        if (ImGui.Button(snapshot.InspectorOpen ? "I" : "i", new Num.Vector2(28f, 0f)))
            Send(EditorUiCommandKind.ToggleInspector);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle inspector · Ctrl+I");
        ImGui.End();
    }

    private static void DrawModeButton(string text, string tooltip, EditorToolMode mode, EditorToolMode current)
    {
        if (current == mode) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button(text + "##DevToolMode" + mode, new Num.Vector2(28f, 28f)))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SetToolMode, mode: mode));
        if (current == mode) ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private static void DrawBrowser(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float maxHeight = Math.Max(260f, display.Y - 32f);
        ImGui.SetNextWindowPos(new Num.Vector2(70f, 76f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(300f, Math.Min(440f, maxHeight)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(220f, 180f),
            new Num.Vector2(Math.Max(220f, display.X - 16f), maxHeight));
        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin("Browser###DevToolBrowser", flags))
        {
            ImGui.End();
            return;
        }

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
            if (ImGui.Button(sceneTab ? "Library" : "Library*")) sceneTab = false;
            ImGui.SameLine();
            if (ImGui.Button(sceneTab ? "Scene*" : "Scene")) sceneTab = true;
            ImGui.Separator();
            if (sceneTab) DrawSceneObjectList(snapshot);
            else DrawObjectLibrary(snapshot);
        }
        else
            ImGui.TextDisabled(snapshot.ToolMode + " tools unavailable.");

        ImGui.End();
    }

    private static void DrawObjectLibrary(EditorPresentationSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search##DevToolObjectSearch", ref objectSearch, 128);
        ImGui.TextDisabled("@source   #tag   :category");

        if (snapshot.PlacementActive)
        {
            ImGui.Separator();
            ImGui.Text("Placing " + snapshot.PlacementType);
            ImGui.TextDisabled("Left click room · Shift = repeat · Esc/right click = cancel");
            if (ImGui.Button("Cancel placement")) Send(EditorUiCommandKind.CancelPlacement);
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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Source + " · " + item.Type);
        }
        if (matches == 0) ImGui.TextDisabled("No matching objects.");
    }

    private static void DrawSceneObjectList(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        ImGui.TextDisabled(objects.Length + " placed objects");
        int selectedCount = snapshot.Inspector?.SelectionCount ?? 0;
        if (selectedCount > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Duplicate##SceneSelection")) Send(EditorUiCommandKind.DuplicateSelection);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete##SceneSelection")) Send(EditorUiCommandKind.DeleteSelection);
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

    private static void DrawInspector(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float defaultWidth = InspectorWidth(display);
        float maxHeight = Math.Max(260f, display.Y - 32f);
        ImGui.SetNextWindowPos(new Num.Vector2(Math.Max(8f, display.X - defaultWidth - 8f), 76f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(defaultWidth, Math.Min(440f, maxHeight)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(260f, 180f),
            new Num.Vector2(Math.Max(260f, display.X - 16f), maxHeight));
        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin("Inspector###DevToolInspector", flags))
        {
            ImGui.End();
            return;
        }

        if (snapshot.ToolMode == EditorToolMode.Room)
        {
            RoomSettingsView.DrawInspector(RoomEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, "Fallback for template, terrain or custom RoomSettings controls not migrated yet.");
        }
        else if (snapshot.ToolMode == EditorToolMode.Objects)
            ObjectInspectorView.Draw(snapshot.Inspector);
        else if (snapshot.ToolMode == EditorToolMode.Sound)
        {
            SoundEditorView.DrawInspector(SoundEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, "Fallback for custom SoundPage controls or mod-added sound tooling not migrated yet.");
        }
        else if (snapshot.ToolMode == EditorToolMode.Triggers)
        {
            TriggerEditorView.DrawInspector(TriggerEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, "Fallback for custom Trigger/TriggeredEvent controls not represented by the native inspector.");
        }
        else if (snapshot.ToolMode == EditorToolMode.Map)
        {
            MapEditorView.DrawInspector(MapEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, "Fallback for vanilla or mod-added MapPage controls not represented by the graph editor.");
        }
        else if (snapshot.ToolMode == EditorToolMode.Dialog)
            DialogEditorView.DrawInspector(DialogEditorPresentationHub.Current);
        else if (snapshot.ToolMode == EditorToolMode.Relationships)
        {
            RelationshipEditorView.DrawInspector(RelationshipEditorPresentationHub.Current);
            DrawLegacyFallback(snapshot, "Fallback for custom RelationshipPage extensions not represented by the matrix editor.");
        }
        else
            ImGui.TextDisabled(snapshot.ToolMode + " inspector unavailable.");

        ImGui.End();
    }

    private static void DrawLegacyFallback(EditorPresentationSnapshot snapshot, string tooltip)
    {
        ImGui.Separator();
        bool legacyVisible = snapshot.Inspector?.LegacyUiVisible == true;
        if (ImGui.Button(legacyVisible ? "Hide Original DevUI" : "Show Original DevUI"))
            Send(EditorUiCommandKind.ToggleLegacyUi);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private static void DrawStatusBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        string text = BuildStatusText(snapshot);
        float preferredWidth = Math.Min(
            Math.Max(220f, display.X - 16f),
            Math.Max(220f, ImGui.CalcTextSize(text).X + 28f));

        ImGui.SetNextWindowPos(new Num.Vector2(168f, Math.Max(8f, display.Y - 74f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(preferredWidth, 58f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(180f, 52f),
            new Num.Vector2(Math.Max(180f, display.X - 16f), 180f));
        ImGui.SetNextWindowBgAlpha(0.90f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin("Status###DevToolStatus", flags))
            ImGui.TextDisabled(text);
        ImGui.End();
    }

    private static string BuildStatusText(EditorPresentationSnapshot snapshot)
    {
        if (snapshot.ToolMode == EditorToolMode.Objects)
        {
            int selected = snapshot.Inspector?.SelectionCount ?? 0;
            string placement = snapshot.PlacementActive ? "   ·   Placing " + snapshot.PlacementType : string.Empty;
            return "Objects " + (snapshot.SceneObjects?.Length ?? 0) + "   ·   Selected " + selected +
                   "   ·   " + snapshot.Document + placement;
        }

        if (snapshot.ToolMode == EditorToolMode.Sound)
        {
            EditorSoundPresentationSnapshot sound = SoundEditorPresentationHub.Current;
            return "Sounds " + (sound.Sounds?.Length ?? 0) + "   ·   " + snapshot.Document;
        }

        if (snapshot.ToolMode == EditorToolMode.Triggers)
        {
            EditorTriggerPresentationSnapshot trigger = TriggerEditorPresentationHub.Current;
            return "Triggers " + (trigger.Triggers?.Length ?? 0) + "   ·   " + snapshot.Document;
        }

        if (snapshot.ToolMode == EditorToolMode.Map)
        {
            EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;
            return "Map " + (map.Rooms?.Length ?? 0) + " rooms   ·   " + map.RegionName;
        }

        if (snapshot.ToolMode == EditorToolMode.Dialog)
        {
            EditorDialogPresentationSnapshot dialog = DialogEditorPresentationHub.Current;
            return "Dialog " + dialog.SelectedFileName + "   ·   " + (dialog.Events?.Length ?? 0) + " events";
        }

        if (snapshot.ToolMode == EditorToolMode.Relationships)
        {
            EditorRelationshipPresentationSnapshot rel = RelationshipEditorPresentationHub.Current;
            return "Relationships   ·   Primary " + rel.PrimaryCreature + "   ·   " + snapshot.Document;
        }

        return snapshot.Document + "   ·   " + snapshot.ToolMode;
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
        Num.Vector2 hintSize = new(230f, 44f);
        Num.Vector2 pos = new(
            Math.Min(Math.Max(8f, mouse.X + 18f), Math.Max(8f, display.X - hintSize.X - 8f)),
            Math.Min(Math.Max(8f, mouse.Y + 18f), Math.Max(8f, display.Y - hintSize.Y - 8f)));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(hintSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##DevToolPlacementHint", flags))
        {
            ImGui.Text("Place " + snapshot.PlacementType);
            ImGui.TextDisabled(io.KeyShift ? "Click · continuous" : "Click · once   Shift · continuous");
        }
        ImGui.End();
    }

    private static float InspectorWidth(Num.Vector2 display) =>
        Math.Min(360f, Math.Max(300f, display.X * 0.26f));

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
