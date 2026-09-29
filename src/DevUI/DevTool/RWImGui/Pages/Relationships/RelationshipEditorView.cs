using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Relationships;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Icon-first ecology relationship atlas.
///
/// The default view is a semantic map rather than a table or card grid. The selected primary
/// creature lives at the center; target creatures use Rain World's prepared sandbox/creature icons
/// and are spatially grouped by the primary creature's relationship toward them. Two parallel
/// directional rails visualize both directions, while color and line weight encode type and
/// intensity. Exact enum names/values remain in hover details and the inspector.
/// </summary>
internal static class RelationshipEditorView
{
    private enum RelationshipViewMode
    {
        Atlas,
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

    private sealed class AtlasNodeLayout
    {
        internal PairPresentation Pair;
        internal Num.Vector2 Center;
        internal float Size;
        internal bool Hovered;
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

    private static RelationshipViewMode viewMode = RelationshipViewMode.Atlas;
    private static RelationshipFilterMode filterMode = RelationshipFilterMode.All;

    private static EditorRelationshipRowSnapshot[] projectedRowsSource;
    private static PairPresentation[] projectedRows = Array.Empty<PairPresentation>();
    private static string projectedFilter = string.Empty;
    private static RelationshipFilterMode projectedFilterMode;
    private static PairPresentation[] projectedVisibleRows = Array.Empty<PairPresentation>();
    private static readonly List<AtlasNodeLayout> atlasNodes = new();

    private static string inspectorEditKey = string.Empty;
    private static string inspectorEditPrimary = string.Empty;
    private static string inspectorEditOther = string.Empty;
    private static EditorRelationshipDirection inspectorEditDirection;

    internal static void ResetRetainedState()
    {
        projectedRowsSource = null;
        projectedRows = Array.Empty<PairPresentation>();
        projectedFilter = string.Empty;
        projectedFilterMode = RelationshipFilterMode.All;
        projectedVisibleRows = Array.Empty<PairPresentation>();
        atlasNodes.Clear();

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

        viewMode = RelationshipViewMode.Atlas;
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

        ImGui.TextDisabled(DevToolUiSettings.T("主体", "PRIMARY"));
        DevToolWidgets.FullWidthInputText(
            DevToolUiSettings.T("搜索", "Search"),
            "RelationshipPrimarySearch",
            ref primarySearch,
            128);
        ImGui.Separator();

        string[] creatures =
            snapshot.CreatureTypes ?? Array.Empty<string>();
        string query =
            PrimarySearchQuery();

        int matches = 0;
        for (int i = 0; i < creatures.Length; i++)
        {
            string type =
                creatures[i] ?? string.Empty;
            if (!Matches(type, query))
                continue;

            matches++;
            bool selected =
                string.Equals(
                    type,
                    snapshot.PrimaryCreature,
                    StringComparison.Ordinal);

            if (DrawPrimaryCreatureRow(
                    type,
                    selected))
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

    private static bool DrawPrimaryCreatureRow(
        string creatureId,
        bool selected)
    {
        float width =
            Math.Max(
                120f,
                ImGui.GetContentRegionAvail().X);
        const float height = 36f;

        ImGui.PushID(
            "RelationshipPrimary:" + creatureId);
        bool clicked =
            ImGui.InvisibleButton(
                "##row",
                new Num.Vector2(
                    width,
                    height));
        bool hovered =
            ImGui.IsItemHovered();
        ImGui.PopID();

        Num.Vector2 min =
            ImGui.GetItemRectMin();
        Num.Vector2 max =
            ImGui.GetItemRectMax();
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();

        Num.Vector4 bg =
            selected
                ? new Num.Vector4(0.15f, 0.34f, 0.57f, 0.94f)
                : hovered
                    ? new Num.Vector4(0.12f, 0.16f, 0.22f, 0.94f)
                    : new Num.Vector4(0.055f, 0.07f, 0.095f, 0.56f);

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(bg),
            4f);

        if (selected)
        {
            draw.AddRectFilled(
                min,
                new Num.Vector2(
                    min.X + 3f,
                    max.Y),
                ImGui.GetColorU32(
                    new Num.Vector4(0.42f, 0.78f, 1f, 1f)),
                1f);
        }

        Num.Vector2 iconPos =
            min + new Num.Vector2(6f, 4f);
        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            creatureId,
            iconPos,
            new Num.Vector2(28f, 28f));

        draw.AddText(
            min + new Num.Vector2(40f, 9f),
            ImGui.GetColorU32(
                selected
                    ? new Num.Vector4(0.98f, 0.99f, 1f, 1f)
                    : new Num.Vector4(0.84f, 0.87f, 0.91f, 0.98f)),
            FitText(
                creatureId,
                Math.Max(40f, width - 48f)));

        return clicked;
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

        ImGui.SetNextWindowPos(
            position,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            size,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(
            Math.Min(
                1f,
                DevToolUiSettings.WindowAlpha + 0.04f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(
                "##DevToolRelationshipAtlas",
                flags))
        {
            ImGui.End();
            return;
        }

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

        if (viewMode == RelationshipViewMode.Atlas)
            DrawAtlas(snapshot);
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

        DrawInspectorPairHeader(
            snapshot.PrimaryCreature,
            row.CreatureType,
            string.IsNullOrEmpty(row.DisplayName)
                ? row.CreatureType
                : row.DisplayName);

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

        bool atlas = viewMode == RelationshipViewMode.Atlas;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("图谱", "Atlas"),
                "RelationshipAtlasMode",
                atlas ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fixedWidth: 72f))
        {
            viewMode = RelationshipViewMode.Atlas;
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("热图", "Heatmap"),
                "RelationshipHeatmapMode",
                !atlas ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
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
        for (int i = 0; i < FamilyOrder.Length; i++)
        {
            RelationFamily family =
                FamilyOrder[i];
            string label =
                GetFamilyLabel(family);

            DrawLegendChip(
                family,
                label);

            if (i + 1 < FamilyOrder.Length)
            {
                // SameLineIfFits performs the SameLine call itself. When the next chip does not fit,
                // normal ImGui flow already advances to the next row at the original content X.
                DevToolWidgets.SameLineIfFits(
                    ImGui.CalcTextSize(
                        GetFamilyLabel(FamilyOrder[i + 1])).X + 38f);
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

    private static void DrawAtlas(EditorRelationshipPresentationSnapshot snapshot)
    {
        Num.Vector2 available =
            ImGui.GetContentRegionAvail();
        Num.Vector2 canvasSize =
            new(
                Math.Max(520f, available.X),
                Math.Max(420f, available.Y));

        Num.Vector2 cursorLocal =
            ImGui.GetCursorPos();
        Num.Vector2 origin =
            ImGui.GetCursorScreenPos();

        // Reserve the complete workspace first. Interactive node buttons are submitted afterwards
        // at absolute positions, so they remain the topmost hit targets instead of being occluded by
        // one giant canvas item.
        ImGui.Dummy(canvasSize);
        Num.Vector2 afterCanvas =
            ImGui.GetCursorPos();

        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            origin,
            origin + canvasSize,
            ImGui.GetColorU32(
                new Num.Vector4(0.025f, 0.032f, 0.045f, 0.46f)),
            8f);

        BuildAtlasLayout(
            origin,
            canvasSize);

        Num.Vector2 primaryCenter =
            origin +
            canvasSize * 0.5f;

        Num.Vector2 mouse =
            ImGui.GetIO().MousePos;
        AtlasNodeLayout hot =
            null;
        for (int i = 0; i < atlasNodes.Count; i++)
        {
            AtlasNodeLayout node =
                atlasNodes[i];
            float half =
                node.Size * 0.5f;
            node.Hovered =
                PointInRect(
                    mouse,
                    node.Center - new Num.Vector2(half, half),
                    node.Center + new Num.Vector2(half, half));
            if (node.Hovered)
                hot = node;
        }

        DrawAtlasZones(
            draw,
            origin,
            canvasSize);

        // Connections deliberately sit behind the icons. Dense maps therefore still read as
        // creature clusters first; the selected/hovered pair comes forward with much stronger rails.
        for (int i = 0; i < atlasNodes.Count; i++)
        {
            AtlasNodeLayout node =
                atlasNodes[i];
            bool emphasized =
                node.Hovered ||
                node.Pair.Row?.Selected == true;
            bool deEmphasized =
                hot != null &&
                !ReferenceEquals(
                    hot,
                    node) &&
                node.Pair.Row?.Selected != true;

            DrawBidirectionalConnection(
                draw,
                primaryCenter,
                node.Center,
                node.Pair,
                emphasized,
                deEmphasized);
        }

        DrawPrimaryAtlasNode(
            draw,
            snapshot.PrimaryCreature,
            primaryCenter,
            108f);

        // Restore the local cursor and submit node hit targets over the already-painted graph.
        ImGui.SetCursorPos(cursorLocal);
        for (int i = 0; i < atlasNodes.Count; i++)
        {
            AtlasNodeLayout node =
                atlasNodes[i];
            DrawAtlasNode(
                snapshot,
                node,
                origin,
                cursorLocal);
        }

        ImGui.SetCursorPos(afterCanvas);
    }

    private static void BuildAtlasLayout(
        Num.Vector2 origin,
        Num.Vector2 size)
    {
        atlasNodes.Clear();

        for (int familyIndex = 0; familyIndex < FamilyOrder.Length; familyIndex++)
        {
            RelationFamily family =
                FamilyOrder[familyIndex];

            GetFamilyRegion(
                origin,
                size,
                family,
                out Num.Vector2 regionMin,
                out Num.Vector2 regionMax);

            int count =
                CountFamily(family);
            if (count <= 0)
                continue;

            float width =
                Math.Max(
                    80f,
                    regionMax.X - regionMin.X - 18f);
            float height =
                Math.Max(
                    70f,
                    regionMax.Y - regionMin.Y - 34f);

            float aspect =
                width /
                Math.Max(1f, height);
            int columns =
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        Math.Sqrt(
                            count * aspect)));
            int rows =
                Math.Max(
                    1,
                    (count + columns - 1) /
                    columns);

            const float gap = 5f;
            float cellWidth =
                width /
                columns;
            float cellHeight =
                height /
                rows;
            float nodeSize =
                Math.Max(
                    30f,
                    Math.Min(
                        58f,
                        Math.Min(
                            cellWidth - gap,
                            cellHeight - gap)));

            int localIndex = 0;
            for (int i = 0; i < projectedVisibleRows.Length; i++)
            {
                PairPresentation pair =
                    projectedVisibleRows[i];
                if (pair.ForwardFamily != family)
                    continue;

                int col =
                    localIndex % columns;
                int row =
                    localIndex / columns;

                float x =
                    regionMin.X +
                    9f +
                    cellWidth * (col + 0.5f);
                float y =
                    regionMin.Y +
                    28f +
                    cellHeight * (row + 0.5f);

                atlasNodes.Add(
                    new AtlasNodeLayout
                    {
                        Pair = pair,
                        Center = new Num.Vector2(x, y),
                        Size = nodeSize
                    });

                localIndex++;
            }
        }
    }

    private static int CountFamily(RelationFamily family)
    {
        int count = 0;
        for (int i = 0; i < projectedVisibleRows.Length; i++)
        {
            PairPresentation pair = projectedVisibleRows[i];
            if (pair != null &&
                pair.ForwardFamily == family)
            {
                count++;
            }
        }

        return count;
    }

    private static void GetFamilyRegion(
        Num.Vector2 origin,
        Num.Vector2 size,
        RelationFamily family,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        float x =
            origin.X;
        float y =
            origin.Y;
        float w =
            size.X;
        float h =
            size.Y;

        switch (family)
        {
            case RelationFamily.Fear:
                min = new Num.Vector2(x + w * 0.015f, y + h * 0.055f);
                max = new Num.Vector2(x + w * 0.285f, y + h * 0.485f);
                return;

            case RelationFamily.Unease:
                min = new Num.Vector2(x + w * 0.015f, y + h * 0.515f);
                max = new Num.Vector2(x + w * 0.285f, y + h * 0.945f);
                return;

            case RelationFamily.Predation:
                min = new Num.Vector2(x + w * 0.305f, y + h * 0.025f);
                max = new Num.Vector2(x + w * 0.695f, y + h * 0.285f);
                return;

            case RelationFamily.Neutral:
                min = new Num.Vector2(x + w * 0.305f, y + h * 0.715f);
                max = new Num.Vector2(x + w * 0.695f, y + h * 0.975f);
                return;

            case RelationFamily.Hostility:
                min = new Num.Vector2(x + w * 0.715f, y + h * 0.055f);
                max = new Num.Vector2(x + w * 0.985f, y + h * 0.485f);
                return;

            case RelationFamily.Untracked:
                min = new Num.Vector2(x + w * 0.715f, y + h * 0.515f);
                max = new Num.Vector2(x + w * 0.985f, y + h * 0.945f);
                return;

            default:
                min = new Num.Vector2(x + w * 0.355f, y + h * 0.305f);
                max = new Num.Vector2(x + w * 0.645f, y + h * 0.445f);
                return;
        }
    }

    private static void DrawAtlasZones(
        ImDrawListPtr draw,
        Num.Vector2 origin,
        Num.Vector2 size)
    {
        for (int i = 0; i < FamilyOrder.Length; i++)
        {
            RelationFamily family =
                FamilyOrder[i];
            int count =
                CountFamily(family);
            if (count <= 0)
                continue;

            GetFamilyRegion(
                origin,
                size,
                family,
                out Num.Vector2 min,
                out Num.Vector2 max);

            Num.Vector4 color =
                GetFamilyColor(family);

            draw.AddRectFilled(
                min,
                max,
                ImGui.GetColorU32(
                    new Num.Vector4(
                        color.X * 0.07f,
                        color.Y * 0.07f,
                        color.Z * 0.07f,
                        0.58f)),
                9f);

            draw.AddRect(
                min,
                max,
                ImGui.GetColorU32(
                    new Num.Vector4(
                        color.X,
                        color.Y,
                        color.Z,
                        0.22f)),
                9f,
                ImDrawFlags.None,
                1f);

            Num.Vector2 labelCenter =
                new(
                    min.X + 15f,
                    min.Y + 14f);
            DrawRelationGlyph(
                draw,
                labelCenter,
                family,
                color,
                11f);

            string text =
                GetFamilyLabel(family) +
                "  " +
                count;
            draw.AddText(
                min + new Num.Vector2(27f, 6f),
                ImGui.GetColorU32(
                    new Num.Vector4(
                        color.X,
                        color.Y,
                        color.Z,
                        0.92f)),
                text);
        }

        Num.Vector2 center =
            origin +
            size * 0.5f;
        float ring =
            Math.Min(
                size.X,
                size.Y) *
            0.13f;
        draw.AddCircle(
            center,
            ring,
            ImGui.GetColorU32(
                new Num.Vector4(0.30f, 0.38f, 0.50f, 0.20f)),
            48,
            1f);
        draw.AddCircle(
            center,
            ring + 12f,
            ImGui.GetColorU32(
                new Num.Vector4(0.30f, 0.38f, 0.50f, 0.10f)),
            48,
            1f);
    }

    private static void DrawPrimaryAtlasNode(
        ImDrawListPtr draw,
        string creatureId,
        Num.Vector2 center,
        float size)
    {
        float half =
            size * 0.5f;
        Num.Vector2 min =
            center -
            new Num.Vector2(
                half,
                half);
        Num.Vector2 max =
            center +
            new Num.Vector2(
                half,
                half);

        draw.AddCircleFilled(
            center,
            half + 10f,
            ImGui.GetColorU32(
                new Num.Vector4(0.12f, 0.30f, 0.48f, 0.20f)),
            48);

        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Num.Vector4(0.055f, 0.075f, 0.105f, 0.98f)),
            12f);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Num.Vector4(0.38f, 0.72f, 1f, 0.94f)),
            12f,
            ImDrawFlags.None,
            2f);

        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            creatureId,
            min + new Num.Vector2(14f, 10f),
            new Num.Vector2(
                size - 28f,
                size - 42f));

        string label =
            FitText(
                creatureId,
                size - 14f);
        Num.Vector2 textSize =
            ImGui.CalcTextSize(label);
        draw.AddText(
            new Num.Vector2(
                center.X - textSize.X * 0.5f,
                max.Y - 23f),
            ImGui.GetColorU32(
                new Num.Vector4(0.94f, 0.97f, 1f, 1f)),
            label);
    }

    private static void DrawAtlasNode(
        EditorRelationshipPresentationSnapshot snapshot,
        AtlasNodeLayout node,
        Num.Vector2 canvasOrigin,
        Num.Vector2 canvasCursorLocal)
    {
        if (node?.Pair?.Row == null)
            return;

        float size =
            node.Size;
        float half =
            size * 0.5f;
        Num.Vector2 min =
            node.Center -
            new Num.Vector2(
                half,
                half);
        Num.Vector2 max =
            node.Center +
            new Num.Vector2(
                half,
                half);

        Num.Vector2 local =
            canvasCursorLocal +
            (min - canvasOrigin);
        ImGui.SetCursorPos(local);

        ImGui.PushID(
            "RelationshipAtlasNode:" +
            node.Pair.Row.CreatureType);
        bool clicked =
            ImGui.InvisibleButton(
                "##node",
                new Num.Vector2(
                    size,
                    size));
        bool hovered =
            ImGui.IsItemHovered();
        ImGui.PopID();

        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();
        Num.Vector4 forwardColor =
            GetFamilyColor(
                node.Pair.ForwardFamily);
        Num.Vector4 reverseColor =
            GetFamilyColor(
                node.Pair.ReverseFamily);

        bool selected =
            node.Pair.Row.Selected;

        Num.Vector4 bg =
            selected
                ? new Num.Vector4(0.11f, 0.19f, 0.29f, 0.98f)
                : hovered
                    ? new Num.Vector4(0.10f, 0.13f, 0.18f, 0.98f)
                    : new Num.Vector4(0.045f, 0.055f, 0.075f, 0.94f);

        draw.AddCircleFilled(
            node.Center,
            half,
            ImGui.GetColorU32(bg),
            32);

        draw.AddCircle(
            node.Center,
            half - 1f,
            ImGui.GetColorU32(
                new Num.Vector4(
                    reverseColor.X,
                    reverseColor.Y,
                    reverseColor.Z,
                    selected || hovered ? 1f : 0.78f)),
            32,
            1.2f +
            Clamp01(node.Pair.Reverse.Intensity) *
            2.2f);

        // Forward relationship is the semantic zone and also the lower arc, so the node remains
        // legible if it is ever dragged away from its group in a future layout editor.
        DrawArc(
            draw,
            node.Center,
            half - 4f,
            0f,
            (float)Math.PI,
            Clamp01(node.Pair.Forward.Intensity),
            forwardColor,
            2.2f +
            Clamp01(node.Pair.Forward.Intensity) *
            1.8f);

        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            node.Pair.Row.CreatureType,
            min + new Num.Vector2(
                size * 0.16f,
                size * 0.13f),
            new Num.Vector2(
                size * 0.68f,
                size * 0.68f));

        if (node.Pair.Asymmetric)
        {
            DrawSplitMarker(
                draw,
                new Num.Vector2(
                    max.X - 6f,
                    min.Y + 6f),
                forwardColor,
                reverseColor);
        }

        if (node.Pair.Changed)
        {
            draw.AddCircleFilled(
                new Num.Vector2(
                    min.X + 6f,
                    min.Y + 6f),
                2.8f,
                ImGui.GetColorU32(
                    new Num.Vector4(0.38f, 0.82f, 1f, 1f)),
                12);
        }

        if (selected)
        {
            draw.AddCircle(
                node.Center,
                half + 3f,
                ImGui.GetColorU32(
                    new Num.Vector4(0.50f, 0.84f, 1f, 1f)),
                32,
                2f);
        }

        if (clicked)
        {
            RelationshipEditorCommandQueue.Enqueue(
                new RelationshipEditorCommand(
                    RelationshipEditorCommandKind.SelectPair,
                    other: node.Pair.Row.CreatureType,
                    direction: EditorRelationshipDirection.PrimaryToOther));
        }

        if (hovered)
        {
            DevToolTooltip.Show(
                BuildPairTooltip(
                    snapshot,
                    node.Pair));
        }
    }

    private static void DrawBidirectionalConnection(
        ImDrawListPtr draw,
        Num.Vector2 primary,
        Num.Vector2 target,
        PairPresentation pair,
        bool emphasized,
        bool deEmphasized)
    {
        Num.Vector2 delta =
            target -
            primary;
        float length =
            (float)Math.Sqrt(
                delta.X * delta.X +
                delta.Y * delta.Y);
        if (length < 4f)
            return;

        Num.Vector2 direction =
            delta /
            length;
        Num.Vector2 normal =
            new(
                -direction.Y,
                direction.X);

        float inset =
            58f;
        Num.Vector2 start =
            primary +
            direction * inset;
        Num.Vector2 end =
            target -
            direction *
            Math.Max(
                18f,
                Math.Min(
                    pair == null
                        ? 18f
                        : 32f,
                    length * 0.12f));

        DrawDirectionalRail(
            draw,
            start + normal * 3.2f,
            end + normal * 3.2f,
            pair.ForwardFamily,
            pair.Forward.Intensity,
            true,
            emphasized,
            deEmphasized);

        DrawDirectionalRail(
            draw,
            end - normal * 3.2f,
            start - normal * 3.2f,
            pair.ReverseFamily,
            pair.Reverse.Intensity,
            false,
            emphasized,
            deEmphasized);
    }

    private static void DrawDirectionalRail(
        ImDrawListPtr draw,
        Num.Vector2 from,
        Num.Vector2 to,
        RelationFamily family,
        float intensity,
        bool forward,
        bool emphasized,
        bool deEmphasized)
    {
        float strength =
            Clamp01(intensity);
        Num.Vector4 color =
            GetFamilyColor(family);

        float alpha =
            deEmphasized
                ? 0.045f
                : emphasized
                    ? 0.95f
                    : 0.10f + strength * 0.16f;

        uint packed =
            ImGui.GetColorU32(
                new Num.Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    alpha));

        float thickness =
            emphasized
                ? 2.0f + strength * 3.4f
                : 0.8f + strength * 1.5f;

        draw.AddLine(
            from,
            to,
            packed,
            thickness);

        Num.Vector2 delta =
            to - from;
        float length =
            (float)Math.Sqrt(
                delta.X * delta.X +
                delta.Y * delta.Y);
        if (length < 8f)
            return;

        Num.Vector2 dir =
            delta /
            length;
        Num.Vector2 normal =
            new(
                -dir.Y,
                dir.X);

        float t =
            forward
                ? 0.67f
                : 0.60f;
        Num.Vector2 tip =
            from +
            delta * t;
        float arrow =
            emphasized
                ? 7f
                : 4.5f;

        draw.AddLine(
            tip,
            tip -
            dir * arrow +
            normal * arrow * 0.55f,
            packed,
            thickness);
        draw.AddLine(
            tip,
            tip -
            dir * arrow -
            normal * arrow * 0.55f,
            packed,
            thickness);
    }

    private static bool PointInRect(
        Num.Vector2 point,
        Num.Vector2 min,
        Num.Vector2 max) =>
        point.X >= min.X &&
        point.X <= max.X &&
        point.Y >= min.Y &&
        point.Y <= max.Y;

    private static void DrawInspectorPairHeader(
        string primary,
        string other,
        string otherDisplay)
    {
        float width =
            Math.Max(
                180f,
                ImGui.GetContentRegionAvail().X);
        const float height = 62f;

        Num.Vector2 min =
            ImGui.GetCursorScreenPos();
        ImGui.Dummy(
            new Num.Vector2(
                width,
                height));
        Num.Vector2 max =
            min +
            new Num.Vector2(
                width,
                height);

        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Num.Vector4(0.045f, 0.06f, 0.085f, 0.82f)),
            6f);

        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            primary,
            min + new Num.Vector2(8f, 8f),
            new Num.Vector2(44f, 44f));

        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            other,
            new Num.Vector2(max.X - 52f, min.Y + 8f),
            new Num.Vector2(44f, 44f));

        Num.Vector2 lineStart =
            new(min.X + 62f, min.Y + 31f);
        Num.Vector2 lineEnd =
            new(max.X - 62f, min.Y + 31f);

        draw.AddLine(
            lineStart,
            lineEnd,
            ImGui.GetColorU32(
                new Num.Vector4(0.48f, 0.58f, 0.72f, 0.58f)),
            1.4f);

        float mid =
            (lineStart.X + lineEnd.X) * 0.5f;
        draw.AddLine(
            new Num.Vector2(mid + 8f, min.Y + 31f),
            new Num.Vector2(mid + 2f, min.Y + 27f),
            ImGui.GetColorU32(
                new Num.Vector4(0.64f, 0.74f, 0.88f, 0.92f)),
            1.5f);
        draw.AddLine(
            new Num.Vector2(mid + 8f, min.Y + 31f),
            new Num.Vector2(mid + 2f, min.Y + 35f),
            ImGui.GetColorU32(
                new Num.Vector4(0.64f, 0.74f, 0.88f, 0.92f)),
            1.5f);

        string centerLabel =
            FitText(
                otherDisplay,
                Math.Max(
                    60f,
                    width - 150f));
        Num.Vector2 textSize =
            ImGui.CalcTextSize(centerLabel);
        draw.AddText(
            new Num.Vector2(
                min.X +
                width * 0.5f -
                textSize.X * 0.5f,
                min.Y + 8f),
            ImGui.GetColorU32(
                new Num.Vector4(0.92f, 0.95f, 1f, 1f)),
            centerLabel);
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
