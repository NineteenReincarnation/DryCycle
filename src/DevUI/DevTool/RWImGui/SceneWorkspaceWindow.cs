using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Dedicated Scene surface shared by Objects, Sound and Triggers. The underlying editor state and
/// command queues remain unchanged; this class only moves the scene list out of the Browser when the
/// global Scene placement preference is Center.
/// </summary>
internal static class SceneWorkspaceWindow
{
    private sealed class ObjectSceneRow
    {
        internal EditorObjectSnapshot Item;
        internal EditorObjectTypeSnapshot Metadata;
        internal string Source;
        internal string Category;
        internal string DisplayName;
    }

    private sealed class ObjectSceneGroup
    {
        internal string Source;
        internal readonly List<ObjectSceneRow> Rows = new();
    }

    private static string objectSceneSearch = string.Empty;
    private static int objectSelectionAnchor = -1;

    // The object presentation hub already publishes immutable array snapshots and keeps their
    // references stable on cache-hit/shell-only frames. Retain the expensive search/group projection
    // against those array identities so a stable Scene window pays only for ImGui rows, not repeated
    // metadata lookups, fuzzy matching and N x source regrouping every frame.
    private static EditorObjectSnapshot[] projectedObjects;
    private static EditorObjectTypeSnapshot[] projectedLibrary;
    private static string projectedSearch = string.Empty;
    private static bool projectedChinese;
    private static readonly Dictionary<string, EditorObjectTypeSnapshot> metadataByType =
        new(StringComparer.Ordinal);
    private static readonly List<ObjectSceneGroup> projectedGroups = new();
    private static int projectedMatchCount;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available || snapshot.FocusMode ||
            !DevToolUiSettings.SceneInCenter || !ScenePlacementWindow.Supports(snapshot.ToolMode))
            return;

        float scale = Math.Max(0.78f, Math.Min(2.2f, DevToolUiSettings.UiScale));
        float width = Math.Min(
            Math.Max(500f, display.X * 0.34f),
            Math.Max(420f, Math.Min(760f * Math.Min(1.20f, scale), display.X - 32f)));
        float height = Math.Min(
            Math.Max(420f, display.Y * 0.56f),
            Math.Max(320f, Math.Min(680f * Math.Min(1.12f, scale), display.Y - 80f)));
        Num.Vector2 pos = new(
            Math.Max(8f, (display.X - width) * 0.5f),
            Math.Max(62f, (display.Y - height) * 0.52f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(420f, 300f),
            new Num.Vector2(Math.Max(420f, display.X - 16f), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        string title = DevToolUiSettings.T("场景###DevToolSceneWorkspace", "Scene###DevToolSceneWorkspace");
        if (!ImGui.Begin(title, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("SceneWorkspace");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.12f);

        switch (snapshot.ToolMode)
        {
            case EditorToolMode.Objects:
                DrawObjectScene(snapshot);
                break;
            case EditorToolMode.Sound:
                SoundEditorView.DrawSceneWorkspace(SoundEditorPresentationHub.Current);
                break;
            case EditorToolMode.Triggers:
                TriggerEditorView.DrawSceneWorkspace(TriggerEditorPresentationHub.Current);
                break;
        }

        ImGui.End();
    }

    private static void DrawObjectScene(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        int selectedCount = snapshot.Inspector?.SelectionCount ?? 0;

        if (objectSelectionAnchor >= objects.Length)
            objectSelectionAnchor = -1;

        DevToolWidgets.MutedText(DevToolUiSettings.T(
            $"已放置 {objects.Length} 个物件 · 已选 {selectedCount}",
            $"{objects.Length} placed · {selectedCount} selected"));

        if (selectedCount > 0)
        {
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("复制", "Duplicate"),
                    "CenterSceneSelectionDuplicate",
                    DevToolButtonTone.Normal))
            {
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DuplicateSelection));
            }

            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除", "Delete"),
                    "CenterSceneSelectionDelete",
                    DevToolButtonTone.Danger))
            {
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.DeleteSelection));
            }
        }

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("搜索场景物件", "Search scene objects"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##CenterSceneObjectSearch", ref objectSceneSearch, 128);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureObjectProjection(snapshot, objects);

        ImGuiIOPtr io = ImGui.GetIO();
        for (int sourceIndex = 0; sourceIndex < projectedGroups.Count; sourceIndex++)
        {
            ObjectSceneGroup group = projectedGroups[sourceIndex];
            DevToolWidgets.SourceHeader(group.Source, ObjectSourceColor(group.Source), 1.34f, 1f);

            string lastCategory = null;
            List<ObjectSceneRow> rows = group.Rows;
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                ObjectSceneRow row = rows[rowIndex];
                EditorObjectSnapshot item = row.Item;

                if (!string.Equals(lastCategory, row.Category, StringComparison.Ordinal))
                {
                    lastCategory = row.Category;
                    DevToolWidgets.MutedText(row.Category);
                }

                string label = row.DisplayName + "  ·  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") +
                               ")##CenterSceneObject" + item.Index;
                if (!ImGui.Selectable(label, item.Selected))
                {
                    if (ImGui.IsItemHovered())
                        DevToolTooltip.Show(group.Source + " · " + item.Type + " · " + row.Category);
                    continue;
                }

                if (io.KeyShift && objectSelectionAnchor >= 0)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.SelectObjectRange,
                        index: item.Index,
                        secondaryIndex: objectSelectionAnchor,
                        flag: io.KeyCtrl));
                }
                else if (io.KeyCtrl)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.ToggleObjectSelection, item.Index));
                    objectSelectionAnchor = item.Index;
                }
                else
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, item.Index));
                    objectSelectionAnchor = item.Index;
                }
            }
        }

        if (projectedMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景物件。", "No matching scene objects."));
    }

    private static void EnsureObjectProjection(EditorPresentationSnapshot snapshot, EditorObjectSnapshot[] objects)
    {
        EditorObjectTypeSnapshot[] library = snapshot.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        string normalizedSearch = objectSceneSearch?.Trim() ?? string.Empty;
        bool chinese = DevToolUiSettings.IsChinese;

        bool libraryChanged = !ReferenceEquals(projectedLibrary, library);
        if (!libraryChanged &&
            ReferenceEquals(projectedObjects, objects) &&
            string.Equals(projectedSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedChinese == chinese)
            return;

        if (libraryChanged)
        {
            metadataByType.Clear();
            for (int i = 0; i < library.Length; i++)
            {
                EditorObjectTypeSnapshot metadata = library[i];
                if (metadata == null || string.IsNullOrEmpty(metadata.Type)) continue;
                metadataByType[metadata.Type] = metadata;
            }
        }

        projectedGroups.Clear();
        projectedMatchCount = 0;
        Dictionary<string, ObjectSceneGroup> groupsBySource =
            new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;
            metadataByType.TryGetValue(item.Type ?? string.Empty, out EditorObjectTypeSnapshot metadata);
            if (!MatchesObject(item, metadata, normalizedSearch)) continue;

            string source = string.IsNullOrWhiteSpace(metadata?.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : metadata.Source;
            string category = string.IsNullOrWhiteSpace(metadata?.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : metadata.Category;
            string displayName = string.IsNullOrWhiteSpace(metadata?.DisplayName)
                ? item.Type
                : metadata.DisplayName;

            if (!groupsBySource.TryGetValue(source, out ObjectSceneGroup group))
            {
                group = new ObjectSceneGroup { Source = source };
                groupsBySource.Add(source, group);
                projectedGroups.Add(group);
            }

            group.Rows.Add(new ObjectSceneRow
            {
                Item = item,
                Metadata = metadata,
                Source = source,
                Category = category,
                DisplayName = displayName
            });
            projectedMatchCount++;
        }

        projectedObjects = objects;
        projectedLibrary = library;
        projectedSearch = normalizedSearch;
        projectedChinese = chinese;
    }

    private static bool MatchesObject(EditorObjectSnapshot item, EditorObjectTypeSnapshot metadata, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Contains(item.Type, query) ||
               Contains(metadata?.DisplayName, query) ||
               Contains(metadata?.Source, query) ||
               Contains(metadata?.Category, query) ||
               Fuzzy(item.Type, query) ||
               Fuzzy(metadata?.DisplayName, query);
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

    private static Num.Vector4 ObjectSourceColor(string source)
    {
        source ??= string.Empty;
        if (source.IndexOf("DryCycle", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.36f, 0.72f, 1f, 1f);
        if (source.IndexOf("RegionKit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.StartsWith("RK", StringComparison.OrdinalIgnoreCase))
            return new Num.Vector4(1f, 0.70f, 0.34f, 1f);
        if (source.IndexOf("Vanilla", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("Rain World", StringComparison.OrdinalIgnoreCase) >= 0)
            return new Num.Vector4(0.88f, 0.88f, 0.88f, 1f);
        return new Num.Vector4(0.78f, 0.72f, 1f, 1f);
    }
}
