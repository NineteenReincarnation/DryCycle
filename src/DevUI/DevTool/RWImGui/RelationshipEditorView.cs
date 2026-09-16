using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Relationships;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class RelationshipEditorView
{
    private sealed class MatrixRowPresentation
    {
        internal EditorRelationshipRowSnapshot Row;
        internal EditorRelationshipValueSnapshot Forward;
        internal EditorRelationshipValueSnapshot Reverse;
        internal string Name;
        internal string ForwardLabel;
        internal string ReverseLabel;
    }

    private static readonly EditorRelationshipValueSnapshot EmptyRelationship = new();
    private static string primarySearch = string.Empty;
    private static string matrixSearch = string.Empty;
    private static string observedPrimarySearch;
    private static string normalizedPrimarySearch = string.Empty;
    private static string observedMatrixSearch;
    private static string normalizedMatrixSearch = string.Empty;
    private static bool changedOnly;

    private static string[] projectedCreatureTypes;
    private static string[] projectedCreatureLabels = Array.Empty<string>();
    private static EditorRelationshipRowSnapshot[] projectedRowsSource;
    private static MatrixRowPresentation[] projectedRows = Array.Empty<MatrixRowPresentation>();
    private static string projectedMatrixFilter = string.Empty;
    private static bool projectedMatrixChangedOnly;
    private static MatrixRowPresentation[] projectedVisibleRows = Array.Empty<MatrixRowPresentation>();
    private static string inspectorEditKey = string.Empty;
    private static string inspectorEditPrimary = string.Empty;
    private static string inspectorEditOther = string.Empty;
    private static EditorRelationshipDirection inspectorEditDirection;

    internal static void ResetRetainedState()
    {
        projectedCreatureTypes = null;
        projectedCreatureLabels = Array.Empty<string>();
        projectedRowsSource = null;
        projectedRows = Array.Empty<MatrixRowPresentation>();
        projectedMatrixFilter = string.Empty;
        projectedMatrixChangedOnly = false;
        projectedVisibleRows = Array.Empty<MatrixRowPresentation>();
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
        changedOnly = false;
    }

    internal static void DrawBrowser(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("关系编辑器不可用。", "Relationship editor unavailable."), true);
            return;
        }

        ImGui.TextDisabled(DevToolUiSettings.T("主生物", "PRIMARY CREATURE"));
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索", "Search"), "RelationshipPrimarySearch", ref primarySearch, 128);
        ImGui.Separator();

        string[] creatures = snapshot.CreatureTypes ?? Array.Empty<string>();
        EnsureCreatureLabels(creatures);
        string primaryQuery = PrimarySearchQuery();
        int matches = 0;
        for (int i = 0; i < creatures.Length; i++)
        {
            string type = creatures[i];
            if (!Matches(type, primaryQuery)) continue;
            matches++;
            bool selected = string.Equals(type, snapshot.PrimaryCreature, StringComparison.Ordinal);
            if (ImGui.Selectable(projectedCreatureLabels[i], selected))
            {
                RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                    RelationshipEditorCommandKind.SelectPrimary,
                    primary: type));
                    }
            if (selected) ImGui.SetItemDefaultFocus();
        }
        if (matches == 0) DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的生物。", "No matching creatures."), true);

        ImGui.Separator();
        ImGui.TextDisabled(DevToolUiSettings.T("矩阵过滤", "MATRIX FILTER"));
        DevToolWidgets.FullWidthInputText(DevToolUiSettings.T("搜索行", "Search rows"), "RelationshipMatrixSearch", ref matrixSearch, 128);
        ImGui.Checkbox(DevToolUiSettings.T("仅显示已修改##RelationshipChangedOnly", "Changed only##RelationshipChangedOnly"), ref changedOnly);
        ImGui.TextDisabled(DevToolUiSettings.T("* = 直接覆盖", "* = direct override"));
    }

    internal static void DrawMatrix(
        EditorRelationshipPresentationSnapshot snapshot,
        Num.Vector2 position,
        Num.Vector2 size)
    {
        if (!snapshot.Available || size.X < 180f || size.Y < 120f) return;

        ImGui.SetNextWindowPos(position, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(520f, 280f), new Num.Vector2(4000f, 4000f));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(DevToolUiSettings.T("关系矩阵###DevToolRelationshipMatrix", "Relationships###DevToolRelationshipMatrix"), flags))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("Relationships");

        string primaryText = DevToolUiSettings.T("主体：", "Primary: ") + (string.IsNullOrEmpty(snapshot.PrimaryCreature) ? DevToolUiSettings.T("<无>", "<none>") : snapshot.PrimaryCreature);
        ImGui.Text(primaryText);
        string hint = DevToolUiSettings.T("点击任意方向进行检查/编辑", "click either direction to inspect/edit");
        if (DevToolWidgets.SameLineIfFits(ImGui.CalcTextSize(hint).X))
            ImGui.TextDisabled(hint);
        else
            ImGui.TextDisabled(hint);
        ImGui.Separator();

        float available = ImGui.GetContentRegionAvail().X;
        float nameWidth = Math.Max(120f, Math.Min(220f, available * 0.27f));
        float relationWidth = Math.Max(150f, (available - nameWidth - 28f) * 0.5f);

        ImGui.TextDisabled(DevToolUiSettings.T("生物", "Creature"));
        ImGui.SameLine(nameWidth);
        ImGui.TextDisabled(snapshot.PrimaryCreature + DevToolUiSettings.T("  →  其他", "  →  Other"));
        ImGui.SameLine(nameWidth + relationWidth + 12f);
        ImGui.TextDisabled(DevToolUiSettings.T("其他  →  ", "Other  →  ") + snapshot.PrimaryCreature);
        ImGui.Separator();

        EditorRelationshipRowSnapshot[] rows = snapshot.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        EnsureMatrixRows(rows);
        EnsureVisibleMatrixRows(MatrixSearchQuery(), changedOnly);

        if (projectedVisibleRows.Length == 0)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("没有关系符合当前过滤条件。", "No relationships match the current filter."));
            ImGui.End();
            return;
        }

        using (DevToolListClipper clipper = new(projectedVisibleRows.Length))
        {
            while (clipper.Step(out int firstVisible, out int lastVisibleExclusive))
            {
                for (int i = firstVisible; i < lastVisibleExclusive; i++)
                {
                    MatrixRowPresentation presentation = projectedVisibleRows[i];
                    EditorRelationshipRowSnapshot row = presentation.Row;

                    float startX = ImGui.GetCursorPosX();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(presentation.Name);
                    if (ImGui.IsItemHovered() && !string.Equals(presentation.Name, row.CreatureType, StringComparison.Ordinal))
                        DevToolTooltip.Show(row.CreatureType);

                    ImGui.SameLine(startX + nameWidth);
                    DrawRelationButton(snapshot, row, presentation.ForwardLabel,
                        EditorRelationshipDirection.PrimaryToOther, relationWidth);
                    ImGui.SameLine(startX + nameWidth + relationWidth + 12f);
                    DrawRelationButton(snapshot, row, presentation.ReverseLabel,
                        EditorRelationshipDirection.OtherToPrimary, relationWidth);
                }
            }
        }

        ImGui.End();
    }

    internal static void DrawInspector(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("关系编辑器不可用。", "Relationship editor unavailable."), true);
            return;
        }

        EditorRelationshipRowSnapshot row = FindRow(snapshot, snapshot.SelectedOtherCreature);
        if (row == null)
        {
            ImGui.TextWrapped(DevToolUiSettings.T("请从关系矩阵中选择一个方向。", "Select one direction from the relationship matrix."));
            return;
        }

        bool forward = snapshot.SelectedDirection == EditorRelationshipDirection.PrimaryToOther;
        string from = forward ? snapshot.PrimaryCreature : row.CreatureType;
        string to = forward ? row.CreatureType : snapshot.PrimaryCreature;
        EditorRelationshipValueSnapshot relationship =
            (forward ? row.PrimaryToOther : row.OtherToPrimary) ?? EmptyRelationship;

        ImGui.Text(from);
        ImGui.SameLine();
        ImGui.TextDisabled("→");
        ImGui.SameLine();
        ImGui.Text(to);
        ImGui.TextDisabled(relationship.DirectOverride
            ? DevToolUiSettings.T("直接覆盖", "Direct override")
            : DevToolUiSettings.T("有效值 / 继承值", "Effective / inherited value"));
        ImGui.Separator();

        string[] types = snapshot.RelationshipTypes ?? Array.Empty<string>();
        string currentType = relationship.Type ?? string.Empty;
        if (ImGui.BeginCombo(DevToolUiSettings.T("关系##RelationshipType", "Relationship##RelationshipType"), currentType))
        {
            for (int i = 0; i < types.Length; i++)
            {
                string type = types[i];
                bool selected = string.Equals(type, currentType, StringComparison.Ordinal);
                if (ImGui.Selectable(type + "##RelationshipType" + i, selected))
                    SendType(snapshot, row.CreatureType, type);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        string editKey = GetInspectorEditKey(snapshot.PrimaryCreature, row.CreatureType, snapshot.SelectedDirection);
        DevToolNumericEditResult<float> intensityEdit = DevToolNumericWidgets.SliderFloat(
            DevToolNumericScope.Relationship,
            editKey,
            DevToolUiSettings.T("强度##RelationshipIntensity", "Intensity##RelationshipIntensity"),
            relationship.Intensity,
            0f,
            1f);
        if (intensityEdit.Committed)
        {
            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                RelationshipEditorCommandKind.SetRelationshipIntensity,
                primary: snapshot.PrimaryCreature,
                other: row.CreatureType,
                value: intensityEdit.Value,
                direction: snapshot.SelectedDirection));
        }

        ImGui.Separator();
        bool canReset = relationship.DirectOverride;
        if (!canReset) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("重置覆盖", "Reset Override"), "ResetRelationshipOverride", DevToolButtonTone.Subtle))
        {
            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                RelationshipEditorCommandKind.ResetRelationship,
                primary: snapshot.PrimaryCreature,
                other: row.CreatureType,
                direction: snapshot.SelectedDirection));
            DevToolNumericWidgets.Discard(DevToolNumericScope.Relationship, editKey);
        }
        if (!canReset) ImGui.EndDisabled();

        ImGui.TextWrapped(DevToolUiSettings.T(
            "重置只会恢复原版/继承关系，不会写入一个替代值。",
            "Reset reveals the original/inherited Rain World relationship; it does not write a replacement value."));
    }

    private static void DrawRelationButton(
        EditorRelationshipPresentationSnapshot snapshot,
        EditorRelationshipRowSnapshot row,
        string label,
        EditorRelationshipDirection direction,
        float width)
    {
        bool selected = row.Selected && snapshot.SelectedDirection == direction;
        if (selected) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
        if (ImGui.Button(label, new Num.Vector2(width, 0f)))
        {
            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                RelationshipEditorCommandKind.SelectPair,
                other: row.CreatureType,
                direction: direction));
        }
        if (selected) ImGui.PopStyleVar();
    }

    private static void SendType(EditorRelationshipPresentationSnapshot snapshot, string other, string type)
    {
        RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
            RelationshipEditorCommandKind.SetRelationshipType,
            primary: snapshot.PrimaryCreature,
            other: other,
            text: type,
            direction: snapshot.SelectedDirection));
    }

    private static EditorRelationshipRowSnapshot FindRow(EditorRelationshipPresentationSnapshot snapshot, string type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        EditorRelationshipRowSnapshot[] rows = snapshot.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        for (int i = 0; i < rows.Length; i++)
            if (string.Equals(rows[i].CreatureType, type, StringComparison.Ordinal)) return rows[i];
        return null;
    }

    private static void EnsureCreatureLabels(string[] creatures)
    {
        if (ReferenceEquals(projectedCreatureTypes, creatures)) return;
        string[] labels = new string[creatures.Length];
        for (int i = 0; i < creatures.Length; i++)
            labels[i] = (creatures[i] ?? string.Empty) + "##RelationshipPrimary" + i;
        projectedCreatureTypes = creatures;
        projectedCreatureLabels = labels;
    }

    private static void EnsureMatrixRows(EditorRelationshipRowSnapshot[] rows)
    {
        if (ReferenceEquals(projectedRowsSource, rows)) return;
        MatrixRowPresentation[] next = new MatrixRowPresentation[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            EditorRelationshipRowSnapshot row = rows[i];
            EditorRelationshipValueSnapshot forward = row?.PrimaryToOther ?? EmptyRelationship;
            EditorRelationshipValueSnapshot reverse = row?.OtherToPrimary ?? EmptyRelationship;
            string creatureType = row?.CreatureType ?? string.Empty;
            next[i] = new MatrixRowPresentation
            {
                Row = row,
                Forward = forward,
                Reverse = reverse,
                Name = string.IsNullOrEmpty(row?.DisplayName) ? creatureType : row.DisplayName,
                ForwardLabel = BuildRelationLabel(forward, "F", creatureType),
                ReverseLabel = BuildRelationLabel(reverse, "R", creatureType)
            };
        }
        projectedRowsSource = rows;
        projectedRows = next;
        projectedMatrixFilter = null;
        projectedVisibleRows = Array.Empty<MatrixRowPresentation>();
    }

    private static void EnsureVisibleMatrixRows(string matrixQuery, bool onlyChanged)
    {
        matrixQuery ??= string.Empty;
        if (string.Equals(projectedMatrixFilter, matrixQuery, StringComparison.Ordinal) &&
            projectedMatrixChangedOnly == onlyChanged)
            return;

        int count = 0;
        for (int i = 0; i < projectedRows.Length; i++)
        {
            MatrixRowPresentation presentation = projectedRows[i];
            EditorRelationshipRowSnapshot row = presentation.Row;
            if (row == null) continue;
            if (!Matches(row.CreatureType, matrixQuery) && !Matches(row.DisplayName, matrixQuery)) continue;
            if (onlyChanged && !presentation.Forward.DirectOverride && !presentation.Reverse.DirectOverride) continue;
            count++;
        }

        MatrixRowPresentation[] next = new MatrixRowPresentation[count];
        int write = 0;
        for (int i = 0; i < projectedRows.Length; i++)
        {
            MatrixRowPresentation presentation = projectedRows[i];
            EditorRelationshipRowSnapshot row = presentation.Row;
            if (row == null) continue;
            if (!Matches(row.CreatureType, matrixQuery) && !Matches(row.DisplayName, matrixQuery)) continue;
            if (onlyChanged && !presentation.Forward.DirectOverride && !presentation.Reverse.DirectOverride) continue;
            next[write++] = presentation;
        }

        projectedMatrixFilter = matrixQuery;
        projectedMatrixChangedOnly = onlyChanged;
        projectedVisibleRows = next;
    }

    private static string BuildRelationLabel(
        EditorRelationshipValueSnapshot value,
        string directionId,
        string creatureType)
    {
        string text = (string.IsNullOrEmpty(value.Type) ? "?" : value.Type) + "  " + value.Intensity.ToString("0.00");
        if (value.DirectOverride) text += "  *";
        return text + "##Relationship" + directionId + creatureType;
    }

    private static string GetInspectorEditKey(
        string primary,
        string other,
        EditorRelationshipDirection direction)
    {
        primary ??= string.Empty;
        other ??= string.Empty;
        if (string.Equals(inspectorEditPrimary, primary, StringComparison.Ordinal) &&
            string.Equals(inspectorEditOther, other, StringComparison.Ordinal) &&
            inspectorEditDirection == direction &&
            !string.IsNullOrEmpty(inspectorEditKey))
            return inspectorEditKey;

        inspectorEditPrimary = primary;
        inspectorEditOther = other;
        inspectorEditDirection = direction;
        inspectorEditKey = primary + ">" + other + ":" + direction;
        return inspectorEditKey;
    }

    private static string PrimarySearchQuery()
    {
        if (string.Equals(observedPrimarySearch, primarySearch, StringComparison.Ordinal)) return normalizedPrimarySearch;
        observedPrimarySearch = primarySearch;
        normalizedPrimarySearch = primarySearch?.Trim() ?? string.Empty;
        return normalizedPrimarySearch;
    }

    private static string MatrixSearchQuery()
    {
        if (string.Equals(observedMatrixSearch, matrixSearch, StringComparison.Ordinal)) return normalizedMatrixSearch;
        observedMatrixSearch = matrixSearch;
        normalizedMatrixSearch = matrixSearch?.Trim() ?? string.Empty;
        return normalizedMatrixSearch;
    }

    private static bool Matches(string value, string normalizedQuery) =>
        string.IsNullOrEmpty(normalizedQuery) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) >= 0);
}
