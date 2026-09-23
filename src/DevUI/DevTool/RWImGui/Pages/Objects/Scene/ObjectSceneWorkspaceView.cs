using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Commands;
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

        public string StableId => "CenterSceneObject:" + (Item?.StableId ?? 0L);
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

    private static readonly float[] GridSteps = { 10f, 20f, 40f };

    private static string search = string.Empty;
    private static int selectionAnchor = -1;
    private static long selectionAnchorStableId;
    private static int gridStepIndex = 1;

    private static EditorObjectSnapshot[] projectedObjects;
    private static string projectedSearch = string.Empty;
    private static bool projectedChinese;
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
        projectedSearch = string.Empty;
        projectedChinese = false;
        GroupsBySource.Clear();
        ProjectedGroups.Clear();
        projectedMatchCount = 0;
        search = string.Empty;
        observedSearch = null;
        normalizedSearch = string.Empty;
        selectionAnchor = -1;
        selectionAnchorStableId = 0L;
        gridStepIndex = 1;
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
        {
            selectionAnchor = -1;
            selectionAnchorStableId = 0L;
        }

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

        if (selectedCount > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DevToolWidgets.SectionHeader(DevToolUiSettings.T("布局", "LAYOUT"), 1.08f);

            DevToolWidgets.MutedText(DevToolUiSettings.T("网格", "Grid"));
            for (int i = 0; i < GridSteps.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                string label = GridSteps[i].ToString("0");
                if (DevToolWidgets.ActionButton(
                        label,
                        "ObjectGridStep" + i,
                        gridStepIndex == i ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
                {
                    gridStepIndex = i;
                    ObjectMarqueeSelectionView.GridStep = GridSteps[i];
                }
            }

            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    ObjectMarqueeSelectionView.GridVisible
                        ? DevToolUiSettings.T("隐藏网格", "Hide Grid")
                        : DevToolUiSettings.T("显示网格", "Show Grid"),
                    "ObjectToggleSceneGrid",
                    DevToolButtonTone.Subtle))
                ObjectMarqueeSelectionView.GridVisible = !ObjectMarqueeSelectionView.GridVisible;

            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("吸附", "Snap"),
                    "ObjectSnapSelectionToGrid",
                    DevToolButtonTone.Normal))
            {
                EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                    EditorUiCommandKind.SnapSelectionToGrid,
                    x: GridSteps[Math.Max(0, Math.Min(gridStepIndex, GridSteps.Length - 1))]));
            }

            if (selectedCount >= 2)
            {
                ImGui.Spacing();
                DevToolWidgets.MutedText(DevToolUiSettings.T("对齐", "Align"));

                DrawAlignButton(
                    DevToolUiSettings.T("左", "Left"),
                    "ObjectAlignLeft",
                    ObjectSelectionAlignment.Left);
                ImGui.SameLine();
                DrawAlignButton(
                    DevToolUiSettings.T("中X", "Center X"),
                    "ObjectAlignCenterX",
                    ObjectSelectionAlignment.HorizontalCenter);
                ImGui.SameLine();
                DrawAlignButton(
                    DevToolUiSettings.T("右", "Right"),
                    "ObjectAlignRight",
                    ObjectSelectionAlignment.Right);

                DrawAlignButton(
                    DevToolUiSettings.T("下", "Bottom"),
                    "ObjectAlignBottom",
                    ObjectSelectionAlignment.Bottom);
                ImGui.SameLine();
                DrawAlignButton(
                    DevToolUiSettings.T("中Y", "Center Y"),
                    "ObjectAlignCenterY",
                    ObjectSelectionAlignment.VerticalCenter);
                ImGui.SameLine();
                DrawAlignButton(
                    DevToolUiSettings.T("上", "Top"),
                    "ObjectAlignTop",
                    ObjectSelectionAlignment.Top);
            }

            if (selectedCount >= 3)
            {
                ImGui.Spacing();
                DevToolWidgets.MutedText(DevToolUiSettings.T("分布", "Distribute"));
                if (DevToolWidgets.ActionButton(
                        DevToolUiSettings.T("水平等距", "Horizontal"),
                        "ObjectDistributeHorizontal",
                        DevToolButtonTone.Subtle))
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.DistributeSelection,
                        index: (int)ObjectSelectionDistribution.Horizontal));
                }

                ImGui.SameLine();
                if (DevToolWidgets.ActionButton(
                        DevToolUiSettings.T("垂直等距", "Vertical"),
                        "ObjectDistributeVertical",
                        DevToolButtonTone.Subtle))
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.DistributeSelection,
                        index: (int)ObjectSelectionDistribution.Vertical));
                }
            }
        }

        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T("搜索场景物件", "Search scene objects"));
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##CenterSceneObjectSearch", ref search, 128);
        ObjectSceneVisibilityState.SetSearchQuery(search);

        ObjectSceneFilterControls.DrawFocusSummary("Center");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureProjection(objects);

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
                    ObjectSceneFilterControls.DrawCategoryHeader(
                        row.Category,
                        "Center:" + group.Source);
                }

                if (!DevToolExplorerRowRenderer.DrawSelectable(row, item.Selected))
                    continue;

                if (io.KeyShift && selectionAnchor >= 0)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.SelectObjectRange,
                        index: item.Index,
                        secondaryIndex: selectionAnchor,
                        flag: io.KeyCtrl,
                        stableId: item.StableId,
                        secondaryStableId: selectionAnchorStableId));
                }
                else if (io.KeyCtrl)
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.ToggleObjectSelection,
                        item.Index,
                        stableId: item.StableId));
                    selectionAnchor = item.Index;
                    selectionAnchorStableId = item.StableId;
                }
                else
                {
                    EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                        EditorUiCommandKind.SelectObject,
                        item.Index,
                        stableId: item.StableId));
                    selectionAnchor = item.Index;
                    selectionAnchorStableId = item.StableId;
                }
            }
        }

        if (projectedMatchCount == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的场景物件。", "No matching scene objects."));
    }

    private static void DrawAlignButton(
        string label,
        string id,
        ObjectSelectionAlignment alignment)
    {
        if (!DevToolWidgets.ActionButton(label, id, DevToolButtonTone.Subtle))
            return;

        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
            EditorUiCommandKind.AlignSelection,
            index: (int)alignment));
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

    private static void EnsureProjection(EditorObjectSnapshot[] objects)
    {
        string query = SearchQuery();
        bool chinese = DevToolUiSettings.IsChinese;

        if (ReferenceEquals(projectedObjects, objects) &&
            string.Equals(projectedSearch, query, StringComparison.Ordinal) &&
            projectedChinese == chinese)
            return;

        ProjectedGroups.Clear();
        GroupsBySource.Clear();
        projectedMatchCount = 0;

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null || !ObjectSceneVisibilityState.MatchesQuery(item, query)) continue;

            string source = string.IsNullOrWhiteSpace(item.Source)
                ? DevToolUiSettings.T("未知来源", "Unknown Source")
                : item.Source;
            string category = string.IsNullOrWhiteSpace(item.Category)
                ? DevToolUiSettings.T("未分类", "Unsorted")
                : item.Category;
            string displayName = string.IsNullOrWhiteSpace(item.DisplayName)
                ? item.Type
                : item.DisplayName;

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

        for (int i = 0; i < ProjectedGroups.Count; i++)
        {
            ProjectedGroups[i].Rows.Sort((a, b) =>
            {
                int category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (category != 0) return category;
                long aId = a.Item?.StableId ?? 0L;
                long bId = b.Item?.StableId ?? 0L;
                int stable = aId.CompareTo(bId);
                return stable != 0 ? stable : (a.Item?.Index ?? -1).CompareTo(b.Item?.Index ?? -1);
            });
        }

        projectedObjects = objects;
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

}
