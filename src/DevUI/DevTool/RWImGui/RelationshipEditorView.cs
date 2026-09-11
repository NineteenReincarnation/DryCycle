using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Relationships;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class RelationshipEditorView
{
    private static string primarySearch = string.Empty;
    private static string matrixSearch = string.Empty;
    private static bool changedOnly;
    private static readonly Dictionary<string, float> IntensityEdits = new(StringComparer.Ordinal);

    internal static void DrawBrowser(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Relationship editor unavailable.");
            return;
        }

        ImGui.TextDisabled("PRIMARY CREATURE");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search##RelationshipPrimarySearch", ref primarySearch, 128);
        ImGui.Separator();

        string[] creatures = snapshot.CreatureTypes ?? Array.Empty<string>();
        int matches = 0;
        for (int i = 0; i < creatures.Length; i++)
        {
            string type = creatures[i];
            if (!Matches(type, primarySearch)) continue;
            matches++;
            bool selected = string.Equals(type, snapshot.PrimaryCreature, StringComparison.Ordinal);
            if (ImGui.Selectable(type + "##RelationshipPrimary" + i, selected))
            {
                RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                    RelationshipEditorCommandKind.SelectPrimary,
                    primary: type));
                IntensityEdits.Clear();
            }
            if (selected) ImGui.SetItemDefaultFocus();
        }
        if (matches == 0) ImGui.TextDisabled("No matching creatures.");

        ImGui.Separator();
        ImGui.TextDisabled("MATRIX FILTER");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Search rows##RelationshipMatrixSearch", ref matrixSearch, 128);
        ImGui.Checkbox("Changed only##RelationshipChangedOnly", ref changedOnly);
        ImGui.TextDisabled("* = direct override");
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
        ImGui.SetNextWindowBgAlpha(0.98f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin("Relationships###DevToolRelationshipMatrix", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("Primary: " + (string.IsNullOrEmpty(snapshot.PrimaryCreature) ? "<none>" : snapshot.PrimaryCreature));
        ImGui.SameLine();
        ImGui.TextDisabled("click either direction to inspect/edit");
        ImGui.Separator();

        float available = ImGui.GetContentRegionAvail().X;
        float nameWidth = Math.Max(120f, Math.Min(220f, available * 0.27f));
        float relationWidth = Math.Max(150f, (available - nameWidth - 28f) * 0.5f);

        ImGui.TextDisabled("Creature");
        ImGui.SameLine(nameWidth);
        ImGui.TextDisabled(snapshot.PrimaryCreature + "  →  Other");
        ImGui.SameLine(nameWidth + relationWidth + 12f);
        ImGui.TextDisabled("Other  →  " + snapshot.PrimaryCreature);
        ImGui.Separator();

        EditorRelationshipRowSnapshot[] rows = snapshot.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        int visible = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            EditorRelationshipRowSnapshot row = rows[i];
            if (!Matches(row.CreatureType, matrixSearch) && !Matches(row.DisplayName, matrixSearch)) continue;
            if (changedOnly && row.PrimaryToOther?.DirectOverride != true && row.OtherToPrimary?.DirectOverride != true) continue;
            visible++;

            float startX = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            string name = string.IsNullOrEmpty(row.DisplayName) ? row.CreatureType : row.DisplayName;
            ImGui.TextUnformatted(name);
            if (ImGui.IsItemHovered() && !string.Equals(name, row.CreatureType, StringComparison.Ordinal))
                ImGui.SetTooltip(row.CreatureType);

            ImGui.SameLine(startX + nameWidth);
            DrawRelationButton(snapshot, row, EditorRelationshipDirection.PrimaryToOther, relationWidth);
            ImGui.SameLine(startX + nameWidth + relationWidth + 12f);
            DrawRelationButton(snapshot, row, EditorRelationshipDirection.OtherToPrimary, relationWidth);
        }

        if (visible == 0)
            ImGui.TextDisabled("No relationships match the current filter.");

        ImGui.End();
    }

    internal static void DrawInspector(EditorRelationshipPresentationSnapshot snapshot)
    {
        if (!snapshot.Available)
        {
            ImGui.TextDisabled("Relationship editor unavailable.");
            return;
        }

        EditorRelationshipRowSnapshot row = FindRow(snapshot, snapshot.SelectedOtherCreature);
        if (row == null)
        {
            ImGui.TextDisabled("Select one direction from the relationship matrix.");
            return;
        }

        bool forward = snapshot.SelectedDirection == EditorRelationshipDirection.PrimaryToOther;
        string from = forward ? snapshot.PrimaryCreature : row.CreatureType;
        string to = forward ? row.CreatureType : snapshot.PrimaryCreature;
        EditorRelationshipValueSnapshot relationship = forward ? row.PrimaryToOther : row.OtherToPrimary;

        ImGui.Text(from);
        ImGui.SameLine();
        ImGui.TextDisabled("→");
        ImGui.SameLine();
        ImGui.Text(to);
        ImGui.TextDisabled(relationship.DirectOverride ? "Direct override" : "Effective / inherited value");
        ImGui.Separator();

        string[] types = snapshot.RelationshipTypes ?? Array.Empty<string>();
        string currentType = relationship.Type ?? string.Empty;
        if (ImGui.BeginCombo("Relationship##RelationshipType", currentType))
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

        string editKey = snapshot.PrimaryCreature + ">" + row.CreatureType + ":" + snapshot.SelectedDirection;
        float intensity = GetIntensity(editKey, relationship.Intensity);
        bool changed = ImGui.SliderFloat("Intensity##RelationshipIntensity", ref intensity, 0f, 1f, "%.3f");
        IntensityEdits[editKey] = intensity;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                RelationshipEditorCommandKind.SetRelationshipIntensity,
                primary: snapshot.PrimaryCreature,
                other: row.CreatureType,
                value: intensity,
                direction: snapshot.SelectedDirection));
        }
        else if (!changed && !ImGui.IsItemActive())
        {
            IntensityEdits[editKey] = relationship.Intensity;
        }

        ImGui.Separator();
        bool canReset = relationship.DirectOverride;
        if (!canReset) ImGui.BeginDisabled();
        if (ImGui.Button("Reset Override"))
        {
            RelationshipEditorCommandQueue.Enqueue(new RelationshipEditorCommand(
                RelationshipEditorCommandKind.ResetRelationship,
                primary: snapshot.PrimaryCreature,
                other: row.CreatureType,
                direction: snapshot.SelectedDirection));
            IntensityEdits.Remove(editKey);
        }
        if (!canReset) ImGui.EndDisabled();

        ImGui.TextDisabled("Reset reveals the original/inherited Rain World relationship; it does not write a replacement value.");
    }

    private static void DrawRelationButton(
        EditorRelationshipPresentationSnapshot snapshot,
        EditorRelationshipRowSnapshot row,
        EditorRelationshipDirection direction,
        float width)
    {
        EditorRelationshipValueSnapshot value = direction == EditorRelationshipDirection.PrimaryToOther
            ? row.PrimaryToOther
            : row.OtherToPrimary;
        value ??= new EditorRelationshipValueSnapshot();

        bool selected = row.Selected && snapshot.SelectedDirection == direction;
        if (selected) ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
        string text = (string.IsNullOrEmpty(value.Type) ? "?" : value.Type) + "  " + value.Intensity.ToString("0.00");
        if (value.DirectOverride) text += "  *";
        string id = direction == EditorRelationshipDirection.PrimaryToOther ? "F" : "R";
        if (ImGui.Button(text + "##Relationship" + id + row.CreatureType, new Num.Vector2(width, 0f)))
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

    private static float GetIntensity(string key, float fallback)
    {
        if (IntensityEdits.TryGetValue(key, out float value)) return value;
        IntensityEdits[key] = fallback;
        return fallback;
    }

    private static bool Matches(string value, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
}
