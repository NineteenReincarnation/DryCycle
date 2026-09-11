using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevToolOverlay
{
    private static string objectSearch = string.Empty;
    private static bool sceneTab;
    private static int editedObjectIndex = -1;
    private static float editX;
    private static float editY;

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1366f;
        if (display.Y < 1f) display.Y = 768f;

        DrawTopBar(snapshot, display);
        if (snapshot.FocusMode) return;

        DrawActivityBar(snapshot, display);
        if (snapshot.BrowserOpen) DrawBrowser(snapshot, display);
        if (snapshot.InspectorOpen) DrawInspector(snapshot, display);
        DrawStatusBar(snapshot, display);
    }

    private static void DrawTopBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(58f, 8f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(Math.Max(420f, display.X - 116f), 40f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.95f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        if (!ImGui.Begin("##DevToolTopBar", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled(string.IsNullOrEmpty(snapshot.RoomName) ? snapshot.Document : snapshot.RoomName);
        ImGui.SameLine();
        ImGui.TextDisabled("·");
        ImGui.SameLine();
        ImGui.Text(snapshot.ToolMode.ToString());

        ImGui.SameLine(0f, 20f);
        if (ImGui.Button("Save  Ctrl+S"))
            Send(EditorUiCommandKind.Save);

        ImGui.SameLine();
        bool undoDisabled = !snapshot.CanUndo;
        if (undoDisabled) ImGui.BeginDisabled();
        string undo = string.IsNullOrEmpty(snapshot.UndoLabel) ? "Undo" : "Undo " + snapshot.UndoLabel;
        if (ImGui.Button(undo + "##DevToolUndo")) Send(EditorUiCommandKind.Undo);
        if (undoDisabled) ImGui.EndDisabled();

        ImGui.SameLine();
        bool redoDisabled = !snapshot.CanRedo;
        if (redoDisabled) ImGui.BeginDisabled();
        string redo = string.IsNullOrEmpty(snapshot.RedoLabel) ? "Redo" : "Redo " + snapshot.RedoLabel;
        if (ImGui.Button(redo + "##DevToolRedo")) Send(EditorUiCommandKind.Redo);
        if (redoDisabled) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Focus  Tab")) Send(EditorUiCommandKind.ToggleFocus);

        ImGui.End();
    }

    private static void DrawActivityBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(8f, 56f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(44f, 250f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        if (!ImGui.Begin("##DevToolActivity", flags))
        {
            ImGui.End();
            return;
        }

        DrawModeButton("R", "Room", EditorToolMode.Room, snapshot.ToolMode);
        DrawModeButton("O", "Objects", EditorToolMode.Objects, snapshot.ToolMode);
        DrawModeButton("S", "Sound", EditorToolMode.Sound, snapshot.ToolMode);
        DrawModeButton("T", "Triggers", EditorToolMode.Triggers, snapshot.ToolMode);
        DrawModeButton("M", "Map", EditorToolMode.Map, snapshot.ToolMode);

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
        float height = Math.Max(260f, display.Y - 94f);
        ImGui.SetNextWindowPos(new Num.Vector2(58f, 56f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(300f, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("Browser###DevToolBrowser", flags))
        {
            ImGui.End();
            return;
        }

        if (snapshot.ToolMode != EditorToolMode.Objects)
        {
            ImGui.Text(snapshot.ToolMode + " tools");
            ImGui.Separator();
            ImGui.TextDisabled("This workspace will reuse the same overlay shell.");
            ImGui.TextDisabled("Objects is the first functional migration target.");
            ImGui.End();
            return;
        }

        if (ImGui.Button(sceneTab ? "Library" : "Library*")) sceneTab = false;
        ImGui.SameLine();
        if (ImGui.Button(sceneTab ? "Scene*" : "Scene")) sceneTab = true;
        ImGui.Separator();

        if (sceneTab) DrawSceneObjectList(snapshot);
        else DrawObjectLibrary(snapshot);

        ImGui.End();
    }

    private static void DrawObjectLibrary(EditorPresentationSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search##DevToolObjectSearch", ref objectSearch, 128);
        ImGui.TextDisabled("@source   #tag   :category");
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

            string label = item.DisplayName + "##AddObject" + item.Type;
            if (ImGui.Selectable(label, false))
            {
                // First implementation places new objects at the conventional room-camera
                // center. Scene click-placement will replace this once the scene gizmo layer
                // owns a stable camera/screen transform.
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.CreateObject,
                    text: item.Type,
                    x: snapshot.Inspector.HasSelection ? snapshot.Inspector.X : 683f,
                    y: snapshot.Inspector.HasSelection ? snapshot.Inspector.Y : 384f));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(item.Source + " · " + item.Type);
        }

        if (matches == 0) ImGui.TextDisabled("No matching objects.");
    }

    private static void DrawSceneObjectList(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        ImGui.TextDisabled(objects.Length + " placed objects");
        ImGui.Separator();

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            string label = item.Type + "  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")##SceneObject" + item.Index;
            if (ImGui.Selectable(label, item.Selected))
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, item.Index));
        }
    }

    private static void DrawInspector(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        float width = Math.Min(330f, Math.Max(280f, display.X * 0.24f));
        float height = Math.Max(260f, display.Y - 94f);
        ImGui.SetNextWindowPos(new Num.Vector2(display.X - width - 8f, 56f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("Inspector###DevToolInspector", flags))
        {
            ImGui.End();
            return;
        }

        EditorInspectorSnapshot inspector = snapshot.Inspector ?? new EditorInspectorSnapshot();
        if (!inspector.HasSelection)
        {
            editedObjectIndex = -1;
            ImGui.TextDisabled("Nothing selected.");
            ImGui.End();
            return;
        }

        ImGui.Text(inspector.Type);
        ImGui.TextDisabled(inspector.DataType);
        ImGui.Separator();

        if (editedObjectIndex != inspector.ObjectIndex)
        {
            editedObjectIndex = inspector.ObjectIndex;
            editX = inspector.X;
            editY = inspector.Y;
        }

        ImGui.TextDisabled("Transform");
        ImGui.SetNextItemWidth(-1f);
        bool xChanged = ImGui.InputFloat("X##DevToolPosX", ref editX, 1f, 20f, "%.1f");
        ImGui.SetNextItemWidth(-1f);
        bool yChanged = ImGui.InputFloat("Y##DevToolPosY", ref editY, 1f, 20f, "%.1f");
        if ((xChanged || yChanged) && ImGui.IsItemDeactivatedAfterEdit())
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetObjectPosition,
                inspector.ObjectIndex,
                x: editX,
                y: editY));
        }
        if (ImGui.Button("Apply Position"))
        {
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.SetObjectPosition,
                inspector.ObjectIndex,
                x: editX,
                y: editY));
        }

        ImGui.Separator();
        ImGui.TextDisabled("Data");
        if (string.IsNullOrEmpty(inspector.SerializedData))
            ImGui.TextDisabled("No serialized data.");
        else
            ImGui.TextWrapped(inspector.SerializedData);

        ImGui.Separator();
        if (ImGui.Button("Delete Object"))
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteObject, inspector.ObjectIndex));

        ImGui.End();
    }

    private static void DrawStatusBar(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        ImGui.SetNextWindowPos(new Num.Vector2(58f, display.Y - 30f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Num.Vector2(Math.Max(420f, display.X - 116f), 22f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.90f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##DevToolStatus", flags))
        {
            int selected = 0;
            EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
            for (int i = 0; i < objects.Length; i++) if (objects[i].Selected) selected++;
            ImGui.TextDisabled("Objects " + objects.Length + "   ·   Selected " + selected + "   ·   " + snapshot.Document);
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
