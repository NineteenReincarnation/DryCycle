using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Relationships;

internal static class RelationshipEditorActions
{
    internal static void SelectPrimary(EditorSession session, string typeName)
    {
        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
        if (state == null || ResolveTemplate(typeName) == null) return;
        state.PrimaryCreature = typeName;
        if (string.Equals(state.SelectedOtherCreature, typeName, StringComparison.Ordinal))
            state.SelectedOtherCreature = string.Empty;
    }

    internal static void SelectPair(EditorSession session, string otherType, EditorRelationshipDirection direction)
    {
        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
        if (state == null || ResolveTemplate(otherType) == null) return;
        if (string.Equals(state.PrimaryCreature, otherType, StringComparison.Ordinal)) return;
        state.SelectedOtherCreature = otherType;
        state.SelectedDirection = direction;
    }

    internal static bool SetType(
        EditorSession session,
        string primaryType,
        string otherType,
        EditorRelationshipDirection direction,
        string relationshipType)
    {
        if (string.IsNullOrEmpty(relationshipType) || !TryResolvePair(primaryType, otherType, direction, out CreatureTemplate from, out CreatureTemplate to))
            return false;

        CreatureTemplate.Relationship current = RelationshipPage.GetEffectiveRelationship(from, to);
        CreatureTemplate.Relationship next = new(new CreatureTemplate.Relationship.Type(relationshipType, false), current.intensity);
        return SetRelationship(session, from, to, next, "Change relationship type");
    }

    internal static bool SetIntensity(
        EditorSession session,
        string primaryType,
        string otherType,
        EditorRelationshipDirection direction,
        float intensity)
    {
        if (!TryResolvePair(primaryType, otherType, direction, out CreatureTemplate from, out CreatureTemplate to))
            return false;

        CreatureTemplate.Relationship current = RelationshipPage.GetEffectiveRelationship(from, to);
        CreatureTemplate.Relationship next = new(current.type, Mathf.Clamp01(intensity));
        return SetRelationship(session, from, to, next, "Change relationship intensity");
    }

    internal static bool Reset(
        EditorSession session,
        string primaryType,
        string otherType,
        EditorRelationshipDirection direction)
    {
        if (!TryResolvePair(primaryType, otherType, direction, out CreatureTemplate from, out CreatureTemplate to))
            return false;

        if (!RelationshipPage.changedRelationships.TryGetValue(from.type, out var changed) || !changed.ContainsKey(to.type))
            return false;

        RelationshipStateSnapshot before = RelationshipStateSnapshot.Capture();
        RelationshipPage.ResetChangedRelationship(from.type, to.type);
        RefreshPage(session);
        RelationshipStateSnapshot after = RelationshipStateSnapshot.Capture();
        if (SnapshotHistoryEntry.TryCreate("Reset relationship override", before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static bool SetRelationship(
        EditorSession session,
        CreatureTemplate from,
        CreatureTemplate to,
        CreatureTemplate.Relationship relationship,
        string label)
    {
        if (session?.Owner?.activePage is not RelationshipPage || from?.type == null || to?.type == null)
            return false;

        RelationshipStateSnapshot before = RelationshipStateSnapshot.Capture();
        RelationshipPage.SetChangedRelationship(from.type, to.type, relationship);
        RefreshPage(session);
        RelationshipStateSnapshot after = RelationshipStateSnapshot.Capture();
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static bool TryResolvePair(
        string primaryType,
        string otherType,
        EditorRelationshipDirection direction,
        out CreatureTemplate from,
        out CreatureTemplate to)
    {
        CreatureTemplate primary = ResolveTemplate(primaryType);
        CreatureTemplate other = ResolveTemplate(otherType);
        if (primary == null || other == null || primary.type == other.type)
        {
            from = null;
            to = null;
            return false;
        }

        if (direction == EditorRelationshipDirection.PrimaryToOther)
        {
            from = primary;
            to = other;
        }
        else
        {
            from = other;
            to = primary;
        }
        return true;
    }

    private static CreatureTemplate ResolveTemplate(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        try
        {
            return StaticWorld.GetCreatureTemplate(new CreatureTemplate.Type(typeName, false));
        }
        catch
        {
            return null;
        }
    }

    private static void RefreshPage(EditorSession session)
    {
        if (session?.Owner?.activePage is not RelationshipPage page) return;
        try
        {
            page.refresh = true;
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool relationship refresh failed: " + error.Message);
        }
    }
}
