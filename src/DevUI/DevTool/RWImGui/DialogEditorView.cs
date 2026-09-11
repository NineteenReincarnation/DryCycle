using System;
using System.IO;
using DryCycle.DevUI.DevTool.Dialog;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DialogEditorView
{
    private static string search = string.Empty;

    internal static void DrawBrowser(EditorDialogPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Dialog browser unavailable.");
            return;
        }

        ImGui.TextDisabled("LANGUAGE · " + snapshot.Language);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search dialogs##DialogSearch", ref search, 128);
        ImGui.Separator();

        string[] paths = snapshot.DialogPaths ?? Array.Empty<string>();
        int matches = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            string file = Path.GetFileName(path) ?? path;
            if (!Matches(file, search)) continue;
            matches++;
            bool selected = string.Equals(path, snapshot.SelectedPath, StringComparison.Ordinal);
            if (ImGui.Selectable(file + "##DialogFile" + i, selected))
                DialogEditorCommandQueue.Enqueue(new DialogEditorCommand(DialogEditorCommandKind.SelectDialog, path));
            if (selected) ImGui.SetItemDefaultFocus();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        }

        if (matches == 0) ImGui.TextDisabled("No matching dialog files.");
    }

    internal static void DrawPreview(
        EditorDialogPresentationSnapshot snapshot,
        Num.Vector2 position,
        Num.Vector2 size)
    {
        if (!snapshot.Available || size.X < 180f || size.Y < 120f) return;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.98f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("Dialog Preview###DevToolDialogPreview", flags))
        {
            ImGui.End();
            return;
        }

        if (string.IsNullOrEmpty(snapshot.SelectedPath))
        {
            ImGui.TextDisabled("Select a dialog file from the browser.");
            ImGui.End();
            return;
        }

        ImGui.Text(snapshot.SelectedFileName);
        ImGui.SameLine();
        ImGui.TextDisabled((snapshot.Events?.Length ?? 0) + " events");
        ImGui.Separator();

        EditorDialogEventSnapshot[] events = snapshot.Events ?? Array.Empty<EditorDialogEventSnapshot>();
        if (events.Length == 0)
        {
            ImGui.TextDisabled("No previewable events were produced for this file/slugcat/language combination.");
            ImGui.End();
            return;
        }

        for (int i = 0; i < events.Length; i++)
        {
            EditorDialogEventSnapshot value = events[i];
            ImGui.PushID(i);
            switch (value.Kind)
            {
                case EditorDialogEventKind.Text:
                    ImGui.TextDisabled("TEXT  ·  wait " + value.InitialWait + "  ·  linger " + value.Linger);
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                case EditorDialogEventKind.Chatlog:
                    ImGui.TextDisabled("CHATLOG");
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                case EditorDialogEventKind.Wait:
                    ImGui.TextDisabled("WAIT");
                    ImGui.Text("" + value.InitialWait + " ticks");
                    break;
                case EditorDialogEventKind.Special:
                    ImGui.TextDisabled("SPECIAL EVENT  ·  wait " + value.InitialWait);
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                default:
                    ImGui.TextDisabled("EVENT  ·  wait " + value.InitialWait);
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
            }
            ImGui.PopID();
            if (i < events.Length - 1) ImGui.Separator();
        }

        ImGui.End();
    }

    internal static void DrawInspector(EditorDialogPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Dialog browser unavailable.");
            return;
        }

        ImGui.TextDisabled("DIALOG PREVIEW");
        ImGui.Text("Language");
        ImGui.SameLine();
        ImGui.TextDisabled(snapshot.Language);
        ImGui.Text("File");
        ImGui.TextWrapped(string.IsNullOrEmpty(snapshot.SelectedFileName) ? "<none>" : snapshot.SelectedFileName);
        ImGui.Text("Events");
        ImGui.SameLine();
        ImGui.TextDisabled((snapshot.Events?.Length ?? 0).ToString());

        if (!string.IsNullOrEmpty(snapshot.SelectedPath))
        {
            ImGui.Separator();
            ImGui.TextDisabled("SOURCE PATH");
            ImGui.TextWrapped(snapshot.SelectedPath);
        }

        ImGui.Separator();
        ImGui.TextWrapped("Vanilla DialogPage is a preview tool, not a text-file editor. The rebuilt workspace intentionally preserves that boundary rather than writing conversation resources from DevTool.");
    }

    private static bool Matches(string value, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
}
