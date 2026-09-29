using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

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
        internal readonly List<ObjectLibraryRow> Rows = new();
        internal readonly List<CategoryRun> CategoryRuns = new();
    }

    private const float BrowserPaneFontScale = 1.22f;

    private static string objectSearch = string.Empty;
    private static string activeObjectSource = string.Empty;
    private static EditorObjectTypeSnapshot[] projectedObjectLibrary;
    private static string projectedObjectSearch = string.Empty;
    private static bool projectedObjectChinese;
    private static readonly Dictionary<string, ObjectLibraryGroup> ObjectLibraryGroupsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ObjectLibraryGroup> ObjectLibraryGroups = new();
    private static string observedObjectSearch;
    private static string normalizedObjectSearch = string.Empty;

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
        activeObjectSource = string.Empty;
        projectedObjectLibrary = null;
        projectedObjectSearch = string.Empty;
        projectedObjectChinese = false;
        observedObjectSearch = null;
        normalizedObjectSearch = string.Empty;

        objectSearch = string.Empty;
        placementLabelType = string.Empty;
        placementLabelChinese = false;
        placementLabelText = string.Empty;
    }

    internal static void Draw(EditorPresentationSnapshot snapshot)
    {
        // Browser is now exclusively the object library. The dedicated Scene workspace is opened
        // from the Object page's top Scene button, so a single Library tab only wastes vertical
        // space and duplicates navigation.
        DrawObjectLibrary(snapshot);
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

        DrawObjectSourceTabs();
        ObjectLibraryGroup activeGroup = ResolveActiveObjectGroup();
        if (activeGroup == null)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有可用的物件来源。", "No object sources available."));
            return;
        }

        if (activeGroup.Rows.Count == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("当前 Mod 没有匹配的物件。", "No matching objects in this mod."));
            return;
        }

        List<ObjectLibraryRow> rows = activeGroup.Rows;
        List<CategoryRun> categoryRuns = activeGroup.CategoryRuns;
        for (int categoryIndex = 0; categoryIndex < categoryRuns.Count; categoryIndex++)
        {
            CategoryRun run = categoryRuns[categoryIndex];
            if (categoryIndex > 0)
                ImGui.Spacing();

            DrawObjectCategoryLabel(run.Category);

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
                    {
                        EditorUiCommandQueue.Enqueue(
                            new EditorUiCommand(
                                EditorUiCommandKind.BeginPlacement,
                                text: item.Type));
                    }
                }
            }
        }
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
        ObjectLibraryGroupsBySource.Clear();
        ObjectLibraryGroups.Clear();

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
            if (item == null)
                continue;

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
                    Source = source
                };
                ObjectLibraryGroupsBySource[source] = group;
                ObjectLibraryGroups.Add(group);
            }

            if (!MatchesLibrary(item, normalizedSearch, searchMode, needle))
                continue;

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
                TooltipText = (item.Source ?? string.Empty) + " | " + item.Type
            });
        }

        ObjectLibraryGroups.Sort(CompareObjectSources);
        EnsureActiveObjectSource();

        projectedObjectLibrary = library;
        projectedObjectSearch = normalizedSearch;
        projectedObjectChinese = chinese;
    }

    private static void DrawObjectSourceTabs()
    {
        if (ObjectLibraryGroups.Count == 0)
            return;

        DevToolWidgets.MutedText(DevToolUiSettings.T("Mod", "MOD"));

        for (int i = 0; i < ObjectLibraryGroups.Count; i++)
        {
            ObjectLibraryGroup group = ObjectLibraryGroups[i];
            bool active = string.Equals(
                activeObjectSource,
                group.Source,
                StringComparison.OrdinalIgnoreCase);

            if (DevToolWidgets.ActionButton(
                    group.Source,
                    "ObjectSourceTab:" + group.Source,
                    active ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            {
                activeObjectSource = group.Source;
            }

            if (i + 1 < ObjectLibraryGroups.Count)
            {
                float nextWidth =
                    DevToolWidgets.ButtonWidth(
                        ObjectLibraryGroups[i + 1].Source);
                DevToolWidgets.SameLineIfFits(nextWidth);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static ObjectLibraryGroup ResolveActiveObjectGroup()
    {
        EnsureActiveObjectSource();

        if (!string.IsNullOrEmpty(activeObjectSource) &&
            ObjectLibraryGroupsBySource.TryGetValue(
                activeObjectSource,
                out ObjectLibraryGroup group))
        {
            return group;
        }

        return ObjectLibraryGroups.Count > 0
            ? ObjectLibraryGroups[0]
            : null;
    }

    private static void EnsureActiveObjectSource()
    {
        if (!string.IsNullOrEmpty(activeObjectSource) &&
            ObjectLibraryGroupsBySource.ContainsKey(activeObjectSource))
            return;

        activeObjectSource =
            ObjectLibraryGroups.Count > 0
                ? ObjectLibraryGroups[0].Source
                : string.Empty;
    }

    private static int CompareObjectSources(
        ObjectLibraryGroup left,
        ObjectLibraryGroup right)
    {
        int leftPriority = ObjectSourcePriority(left?.Source);
        int rightPriority = ObjectSourcePriority(right?.Source);
        int priority = leftPriority.CompareTo(rightPriority);
        if (priority != 0)
            return priority;

        return string.Compare(
            left?.Source,
            right?.Source,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int ObjectSourcePriority(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 3;

        if (source.IndexOf("PlacedObject Registry", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("Vanilla", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("Rain World", StringComparison.OrdinalIgnoreCase) >= 0)
            return 0;

        if (source.IndexOf("DryCycle", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;

        return 2;
    }

    private static void DrawObjectCategoryLabel(string category)
    {
        // Category tags use the same primary-gold language as the Browser title, but at a smaller
        // hierarchy level so they stay clearly subordinate to the pane title.
        const float categoryScale = 1.52f;
        DevToolWidgets.PrimaryLabel(
            category ?? string.Empty,
            categoryScale,
            BrowserPaneFontScale,
            1.35f);
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
