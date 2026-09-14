using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
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
        return SetRelationship(session, from, to, primaryType, otherType, next, "Change relationship type");
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
        return SetRelationship(session, from, to, primaryType, otherType, next, "Change relationship intensity");
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

        SingleRelationshipStateSnapshot before =
            SingleRelationshipStateSnapshot.Capture(from.type, to.type, primaryType, otherType);
        RelationshipPage.ResetChangedRelationship(from.type, to.type);
        SingleRelationshipStateSnapshot after =
            SingleRelationshipStateSnapshot.Capture(from.type, to.type, primaryType, otherType);
        if (!SnapshotHistoryEntry.TryCreate("Reset relationship override", before, after, out SnapshotHistoryEntry entry))
            return false;

        RefreshPage(session);
        session.History.Push(entry);
        return true;
    }

    private static bool SetRelationship(
        EditorSession session,
        CreatureTemplate from,
        CreatureTemplate to,
        string primaryType,
        string otherType,
        CreatureTemplate.Relationship relationship,
        string label)
    {
        if (session?.Owner?.activePage is not RelationshipPage || from?.type == null || to?.type == null)
            return false;

        SingleRelationshipStateSnapshot before =
            SingleRelationshipStateSnapshot.Capture(from.type, to.type, primaryType, otherType);
        RelationshipPage.SetChangedRelationship(from.type, to.type, relationship);
        SingleRelationshipStateSnapshot after =
            SingleRelationshipStateSnapshot.Capture(from.type, to.type, primaryType, otherType);
        if (!SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            return false;

        RefreshPage(session);
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

        // The rebuilt relationship matrix owns presentation while the vanilla page is quiescent.
        // Rebuilding the complete hidden RelationshipPage for every slider step defeats the pair-
        // level snapshot/presentation path, so mark it stale and materialize once on legacy return.
        // Opaque third-party subtrees make TryDeferRefresh fail closed and retain vanilla Refresh.
        if (LegacyDevUiQuiescenceController.TryDeferRefresh(session))
        {
            page.refresh = false;
            return;
        }

        try
        {
            // Refresh immediately on the compatibility path; do not also leave RelationshipPage's
            // one-shot refresh flag armed or vanilla Update would rebuild the page a second time.
            page.refresh = false;
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool relationship refresh failed: " + error.Message);
        }
    }

    /// <summary>
    /// Normal relationship edits own exactly one directed override. Capturing the complete global
    /// changedRelationships dictionary on every intensity slider step made history O(all overrides)
    /// and forced Undo/Redo through the full presentation path. This member snapshot preserves the
    /// direct-override bit and value for one direction while retaining the UI pair used by the row
    /// hint, so edit/undo/redo all stay O(1) model work plus one-row presentation recapture.
    /// </summary>
    private sealed class SingleRelationshipStateSnapshot : IEditorStateSnapshot
    {
        private readonly CreatureTemplate.Type from;
        private readonly CreatureTemplate.Type to;
        private readonly string hintPrimary;
        private readonly string hintOther;
        private readonly bool present;
        private readonly CreatureTemplate.Relationship.Type relationshipType;
        private readonly float intensity;

        private SingleRelationshipStateSnapshot(
            CreatureTemplate.Type from,
            CreatureTemplate.Type to,
            string hintPrimary,
            string hintOther,
            bool present,
            CreatureTemplate.Relationship.Type relationshipType,
            float intensity)
        {
            this.from = from;
            this.to = to;
            this.hintPrimary = hintPrimary ?? string.Empty;
            this.hintOther = hintOther ?? string.Empty;
            this.present = present;
            this.relationshipType = relationshipType;
            this.intensity = intensity;
            Fingerprint = present
                ? "1|" + (relationshipType?.value ?? string.Empty) + "|" +
                  intensity.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "0";
        }

        public string Kind =>
            "Relationship:" + (from?.value ?? string.Empty) + ">" + (to?.value ?? string.Empty);

        public string Fingerprint { get; }

        internal static SingleRelationshipStateSnapshot Capture(
            CreatureTemplate.Type from,
            CreatureTemplate.Type to,
            string hintPrimary,
            string hintOther)
        {
            if (from == null || to == null) return null;

            bool present =
                RelationshipPage.changedRelationships.TryGetValue(from, out var changed) &&
                changed != null &&
                changed.TryGetValue(to, out CreatureTemplate.Relationship relationship) &&
                relationship != null;

            return new SingleRelationshipStateSnapshot(
                from,
                to,
                hintPrimary,
                hintOther,
                present,
                present ? relationship.type : null,
                present ? relationship.intensity : 0f);
        }

        public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
            session?.Owner?.activePage is RelationshipPage
                ? Capture(from, to, hintPrimary, hintOther)
                : null;

        public bool Restore(EditorSession session)
        {
            if (session?.Owner?.activePage is not RelationshipPage)
                return false;

            if (present)
            {
                if (relationshipType == null) return false;
                RelationshipPage.SetChangedRelationship(
                    from,
                    to,
                    new CreatureTemplate.Relationship(relationshipType, intensity));
            }
            else
            {
                RelationshipPage.ResetChangedRelationship(from, to);
            }

            RelationshipPresentationChangeHintHub.MarkPair(session, hintPrimary, hintOther);
            RefreshPage(session);
            return true;
        }
    }
}
