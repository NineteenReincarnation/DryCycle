using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Relationships;

public enum EditorRelationshipDirection
{
    PrimaryToOther,
    OtherToPrimary
}

public sealed class EditorRelationshipValueSnapshot
{
    public string Type { get; init; } = string.Empty;
    public float Intensity { get; init; }
    public bool DirectOverride { get; init; }
}

public sealed class EditorRelationshipRowSnapshot
{
    public string CreatureType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Selected { get; init; }
    public EditorRelationshipValueSnapshot PrimaryToOther { get; init; } = new();
    public EditorRelationshipValueSnapshot OtherToPrimary { get; init; } = new();
}

public sealed class EditorRelationshipPresentationSnapshot
{
    public static readonly EditorRelationshipPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string PrimaryCreature { get; init; } = string.Empty;
    public string SelectedOtherCreature { get; init; } = string.Empty;
    public EditorRelationshipDirection SelectedDirection { get; init; }
    public string[] CreatureTypes { get; init; } = Array.Empty<string>();
    public string[] RelationshipTypes { get; init; } = Array.Empty<string>();
    public EditorRelationshipRowSnapshot[] Rows { get; init; } = Array.Empty<EditorRelationshipRowSnapshot>();
}

internal sealed class RelationshipEditorState
{
    internal string PrimaryCreature = string.Empty;
    internal string SelectedOtherCreature = string.Empty;
    internal EditorRelationshipDirection SelectedDirection = EditorRelationshipDirection.PrimaryToOther;
}

internal static class RelationshipEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, RelationshipEditorState> states = new();

    internal static RelationshipEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new RelationshipEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, RelationshipEditorState>();
}

public static class RelationshipEditorPresentationHub
{
    private static volatile EditorRelationshipPresentationSnapshot current = EditorRelationshipPresentationSnapshot.Empty;
    private static string[] relationshipTypes = Array.Empty<string>();
    private static int relationshipTypeCount = -1;

    private static EditorSession observedSession;
    private static RelationshipPage observedPage;
    private static long observedRevision;
    private static int observedCreatureTypeCount = -1;
    private static string observedPrimary = string.Empty;
    private static string observedOther = string.Empty;
    private static EditorRelationshipDirection observedDirection;

    public static EditorRelationshipPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Relationships ||
            session.Owner?.activePage is not RelationshipPage page)
        {
            Clear();
            return;
        }

        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
            EditorRevisionHub.Mark(session, EditorRevisionKind.Relationships);

        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Relationships);
        int creatureTypeCount = ExtEnum<CreatureTemplate.Type>.values.Count;
        int nextRelationshipTypeCount = ExtEnum<CreatureTemplate.Relationship.Type>.values.Count;

        bool sameIdentity =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedPage, page) &&
            current.Available;
        bool modelStable =
            sameIdentity &&
            observedRevision == revision &&
            observedCreatureTypeCount == creatureTypeCount &&
            relationshipTypeCount == nextRelationshipTypeCount;
        bool primaryStable =
            modelStable &&
            string.Equals(observedPrimary, state.PrimaryCreature, StringComparison.Ordinal);

        if (primaryStable &&
            string.Equals(observedOther, state.SelectedOtherCreature, StringComparison.Ordinal) &&
            observedDirection == state.SelectedDirection)
        {
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Relationships,
                DevToolPresentationOutcome.CacheHit);
            return;
        }

        // Pair/direction selection does not affect relationship values. Preserve the entire matrix
        // and replace only the old/new Selected rows. Changing the primary creature is different: it
        // changes every effective relationship and therefore correctly falls through to a full build.
        if (primaryStable)
        {
            PublishSelectionOnly(state);
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Relationships,
                DevToolPresentationOutcome.PartialRebuild);
            return;
        }

        List<CreatureTemplate> templates = CollectTemplates();
        if (templates.Count == 0)
        {
            current = new EditorRelationshipPresentationSnapshot { Available = true };
            Observe(session, page, revision, creatureTypeCount, state);
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Relationships,
                DevToolPresentationOutcome.FullRebuild);
            return;
        }

        CreatureTemplate primary = FindTemplate(templates, state.PrimaryCreature) ?? templates[0];
        state.PrimaryCreature = primary.type.value;

        if (!string.IsNullOrEmpty(state.SelectedOtherCreature) &&
            FindTemplate(templates, state.SelectedOtherCreature) == null)
            state.SelectedOtherCreature = string.Empty;

        string[] creatureTypes = new string[templates.Count];
        List<EditorRelationshipRowSnapshot> rows = new(Math.Max(0, templates.Count - 1));
        for (int i = 0; i < templates.Count; i++)
        {
            CreatureTemplate other = templates[i];
            creatureTypes[i] = other.type.value;
            if (other.type == primary.type) continue;

            rows.Add(new EditorRelationshipRowSnapshot
            {
                CreatureType = other.type.value,
                DisplayName = string.IsNullOrEmpty(other.name) ? other.type.value : other.name,
                Selected = string.Equals(state.SelectedOtherCreature, other.type.value, StringComparison.Ordinal),
                PrimaryToOther = Capture(primary, other),
                OtherToPrimary = Capture(other, primary)
            });
        }

        RebuildRelationshipTypesIfNeeded();

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = true,
            PrimaryCreature = state.PrimaryCreature,
            SelectedOtherCreature = state.SelectedOtherCreature,
            SelectedDirection = state.SelectedDirection,
            CreatureTypes = creatureTypes,
            RelationshipTypes = relationshipTypes,
            Rows = rows.ToArray()
        };

        Observe(session, page, revision, creatureTypeCount, state);
        DevToolPerformanceMonitor.RecordPresentation(
            DevToolPresentationChannel.Relationships,
            DevToolPresentationOutcome.FullRebuild);
    }

    private static void PublishSelectionOnly(RelationshipEditorState state)
    {
        EditorRelationshipRowSnapshot[] source = current.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        EditorRelationshipRowSnapshot[] next = null;
        string selectedOther = state?.SelectedOtherCreature ?? string.Empty;

        for (int i = 0; i < source.Length; i++)
        {
            EditorRelationshipRowSnapshot row = source[i];
            bool selected = string.Equals(row.CreatureType, selectedOther, StringComparison.Ordinal);
            if (row.Selected == selected) continue;

            next ??= (EditorRelationshipRowSnapshot[])source.Clone();
            next[i] = new EditorRelationshipRowSnapshot
            {
                CreatureType = row.CreatureType,
                DisplayName = row.DisplayName,
                Selected = selected,
                PrimaryToOther = row.PrimaryToOther,
                OtherToPrimary = row.OtherToPrimary
            };
        }

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = current.Available,
            PrimaryCreature = current.PrimaryCreature,
            SelectedOtherCreature = selectedOther,
            SelectedDirection = state?.SelectedDirection ?? EditorRelationshipDirection.PrimaryToOther,
            CreatureTypes = current.CreatureTypes,
            RelationshipTypes = current.RelationshipTypes,
            Rows = next ?? source
        };

        observedOther = selectedOther;
        observedDirection = state?.SelectedDirection ?? EditorRelationshipDirection.PrimaryToOther;
    }

    internal static void Clear()
    {
        current = EditorRelationshipPresentationSnapshot.Empty;
        observedSession = null;
        observedPage = null;
        observedRevision = 0L;
        observedCreatureTypeCount = -1;
        observedPrimary = string.Empty;
        observedOther = string.Empty;
        observedDirection = EditorRelationshipDirection.PrimaryToOther;
    }

    private static void Observe(
        EditorSession session,
        RelationshipPage page,
        long revision,
        int creatureTypeCount,
        RelationshipEditorState state)
    {
        observedSession = session;
        observedPage = page;
        observedRevision = revision;
        observedCreatureTypeCount = creatureTypeCount;
        observedPrimary = state?.PrimaryCreature ?? string.Empty;
        observedOther = state?.SelectedOtherCreature ?? string.Empty;
        observedDirection = state?.SelectedDirection ?? EditorRelationshipDirection.PrimaryToOther;
    }

    private static EditorRelationshipValueSnapshot Capture(CreatureTemplate from, CreatureTemplate to)
    {
        CreatureTemplate.Relationship effective = RelationshipPage.GetEffectiveRelationship(from, to);
        bool direct = RelationshipPage.changedRelationships.TryGetValue(from.type, out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> changed) &&
                      changed.ContainsKey(to.type);
        return new EditorRelationshipValueSnapshot
        {
            Type = effective.type?.value ?? string.Empty,
            Intensity = effective.intensity,
            DirectOverride = direct
        };
    }

    private static List<CreatureTemplate> CollectTemplates()
    {
        List<CreatureTemplate> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < ExtEnum<CreatureTemplate.Type>.values.Count; i++)
        {
            string entry = ExtEnum<CreatureTemplate.Type>.values.GetEntry(i);
            if (string.IsNullOrEmpty(entry) || !seen.Add(entry)) continue;
            try
            {
                CreatureTemplate.Type type = new(entry, false);
                CreatureTemplate template = StaticWorld.GetCreatureTemplate(type);
                if (template?.type == null) continue;
                result.Add(template);
            }
            catch
            {
                // An ExtEnum entry may be registered before its template exists. It is safer
                // to skip it until StaticWorld can resolve it than to manufacture editor data.
            }
        }

        result.Sort((a, b) => string.Compare(a?.type?.value, b?.type?.value, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static CreatureTemplate FindTemplate(List<CreatureTemplate> templates, string typeName)
    {
        if (templates == null || string.IsNullOrEmpty(typeName)) return null;
        for (int i = 0; i < templates.Count; i++)
        {
            CreatureTemplate template = templates[i];
            if (template?.type != null && string.Equals(template.type.value, typeName, StringComparison.Ordinal))
                return template;
        }
        return null;
    }

    private static void RebuildRelationshipTypesIfNeeded()
    {
        int count = ExtEnum<CreatureTemplate.Relationship.Type>.values.Count;
        if (count == relationshipTypeCount) return;
        relationshipTypes = new string[count];
        for (int i = 0; i < count; i++)
            relationshipTypes[i] = ExtEnum<CreatureTemplate.Relationship.Type>.values.GetEntry(i);
        relationshipTypeCount = count;
    }
}

public enum RelationshipEditorCommandKind
{
    SelectPrimary,
    SelectPair,
    SetRelationshipType,
    SetRelationshipIntensity,
    ResetRelationship
}

public readonly struct RelationshipEditorCommand
{
    public RelationshipEditorCommand(
        RelationshipEditorCommandKind kind,
        string primary = null,
        string other = null,
        string text = null,
        float value = 0f,
        EditorRelationshipDirection direction = EditorRelationshipDirection.PrimaryToOther)
    {
        Kind = kind;
        Primary = primary;
        Other = other;
        Text = text;
        Value = value;
        Direction = direction;
    }

    public RelationshipEditorCommandKind Kind { get; }
    public string Primary { get; }
    public string Other { get; }
    public string Text { get; }
    public float Value { get; }
    public EditorRelationshipDirection Direction { get; }
}

public static class RelationshipEditorCommandQueue
{
    private static readonly ConcurrentQueue<RelationshipEditorCommand> queue = new();

    public static void Enqueue(RelationshipEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        long historyBeforeBatch = session?.History.Revision ?? 0L;
        bool nonHistoryDirty = false;

        while (queue.TryDequeue(out RelationshipEditorCommand command))
        {
            try
            {
                long historyBeforeCommand = session?.History.Revision ?? 0L;
                bool changed = false;
                bool modelCommand = false;

                switch (command.Kind)
                {
                    case RelationshipEditorCommandKind.SelectPrimary:
                    {
                        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
                        string before = state?.PrimaryCreature ?? string.Empty;
                        RelationshipEditorActions.SelectPrimary(session, command.Primary);
                        changed = !string.Equals(before, state?.PrimaryCreature ?? string.Empty, StringComparison.Ordinal);
                        break;
                    }
                    case RelationshipEditorCommandKind.SelectPair:
                    {
                        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
                        string otherBefore = state?.SelectedOtherCreature ?? string.Empty;
                        EditorRelationshipDirection directionBefore =
                            state?.SelectedDirection ?? EditorRelationshipDirection.PrimaryToOther;
                        RelationshipEditorActions.SelectPair(session, command.Other, command.Direction);
                        changed = !string.Equals(otherBefore, state?.SelectedOtherCreature ?? string.Empty, StringComparison.Ordinal) ||
                                  directionBefore != (state?.SelectedDirection ?? EditorRelationshipDirection.PrimaryToOther);
                        break;
                    }
                    case RelationshipEditorCommandKind.SetRelationshipType:
                        changed = RelationshipEditorActions.SetType(session, command.Primary, command.Other, command.Direction, command.Text);
                        modelCommand = true;
                        break;
                    case RelationshipEditorCommandKind.SetRelationshipIntensity:
                        changed = RelationshipEditorActions.SetIntensity(session, command.Primary, command.Other, command.Direction, command.Value);
                        modelCommand = true;
                        break;
                    case RelationshipEditorCommandKind.ResetRelationship:
                        changed = RelationshipEditorActions.Reset(session, command.Primary, command.Other, command.Direction);
                        modelCommand = true;
                        break;
                }

                // Primary/pair selection is explicit presentation state and is observed directly by
                // RelationshipEditorPresentationHub. Only model mutations need the workspace clock.
                if (modelCommand && changed && (session?.History.Revision ?? 0L) == historyBeforeCommand)
                    nonHistoryDirty = true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool relationship command failed: " + error.Message);
            }
        }

        if (nonHistoryDirty && (session?.History.Revision ?? 0L) == historyBeforeBatch)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Relationships);
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
