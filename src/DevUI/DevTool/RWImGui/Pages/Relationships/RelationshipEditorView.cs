using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Relationships;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Icon-first ecology relationship workspace.
///
/// The old implementation rendered every relationship as two wide text buttons. That made the
/// developer read type names and decimals line by line before the actual ecology pattern became
/// visible. This view instead treats a pair as one visual object:
/// - left semicircle  = Primary -> Other
/// - right semicircle = Other -> Primary
/// - hue/glyph        = relationship family
/// - arc fill/thickness = intensity
/// Exact enum names and values remain available on hover and in the inspector.
/// </summary>
internal static class RelationshipEditorView
{
    private enum RelationshipViewMode
    {
        Gallery,
        Heatmap
    }

    private enum RelationshipFilterMode
    {
        All,
        Changed,
        Asymmetric,
        Strong
    }

    private enum RelationFamily
    {
        Predation,
        Hostility,
        Fear,
        Unease,
        Neutral,
        Untracked,
        Other
    }

    private sealed class PairPresentation
    {
        internal EditorRelationshipRowSnapshot Row;
        internal EditorRelationshipValueSnapshot Forward;
        internal EditorRelationshipValueSnapshot Reverse;
        internal string Name;
        internal RelationFamily ForwardFamily;
        internal RelationFamily ReverseFamily;
        internal bool Asymmetric;
        internal bool Changed;
        internal float PeakIntensity;
    }

    private static readonly EditorRelationshipValueSnapshot EmptyRelationship = new();
    private static readonly RelationFamily[] FamilyOrder =
    {
        RelationFamily.Predation,
        RelationFamily.Hostility,
        RelationFamily.Fear,
        RelationFamily.Unease,
        RelationFamily.Neutral,
        RelationFamily.Untracked,
        RelationFamily.Other
    };

    private static string primarySearch = string.Empty;
    private static string matrixSearch = string.Empty;
    private static string observedPrimarySearch;
    private static string normalizedPrimarySearch = string.Empty;
    private static string observedMatrixSearch;
    private static string normalizedMatrixSearch = string.Empty;

    private static RelationshipViewMode viewMode = RelationshipViewMode.Gallery;
    private static RelationshipFilterMode filterMode = RelationshipFilterMode.All;

    private static string[] projectedCreatureTypes;
    private static DevToolExplorerListItem[] projectedCreatureRows = Array.Empty<DevToolExplorerListItem>();
    private static EditorRelationshipRowSnapshot[] projectedRowsSource;
    private static PairPresentation[] projectedRows = Array.Empty<PairPresentation>();
    private static string projectedFilter = string.Empty;
    private static RelationshipFilterMode projectedFilterMode;
    private static PairPresentation[] projectedVisibleRows = Array.Empty<PairPresentation>();

    private static string inspectorEditKey = string.Empty;
    private static string inspectorEditPrimary = string.Empty;
    private static string inspectorEditOther = string.Empty;
    private static EditorRelationshipDirection inspectorEditDirection;

    internal static void ResetRetainedState()
    {
        projectedCreatureTypes = null;
        projectedCreatureRows = Array.Empty<DevToolExplorerListItem>();
        projectedRowsSource = null;
        projectedRows = Array.Empty<PairPresentation>();
        projectedFilter = string.Empty;
        projectedFilterMode = RelationshipFilterMode.All;
        projectedVisibleRows = Array.Empty<PairPresentation>();

        inspectorEditKey = string.Empty;
        inspectorEditPrimary = string.Empty;
        inspectorEditOther = string.Empty;
        inspectorEditDirection = default;

        primarySearch = string.Empty;
        matrixSearch = string.Empty;
        observedPrimarySearch = null;
        normalizedPrimarySearch = string.Empty;
        observedMatrixSearch = null;
        normalizedMatrixSearch = string.Empty;

        viewMode = RelationshipViewMode.Gallery;
        filterMode = RelationshipFilterMode.All;
    }

    internal static void DrawBrowser(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("关系编辑器不可用。", "Relationship editor unavailable."),
                true);
            return;
        }

        ImGui.TextDisabled(DevToolUiSettings.T("主生物", "PRIMARY CREATURE"));
        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T("搜索", "Search"),
            "RelationshipPrimarySearch",
            ref primarySearch,
            128);
        ImGui.Separator();

        string[] creatures = snapshot.CreatureTypes ?? Array.Empty<string>();
        EnsureCreatureRows(creatures);
        string primaryQuery = PrimarySearchQuery();

        int matches = 0;
        for (int i = 0; i < creatures.Length; i++)
        {
            string type = creatures[i];
            if (!Matches(type, primaryQuery))
                continue;

            matches++;
            bool selected =
                string.Equals(
                    type,
                    snapshot.PrimaryCreature,
                    StringComparison.Ordinal);

            if (DevToolExplorerRowRenderer.DrawSelectable(
                    projectedCreatureRows[i],
                    selected,
                    defaultFocus: true))
            {
                RelationshipEditorCommandQueue.Enqueue(
                    new RelationshipEditorCommand(
                        RelationshipEditorCommandKind.SelectPrimary,
                        primary: type));
            }
        }

        if (matches == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("没有匹配的生物。", "No matching creatures."),
                true);
        }
    }

    internal static void DrawMatrix(
        EditorRelationshipPresentationSnapshot snapshot,
        Num.Vector2 position,
        Num.Vector2 size)
    {
        if (!snapshot.Available ||
            size.X < 180f ||
            size.Y < 120f)
            return;

        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(560f, 340f),
            new Num.Vector2(4000f, 4000f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T(
                    "生态关系###DevToolRelationshipMatrix",
                    "Ecology Relationships###DevToolRelationshipMatrix"),
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Relationships");

        EditorRelationshipRowSnapshot[] rows =
            snapshot.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        EnsureRows(rows);
        EnsureVisibleRows(MatrixSearchQuery(), filterMode);

        DrawWorkspaceHeader(snapshot);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (projectedVisibleRows.Length == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "没有关系符合当前过滤条件。",
                    "No relationships match the current filter."),
                true);
            ImGui.End();
            return;
        }

        if (viewMode == RelationshipViewMode.Gallery)
            DrawGallery(snapshot);
        else
            DrawHeatmap(snapshot);

        ImGui.End();
    }

    internal static void DrawInspector(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("关系编辑器不可用。", "Relationship editor unavailable."),
                true);
            return;
        }

        EditorRelationshipRowSnapshot row =
            FindRow(
                snapshot,
                snapshot.SelectedOtherCreature);

        if (row == null)
        {
            ImGui.TextWrapped(
                DevToolUiSettings.T(
                    "从生态关系视图中选择一个生物。",
                    "Select a creature from the ecology relationship view."));
            return;
        }

        ImGui.TextUnformatted(
            string.IsNullOrEmpty(row.DisplayName)
                ? row.CreatureType
                : row.DisplayName);

        ImGui.SameLine();
        ImGui.TextDisabled(" / ");
        ImGui.SameLine();
        ImGui.TextDisabled(snapshot.PrimaryCreature);
        ImGui.Separator();

        DrawDirectionEditor(
            snapshot,
            row,
            EditorRelationshipDirection.PrimaryToOther);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDirectionEditor(
            snapshot,
            row,
            EditorRelationshipDirection.OtherToPrimary);
    }

    private static void DrawWorkspaceHeader(EditorRelationshipPresentationSnapshot snapshot)
    {
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextDisabled(DevToolUiSettings.T("主体", "PRIMARY"));
        ImGui.SameLine();
        ImGui.TextUnformatted(
            string.IsNullOrEmpty(snapshot.PrimaryCreature)
                ? "-"
                : snapshot.PrimaryCreature);
        ImGui.SetWindowFontScale(1f);

        ImGui.SameLine();
        ImGui.Dummy(new Num.Vector2(10f, 0f));
        ImGui.SameLine();

        bool gallery = viewMode == RelationshipViewMode.Gallery;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("画廊", "Gallery"),
                "RelationshipGalleryMode",
                gallery ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fixedWidth: 72f))
        {
            viewMode = RelationshipViewMode.Gallery;
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("热图", "Heatmap"),
                "RelationshipHeatmapMode",
                !gallery ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fixedWidth: 72f))
        {
            viewMode = RelationshipViewMode.Heatmap;
        }

        ImGui.Spacing();

        float searchWidth =
            Math.Max(
                180f,
                Math.Min(
                    360f,
                    ImGui.GetContentRegionAvail().X * 0.42f));
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputText(
            DevToolUiSettings.T(
                "搜索目标##RelationshipMatrixSearch",
                "Search targets##RelationshipMatrixSearch"),
            ref matrixSearch,
            128);

        if (DevToolWidgets.SameLineIfFits(285f))
            DrawFilterButtons();
        else
        {
            ImGui.Spacing();
            DrawFilterButtons();
        }

        ImGui.Spacing();
        DrawLegend();
    }

    private static void DrawFilterButtons()
    {
        DrawFilterButton(
            RelationshipFilterMode.All,
            DevToolUiSettings.T("全部", "All"),
            DevToolUiSettings.T("显示全部关系", "Show all relationships"));
        ImGui.SameLine();

        DrawFilterButton(
            RelationshipFilterMode.Changed,
            DevToolUiSettings.T("修改", "Edited"),
            DevToolUiSettings.T("只显示直接覆盖过的关系", "Show direct overrides only"));
        ImGui.SameLine();

        DrawFilterButton(
            RelationshipFilterMode.Asymmetric,
            DevToolUiSettings.T("差异", "Split"),
            DevToolUiSettings.T("只显示双向类型或强度明显不一致的关系", "Show asymmetric pairs only"));
        ImGui.SameLine();

        DrawFilterButton(
            RelationshipFilterMode.Strong,
            DevToolUiSettings.T("强", "Strong"),
            DevToolUiSettings.T("只显示任一方向强度达到 0.75 的关系", "Show pairs with intensity at least 0.75"));
    }

    private static void DrawFilterButton(
        RelationshipFilterMode mode,
        string label,
        string tooltip)
    {
        bool active =
            filterMode == mode;

        if (DevToolWidgets.ActionButton(
                label,
                "RelationshipFilter:" + mode,
                active ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fixedWidth: 62f))
        {
            filterMode = mode;
        }

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(tooltip);
    }

    private static void DrawLegend()
    {
        float start =
            ImGui.GetCursorPosX();

        for (int i = 0; i < FamilyOrder.Length; i++)
        {
            RelationFamily family =
                FamilyOrder[i];
            string label =
                GetFamilyLabel(family);

            DrawLegendChip(
                family,
                label);

            if (i + 1 < FamilyOrder.Length &&
                DevToolWidgets.SameLineIfFits(
                    ImGui.CalcTextSize(label).X + 38f))
            {
                ImGui.SameLine();
            }
            else if (i + 1 < FamilyOrder.Length)
            {
                ImGui.SetCursorPosX(start);
            }
        }
    }

    private static void DrawLegendChip(
        RelationFamily family,
        string label)
    {
        Num.Vector4 color =
            GetFamilyColor(family);

        ImGui.PushStyleColor(
            ImGuiCol.Button,
            new Num.Vector4(
                color.X * 0.32f,
                color.Y * 0.32f,
                color.Z * 0.32f,
                0.82f));
        ImGui.PushStyleColor(
            ImGuiCol.ButtonHovered,
            new Num.Vector4(
                color.X * 0.52f,
                color.Y * 0.52f,
                color.Z * 0.52f,
                0.94f));
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            new Num.Vector4(
                color.X,
                color.Y,
                color.Z,
                0.72f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);

        ImGui.Button(
            label + "##RelationshipLegend" + family,
            new Num.Vector2(
                Math.Max(
                    54f,
                    ImGui.CalcTextSize(label).X + 24f),
                0f));

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
    }

    private static void DrawGallery(EditorRelationshipPresentationSnapshot snapshot)
    {
        float available =
            Math.Max(
                150f,
                ImGui.GetContentRegionAvail().X);
        const float targetCardWidth = 176f;
        const float cardGap = 8f;
        const float cardHeight = 122f;

        int columns =
            Math.Max(
                1,
                (int)Math.Floor(
                    (available + cardGap) /
                    (targetCardWidth + cardGap)));

        float cardWidth =
            Math.Max(
                148f,
                (available - cardGap * (columns - 1)) /
                columns);

        for (int familyIndex = 0; familyIndex < FamilyOrder.Length; familyIndex++)
        {
            RelationFamily family =
                FamilyOrder[familyIndex];

            int count =
                CountFamily(family);
            if (count == 0)
                continue;

            DrawFamilyHeader(
                family,
                count);

            int column = 0;
            for (int i = 0; i < projectedVisibleRows.Length; i++)
            {
                PairPresentation pair =
                    projectedVisibleRows[i];
                if (pair.ForwardFamily != family)
                    continue;

                if (column > 0)
                    ImGui.SameLine(0f, cardGap);

                DrawGalleryCard(
                    snapshot,
                    pair,
                    cardWidth,
                    cardHeight);

                column++;
                if (column >= columns)
                    column = 0;
            }

            ImGui.Spacing();
        }
    }

    private static void DrawFamilyHeader(
        RelationFamily family,
        int count)
    {
        Num.Vector4 color =
            GetFamilyColor(family);

        Num.Vector2 cursor =
            ImGui.GetCursorScreenPos();

        ImGui.GetWindowDrawList().AddRectFilled(
            cursor,
            cursor + new Num.Vector2(4f, ImGui.GetTextLineHeight() + 5f),
            ImGui.GetColorU32(color),
            2f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + 10f);
        ImGui.TextUnformatted(
            GetFamilyLabel(family));
        ImGui.SameLine();
        ImGui.TextDisabled(count.ToString());
        ImGui.Spacing();
    }

    private static int CountFamily(RelationFamily family)
    {
        int count = 0;
        for (int i = 0; i < projectedVisibleRows.Length; i++)
        {
            if (projectedVisibleRows[i].ForwardFamily == family)
                count++;
        }
        return count;
    }

    private static void DrawGalleryCard(
        EditorRelationshipPresentationSnapshot snapshot,
        PairPresentation pair,
        float width,
        float height)
    {
        string id =
            "##RelationshipCard:" +
            (pair.Row?.CreatureType ?? string.Empty);

        ImGui.PushID(id);
        bool clicked =
            ImGui.InvisibleButton(
                "##card",
                new Num.Vector2(width, height));
        bool hovered =
            ImGui.IsItemHovered();
        ImGui.PopID();

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();

        bool selected =
            pair.Row?.Selected == true;

        Num.Vector4 baseColor =
            hovered
                ? new Num.Vector4(0.085f, 0.115f, 0.16f, 0.94f)
                : new Num.Vector4(0.055f, 0.07f, 0.095f, 0.90f);

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(baseColor),
            7f);

        Num.Vector4 borderColor =
            selected
                ? new Num.Vector4(0.36f, 0.72f, 1f, 0.98f)
                : hovered
                    ? new Num.Vector4(0.36f, 0.46f, 0.60f, 0.84f)
                    : new Num.Vector4(0.22f, 0.27f, 0.34f, 0.68f);

        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(borderColor),
            7f,
            ImDrawFlags.None,
            selected ? 1.8f : 1f);

        Num.Vector4 forwardColor =
            GetFamilyColor(pair.ForwardFamily);
        Num.Vector4 reverseColor =
            GetFamilyColor(pair.ReverseFamily);

        draw.AddRectFilled(
            min + new Num.Vector2(4f, 4f),
            new Num.Vector2(
                (min.X + max.X) * 0.5f - 1f,
                min.Y + 7f),
            ImGui.GetColorU32(forwardColor),
            2f);

        draw.AddRectFilled(
            new Num.Vector2(
                (min.X + max.X) * 0.5f + 1f,
                min.Y + 4f),
            new Num.Vector2(
                max.X - 4f,
                min.Y + 7f),
            ImGui.GetColorU32(reverseColor),
            2f);

        string fittedName =
            FitText(
                pair.Name,
                Math.Max(40f, width - 18f));
        draw.AddText(
            min + new Num.Vector2(9f, 13f),
            ImGui.GetColorU32(
                new Num.Vector4(0.93f, 0.95f, 0.98f, 1f)),
            fittedName);

        Num.Vector2 center =
            new(
                (min.X + max.X) * 0.5f,
                min.Y + 67f);
        float radius =
            Math.Max(
                25f,
                Math.Min(
                    34f,
                    width * 0.20f));

        DrawArc(
            draw,
            center,
            radius,
            (float)(Math.PI * 0.5),
            (float)(Math.PI * 1.5),
            1f,
            new Num.Vector4(0.18f, 0.22f, 0.28f, 0.70f),
            3f);
        DrawArc(
            draw,
            center,
            radius,
            (float)(-Math.PI * 0.5),
            (float)(Math.PI * 0.5),
            1f,
            new Num.Vector4(0.18f, 0.22f, 0.28f, 0.70f),
            3f);

        DrawArc(
            draw,
            center,
            radius,
            (float)(Math.PI * 0.5),
            (float)(Math.PI * 1.5),
            Clamp01(pair.Forward.Intensity),
            forwardColor,
            3.4f + Clamp01(pair.Forward.Intensity) * 2f);

        DrawArc(
            draw,
            center,
            radius,
            (float)(-Math.PI * 0.5),
            (float)(Math.PI * 0.5),
            Clamp01(pair.Reverse.Intensity),
            reverseColor,
            3.4f + Clamp01(pair.Reverse.Intensity) * 2f);

        DrawCreatureSigil(
            draw,
            center,
            pair.Row?.CreatureType ?? string.Empty,
            selected);

        DrawRelationGlyph(
            draw,
            center + new Num.Vector2(-radius - 17f, 13f),
            pair.ForwardFamily,
            forwardColor,
            11f);
        DrawRelationGlyph(
            draw,
            center + new Num.Vector2(radius + 17f, 13f),
            pair.ReverseFamily,
            reverseColor,
            11f);

        DrawIntensityTick(
            draw,
            new Num.Vector2(min.X + 12f, max.Y - 13f),
            Math.Max(24f, width * 0.28f),
            pair.Forward.Intensity,
            forwardColor);
        DrawIntensityTick(
            draw,
            new Num.Vector2(max.X - 12f, max.Y - 13f),
            -Math.Max(24f, width * 0.28f),
            pair.Reverse.Intensity,
            reverseColor);

        if (pair.Asymmetric)
        {
            DrawSplitMarker(
                draw,
                new Num.Vector2(
                    center.X,
                    max.Y - 12f),
                forwardColor,
                reverseColor);
        }

        if (pair.Changed)
        {
            draw.AddRectFilled(
                max - new Num.Vector2(11f, 11f),
                max - new Num.Vector2(5f, 5f),
                ImGui.GetColorU32(
                    new Num.Vector4(0.35f, 0.82f, 1f, 0.96f)),
                1f);
        }

        if (selected)
        {
            bool forwardSelected =
                snapshot.SelectedDirection ==
                EditorRelationshipDirection.PrimaryToOther;
            if (forwardSelected)
            {
                draw.AddRectFilled(
                    min + new Num.Vector2(1f, 10f),
                    new Num.Vector2(min.X + 4f, max.Y - 10f),
                    ImGui.GetColorU32(forwardColor),
                    1f);
            }
            else
            {
                draw.AddRectFilled(
                    new Num.Vector2(max.X - 4f, min.Y + 10f),
                    max - new Num.Vector2(1f, 10f),
                    ImGui.GetColorU32(reverseColor),
                    1f);
            }
        }

        if (clicked &&
            pair.Row != null)
        {
            Num.Vector2 mouse =
                ImGui.GetIO().MousePos;
            EditorRelationshipDirection direction =
                mouse.X < center.X
                    ? EditorRelationshipDirection.PrimaryToOther
                    : EditorRelationshipDirection.OtherToPrimary;

            RelationshipEditorCommandQueue.Enqueue(
                new RelationshipEditorCommand(
                    RelationshipEditorCommandKind.SelectPair,
                    other: pair.Row.CreatureType,
                    direction: direction));
        }

        if (hovered)
            DevToolTooltip.Show(BuildPairTooltip(snapshot, pair));
    }

    private static void DrawHeatmap(EditorRelationshipPresentationSnapshot snapshot)
    {
        float available =
            ImGui.GetContentRegionAvail().X;
        float nameWidth =
            Math.Max(
                150f,
                Math.Min(
                    290f,
                    available * 0.42f));
        float cellGap = 8f;
        float cellWidth =
            Math.Max(
                74f,
                Math.Min(
                    128f,
                    (available - nameWidth - cellGap * 2f) * 0.5f));

        ImGui.TextDisabled(DevToolUiSettings.T("目标", "TARGET"));
        ImGui.SameLine(nameWidth);
        DrawDirectionHeaderGlyph(true, cellWidth);
        ImGui.SameLine(nameWidth + cellWidth + cellGap);
        DrawDirectionHeaderGlyph(false, cellWidth);
        ImGui.Separator();

        using DevToolListClipper clipper =
            new(projectedVisibleRows.Length);
        while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
        {
            for (int i = firstVisible; i < lastVisibleExclusive; i++)
            {
                PairPresentation pair =
                    projectedVisibleRows[i];
                float startX =
                    ImGui.GetCursorPosX();

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(
                    FitText(
                        pair.Name,
                        nameWidth - 12f));

                ImGui.SameLine(startX + nameWidth);
                DrawHeatCell(
                    snapshot,
                    pair,
                    EditorRelationshipDirection.PrimaryToOther,
                    cellWidth);

                ImGui.SameLine(startX + nameWidth + cellWidth + cellGap);
                DrawHeatCell(
                    snapshot,
                    pair,
                    EditorRelationshipDirection.OtherToPrimary,
                    cellWidth);
            }
        }
    }

    private static void DrawDirectionHeaderGlyph(
        bool forward,
        float width)
    {
        ImGui.InvisibleButton(
            forward
                ? "##RelationshipHeaderForward"
                : "##RelationshipHeaderReverse",
            new Num.Vector2(width, ImGui.GetFrameHeight()));

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();

        float y =
            (min.Y + max.Y) * 0.5f;
        float x0 =
            min.X + 16f;
        float x1 =
            max.X - 16f;

        if (!forward)
        {
            float t = x0;
            x0 = x1;
            x1 = t;
        }

        uint color =
            ImGui.GetColorU32(
                new Num.Vector4(0.68f, 0.76f, 0.88f, 0.92f));
        draw.AddLine(
            new Num.Vector2(x0, y),
            new Num.Vector2(x1, y),
            color,
            1.6f);

        float sign =
            x1 >= x0 ? 1f : -1f;
        draw.AddLine(
            new Num.Vector2(x1, y),
            new Num.Vector2(x1 - sign * 5f, y - 4f),
            color,
            1.6f);
        draw.AddLine(
            new Num.Vector2(x1, y),
            new Num.Vector2(x1 - sign * 5f, y + 4f),
            color,
            1.6f);
    }

    private static void DrawHeatCell(
        EditorRelationshipPresentationSnapshot snapshot,
        PairPresentation pair,
        EditorRelationshipDirection direction,
        float width)
    {
        bool forward =
            direction ==
            EditorRelationshipDirection.PrimaryToOther;
        EditorRelationshipValueSnapshot value =
            forward
                ? pair.Forward
                : pair.Reverse;
        RelationFamily family =
            forward
                ? pair.ForwardFamily
                : pair.ReverseFamily;
        Num.Vector4 color =
            GetFamilyColor(family);

        ImGui.PushID(
            (pair.Row?.CreatureType ?? string.Empty) +
            ":" +
            direction);
        bool clicked =
            ImGui.InvisibleButton(
                "##heat",
                new Num.Vector2(
                    width,
                    Math.Max(
                        28f,
                        ImGui.GetFrameHeight())));
        bool hovered =
            ImGui.IsItemHovered();
        ImGui.PopID();

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();

        float intensity =
            Clamp01(value.Intensity);
        Num.Vector4 fill =
            new(
                color.X * (0.30f + intensity * 0.38f),
                color.Y * (0.30f + intensity * 0.38f),
                color.Z * (0.30f + intensity * 0.38f),
                hovered ? 0.98f : 0.88f);

        bool selected =
            pair.Row?.Selected == true &&
            snapshot.SelectedDirection == direction;

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(fill),
            4f);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                selected
                    ? new Num.Vector4(0.55f, 0.84f, 1f, 1f)
                    : new Num.Vector4(color.X, color.Y, color.Z, 0.74f)),
            4f,
            ImDrawFlags.None,
            selected ? 2f : 1f);

        DrawRelationGlyph(
            draw,
            new Num.Vector2(
                min.X + 17f,
                (min.Y + max.Y) * 0.5f),
            family,
            new Num.Vector4(0.96f, 0.98f, 1f, 1f),
            10f);

        float barLeft =
            min.X + 34f;
        float barRight =
            max.X - 10f;
        float y =
            max.Y - 7f;

        draw.AddLine(
            new Num.Vector2(barLeft, y),
            new Num.Vector2(barRight, y),
            ImGui.GetColorU32(
                new Num.Vector4(0.10f, 0.12f, 0.15f, 0.92f)),
            3f);
        draw.AddLine(
            new Num.Vector2(barLeft, y),
            new Num.Vector2(
                barLeft +
                (barRight - barLeft) * intensity,
                y),
            ImGui.GetColorU32(
                new Num.Vector4(0.95f, 0.97f, 1f, 0.96f)),
            3f);

        if (value.DirectOverride)
        {
            draw.AddRectFilled(
                max - new Num.Vector2(8f, 8f),
                max - new Num.Vector2(3f, 3f),
                ImGui.GetColorU32(
                    new Num.Vector4(0.35f, 0.82f, 1f, 0.98f)));
        }

        if (clicked &&
            pair.Row != null)
        {
            RelationshipEditorCommandQueue.Enqueue(
                new RelationshipEditorCommand(
                    RelationshipEditorCommandKind.SelectPair,
                    other: pair.Row.CreatureType,
                    direction: direction));
        }

        if (hovered)
        {
            DevToolTooltip.Show(
                BuildDirectionTooltip(
                    snapshot,
                    pair,
                    direction));
        }
    }

    private static void DrawDirectionEditor(
        EditorRelationshipPresentationSnapshot snapshot,
        EditorRelationshipRowSnapshot row,
        EditorRelationshipDirection direction)
    {
        bool forward =
            direction ==
            EditorRelationshipDirection.PrimaryToOther;
        string from =
            forward
                ? snapshot.PrimaryCreature
                : row.CreatureType;
        string to =
            forward
                ? row.CreatureType
                : snapshot.PrimaryCreature;

        EditorRelationshipValueSnapshot relationship =
            (forward
                ? row.PrimaryToOther
                : row.OtherToPrimary) ??
            EmptyRelationship;

        RelationFamily family =
            Classify(relationship.Type);
        Num.Vector4 color =
            GetFamilyColor(family);

        bool selected =
            row.Selected &&
            snapshot.SelectedDirection == direction;

        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            new Num.Vector4(
                color.X * 0.08f,
                color.Y * 0.08f,
                color.Z * 0.08f,
                0.62f));
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            selected
                ? new Num.Vector4(color.X, color.Y, color.Z, 0.92f)
                : new Num.Vector4(color.X, color.Y, color.Z, 0.36f));

        string childId =
            forward
                ? "##RelationshipForwardInspector"
                : "##RelationshipReverseInspector";
        if (ImGui.BeginChild(
                childId,
                new Num.Vector2(
                    0f,
                    172f),
                ImGuiChildFlags.Borders))
        {
            float headerY =
                ImGui.GetCursorScreenPos().Y +
                ImGui.GetTextLineHeight() * 0.5f;
            DrawRelationGlyph(
                ImGui.GetWindowDrawList(),
                new Num.Vector2(
                    ImGui.GetCursorScreenPos().X + 10f,
                    headerY),
                family,
                color,
                10f);

            ImGui.SetCursorPosX(
                ImGui.GetCursorPosX() + 25f);
            ImGui.TextUnformatted(from);
            ImGui.SameLine();
            ImGui.TextDisabled(">");
            ImGui.SameLine();
            ImGui.TextUnformatted(to);

            ImGui.SameLine();
            ImGui.TextDisabled(
                relationship.DirectOverride
                    ? DevToolUiSettings.T("覆盖", "override")
                    : DevToolUiSettings.T("继承", "inherited"));

            if (!selected)
            {
                ImGui.SameLine();
                if (DevToolWidgets.ActionButton(
                        DevToolUiSettings.T("编辑", "Edit"),
                        forward
                            ? "RelationshipEditForward"
                            : "RelationshipEditReverse",
                        DevToolButtonTone.Subtle,
                        fixedWidth: 52f))
                {
                    RelationshipEditorCommandQueue.Enqueue(
                        new RelationshipEditorCommand(
                            RelationshipEditorCommandKind.SelectPair,
                            other: row.CreatureType,
                            direction: direction));
                }
            }

            string[] types =
                snapshot.RelationshipTypes ?? Array.Empty<string>();
            string currentType =
                relationship.Type ?? string.Empty;

            if (ImGui.BeginCombo(
                    (forward
                        ? "##RelationshipTypeForward"
                        : "##RelationshipTypeReverse"),
                    currentType))
            {
                for (int i = 0; i < types.Length; i++)
                {
                    string type =
                        types[i];
                    bool typeSelected =
                        string.Equals(
                            type,
                            currentType,
                            StringComparison.Ordinal);

                    if (ImGui.Selectable(
                            type +
                            "##RelationshipType" +
                            direction +
                            i,
                            typeSelected))
                    {
                        SendType(
                            snapshot,
                            row.CreatureType,
                            type,
                            direction);
                    }

                    if (typeSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            string editKey =
                GetInspectorEditKey(
                    snapshot.PrimaryCreature,
                    row.CreatureType,
                    direction);

            DevToolNumericEditResult<float> intensityEdit =
                DevToolNumericWidgets.SliderFloat(
                    DevToolNumericScope.Relationship,
                    editKey,
                    forward
                        ? DevToolUiSettings.T(
                            "强度##RelationshipIntensityForward",
                            "Intensity##RelationshipIntensityForward")
                        : DevToolUiSettings.T(
                            "强度##RelationshipIntensityReverse",
                            "Intensity##RelationshipIntensityReverse"),
                    relationship.Intensity,
                    0f,
                    1f);

            if (intensityEdit.Committed)
            {
                RelationshipEditorCommandQueue.Enqueue(
                    new RelationshipEditorCommand(
                        RelationshipEditorCommandKind.SetRelationshipIntensity,
                        primary: snapshot.PrimaryCreature,
                        other: row.CreatureType,
                        value: intensityEdit.Value,
                        direction: direction));
            }

            bool canReset =
                relationship.DirectOverride;
            if (!canReset)
                ImGui.BeginDisabled();

            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("重置覆盖", "Reset"),
                    forward
                        ? "ResetRelationshipOverrideForward"
                        : "ResetRelationshipOverrideReverse",
                    DevToolButtonTone.Subtle))
            {
                RelationshipEditorCommandQueue.Enqueue(
                    new RelationshipEditorCommand(
                        RelationshipEditorCommandKind.ResetRelationship,
                        primary: snapshot.PrimaryCreature,
                        other: row.CreatureType,
                        direction: direction));

                DevToolNumericWidgets.Discard(
                    DevToolNumericScope.Relationship,
                    editKey);
            }

            if (!canReset)
                ImGui.EndDisabled();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static void SendType(
        EditorRelationshipPresentationSnapshot snapshot,
        string other,
        string type,
        EditorRelationshipDirection direction)
    {
        RelationshipEditorCommandQueue.Enqueue(
            new RelationshipEditorCommand(
                RelationshipEditorCommandKind.SetRelationshipType,
                primary: snapshot.PrimaryCreature,
                other: other,
                text: type,
                direction: direction));
    }

    private static EditorRelationshipRowSnapshot FindRow(
        EditorRelationshipPresentationSnapshot snapshot,
        string type)
    {
        if (string.IsNullOrEmpty(type))
            return null;

        EditorRelationshipRowSnapshot[] rows =
            snapshot.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();

        for (int i = 0; i < rows.Length; i++)
        {
            if (string.Equals(
                    rows[i].CreatureType,
                    type,
                    StringComparison.Ordinal))
                return rows[i];
        }

        return null;
    }

    private static void EnsureCreatureRows(string[] creatures)
    {
        if (ReferenceEquals(
                projectedCreatureTypes,
                creatures))
            return;

        DevToolExplorerListItem[] rows =
            new DevToolExplorerListItem[creatures.Length];

        for (int i = 0; i < creatures.Length; i++)
        {
            string type =
                creatures[i] ?? string.Empty;
            rows[i] =
                new DevToolExplorerListItem(
                    "RelationshipPrimary:" + type,
                    type);
        }

        projectedCreatureTypes =
            creatures;
        projectedCreatureRows =
            rows;
    }

    private static void EnsureRows(
        EditorRelationshipRowSnapshot[] rows)
    {
        if (ReferenceEquals(
                projectedRowsSource,
                rows))
            return;

        PairPresentation[] next =
            new PairPresentation[rows.Length];

        for (int i = 0; i < rows.Length; i++)
        {
            EditorRelationshipRowSnapshot row =
                rows[i];
            EditorRelationshipValueSnapshot forward =
                row?.PrimaryToOther ??
                EmptyRelationship;
            EditorRelationshipValueSnapshot reverse =
                row?.OtherToPrimary ??
                EmptyRelationship;

            RelationFamily forwardFamily =
                Classify(forward.Type);
            RelationFamily reverseFamily =
                Classify(reverse.Type);

            next[i] =
                new PairPresentation
                {
                    Row = row,
                    Forward = forward,
                    Reverse = reverse,
                    Name =
                        string.IsNullOrEmpty(row?.DisplayName)
                            ? row?.CreatureType ?? string.Empty
                            : row.DisplayName,
                    ForwardFamily = forwardFamily,
                    ReverseFamily = reverseFamily,
                    Asymmetric =
                        IsAsymmetric(
                            forward,
                            reverse,
                            forwardFamily,
                            reverseFamily),
                    Changed =
                        forward.DirectOverride ||
                        reverse.DirectOverride,
                    PeakIntensity =
                        Math.Max(
                            Clamp01(forward.Intensity),
                            Clamp01(reverse.Intensity))
                };
        }

        projectedRowsSource =
            rows;
        projectedRows =
            next;
        projectedFilter =
            null;
        projectedVisibleRows =
            Array.Empty<PairPresentation>();
    }

    private static void EnsureVisibleRows(
        string query,
        RelationshipFilterMode mode)
    {
        query ??= string.Empty;

        if (string.Equals(
                projectedFilter,
                query,
                StringComparison.Ordinal) &&
            projectedFilterMode == mode)
            return;

        int count = 0;
        for (int i = 0; i < projectedRows.Length; i++)
        {
            if (PassesFilter(
                    projectedRows[i],
                    query,
                    mode))
                count++;
        }

        PairPresentation[] next =
            new PairPresentation[count];
        int write = 0;

        for (int i = 0; i < projectedRows.Length; i++)
        {
            PairPresentation pair =
                projectedRows[i];
            if (!PassesFilter(
                    pair,
                    query,
                    mode))
                continue;

            next[write++] =
                pair;
        }

        projectedFilter =
            query;
        projectedFilterMode =
            mode;
        projectedVisibleRows =
            next;
    }

    private static bool PassesFilter(
        PairPresentation pair,
        string query,
        RelationshipFilterMode mode)
    {
        if (pair?.Row == null)
            return false;

        if (!Matches(
                pair.Row.CreatureType,
                query) &&
            !Matches(
                pair.Row.DisplayName,
                query))
            return false;

        return mode switch
        {
            RelationshipFilterMode.Changed =>
                pair.Changed,
            RelationshipFilterMode.Asymmetric =>
                pair.Asymmetric,
            RelationshipFilterMode.Strong =>
                pair.PeakIntensity >= 0.75f,
            _ => true
        };
    }

    private static bool IsAsymmetric(
        EditorRelationshipValueSnapshot forward,
        EditorRelationshipValueSnapshot reverse,
        RelationFamily forwardFamily,
        RelationFamily reverseFamily)
    {
        if (forwardFamily != reverseFamily)
            return true;

        if (!string.Equals(
                forward.Type ?? string.Empty,
                reverse.Type ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            return true;

        return Math.Abs(
                   Clamp01(forward.Intensity) -
                   Clamp01(reverse.Intensity)) >=
               0.26f;
    }

    private static RelationFamily Classify(string type)
    {
        string token =
            NormalizeType(type);

        if (token.Contains("eat") ||
            token.Contains("prey") ||
            token.Contains("predat") ||
            token.Contains("consume"))
            return RelationFamily.Predation;

        if (token.Contains("antagon") ||
            token.Contains("hostil") ||
            token.Contains("attack") ||
            token.Contains("aggress") ||
            token.Contains("rival"))
            return RelationFamily.Hostility;

        if (token.Contains("afraid") ||
            token.Contains("fear") ||
            token.Contains("flee"))
            return RelationFamily.Fear;

        if (token.Contains("uncomfort") ||
            token.Contains("avoid") ||
            token.Contains("stayaway"))
            return RelationFamily.Unease;

        if (token.Contains("doesnttrack") ||
            token.Contains("donottrack") ||
            token.Contains("notrack"))
            return RelationFamily.Untracked;

        if (token.Contains("ignore") ||
            token.Contains("neutral"))
            return RelationFamily.Neutral;

        return RelationFamily.Other;
    }

    private static string NormalizeType(string type)
    {
        if (string.IsNullOrEmpty(type))
            return string.Empty;

        return type
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static Num.Vector4 GetFamilyColor(
        RelationFamily family)
    {
        return family switch
        {
            RelationFamily.Predation =>
                new Num.Vector4(0.96f, 0.38f, 0.16f, 1f),
            RelationFamily.Hostility =>
                new Num.Vector4(0.90f, 0.22f, 0.24f, 1f),
            RelationFamily.Fear =>
                new Num.Vector4(0.62f, 0.40f, 0.96f, 1f),
            RelationFamily.Unease =>
                new Num.Vector4(0.93f, 0.73f, 0.25f, 1f),
            RelationFamily.Neutral =>
                new Num.Vector4(0.40f, 0.56f, 0.70f, 1f),
            RelationFamily.Untracked =>
                new Num.Vector4(0.39f, 0.42f, 0.48f, 1f),
            _ =>
                new Num.Vector4(0.30f, 0.70f, 0.82f, 1f)
        };
    }

    private static string GetFamilyLabel(
        RelationFamily family)
    {
        return family switch
        {
            RelationFamily.Predation =>
                DevToolUiSettings.T("捕食", "Predation"),
            RelationFamily.Hostility =>
                DevToolUiSettings.T("敌对", "Hostile"),
            RelationFamily.Fear =>
                DevToolUiSettings.T("恐惧", "Fear"),
            RelationFamily.Unease =>
                DevToolUiSettings.T("不适", "Unease"),
            RelationFamily.Neutral =>
                DevToolUiSettings.T("忽略", "Ignore"),
            RelationFamily.Untracked =>
                DevToolUiSettings.T("不追踪", "Untracked"),
            _ =>
                DevToolUiSettings.T("其他", "Other")
        };
    }

    private static void DrawArc(
        ImDrawListPtr draw,
        Num.Vector2 center,
        float radius,
        float startAngle,
        float endAngle,
        float progress,
        Num.Vector4 color,
        float thickness)
    {
        progress =
            Clamp01(progress);

        const int segments = 18;
        int activeSegments =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    segments * progress));

        float span =
            (endAngle - startAngle) *
            progress;

        Num.Vector2 previous =
            ArcPoint(
                center,
                radius,
                startAngle);

        for (int i = 1; i <= activeSegments; i++)
        {
            float t =
                (float)i /
                activeSegments;
            float angle =
                startAngle +
                span * t;
            Num.Vector2 next =
                ArcPoint(
                    center,
                    radius,
                    angle);

            draw.AddLine(
                previous,
                next,
                ImGui.GetColorU32(color),
                thickness);

            previous =
                next;
        }
    }

    private static Num.Vector2 ArcPoint(
        Num.Vector2 center,
        float radius,
        float angle)
    {
        return new Num.Vector2(
            center.X +
            (float)Math.Cos(angle) * radius,
            center.Y +
            (float)Math.Sin(angle) * radius);
    }

    private static void DrawCreatureSigil(
        ImDrawListPtr draw,
        Num.Vector2 center,
        string identity,
        bool selected)
    {
        float half = 13f;
        Num.Vector2 min =
            center -
            new Num.Vector2(half, half);
        Num.Vector2 max =
            center +
            new Num.Vector2(half, half);

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                selected
                    ? new Num.Vector4(0.14f, 0.27f, 0.40f, 0.98f)
                    : new Num.Vector4(0.09f, 0.11f, 0.14f, 0.96f)),
            5f);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Num.Vector4(0.64f, 0.72f, 0.82f, 0.78f)),
            5f,
            ImDrawFlags.None,
            1f);

        int hash =
            StableHash(identity);
        uint mark =
            ImGui.GetColorU32(
                new Num.Vector4(0.76f, 0.84f, 0.93f, 0.94f));

        float yA =
            center.Y - 6f;
        float yB =
            center.Y;
        float yC =
            center.Y + 6f;

        float a =
            4f +
            (hash & 3);
        float b =
            4f +
            ((hash >> 2) & 3);
        float c =
            4f +
            ((hash >> 4) & 3);

        draw.AddLine(
            new Num.Vector2(center.X - a, yA),
            new Num.Vector2(center.X + a, yA),
            mark,
            1.5f);
        draw.AddLine(
            new Num.Vector2(center.X - b, yB),
            new Num.Vector2(center.X + b, yB),
            mark,
            1.5f);
        draw.AddLine(
            new Num.Vector2(center.X - c, yC),
            new Num.Vector2(center.X + c, yC),
            mark,
            1.5f);

        if ((hash & 0x40) != 0)
        {
            draw.AddLine(
                new Num.Vector2(center.X, center.Y - 9f),
                new Num.Vector2(center.X, center.Y + 9f),
                mark,
                1.2f);
        }
    }

    private static void DrawRelationGlyph(
        ImDrawListPtr draw,
        Num.Vector2 center,
        RelationFamily family,
        Num.Vector4 color,
        float size)
    {
        uint c =
            ImGui.GetColorU32(color);
        float h =
            size * 0.5f;

        switch (family)
        {
            case RelationFamily.Predation:
                for (int i = -1; i <= 1; i++)
                {
                    float x =
                        center.X +
                        i * size * 0.28f;
                    draw.AddLine(
                        new Num.Vector2(x - 2f, center.Y - h * 0.45f),
                        new Num.Vector2(x, center.Y + h * 0.45f),
                        c,
                        1.7f);
                    draw.AddLine(
                        new Num.Vector2(x, center.Y + h * 0.45f),
                        new Num.Vector2(x + 2f, center.Y - h * 0.45f),
                        c,
                        1.7f);
                }
                break;

            case RelationFamily.Hostility:
                draw.AddLine(
                    center - new Num.Vector2(h, h),
                    center + new Num.Vector2(h, h),
                    c,
                    2f);
                draw.AddLine(
                    center + new Num.Vector2(-h, h),
                    center + new Num.Vector2(h, -h),
                    c,
                    2f);
                break;

            case RelationFamily.Fear:
                draw.AddLine(
                    center + new Num.Vector2(-h, 0f),
                    center + new Num.Vector2(0f, -h * 0.65f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(0f, -h * 0.65f),
                    center + new Num.Vector2(h, 0f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(h, 0f),
                    center + new Num.Vector2(0f, h * 0.65f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(0f, h * 0.65f),
                    center + new Num.Vector2(-h, 0f),
                    c,
                    1.5f);
                draw.AddRectFilled(
                    center - new Num.Vector2(1.5f, 1.5f),
                    center + new Num.Vector2(1.5f, 1.5f),
                    c);
                break;

            case RelationFamily.Unease:
                for (int i = -1; i <= 1; i++)
                {
                    float y =
                        center.Y +
                        i * 3.5f;
                    draw.AddLine(
                        new Num.Vector2(center.X - h, y),
                        new Num.Vector2(center.X - h * 0.25f, y - 2f),
                        c,
                        1.4f);
                    draw.AddLine(
                        new Num.Vector2(center.X - h * 0.25f, y - 2f),
                        new Num.Vector2(center.X + h * 0.25f, y + 2f),
                        c,
                        1.4f);
                    draw.AddLine(
                        new Num.Vector2(center.X + h * 0.25f, y + 2f),
                        new Num.Vector2(center.X + h, y),
                        c,
                        1.4f);
                }
                break;

            case RelationFamily.Neutral:
                draw.AddLine(
                    new Num.Vector2(center.X - h, center.Y - 2.5f),
                    new Num.Vector2(center.X + h, center.Y - 2.5f),
                    c,
                    1.6f);
                draw.AddLine(
                    new Num.Vector2(center.X - h, center.Y + 2.5f),
                    new Num.Vector2(center.X + h, center.Y + 2.5f),
                    c,
                    1.6f);
                break;

            case RelationFamily.Untracked:
                draw.AddLine(
                    center + new Num.Vector2(-h, -h * 0.55f),
                    center + new Num.Vector2(-h * 0.15f, -h * 0.55f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(h * 0.15f, h * 0.55f),
                    center + new Num.Vector2(h, h * 0.55f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(-h * 0.7f, h * 0.7f),
                    center + new Num.Vector2(h * 0.7f, -h * 0.7f),
                    c,
                    1.7f);
                break;

            default:
                draw.AddLine(
                    center + new Num.Vector2(0f, -h),
                    center + new Num.Vector2(h, 0f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(h, 0f),
                    center + new Num.Vector2(0f, h),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(0f, h),
                    center + new Num.Vector2(-h, 0f),
                    c,
                    1.5f);
                draw.AddLine(
                    center + new Num.Vector2(-h, 0f),
                    center + new Num.Vector2(0f, -h),
                    c,
                    1.5f);
                break;
        }
    }

    private static void DrawIntensityTick(
        ImDrawListPtr draw,
        Num.Vector2 start,
        float width,
        float intensity,
        Num.Vector4 color)
    {
        float direction =
            width >= 0f
                ? 1f
                : -1f;
        float length =
            Math.Abs(width);
        Num.Vector2 end =
            new(
                start.X + width,
                start.Y);
        Num.Vector2 activeEnd =
            new(
                start.X +
                direction *
                length *
                Clamp01(intensity),
                start.Y);

        draw.AddLine(
            start,
            end,
            ImGui.GetColorU32(
                new Num.Vector4(0.17f, 0.20f, 0.24f, 0.92f)),
            3f);
        draw.AddLine(
            start,
            activeEnd,
            ImGui.GetColorU32(color),
            3f);
    }

    private static void DrawSplitMarker(
        ImDrawListPtr draw,
        Num.Vector2 center,
        Num.Vector4 leftColor,
        Num.Vector4 rightColor)
    {
        float s = 4.5f;
        uint left =
            ImGui.GetColorU32(leftColor);
        uint right =
            ImGui.GetColorU32(rightColor);

        draw.AddLine(
            center + new Num.Vector2(0f, -s),
            center + new Num.Vector2(-s, 0f),
            left,
            1.6f);
        draw.AddLine(
            center + new Num.Vector2(-s, 0f),
            center + new Num.Vector2(0f, s),
            left,
            1.6f);

        draw.AddLine(
            center + new Num.Vector2(0f, -s),
            center + new Num.Vector2(s, 0f),
            right,
            1.6f);
        draw.AddLine(
            center + new Num.Vector2(s, 0f),
            center + new Num.Vector2(0f, s),
            right,
            1.6f);
    }

    private static string BuildPairTooltip(
        EditorRelationshipPresentationSnapshot snapshot,
        PairPresentation pair)
    {
        return
            pair.Name +
            "\n" +
            snapshot.PrimaryCreature +
            " > " +
            pair.Row.CreatureType +
            " : " +
            (pair.Forward.Type ?? string.Empty) +
            " " +
            pair.Forward.Intensity.ToString("0.00") +
            (pair.Forward.DirectOverride ? " *" : string.Empty) +
            "\n" +
            pair.Row.CreatureType +
            " > " +
            snapshot.PrimaryCreature +
            " : " +
            (pair.Reverse.Type ?? string.Empty) +
            " " +
            pair.Reverse.Intensity.ToString("0.00") +
            (pair.Reverse.DirectOverride ? " *" : string.Empty);
    }

    private static string BuildDirectionTooltip(
        EditorRelationshipPresentationSnapshot snapshot,
        PairPresentation pair,
        EditorRelationshipDirection direction)
    {
        bool forward =
            direction ==
            EditorRelationshipDirection.PrimaryToOther;
        EditorRelationshipValueSnapshot value =
            forward
                ? pair.Forward
                : pair.Reverse;
        string from =
            forward
                ? snapshot.PrimaryCreature
                : pair.Row.CreatureType;
        string to =
            forward
                ? pair.Row.CreatureType
                : snapshot.PrimaryCreature;

        return
            from +
            " > " +
            to +
            "\n" +
            (value.Type ?? string.Empty) +
            "  " +
            value.Intensity.ToString("0.00") +
            (value.DirectOverride
                ? DevToolUiSettings.T("\n直接覆盖", "\nDirect override")
                : DevToolUiSettings.T("\n继承/有效值", "\nInherited/effective"));
    }

    private static string FitText(
        string text,
        float maxWidth)
    {
        text ??= string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string suffix = "...";
        float suffixWidth =
            ImGui.CalcTextSize(suffix).X;
        if (suffixWidth >= maxWidth)
            return suffix;

        int length =
            text.Length;
        while (length > 1)
        {
            length--;
            string candidate =
                text.Substring(0, length) +
                suffix;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                return candidate;
        }

        return suffix;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            int hash = 17;
            value ??= string.Empty;
            for (int i = 0; i < value.Length; i++)
                hash = hash * 31 + value[i];
            return hash;
        }
    }

    private static float Clamp01(float value) =>
        Math.Max(0f, Math.Min(1f, value));

    private static string GetInspectorEditKey(
        string primary,
        string other,
        EditorRelationshipDirection direction)
    {
        primary ??= string.Empty;
        other ??= string.Empty;

        if (string.Equals(
                inspectorEditPrimary,
                primary,
                StringComparison.Ordinal) &&
            string.Equals(
                inspectorEditOther,
                other,
                StringComparison.Ordinal) &&
            inspectorEditDirection == direction &&
            !string.IsNullOrEmpty(inspectorEditKey))
        {
            return inspectorEditKey;
        }

        inspectorEditPrimary =
            primary;
        inspectorEditOther =
            other;
        inspectorEditDirection =
            direction;
        inspectorEditKey =
            primary +
            ">" +
            other +
            ":" +
            direction;
        return inspectorEditKey;
    }

    private static string PrimarySearchQuery()
    {
        if (string.Equals(
                observedPrimarySearch,
                primarySearch,
                StringComparison.Ordinal))
        {
            return normalizedPrimarySearch;
        }

        observedPrimarySearch =
            primarySearch;
        normalizedPrimarySearch =
            primarySearch?.Trim() ??
            string.Empty;
        return normalizedPrimarySearch;
    }

    private static string MatrixSearchQuery()
    {
        if (string.Equals(
                observedMatrixSearch,
                matrixSearch,
                StringComparison.Ordinal))
        {
            return normalizedMatrixSearch;
        }

        observedMatrixSearch =
            matrixSearch;
        normalizedMatrixSearch =
            matrixSearch?.Trim() ??
            string.Empty;
        return normalizedMatrixSearch;
    }

    private static bool Matches(
        string value,
        string normalizedQuery) =>
        string.IsNullOrEmpty(normalizedQuery) ||
        (!string.IsNullOrEmpty(value) &&
         value.IndexOf(
             normalizedQuery,
             StringComparison.OrdinalIgnoreCase) >=
         0);
}
