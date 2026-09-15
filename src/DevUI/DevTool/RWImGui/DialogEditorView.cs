using System;
using System.Collections.Generic;
using System.IO;
using DryCycle.DevUI.DevTool.Dialog;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DialogEditorView
{
    private sealed class DialogBrowserRow
    {
        internal string Path;
        internal string File;
        internal string Label;
    }

    private static string search = string.Empty;
    private static string[] projectedPaths;
    private static string projectedSearch = string.Empty;
    private static readonly List<DialogBrowserRow> projectedRows = new();

    internal static void DrawBrowser(EditorDialogPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("对话浏览器不可用。", "Dialog browser unavailable."), true);
            return;
        }

        ImGui.TextDisabled(DevToolUiSettings.T("语言 · ", "LANGUAGE · ") + snapshot.Language);
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索对话", "Search dialogs"), "DialogSearch", ref search, 128);
        ImGui.Separator();

        EnsureBrowserProjection(snapshot.DialogPaths ?? Array.Empty<string>());
        for (int i = 0; i < projectedRows.Count; i++)
        {
            DialogBrowserRow row = projectedRows[i];
            bool selected = string.Equals(row.Path, snapshot.SelectedPath, StringComparison.Ordinal);
            if (ImGui.Selectable(row.Label, selected))
                DialogEditorCommandQueue.Enqueue(new DialogEditorCommand(DialogEditorCommandKind.SelectDialog, row.Path));
            if (selected) ImGui.SetItemDefaultFocus();
            if (ImGui.IsItemHovered()) DevToolTooltip.Show(row.Path);
        }

        if (projectedRows.Count == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的对话文件。", "No matching dialog files."), true);
    }

    internal static void DrawPreview(
        EditorDialogPresentationSnapshot snapshot,
        Num.Vector2 position,
        Num.Vector2 size)
    {
        if (!snapshot.Available || size.X < 180f || size.Y < 120f) return;

        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(320f, 220f), new Num.Vector2(4000f, 4000f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(DevToolUiSettings.T("对话预览###DevToolDialogPreview", "Dialog Preview###DevToolDialogPreview"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("DialogPreview");

        if (string.IsNullOrEmpty(snapshot.SelectedPath))
        {
            ImGui.TextDisabled(DevToolUiSettings.T("请从浏览器选择一个对话文件。", "Select a dialog file from the browser."));
            ImGui.End();
            return;
        }

        ImGui.Text(snapshot.SelectedFileName);
        string eventCount = (snapshot.Events?.Length ?? 0) + DevToolUiSettings.T(" 个事件", " events");
        if (DevToolWidgets.SameLineIfFits(ImGui.CalcTextSize(eventCount).X))
            ImGui.TextDisabled(eventCount);
        else
            ImGui.TextDisabled(eventCount);
        ImGui.Separator();

        EditorDialogEventSnapshot[] events = snapshot.Events ?? Array.Empty<EditorDialogEventSnapshot>();
        if (events.Length == 0)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前文件/蛞蝓猫/语言组合没有可预览事件。", "No previewable events were produced for this file/slugcat/language combination."));
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
                    ImGui.TextDisabled(DevToolUiSettings.T("文本  ·  等待 ", "TEXT  ·  wait ") + value.InitialWait + DevToolUiSettings.T("  ·  停留 ", "  ·  linger ") + value.Linger);
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                case EditorDialogEventKind.Chatlog:
                    ImGui.TextDisabled(DevToolUiSettings.T("聊天记录", "CHATLOG"));
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                case EditorDialogEventKind.Wait:
                    ImGui.TextDisabled(DevToolUiSettings.T("等待", "WAIT"));
                    ImGui.Text(value.InitialWait + DevToolUiSettings.T(" tick", " ticks"));
                    break;
                case EditorDialogEventKind.Special:
                    ImGui.TextDisabled(DevToolUiSettings.T("特殊事件  ·  等待 ", "SPECIAL EVENT  ·  wait ") + value.InitialWait);
                    ImGui.TextWrapped(value.Text ?? string.Empty);
                    break;
                default:
                    ImGui.TextDisabled(DevToolUiSettings.T("事件  ·  等待 ", "EVENT  ·  wait ") + value.InitialWait);
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
            DevToolWidgets.MutedText(DevToolUiSettings.T("对话浏览器不可用。", "Dialog browser unavailable."), true);
            return;
        }

        ImGui.TextDisabled(DevToolUiSettings.T("对话预览", "DIALOG PREVIEW"));
        ImGui.Text(DevToolUiSettings.T("语言", "Language"));
        ImGui.SameLine();
        ImGui.TextDisabled(snapshot.Language);
        ImGui.Text(DevToolUiSettings.T("文件", "File"));
        ImGui.TextWrapped(string.IsNullOrEmpty(snapshot.SelectedFileName) ? DevToolUiSettings.T("<无>", "<none>") : snapshot.SelectedFileName);
        ImGui.Text(DevToolUiSettings.T("事件", "Events"));
        ImGui.SameLine();
        ImGui.TextDisabled((snapshot.Events?.Length ?? 0).ToString());

        if (!string.IsNullOrEmpty(snapshot.SelectedPath))
        {
            ImGui.Separator();
            ImGui.TextDisabled(DevToolUiSettings.T("源路径", "SOURCE PATH"));
            ImGui.TextWrapped(snapshot.SelectedPath);
        }

        ImGui.Separator();
        ImGui.TextWrapped(DevToolUiSettings.T(
            "原版 DialogPage 本质上是预览工具，而不是文本文件编辑器。新版工作区保留这个边界，不会从 DevTool 直接写入对话资源。",
            "Vanilla DialogPage is a preview tool, not a text-file editor. The rebuilt workspace intentionally preserves that boundary rather than writing conversation resources from DevTool."));
    }

    private static void EnsureBrowserProjection(string[] paths)
    {
        string normalizedSearch = search?.Trim() ?? string.Empty;
        if (ReferenceEquals(projectedPaths, paths) &&
            string.Equals(projectedSearch, normalizedSearch, StringComparison.Ordinal))
            return;

        projectedRows.Clear();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            string file = Path.GetFileName(path) ?? path;
            if (!Matches(file, normalizedSearch)) continue;
            projectedRows.Add(new DialogBrowserRow
            {
                Path = path,
                File = file,
                Label = file + "##DialogFile" + i
            });
        }

        projectedPaths = paths;
        projectedSearch = normalizedSearch;
    }

    private static bool Matches(string value, string normalizedQuery) =>
        string.IsNullOrEmpty(normalizedQuery) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) >= 0);
}
