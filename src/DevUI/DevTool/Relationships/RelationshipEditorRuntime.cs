using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Compatibility;
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
    public bool LoadingRows { get; init; }
    public int LoadedRowCount { get; init; }
    public int TotalRowCount { get; init; }
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

    private sealed class PrimaryRowsBuildJob
    {
        internal string PrimaryId = string.Empty;
        internal long Revision;
        internal CreatureTemplate Primary;
        internal int NextTemplateIndex;
        internal Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> PrimaryChanged;
        internal EditorRelationshipRowSnapshot[] Buffer = Array.Empty<EditorRelationshipRowSnapshot>();
        internal int Count;
        internal int LastPublishedCount;
    }

    // A relationship matrix for ~100 creatures is still small in memory, so keeping more recently
    // visited primaries is substantially cheaper than re-running third-party effective-relationship
    // hooks when the developer switches back and forth between creatures.
    private const int MaxPrimaryRowsCacheEntries = 24;
    private const int PrimaryRowsMaxPerPublish = 4;
    private const int PrimaryRowsPublishBatch = 6;
    private const double PrimaryRowsBudgetMs = 0.70;
    // Hidden third-party RelationshipPage nodes are conservatively treated as opaque writers by the
    // compatibility backend. Do not let their heartbeat invalidate a completed matrix every frame.
    // The rebuilt editor still performs a periodic full audit, while DryCycle edits use exact pair
    // hints and remain immediate.
    private const int OpaqueCompatibilityAuditFrames = 180;

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
    private static PrimaryRowsBuildJob activePrimaryRowsBuild;

    private static EditorSession observedSession;
    private static RelationshipPage observedPage;
    private static long observedRevision;
    private static int observedCreatureTypeCount = -1;
    private static string observedPrimary = string.Empty;
    private static string observedOther = string.Empty;
    private static EditorRelationshipDirection observedDirection;
    private static int nextOpaqueCompatibilityAuditFrame;

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
            activePrimaryRowsBuild = null;
            nextOpaqueCompatibilityAuditFrame = 0;
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

        if (current.LoadingRows &&
            sameIdentity &&
            observedCreatureTypeCount == creatureTypeCount &&
            relationshipTypeCount == nextRelationshipTypeCount &&
            activePrimaryRowsBuild != null &&
            string.Equals(
                activePrimaryRowsBuild.PrimaryId,
                state.PrimaryCreature,
                StringComparison.Ordinal))
        {
            if (activePrimaryRowsBuild.Revision != revision)
            {
                RelationshipPresentationChangeHint loadingHint =
                    RelationshipPresentationChangeHintHub.Consume(session);
                ReconcileActiveBuildRevision(
                    activePrimaryRowsBuild,
                    revision,
                    loadingHint);
            }

            PublishProgressiveRows(
                session,
                page,
                state,
                revision,
                creatureTypeCount);
            LastOutcome =
                current.LoadingRows
                    ? DevToolPresentationOutcome.PartialRebuild
                    : DevToolPresentationOutcome.FullRebuild;
            return;
        }

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

        if (modelChanged &&
            !hint.HasPair &&
            !hint.Full &&
            !session.LegacyUiVisible &&
            LegacyDevUiQuiescenceController.HasExternalCompatibilityNodes(page))
        {
            int frame =
                UnityEngine.Time.frameCount;
            if (nextOpaqueCompatibilityAuditFrame <= 0)
                nextOpaqueCompatibilityAuditFrame =
                    frame + OpaqueCompatibilityAuditFrames;

            if (frame < nextOpaqueCompatibilityAuditFrame)
            {
                // The hidden third-party node was pumped, but no semantic relationship hint exists.
                // Accept its opaque heartbeat without invalidating an otherwise stable matrix.
                observedRevision =
                    revision;
                LastOutcome =
                    DevToolPresentationOutcome.CacheHit;
                return;
            }

            nextOpaqueCompatibilityAuditFrame =
                frame + OpaqueCompatibilityAuditFrames;
        }

        if (modelChanged &&
            !hint.HasPair)
        {
            // Unknown/full writers cannot safely reuse row values from an older model revision.
            primaryRowsCache.Clear();
            primaryRowsCacheOrder.Clear();
            activePrimaryRowsBuild = null;
        }

        // A type/intensity/reset edit changes exactly one directed relationship, but the UI row owns
        // both directions for one primary/other pair. Re-capture that one row and retain every other
        // row plus the static creature/relationship catalogs. Unknown history/legacy writers supply
        // no pair hint (or explicitly mark Full) and therefore fall through to the safe full build.
        if (!current.LoadingRows &&
            primaryStable &&
            hint.HasPair &&
            string.Equals(
                hint.Primary,
                state.PrimaryCreature,
                StringComparison.Ordinal))
        {
            RebasePrimaryRowsCachesForPair(
                hint.Primary,
                hint.Other,
                revision);

            if (TryGetCachedPrimaryRows(
                    state.PrimaryCreature,
                    revision,
                    out EditorRelationshipRowSnapshot[] rebasedRows))
            {
                PublishRowsSnapshot(
                    state,
                    rebasedRows,
                    loading: false,
                    loadedCount: rebasedRows.Length,
                    totalCount: rebasedRows.Length);
                Observe(
                    session,
                    page,
                    revision,
                    creatureTypeCount,
                    state);
                LastOutcome =
                    DevToolPresentationOutcome.PartialRebuild;
                return;
            }

            if (TryPublishPairOnly(
                    state,
                    hint.Other))
            {
                Observe(
                    session,
                    page,
                    revision,
                    creatureTypeCount,
                    state);
                LastOutcome =
                    DevToolPresentationOutcome.PartialRebuild;
                return;
            }
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

        RebuildRelationshipTypesIfNeeded();

        if (TryGetCachedPrimaryRows(
                primary.type.value,
                revision,
                out EditorRelationshipRowSnapshot[] cachedRows))
        {
            activePrimaryRowsBuild =
                null;
            PublishRowsSnapshot(
                state,
                cachedRows,
                loading: false,
                loadedCount: cachedRows.Length,
                totalCount: cachedRows.Length);
            Observe(
                session,
                page,
                revision,
                creatureTypeCount,
                state);
            LastOutcome =
                DevToolPresentationOutcome.FullRebuild;
            return;
        }

        StartPrimaryRowsBuild(
            primary,
            revision);
        PublishProgressiveRows(
            session,
            page,
            state,
            revision,
            creatureTypeCount);
        LastOutcome =
            current.LoadingRows
                ? DevToolPresentationOutcome.PartialRebuild
                : DevToolPresentationOutcome.FullRebuild;
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
        EditorRelationshipRowSnapshot rebuilt =
            BuildRowSnapshot(
                primary,
                other);
        rows[rowIndex] =
            new EditorRelationshipRowSnapshot
            {
                CreatureType =
                    rebuilt.CreatureType,
                DisplayName =
                    rebuilt.DisplayName,
                Selected =
                    string.Equals(
                        state.SelectedOtherCreature,
                        other.type.value,
                        StringComparison.Ordinal),
                PrimaryToOther =
                    rebuilt.PrimaryToOther,
                OtherToPrimary =
                    rebuilt.OtherToPrimary
            };

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = current.Available,
            PrimaryCreature = state.PrimaryCreature,
            SelectedOtherCreature = state.SelectedOtherCreature,
            SelectedDirection = state.SelectedDirection,
            CreatureTypes = current.CreatureTypes,
            RelationshipTypes = current.RelationshipTypes,
            Rows = rows,
            LoadingRows = current.LoadingRows,
            LoadedRowCount = current.LoadedRowCount,
            TotalRowCount = current.TotalRowCount
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
            Rows = next ?? source,
            LoadingRows = current.LoadingRows,
            LoadedRowCount = current.LoadedRowCount,
            TotalRowCount = current.TotalRowCount
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
        activePrimaryRowsBuild = null;
        nextOpaqueCompatibilityAuditFrame = 0;
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
        activePrimaryRowsBuild = null;
        nextOpaqueCompatibilityAuditFrame = 0;
    }

    private static bool TryGetCachedPrimaryRows(
        string primaryId,
        long revision,
        out EditorRelationshipRowSnapshot[] rows)
    {
        if (!string.IsNullOrEmpty(primaryId) &&
            primaryRowsCache.TryGetValue(
                primaryId,
                out PrimaryRowsCacheEntry cached) &&
            cached.Revision == revision)
        {
            rows =
                cached.Rows ??
                Array.Empty<EditorRelationshipRowSnapshot>();
            return true;
        }

        rows =
            Array.Empty<EditorRelationshipRowSnapshot>();
        return false;
    }

    private static void StartPrimaryRowsBuild(
        CreatureTemplate primary,
        long revision)
    {
        string primaryId =
            primary?.type?.value ??
            string.Empty;

        if (activePrimaryRowsBuild != null &&
            activePrimaryRowsBuild.Revision == revision &&
            string.Equals(
                activePrimaryRowsBuild.PrimaryId,
                primaryId,
                StringComparison.Ordinal))
            return;

        RelationshipPage.changedRelationships.TryGetValue(
            primary.type,
            out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> primaryChanged);

        activePrimaryRowsBuild =
            new PrimaryRowsBuildJob
            {
                PrimaryId =
                    primaryId,
                Revision =
                    revision,
                Primary =
                    primary,
                NextTemplateIndex =
                    0,
                PrimaryChanged =
                    primaryChanged,
                Buffer =
                    new EditorRelationshipRowSnapshot[
                        Math.Max(
                            0,
                            templateCatalog.Length - 1)],
                Count =
                    0,
                LastPublishedCount =
                    0
            };
    }

    private static void PublishProgressiveRows(
        EditorSession session,
        RelationshipPage page,
        RelationshipEditorState state,
        long revision,
        int creatureTypeCount)
    {
        PrimaryRowsBuildJob job =
            activePrimaryRowsBuild;
        if (job == null)
            return;

        long started =
            System.Diagnostics.Stopwatch.GetTimestamp();
        int processed =
            0;

        while (job.NextTemplateIndex < templateCatalog.Length &&
               processed < PrimaryRowsMaxPerPublish)
        {
            if (processed > 0 &&
                ElapsedMilliseconds(started) >= PrimaryRowsBudgetMs)
                break;

            CreatureTemplate other =
                templateCatalog[job.NextTemplateIndex++];
            if (other?.type == null ||
                other.type == job.Primary.type)
                continue;

            RelationshipPage.changedRelationships.TryGetValue(
                other.type,
                out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> reverseChanged);

            if (job.Count < job.Buffer.Length)
            {
                job.Buffer[job.Count++] =
                    BuildRowSnapshot(
                        job.Primary,
                        other,
                        job.PrimaryChanged,
                        reverseChanged);
            }

            processed++;
        }

        bool complete =
            job.NextTemplateIndex >= templateCatalog.Length;

        bool selectionChanged =
            !string.Equals(
                observedOther,
                state?.SelectedOtherCreature ?? string.Empty,
                StringComparison.Ordinal) ||
            observedDirection !=
            (state?.SelectedDirection ??
             EditorRelationshipDirection.PrimaryToOther);

        bool shouldPublish =
            complete ||
            selectionChanged ||
            (job.Count > 0 &&
             job.LastPublishedCount == 0) ||
            job.Count - job.LastPublishedCount >=
            PrimaryRowsPublishBatch;

        if (!shouldPublish)
        {
            // Keep ownership/revision observation current without allocating a new immutable
            // presentation array. The last published partial graph remains visible until the next
            // batch is ready.
            Observe(
                session,
                page,
                revision,
                creatureTypeCount,
                state);
            return;
        }

        EditorRelationshipRowSnapshot[] baseRows;
        if (complete &&
            job.Count == job.Buffer.Length)
        {
            // The completed fixed buffer becomes immutable once the job is retired; reuse it
            // directly as both the presentation base and cache storage.
            baseRows =
                job.Buffer;
        }
        else
        {
            baseRows =
                new EditorRelationshipRowSnapshot[job.Count];
            if (job.Count > 0)
            {
                Array.Copy(
                    job.Buffer,
                    baseRows,
                    job.Count);
            }
        }

        job.LastPublishedCount =
            job.Count;

        if (complete)
        {
            CachePrimaryRows(
                job.PrimaryId,
                job.Revision,
                baseRows);
            activePrimaryRowsBuild =
                null;
        }

        PublishRowsSnapshot(
            state,
            baseRows,
            loading: !complete,
            loadedCount: baseRows.Length,
            totalCount: Math.Max(
                0,
                job.Buffer.Length));

        Observe(
            session,
            page,
            revision,
            creatureTypeCount,
            state);
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) *
        1000d /
        System.Diagnostics.Stopwatch.Frequency;

    private static EditorRelationshipRowSnapshot BuildRowSnapshot(
        CreatureTemplate primary,
        CreatureTemplate other,
        Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> primaryChanged = null,
        Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> reverseChanged = null)
    {
        if (primaryChanged == null)
        {
            RelationshipPage.changedRelationships.TryGetValue(
                primary.type,
                out primaryChanged);
        }

        if (reverseChanged == null)
        {
            RelationshipPage.changedRelationships.TryGetValue(
                other.type,
                out reverseChanged);
        }

        return new EditorRelationshipRowSnapshot
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

    private static void ReconcileActiveBuildRevision(
        PrimaryRowsBuildJob job,
        long revision,
        RelationshipPresentationChangeHint hint)
    {
        if (job == null)
            return;

        job.Revision =
            revision;

        if (!hint.HasPair ||
            !string.Equals(
                hint.Primary,
                job.PrimaryId,
                StringComparison.Ordinal))
            return;

        CreatureTemplate other =
            ResolveTemplate(
                hint.Other);
        if (other?.type == null)
            return;

        for (int i = 0; i < job.Count; i++)
        {
            EditorRelationshipRowSnapshot row =
                job.Buffer[i];
            if (!string.Equals(
                    row?.CreatureType,
                    hint.Other,
                    StringComparison.Ordinal))
                continue;

            job.Buffer[i] =
                BuildRowSnapshot(
                    job.Primary,
                    other);
            // Force the next publication to include the repaired row even if the normal batch
            // threshold has not been reached yet.
            job.LastPublishedCount =
                Math.Min(
                    job.LastPublishedCount,
                    Math.Max(
                        0,
                        job.Count - PrimaryRowsPublishBatch));
            return;
        }
    }

    private static void RebasePrimaryRowsCachesForPair(
        string primaryId,
        string otherId,
        long revision)
    {
        if (string.IsNullOrEmpty(primaryId) ||
            string.IsNullOrEmpty(otherId) ||
            primaryRowsCache.Count == 0)
            return;

        CreatureTemplate primary =
            ResolveTemplate(
                primaryId);
        CreatureTemplate other =
            ResolveTemplate(
                otherId);
        if (primary?.type == null ||
            other?.type == null)
            return;

        foreach (KeyValuePair<string, PrimaryRowsCacheEntry> pair in primaryRowsCache)
        {
            PrimaryRowsCacheEntry entry =
                pair.Value;
            if (entry == null)
                continue;

            bool affected =
                string.Equals(
                    pair.Key,
                    primaryId,
                    StringComparison.Ordinal) ||
                string.Equals(
                    pair.Key,
                    otherId,
                    StringComparison.Ordinal);

            if (!affected)
            {
                // A precise pair edit cannot affect any third primary's matrix.
                entry.Revision =
                    revision;
                continue;
            }

            CreatureTemplate cachePrimary =
                string.Equals(
                    pair.Key,
                    primaryId,
                    StringComparison.Ordinal)
                    ? primary
                    : other;
            CreatureTemplate cacheOther =
                ReferenceEquals(
                    cachePrimary,
                    primary)
                    ? other
                    : primary;

            if (TryPatchCachedRow(
                    entry,
                    cachePrimary,
                    cacheOther))
            {
                entry.Revision =
                    revision;
            }
        }
    }

    private static bool TryPatchCachedRow(
        PrimaryRowsCacheEntry entry,
        CreatureTemplate primary,
        CreatureTemplate other)
    {
        EditorRelationshipRowSnapshot[] source =
            entry?.Rows ??
            Array.Empty<EditorRelationshipRowSnapshot>();

        for (int i = 0; i < source.Length; i++)
        {
            if (!string.Equals(
                    source[i]?.CreatureType,
                    other.type.value,
                    StringComparison.Ordinal))
                continue;

            EditorRelationshipRowSnapshot[] next =
                (EditorRelationshipRowSnapshot[])source.Clone();
            next[i] =
                BuildRowSnapshot(
                    primary,
                    other);
            entry.Rows =
                next;
            return true;
        }

        return false;
    }

    private static void PublishRowsSnapshot(
        RelationshipEditorState state,
        EditorRelationshipRowSnapshot[] baseRows,
        bool loading,
        int loadedCount,
        int totalCount)
    {
        EditorRelationshipRowSnapshot[] rows =
            ApplySelection(
                baseRows,
                state?.SelectedOtherCreature);

        current =
            new EditorRelationshipPresentationSnapshot
            {
                Available =
                    true,
                PrimaryCreature =
                    state?.PrimaryCreature ??
                    string.Empty,
                SelectedOtherCreature =
                    state?.SelectedOtherCreature ??
                    string.Empty,
                SelectedDirection =
                    state?.SelectedDirection ??
                    EditorRelationshipDirection.PrimaryToOther,
                CreatureTypes =
                    creatureTypeCatalog,
                RelationshipTypes =
                    relationshipTypes,
                Rows =
                    rows,
                LoadingRows =
                    loading,
                LoadedRowCount =
                    Math.Max(
                        0,
                        loadedCount),
                TotalRowCount =
                    Math.Max(
                        0,
                        totalCount)
            };
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