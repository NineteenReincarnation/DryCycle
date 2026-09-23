using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Objects-page Browser implementation.
///
/// This was previously embedded in DevToolOverlay. Keeping object library/scene projection here makes
/// the Objects page own its Browser state just like the other registered pages, while the shared
/// explorer row contract owns stable row identity and tooltip behavior.
/// </summary>
internal static class ObjectExplorerView
{
    private sealed class CategoryRun
    {
        internal string Category;
        internal int Start;
        internal int Count;
    }

    private sealed class ObjectLibraryRow : IDevToolExplorerListItem
    {
        internal EditorObjectTypeSnapshot Item;
        internal string Category;
        internal string DisplayName;
        internal string TooltipText;

        public string StableId => Item?.Type ?? string.Empty;
        public string PrimaryText => DisplayName ?? string.Empty;
        public string SecondaryText => string.Empty;
        public string StatusText => string.Empty;
        public string Tooltip => TooltipText ?? string.Empty;
    }

    private sealed class ObjectLibraryGroup
    {
        internal string Source;
        internal DevToolSourceMark SourceMark;
        internal readonly List<ObjectLibraryRow> Rows = new();
        internal readonly List<CategoryRun> CategoryRuns = new();
    }

    private sealed class SceneObjectRow : IDevToolExplorerListItem
    {
        internal EditorObjectSnapshot Item;
        internal string Category;
        internal string Label;
        internal string TooltipText;

        public string StableId => "SceneObject:" + (Item?.StableId ?? 0L);
        public string PrimaryText => Label ?? string.Empty;
        public string SecondaryText => string.Empty;
        public string StatusText => string.Empty;
        public string Tooltip => TooltipText ?? string.Empty;
    }

    private sealed class SceneObjectGroup
    {
        internal string Source;
        internal DevToolSourceMark SourceMark;
        internal readonly List<SceneObjectRow> Rows = new();
        internal readonly List<CategoryRun> CategoryRuns = new();
    }

    private const float BrowserPaneFontScale = 1.22f;

    private static string objectSearch = string.Empty;
    private static string sceneSearch = string.Empty;
    private static bool sceneTab;
    private static int sceneSelectionAnchor = -1;
    private static long sceneSelectionAnchorStableId;

    private static EditorObjectTypeSnapshot[] projectedObjectLibrary;
    private static string projectedObjectSearch = string.Empty;
    private static bool projectedObjectChinese;
    private static readonly Dictionary<string, ObjectLibraryGroup> ObjectLibraryGroupsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ObjectLibraryGroup> ObjectLibraryGroups = new();
    private static int objectLibraryMatchCount;
    private static string observedObjectSearch;
    private static string normalizedObjectSearch = string.Empty;

    private static EditorObjectSnapshot[] projectedSceneObjects;
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

        foreach (SceneObjectGroup group in SceneGroupsBySource.Values)
        {
            group.Rows.Clear();
            group.CategoryRuns.Clear();
        }
        SceneGroupsBySource.Clear();
        SceneGroups.Clear();
        projectedSceneObjects = null;
        projectedSceneSearch = string.Empty;
        projectedSceneChinese = false;
        sceneMatchCount = 0;
        observedSceneSearch = null;
        normalizedSceneSearch = string.Empty;

        objectSearch = string.Empty;
        sceneSearch = string.Empty;
        sceneTab = false;
        sceneSelectionAnchor = -1;
        sceneSelectionAnchorStableId = 0L;
        sceneStatusObjectCount = -1;
        sceneStatusSelectionCount = -1;
        sceneStatusChinese = false;
        sceneStatusText = string.Empty;
        placementLabelType = string.Empty;
        placementLabelChinese = false;
        placementLabelText = string.Empty;
    }

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        bool sceneInBrowser = !DevToolUiSettings.SceneInCenter;
        if (!sceneInBrowser)
            sceneTab = false;
        else if (!sceneTab)
            ObjectSceneVisibilityState.SetSearchQuery(string.Empty);

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("资源库", "Library"),
                "ObjectsLibraryTab",
                sceneTab ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
        {
            sceneTab = false;
            if (sceneInBrowser)
                ObjectSceneVisibilityState.SetSearchQuery(string.Empty);
        }

        if (sceneInBrowser)
        {
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("场景", "Scene"),
                    "ObjectsSceneTab",
                    sceneTab ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
                sceneTab = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (sceneTab && sceneInBrowser) DrawSceneObjectList(snapshot);
        else DrawObjectLibrary(snapshot);
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
                        if (DevToolExplorerRowRenderer.DrawSelectable(row, selected))
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.BeginPlacement, text: item.Type));
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
                TooltipText = (item.Source ?? string.Empty) + " · " + item.Type
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
        {
            sceneSelectionAnchor = -1;
            sceneSelectionAnchorStableId = 0L;
        }

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
        ObjectSceneVisibilityState.SetSearchQuery(sceneSearch);
        ObjectSceneFilterControls.DrawFocusSummary("Browser");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureSceneProjection(objects);
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
                ObjectSceneFilterControls.DrawCategoryHeader(run.Category, "BrowserScene");

                using DevToolListClipper clipper = new(run.Count);
                while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
                {
                    for (int localIndex = firstVisible; localIndex < lastVisibleExclusive; localIndex++)
                    {
                        SceneObjectRow row = rows[run.Start + localIndex];
                        EditorObjectSnapshot item = row.Item;

                        bool clicked = DevToolExplorerRowRenderer.DrawSelectable(row, item.Selected);
                        if (!clicked) continue;

                        if (io.KeyShift && sceneSelectionAnchor >= 0)
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                                EditorUiCommandKind.SelectObjectRange,
                                index: item.Index,
                                secondaryIndex: sceneSelectionAnchor,
                                flag: io.KeyCtrl,
                                stableId: item.StableId,
                                secondaryStableId: sceneSelectionAnchorStableId));
                        }
                        else if (io.KeyCtrl)
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                                EditorUiCommandKind.ToggleObjectSelection,
                                item.Index,
                                stableId: item.StableId));
                            sceneSelectionAnchor = item.Index;
                            sceneSelectionAnchorStableId = item.StableId;
                        }
                        else
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                                EditorUiCommandKind.SelectObject,
                                item.Index,
                                stableId: item.StableId));
                            sceneSelectionAnchor = item.Index;
                            sceneSelectionAnchorStableId = item.StableId;
                        }
                    }
                }
            }
        }

        if (sceneMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景物件。", "No matching scene objects."));
    }

    private static void EnsureSceneProjection(EditorObjectSnapshot[] objects)
    {
        string normalizedSearch = NormalizeSceneSearch();
        bool chinese = DevToolUiSettings.IsChinese;
        if (ReferenceEquals(projectedSceneObjects, objects) &&
            string.Equals(projectedSceneSearch, normalizedSearch, StringComparison.Ordinal) &&
            projectedSceneChinese == chinese)
            return;

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
            if (item == null || !ObjectSceneVisibilityState.MatchesQuery(item, normalizedSearch))
                continue;

            string source = string.IsNullOrWhiteSpace(item.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : item.Source;
            string category = string.IsNullOrWhiteSpace(item.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : item.Category;
            string displayName = string.IsNullOrWhiteSpace(item.DisplayName)
                ? item.Type
                : item.DisplayName;

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

            group.Rows.Add(new SceneObjectRow
            {
                Item = item,
                Category = category,
                Label = displayName + "  ·  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")",
                TooltipText = source + " · " + item.Type + " · " + category
            });
            sceneMatchCount++;
        }

        for (int i = 0; i < SceneGroups.Count; i++)
        {
            SceneObjectGroup group = SceneGroups[i];
            group.Rows.Sort((a, b) =>
            {
                int category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (category != 0) return category;
                long aId = a.Item?.StableId ?? 0L;
                long bId = b.Item?.StableId ?? 0L;
                int stable = aId.CompareTo(bId);
                return stable != 0 ? stable : (a.Item?.Index ?? -1).CompareTo(b.Item?.Index ?? -1);
            });

            group.CategoryRuns.Clear();
            for (int rowIndex = 0; rowIndex < group.Rows.Count; rowIndex++)
            {
                string category = group.Rows[rowIndex].Category;
                if (group.CategoryRuns.Count == 0 ||
                    !string.Equals(
                        group.CategoryRuns[group.CategoryRuns.Count - 1].Category,
                        category,
                        StringComparison.Ordinal))
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
            }
        }

        projectedSceneObjects = objects;
        projectedSceneSearch = normalizedSearch;
        projectedSceneChinese = chinese;
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
