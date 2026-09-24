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
    private static readonly float[] GridSteps = { 10f, 20f, 40f };

    private static string search = string.Empty;
    private static long selectionAnchorStableId;
    private static int gridStepIndex = 1;

    private static string observedSearch;
    private static string normalizedSearch = string.Empty;

    private static int statusObjectCount = -1;
    private static int statusSelectionCount = -1;
    private static bool statusChinese;
    private static bool statusValid;
    private static string statusText = string.Empty;

    internal static void ResetRetainedState()
    {
        search = string.Empty;
        observedSearch = null;
        normalizedSearch = string.Empty;
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

        ObjectSceneListProjectionSnapshot projection =
            ObjectSceneListProjection.Get(objects, SearchQuery());

        ImGuiIOPtr io = ImGui.GetIO();
        ObjectSceneProjectedCategory[] categories = projection.Categories;
        for (int categoryIndex = 0; categoryIndex < categories.Length; categoryIndex++)
        {
            ObjectSceneProjectedCategory category = categories[categoryIndex];
            if (categoryIndex > 0) ImGui.Spacing();

            ObjectSceneFilterControls.DrawCategoryHeader(
                category.Category,
                "CenterScene");

            List<ObjectSceneProjectedRow> rows = category.Rows;
            using DevToolListClipper clipper = new(rows.Count);
            while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
            {
                for (int rowIndex = firstVisible; rowIndex < lastVisibleExclusive; rowIndex++)
                {
                    ObjectSceneProjectedRow row = rows[rowIndex];
                    EditorObjectSnapshot item = row.Item;
                    if (!DevToolExplorerRowRenderer.DrawSelectable(row, item.Selected))
                        continue;

                    if (io.KeyShift && selectionAnchorStableId != 0L)
                    {
                        if (!ObjectSceneListProjection.EnqueueRangeSelection(
                                projection,
                                selectionAnchorStableId,
                                item.StableId,
                                io.KeyCtrl))
                        {
                            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                                EditorUiCommandKind.SelectObject,
                                item.Index,
                                stableId: item.StableId));
                            selectionAnchorStableId = item.StableId;
                        }
                    }
                    else if (io.KeyCtrl)
                    {
                        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                            EditorUiCommandKind.ToggleObjectSelection,
                            item.Index,
                            stableId: item.StableId));
                        selectionAnchorStableId = item.StableId;
                    }
                    else
                    {
                        EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                            EditorUiCommandKind.SelectObject,
                            item.Index,
                            stableId: item.StableId));
                        selectionAnchorStableId = item.StableId;
                    }
                }
            }
        }

        if (projection.MatchCount == 0)
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
            ? $"已放置 {objectCount} 个物件 | 已选 {selectedCount}"
            : $"{objectCount} placed | {selectedCount} selected";
        statusObjectCount = objectCount;
        statusSelectionCount = selectedCount;
        statusChinese = chinese;
        statusValid = true;
        return statusText;
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
