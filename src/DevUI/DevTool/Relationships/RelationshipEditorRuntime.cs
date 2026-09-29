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
    private sealed class PrimaryRowsCacheEntry
    {
        internal long Revision;
        internal EditorRelationshipRowSnapshot[] Rows = Array.Empty<EditorRelationshipRowSnapshot>();
    }

    private const int MaxPrimaryRowsCacheEntries = 12;

    private static string[] relationshipTypes = Array.Empty<string>();
    private static int relationshipTypeCount = -1;
    private static CreatureTemplate[] templateCatalog = Array.Empty<CreatureTemplate>();
    private static string[] creatureTypeCatalog = Array.Empty<string>();
    private static Dictionary<string, CreatureTemplate> templateByType =
        new(StringComparer.Ordinal);
    private static int templateCatalogTypeCount = -1;
    private static readonly Dictionary<string, PrimaryRowsCacheEntry> primaryRowsCache =
        new(StringComparer.Ordinal);
    private static readonly Queue<string> primaryRowsCacheOrder = new();

    private static EditorSession observedSession;
    private static RelationshipPage observedPage;
    private static long observedRevision;
    private static int observedCreatureTypeCount = -1;
    private static string observedPrimary = string.Empty;
    private static string observedOther = string.Empty;
    private static EditorRelationshipDirection observedDirection;

    public static EditorRelationshipPresentationSnapshot Current => current;
    internal static DevToolPresentationOutcome LastOutcome { get; private set; } = DevToolPresentationOutcome.FullRebuild;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Relationships ||
            session.Owner?.activePage is not RelationshipPage page)
        {
            Clear();
            return;
        }

        if (!ReferenceEquals(
                observedSession,
                session) ||
            !ReferenceEquals(
                observedPage,
                page))
        {
            primaryRowsCache.Clear();
            primaryRowsCacheOrder.Clear();
        }

        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Relationships);
            RelationshipPresentationChangeHintHub.MarkFull(session);
        }

        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Relationships);
        int creatureTypeCount = ExtEnum<CreatureTemplate.Type>.values.Count;
        int nextRelationshipTypeCount = ExtEnum<CreatureTemplate.Relationship.Type>.values.Count;

        bool sameIdentity =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedPage, page) &&
            current.Available;
        bool catalogStable =
            sameIdentity &&
            observedCreatureTypeCount == creatureTypeCount &&
            relationshipTypeCount == nextRelationshipTypeCount;
        bool primaryStable =
            catalogStable &&
            string.Equals(observedPrimary, state.PrimaryCreature, StringComparison.Ordinal);
        bool modelStable = primaryStable && observedRevision == revision;

        if (modelStable &&
            string.Equals(observedOther, state.SelectedOtherCreature, StringComparison.Ordinal) &&
            observedDirection == state.SelectedDirection)
        {
            LastOutcome = DevToolPresentationOutcome.CacheHit;
            return;
        }

        // Pair/direction selection does not affect relationship values. Preserve the entire matrix
        // and replace only the old/new Selected rows.
        if (modelStable)
        {
            PublishSelectionOnly(state);
            LastOutcome = DevToolPresentationOutcome.PartialRebuild;
            return;
        }

        bool modelChanged = sameIdentity && observedRevision != revision;
        RelationshipPresentationChangeHint hint = modelChanged
            ? RelationshipPresentationChangeHintHub.Consume(session)
            : default;

        // A type/intensity/reset edit changes exactly one directed relationship, but the UI row owns
        // both directions for one primary/other pair. Re-capture that one row and retain every other
        // row plus the static creature/relationship catalogs. Unknown history/legacy writers supply
        // no pair hint (or explicitly mark Full) and therefore fall through to the safe full build.
        if (primaryStable && hint.HasPair &&
            string.Equals(hint.Primary, state.PrimaryCreature, StringComparison.Ordinal) &&
            TryPublishPairOnly(state, hint.Other))
        {
            Observe(session, page, revision, creatureTypeCount, state);
            LastOutcome = DevToolPresentationOutcome.PartialRebuild;
            return;
        }

        EnsureTemplateCatalog(
            creatureTypeCount);
        CreatureTemplate[] templates =
            templateCatalog;
        if (templates.Length == 0)
        {
            current = new EditorRelationshipPresentationSnapshot { Available = true };
            Observe(session, page, revision, creatureTypeCount, state);
            LastOutcome = DevToolPresentationOutcome.FullRebuild;
            return;
        }

        CreatureTemplate primary =
            FindTemplate(state.PrimaryCreature) ??
            templates[0];
        state.PrimaryCreature =
            primary.type.value;

        if (!string.IsNullOrEmpty(state.SelectedOtherCreature) &&
            FindTemplate(state.SelectedOtherCreature) == null)
            state.SelectedOtherCreature =
                string.Empty;

        EditorRelationshipRowSnapshot[] baseRows =
            GetOrBuildPrimaryRows(
                primary,
                revision);
        EditorRelationshipRowSnapshot[] rows =
            ApplySelection(
                baseRows,
                state.SelectedOtherCreature);

        RebuildRelationshipTypesIfNeeded();

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = true,
            PrimaryCreature = state.PrimaryCreature,
            SelectedOtherCreature = state.SelectedOtherCreature,
            SelectedDirection = state.SelectedDirection,
            CreatureTypes = creatureTypeCatalog,
            RelationshipTypes = relationshipTypes,
            Rows = rows
        };

        Observe(session, page, revision, creatureTypeCount, state);
        LastOutcome = DevToolPresentationOutcome.FullRebuild;
    }

    private static bool TryPublishPairOnly(RelationshipEditorState state, string otherType)
    {
        if (state == null || string.IsNullOrEmpty(state.PrimaryCreature) || string.IsNullOrEmpty(otherType))
            return false;

        CreatureTemplate primary = ResolveTemplate(state.PrimaryCreature);
        CreatureTemplate other = ResolveTemplate(otherType);
        if (primary?.type == null || other?.type == null || primary.type == other.type)
            return false;

        EditorRelationshipRowSnapshot[] source = current.Rows ?? Array.Empty<EditorRelationshipRowSnapshot>();
        int rowIndex = -1;
        for (int i = 0; i < source.Length; i++)
        {
            if (!string.Equals(source[i]?.CreatureType, otherType, StringComparison.Ordinal)) continue;
            rowIndex = i;
            break;
        }
        if (rowIndex < 0) return false;

        EditorRelationshipRowSnapshot old = source[rowIndex];
        EditorRelationshipRowSnapshot[] rows = (EditorRelationshipRowSnapshot[])source.Clone();
        rows[rowIndex] = new EditorRelationshipRowSnapshot
        {
            CreatureType = other.type.value,
            DisplayName = string.IsNullOrEmpty(other.name) ? other.type.value : other.name,
            Selected = string.Equals(state.SelectedOtherCreature, other.type.value, StringComparison.Ordinal),
            PrimaryToOther = Capture(primary, other),
            OtherToPrimary = Capture(other, primary)
        };

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = current.Available,
            PrimaryCreature = state.PrimaryCreature,
            SelectedOtherCreature = state.SelectedOtherCreature,
            SelectedDirection = state.SelectedDirection,
            CreatureTypes = current.CreatureTypes,
            RelationshipTypes = current.RelationshipTypes,
            Rows = rows
        };
        return old != null;
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
        RelationshipPresentationChangeHintHub.Clear(observedSession);
        current = EditorRelationshipPresentationSnapshot.Empty;
        observedSession = null;
        observedPage = null;
        observedRevision = 0L;
        observedCreatureTypeCount = -1;
        observedPrimary = string.Empty;
        observedOther = string.Empty;
        observedDirection = EditorRelationshipDirection.PrimaryToOther;
        primaryRowsCache.Clear();
        primaryRowsCacheOrder.Clear();
        LastOutcome = DevToolPresentationOutcome.FullRebuild;
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

    private static EditorRelationshipValueSnapshot Capture(
        CreatureTemplate from,
        CreatureTemplate to)
    {
        RelationshipPage.changedRelationships.TryGetValue(
            from.type,
            out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> directMap);
        return Capture(
            from,
            to,
            directMap);
    }

    private static void EnsureTemplateCatalog(int creatureTypeCount)
    {
        if (templateCatalogTypeCount ==
            creatureTypeCount)
            return;

        List<CreatureTemplate> result =
            new();
        HashSet<string> seen =
            new(StringComparer.Ordinal);

        for (int i = 0; i < ExtEnum<CreatureTemplate.Type>.values.Count; i++)
        {
            string entry =
                ExtEnum<CreatureTemplate.Type>.values.GetEntry(i);
            if (string.IsNullOrEmpty(entry) ||
                !seen.Add(entry))
                continue;

            try
            {
                CreatureTemplate.Type type =
                    new(
                        entry,
                        false);
                CreatureTemplate template =
                    StaticWorld.GetCreatureTemplate(type);
                if (template?.type == null)
                    continue;

                result.Add(template);
            }
            catch
            {
                // Registration can precede template construction. The catalog is rebuilt when the
                // ExtEnum count changes; unresolved entries are skipped rather than stalling the UI.
            }
        }

        result.Sort(
            (a, b) =>
                string.Compare(
                    a?.type?.value,
                    b?.type?.value,
                    StringComparison.OrdinalIgnoreCase));

        templateCatalog =
            result.ToArray();
        creatureTypeCatalog =
            new string[templateCatalog.Length];
        Dictionary<string, CreatureTemplate> nextByType =
            new(
                templateCatalog.Length,
                StringComparer.Ordinal);

        for (int i = 0; i < templateCatalog.Length; i++)
        {
            CreatureTemplate template =
                templateCatalog[i];
            string id =
                template?.type?.value ??
                string.Empty;
            creatureTypeCatalog[i] =
                id;
            if (!string.IsNullOrEmpty(id))
                nextByType[id] =
                    template;
        }

        templateByType =
            nextByType;
        templateCatalogTypeCount =
            creatureTypeCount;

        // Row snapshots depend on the exact template catalog/order.
        primaryRowsCache.Clear();
        primaryRowsCacheOrder.Clear();
    }

    private static EditorRelationshipRowSnapshot[] GetOrBuildPrimaryRows(
        CreatureTemplate primary,
        long revision)
    {
        string primaryId =
            primary?.type?.value ??
            string.Empty;
        if (string.IsNullOrEmpty(primaryId))
            return Array.Empty<EditorRelationshipRowSnapshot>();

        if (primaryRowsCache.TryGetValue(
                primaryId,
                out PrimaryRowsCacheEntry cached) &&
            cached.Revision == revision)
            return cached.Rows;

        RelationshipPage.changedRelationships.TryGetValue(
            primary.type,
            out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> primaryChanged);

        EditorRelationshipRowSnapshot[] rows =
            new EditorRelationshipRowSnapshot[
                Math.Max(
                    0,
                    templateCatalog.Length - 1)];
        int write = 0;

        for (int i = 0; i < templateCatalog.Length; i++)
        {
            CreatureTemplate other =
                templateCatalog[i];
            if (other?.type == null ||
                other.type == primary.type)
                continue;

            RelationshipPage.changedRelationships.TryGetValue(
                other.type,
                out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> reverseChanged);

            rows[write++] =
                new EditorRelationshipRowSnapshot
                {
                    CreatureType =
                        other.type.value,
                    DisplayName =
                        string.IsNullOrEmpty(other.name)
                            ? other.type.value
                            : other.name,
                    Selected =
                        false,
                    PrimaryToOther =
                        Capture(
                            primary,
                            other,
                            primaryChanged),
                    OtherToPrimary =
                        Capture(
                            other,
                            primary,
                            reverseChanged)
                };
        }

        if (write != rows.Length)
            Array.Resize(
                ref rows,
                write);

        CachePrimaryRows(
            primaryId,
            revision,
            rows);
        return rows;
    }

    private static void CachePrimaryRows(
        string primaryId,
        long revision,
        EditorRelationshipRowSnapshot[] rows)
    {
        if (!primaryRowsCache.ContainsKey(primaryId))
            primaryRowsCacheOrder.Enqueue(primaryId);

        primaryRowsCache[primaryId] =
            new PrimaryRowsCacheEntry
            {
                Revision = revision,
                Rows = rows
            };

        while (primaryRowsCache.Count > MaxPrimaryRowsCacheEntries &&
               primaryRowsCacheOrder.Count > 0)
        {
            string remove =
                primaryRowsCacheOrder.Dequeue();
            if (string.Equals(
                    remove,
                    primaryId,
                    StringComparison.Ordinal))
            {
                primaryRowsCacheOrder.Enqueue(remove);
                continue;
            }

            primaryRowsCache.Remove(remove);
        }
    }

    private static EditorRelationshipRowSnapshot[] ApplySelection(
        EditorRelationshipRowSnapshot[] source,
        string selectedOther)
    {
        if (source == null ||
            source.Length == 0 ||
            string.IsNullOrEmpty(selectedOther))
            return source ??
                   Array.Empty<EditorRelationshipRowSnapshot>();

        for (int i = 0; i < source.Length; i++)
        {
            EditorRelationshipRowSnapshot row =
                source[i];
            if (!string.Equals(
                    row?.CreatureType,
                    selectedOther,
                    StringComparison.Ordinal))
                continue;

            EditorRelationshipRowSnapshot[] next =
                (EditorRelationshipRowSnapshot[])source.Clone();
            next[i] =
                new EditorRelationshipRowSnapshot
                {
                    CreatureType =
                        row.CreatureType,
                    DisplayName =
                        row.DisplayName,
                    Selected =
                        true,
                    PrimaryToOther =
                        row.PrimaryToOther,
                    OtherToPrimary =
                        row.OtherToPrimary
                };
            return next;
        }

        return source;
    }

    private static EditorRelationshipValueSnapshot Capture(
        CreatureTemplate from,
        CreatureTemplate to,
        Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> directMap)
    {
        CreatureTemplate.Relationship effective =
            RelationshipPage.GetEffectiveRelationship(
                from,
                to);
        bool direct =
            directMap != null &&
            directMap.ContainsKey(to.type);

        return new EditorRelationshipValueSnapshot
        {
            Type =
                effective.type?.value ??
                string.Empty,
            Intensity =
                effective.intensity,
            DirectOverride =
                direct
        };
    }

    private static CreatureTemplate ResolveTemplate(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        if (templateByType.TryGetValue(
                typeName,
                out CreatureTemplate cached))
            return cached;

        try
        {
            return StaticWorld.GetCreatureTemplate(
                new CreatureTemplate.Type(
                    typeName,
                    false));
        }
        catch
        {
            return null;
        }
    }

    private static CreatureTemplate FindTemplate(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        return templateByType.TryGetValue(
            typeName,
            out CreatureTemplate template)
            ? template
            : null;
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
                if (!modelCommand || !changed)
                    continue;

                RelationshipPresentationChangeHintHub.MarkPair(session, command.Primary, command.Other);
                if ((session?.History.Revision ?? 0L) == historyBeforeCommand)
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