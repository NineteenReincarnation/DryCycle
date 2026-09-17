using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Center Scene workspace owned by the Objects page.
///
/// The shared SceneWorkspaceWindow owns only window chrome and dispatch. Object search, grouping,
/// selection semantics and retained projection state stay here with the rest of the Objects views.
/// </summary>
internal static class ObjectSceneWorkspaceView
{
    private sealed class ObjectSceneRow : IDevToolExplorerListItem
    {
        internal EditorObjectSnapshot Item;
        internal string Category;
        internal string Label;
        internal string TooltipText;

        public string StableId => "CenterSceneObject:" + (Item?.Index ?? -1);
        public string PrimaryText => Label ?? string.Empty;
        public string SecondaryText => string.Empty;
        public string StatusText => string.Empty;
        public string Tooltip => TooltipText ?? string.Empty;
    }

    private sealed class ObjectSceneGroup
    {
        internal string Source;
        internal DevToolSourceMark SourceMark;
        internal readonly List<ObjectSceneRow> Rows = new();
    }

    private static string search = string.Empty;
    private static int selectionAnchor = -1;

    private static EditorObjectSnapshot[] projectedObjects;
    private static EditorObjectTypeSnapshot[] projectedLibrary;
    private static string projectedSearch = string.Empty;
    private static bool projectedChinese;
    private static readonly Dictionary<string, EditorObjectTypeSnapshot> MetadataByType =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ObjectSceneGroup> GroupsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ObjectSceneGroup> ProjectedGroups = new();
    private static int projectedMatchCount;

    private static string observedSearch;
    private static string normalizedSearch = string.Empty;

    private static int statusObjectCount = -1;
    private static int statusSelectionCount = -1;
    private static bool statusChinese;
    private static bool statusValid;
    private static string statusText = string.Empty;

    internal static void ResetRetainedState()
    {
        projectedObjects = null;
        projectedLibrary = null;
        projectedSearch = string.Empty;
        projectedChinese = false;
        MetadataByType.Clear();
        GroupsBySource.Clear();
        ProjectedGroups.Clear();
        projectedMatchCount = 0;
        search = string.Empty;
        observedSearch = null;
        normalizedSearch = string.Empty;
        selectionAnchor = -1;
        statusObjectCount = -1;
        statusSelectionCount = -1;
        statusChinese = false;
        statusValid = false;
        statusText = string.Empty;
    }

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        EditorObjectSnapshot[] objects = snapshot.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        int selectedCount = snapshot.Inspector?.SelectionCount ?? 0;

        if (selectionAnchor >= objects.Length)
            selectionAnchor = -1;

        DevToolWidgets.MutedText(GetStatusText(objects.Length, selectedCount));

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
        ImGui.InputText("##CenterSceneObjectSearch", ref search, 128);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureProjection(snapshot, objects);

        ImGuiIOPtr io = ImGui.GetIO();
        for (int sourceIndex = 0; sourceIndex < ProjectedGroups.Count; sourceIndex++)
        {
            ObjectSceneGroup group = ProjectedGroups[sourceIndex];
            DevToolWidgets.SourceHeader(group.SourceMark, 1.34f, 1f);

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

                if (!DevToolExplorerRowRenderer.DrawSelectable(row, item.Selected))
                    continue;

                if (io.KeyShift && selectionAnchor >= 0)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.SelectObjectRange,
                        index: item.Index,
                        secondaryIndex: selectionAnchor,
                        flag: io.KeyCtrl));
                }
                else if (io.KeyCtrl)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.ToggleObjectSelection, item.Index));
                    selectionAnchor = item.Index;
                }
                else
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, item.Index));
                    selectionAnchor = item.Index;
                }
            }
        }

        if (projectedMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景物件。", "No matching scene objects."));
    }

    private static string GetStatusText(int objectCount, int selectedCount)
    {
        bool chinese = DevToolUiSettings.IsChinese;
        if (statusValid && statusObjectCount == objectCount && statusSelectionCount == selectedCount && statusChinese == chinese)
            return statusText;

        statusText = chinese
            ? $"已放置 {objectCount} 个物件 · 已选 {selectedCount}"
            : $"{objectCount} placed · {selectedCount} selected";
        statusObjectCount = objectCount;
        statusSelectionCount = selectedCount;
        statusChinese = chinese;
        statusValid = true;
        return statusText;
    }

    private static void EnsureProjection(EditorPresentationSnapshot snapshot, EditorObjectSnapshot[] objects)
    {
        EditorObjectTypeSnapshot[] library = snapshot.ObjectLibrary ?? Array.Empty<EditorObjectTypeSnapshot>();
        string query = SearchQuery();
        bool chinese = DevToolUiSettings.IsChinese;

        bool libraryChanged = !ReferenceEquals(projectedLibrary, library);
        if (!libraryChanged &&
            ReferenceEquals(projectedObjects, objects) &&
            string.Equals(projectedSearch, query, StringComparison.Ordinal) &&
            projectedChinese == chinese)
            return;

        if (libraryChanged)
        {
            MetadataByType.Clear();
            for (int i = 0; i < library.Length; i++)
            {
                EditorObjectTypeSnapshot metadata = library[i];
                if (metadata == null || string.IsNullOrEmpty(metadata.Type)) continue;
                MetadataByType[metadata.Type] = metadata;
            }
        }

        ProjectedGroups.Clear();
        GroupsBySource.Clear();
        projectedMatchCount = 0;

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null) continue;
            MetadataByType.TryGetValue(item.Type ?? string.Empty, out EditorObjectTypeSnapshot metadata);
            if (!MatchesObject(item, metadata, query)) continue;

            string source = string.IsNullOrWhiteSpace(metadata?.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : metadata.Source;
            string category = string.IsNullOrWhiteSpace(metadata?.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : metadata.Category;
            string displayName = string.IsNullOrWhiteSpace(metadata?.DisplayName)
                ? item.Type
                : metadata.DisplayName;

            if (!GroupsBySource.TryGetValue(source, out ObjectSceneGroup group))
            {
                group = new ObjectSceneGroup
                {
                    Source = source,
                    SourceMark = DevToolSourcePresentation.FromLabel(source)
                };
                GroupsBySource.Add(source, group);
                ProjectedGroups.Add(group);
            }

            group.Rows.Add(new ObjectSceneRow
            {
                Item = item,
                Category = category,
                Label = displayName + "  ·  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")",
                TooltipText = source + " · " + item.Type + " · " + category
            });
            projectedMatchCount++;
        }

        projectedObjects = objects;
        projectedLibrary = library;
        projectedSearch = query;
        projectedChinese = chinese;
    }

    private static string SearchQuery()
    {
        if (string.Equals(observedSearch, search, StringComparison.Ordinal))
            return normalizedSearch;
        observedSearch = search;
        normalizedSearch = search?.Trim() ?? string.Empty;
        return normalizedSearch;
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
}
