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

internal static class RelationshipEffectiveResolver
{
    private const int MaxInheritedPairVisits = 512;
    private static bool inheritedVisitCapWarningLogged;

    private readonly struct TemplatePair : IEquatable<TemplatePair>
    {
        internal TemplatePair(CreatureTemplate from, CreatureTemplate to)
        {
            From = from;
            To = to;
        }

        internal CreatureTemplate From { get; }
        internal CreatureTemplate To { get; }

        public bool Equals(TemplatePair other) =>
            ReferenceEquals(From, other.From) &&
            ReferenceEquals(To, other.To);

        public override bool Equals(object obj) =>
            obj is TemplatePair other &&
            Equals(other);

        public override int GetHashCode() =>
            (RuntimeHelpers.GetHashCode(From) * 397) ^
            RuntimeHelpers.GetHashCode(To);
    }

    /// <summary>
    /// Resolve the static authoring relationship without executing CreatureRelationship hooks.
    ///
    /// Vanilla DevTools first checks changedRelationships and then lets CreatureRelationship walk
    /// inherited overrides recursively. The rebuilt editor keeps the same source-first inheritance
    /// order, but the common no-override path is a direct array read and the inherited path is
    /// iterative, de-duplicated and bounded so malformed third-party ancestor cycles cannot stall
    /// the game thread.
    /// </summary>
    internal static CreatureTemplate.Relationship Resolve(
        CreatureTemplate from,
        CreatureTemplate to)
    {
        if (from?.type == null ||
            to?.type == null)
            return Ignore();

        Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>>
            changed =
                RelationshipPage.changedRelationships;

        // This is by far the common case in a fresh editor session. Do not allocate Stack/HashSet
        // state for every directed cell when there are no authored DevTools overrides at all.
        if (changed == null ||
            changed.Count == 0)
            return ReadBase(
                from,
                to);

        if (changed.TryGetValue(
                from.type,
                out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> directMap) &&
            directMap != null &&
            directMap.TryGetValue(
                to.type,
                out CreatureTemplate.Relationship direct))
            return direct;

        if (from.ancestor == null &&
            to.ancestor == null)
            return ReadBase(
                from,
                to);

        if (TryResolveInheritedChanged(
                from,
                to,
                changed,
                out CreatureTemplate.Relationship inherited))
            return inherited;

        return ReadBase(
            from,
            to);
    }

    private static bool TryResolveInheritedChanged(
        CreatureTemplate from,
        CreatureTemplate to,
        Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> changed,
        out CreatureTemplate.Relationship relationship)
    {
        relationship =
            default;

        Stack<TemplatePair> pending =
            new();
        HashSet<TemplatePair> visited =
            new();

        // Vanilla recursion checks the source ancestor branch before the target ancestor branch.
        // Stack is LIFO, so push target first and source second.
        if (to.ancestor != null)
        {
            pending.Push(
                new TemplatePair(
                    from,
                    to.ancestor));
        }

        if (from.ancestor != null)
        {
            pending.Push(
                new TemplatePair(
                    from.ancestor,
                    to));
        }

        int visits =
            0;
        while (pending.Count > 0 &&
               visits < MaxInheritedPairVisits)
        {
            TemplatePair pair =
                pending.Pop();
            CreatureTemplate source =
                pair.From;
            CreatureTemplate target =
                pair.To;
            if (source?.type == null ||
                target?.type == null ||
                !visited.Add(pair))
                continue;

            visits++;

            if (changed.TryGetValue(
                    source.type,
                    out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> map) &&
                map != null &&
                map.TryGetValue(
                    target.type,
                    out relationship))
                return true;

            if (target.ancestor != null)
            {
                pending.Push(
                    new TemplatePair(
                        source,
                        target.ancestor));
            }

            if (source.ancestor != null)
            {
                pending.Push(
                    new TemplatePair(
                        source.ancestor,
                        target));
            }
        }

        if (pending.Count > 0 &&
            !inheritedVisitCapWarningLogged)
        {
            inheritedVisitCapWarningLogged =
                true;
            Plugin.Logger?.LogWarning(
                "DevTool relationship inheritance exceeded the bounded " +
                MaxInheritedPairVisits +
                "-pair search while resolving '" +
                (from.type?.value ?? "<unknown>") +
                "' -> '" +
                (to.type?.value ?? "<unknown>") +
                "'. The editor fell back to the static template relationship instead of blocking.");
        }

        return false;
    }

    private static CreatureTemplate.Relationship ReadBase(
        CreatureTemplate from,
        CreatureTemplate to)
    {
        int index =
            to?.type?.Index ??
            -1;
        CreatureTemplate.Relationship[] relationships =
            from?.relationships;
        if (relationships != null &&
            index >= 0 &&
            index < relationships.Length)
            return relationships[index];

        return Ignore();
    }

    private static CreatureTemplate.Relationship Ignore() =>
        new(
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
}

public static class RelationshipEditorPresentationHub
{
    private static volatile EditorRelationshipPresentationSnapshot current = EditorRelationshipPresentationSnapshot.Empty;
    private readonly struct DirectedRelationshipKey : IEquatable<DirectedRelationshipKey>
    {
        internal DirectedRelationshipKey(string from, string to)
        {
            From = from ?? string.Empty;
            To = to ?? string.Empty;
        }

        internal string From { get; }
        internal string To { get; }

        public bool Equals(DirectedRelationshipKey other) =>
            string.Equals(From, other.From, StringComparison.Ordinal) &&
            string.Equals(To, other.To, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is DirectedRelationshipKey other &&
            Equals(other);

        public override int GetHashCode() =>
            (StringComparer.Ordinal.GetHashCode(From) * 397) ^
            StringComparer.Ordinal.GetHashCode(To);
    }

    private sealed class PrimaryRowsCacheEntry
    {
        internal long Revision;
        internal EditorRelationshipRowSnapshot[] Rows = Array.Empty<EditorRelationshipRowSnapshot>();
        internal Dictionary<string, int> RowIndexByCreature =
            new(StringComparer.Ordinal);
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

    private sealed class PairDependencyScope
    {
        internal HashSet<string> PrimarySide =
            new(StringComparer.Ordinal);
        internal HashSet<string> OtherSide =
            new(StringComparer.Ordinal);

        internal bool AffectsPrimary(string creatureId) =>
            !string.IsNullOrEmpty(creatureId) &&
            (PrimarySide.Contains(creatureId) ||
             OtherSide.Contains(creatureId));

        internal bool AffectsDirected(
            string from,
            string to) =>
            !string.IsNullOrEmpty(from) &&
            !string.IsNullOrEmpty(to) &&
            ((PrimarySide.Contains(from) &&
              OtherSide.Contains(to)) ||
             (OtherSide.Contains(from) &&
              PrimarySide.Contains(to)));
    }

    // A relationship matrix for ~100 creatures is still small in memory, so keeping more recently
    // visited primaries is substantially cheaper than re-running third-party effective-relationship
    // hooks when the developer switches back and forth between creatures.
    private const int MaxPrimaryRowsCacheEntries = 24;
    private const int PrimaryRowsMaxPerPublish = 8;
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
    private static CreatureTemplate[] observedStaticTemplateArray;
    private static int observedStaticTemplateLength = -1;
    private static readonly Dictionary<string, PrimaryRowsCacheEntry> primaryRowsCache =
        new(StringComparer.Ordinal);
    private static readonly Queue<string> primaryRowsCacheOrder = new();
    // Directed values are shared by every primary matrix. After A's matrix reads A->B and B->A,
    // opening B must not execute those same effective-relationship hooks again. Exact pair edits
    // invalidate only the two affected directions; opaque/full invalidation clears this cache.
    private static readonly Dictionary<DirectedRelationshipKey, EditorRelationshipValueSnapshot>
        directedRelationshipCache = new();
    private static PrimaryRowsBuildJob activePrimaryRowsBuild;

    private static EditorSession observedSession;
    private static Page observedPage;
    private static bool activationTracePending;
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
            session.Owner?.activePage == null)
        {
            Clear();
            return;
        }

        Page page =
            session.Owner.activePage;

        bool sessionChanged =
            !ReferenceEquals(
                observedSession,
                session);
        bool pageChanged =
            !ReferenceEquals(
                observedPage,
                page);

        if (sessionChanged ||
            pageChanged)
        {
            activationTracePending =
                true;
            Plugin.Logger?.LogInfo(
                "[DevTool.Relationships][Presentation][BEGIN] page=" +
                page.GetType().Name);
        }

        if (sessionChanged)
        {
            // Relationship data is global within one live editor session. A RelationshipPage can
            // be rematerialized when the developer switches tools; do not throw away expensive
            // matrices merely because the Page instance changed. A genuinely new session is the
            // lifetime boundary that invalidates model/value caches.
            primaryRowsCache.Clear();
            primaryRowsCacheOrder.Clear();
            directedRelationshipCache.Clear();
            activePrimaryRowsBuild = null;
            nextOpaqueCompatibilityAuditFrame = 0;
        }
        else if (pageChanged)
        {
            // Keep completed caches and an in-flight build. The next publication will bind the
            // retained data to the new page identity and continue from the previous row cursor.
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

                if (loadingHint.HasPair)
                {
                    PairDependencyScope dependencyScope =
                        BuildPairDependencyScope(
                            loadingHint.Primary,
                            loadingHint.Other);
                    InvalidateDirectedDependencyScope(
                        dependencyScope);
                    RebasePrimaryRowsCachesForScope(
                        dependencyScope,
                        revision);
                    ReconcileActiveBuildRevision(
                        activePrimaryRowsBuild,
                        revision,
                        dependencyScope);
                }
                else
                {
                    // An unknown/full writer can change any effective inherited relationship.
                    // Partial rows captured under the old revision are not safe to retain.
                    CreatureTemplate restartPrimary =
                        activePrimaryRowsBuild.Primary;
                    primaryRowsCache.Clear();
                    primaryRowsCacheOrder.Clear();
                    directedRelationshipCache.Clear();
                    activePrimaryRowsBuild =
                        null;
                    StartPrimaryRowsBuild(
                        restartPrimary,
                        revision);
                }
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
            page is RelationshipPage &&
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
                AdvancePrimaryRowsCacheRevision(
                    revision);
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
            directedRelationshipCache.Clear();
            activePrimaryRowsBuild = null;
        }

        // A type/intensity/reset edit changes exactly one directed relationship, but the UI row owns
        // both directions for one primary/other pair. Re-capture that one row and retain every other
        // row plus the static creature/relationship catalogs. Unknown history/legacy writers supply
        // no pair hint (or explicitly mark Full) and therefore fall through to the safe full build.
        if (!current.LoadingRows &&
            primaryStable &&
            hint.HasPair)
        {
            PairDependencyScope dependencyScope =
                BuildPairDependencyScope(
                    hint.Primary,
                    hint.Other);
            InvalidateDirectedDependencyScope(
                dependencyScope);
            RebasePrimaryRowsCachesForScope(
                dependencyScope,
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

            // A precise inherited override can affect more than the literal endpoint row when either
            // creature has descendants. If the current full matrix is no longer cached, fall through
            // to the normal progressive rebuild instead of patching one row and publishing stale
            // inherited values.
        }

        if (activationTracePending)
        {
            Plugin.Logger?.LogInfo(
                "[DevTool.Relationships][Presentation] catalog begin extEnumCount=" +
                creatureTypeCount);
        }

        EnsureTemplateCatalog(
            creatureTypeCount);
        CreatureTemplate[] templates =
            templateCatalog;

        if (activationTracePending)
        {
            Plugin.Logger?.LogInfo(
                "[DevTool.Relationships][Presentation] catalog ready templates=" +
                templates.Length);
        }
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
        directedRelationshipCache.Clear();
        activePrimaryRowsBuild = null;
        nextOpaqueCompatibilityAuditFrame = 0;
        activationTracePending = false;
        LastOutcome = DevToolPresentationOutcome.FullRebuild;
    }

    private static void Observe(
        EditorSession session,
        Page page,
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
        CreatureTemplate[] liveTemplates =
            StaticWorld.creatureTemplates ??
            Array.Empty<CreatureTemplate>();

        if (templateCatalogTypeCount == creatureTypeCount &&
            ReferenceEquals(
                observedStaticTemplateArray,
                liveTemplates) &&
            observedStaticTemplateLength == liveTemplates.Length)
            return;

        List<CreatureTemplate> result =
            new(
                liveTemplates.Length);
        HashSet<string> resolvedTypes =
            new(
                StringComparer.Ordinal);

        // StaticWorld.creatureTemplates is the authoritative, already-constructed registry used by
        // CreatureRelationship itself. Walking it directly avoids allocating one ExtEnum value and
        // calling StaticWorld.GetCreatureTemplate for every registered name on the first
        // Relationships frame. Null/unresolved registration slots are simply ignored.
        for (int i = 0; i < liveTemplates.Length; i++)
        {
            CreatureTemplate template =
                liveTemplates[i];
            string resolvedId =
                template?.type?.value;
            if (string.IsNullOrEmpty(resolvedId) ||
                !resolvedTypes.Add(resolvedId))
                continue;

            result.Add(template);
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
        observedStaticTemplateArray =
            liveTemplates;
        observedStaticTemplateLength =
            liveTemplates.Length;

        // Row snapshots depend on the exact template catalog/order.
        primaryRowsCache.Clear();
        primaryRowsCacheOrder.Clear();
        directedRelationshipCache.Clear();
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
        Page page,
        RelationshipEditorState state,
        long revision,
        int creatureTypeCount)
    {
        PrimaryRowsBuildJob job =
            activePrimaryRowsBuild;
        if (job == null)
            return;

        if (activationTracePending)
        {
            string next =
                job.NextTemplateIndex >= 0 &&
                job.NextTemplateIndex < templateCatalog.Length
                    ? templateCatalog[job.NextTemplateIndex]?.type?.value ?? "<null>"
                    : "<complete>";
            Plugin.Logger?.LogInfo(
                "[DevTool.Relationships][Presentation] batch begin index=" +
                job.NextTemplateIndex +
                " next=" +
                next);
        }

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

        if (activationTracePending)
        {
            Plugin.Logger?.LogInfo(
                "[DevTool.Relationships][Presentation] batch end loaded=" +
                baseRows.Length +
                "/" +
                Math.Max(0, job.Buffer.Length) +
                " complete=" +
                complete);
            if (complete)
            {
                activationTracePending =
                    false;
                Plugin.Logger?.LogInfo("[DevTool.Relationships][Presentation][READY]");
            }
        }
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
        PairDependencyScope dependencyScope)
    {
        if (job == null)
            return;

        job.Revision =
            revision;

        RelationshipPage.changedRelationships.TryGetValue(
            job.Primary?.type,
            out job.PrimaryChanged);

        if (dependencyScope == null ||
            !dependencyScope.AffectsPrimary(
                job.PrimaryId))
            return;

        bool repaired =
            false;
        for (int i = 0; i < job.Count; i++)
        {
            EditorRelationshipRowSnapshot row =
                job.Buffer[i];
            if (row == null ||
                !dependencyScope.AffectsDirected(
                    job.PrimaryId,
                    row.CreatureType))
                continue;

            CreatureTemplate other =
                ResolveTemplate(
                    row.CreatureType);
            if (other?.type == null)
                continue;

            job.Buffer[i] =
                BuildRowSnapshot(
                    job.Primary,
                    other);
            repaired =
                true;
        }

        if (repaired)
        {
            // Force the next publication to include every repaired inherited row even if the normal
            // batch threshold has not been reached yet.
            job.LastPublishedCount =
                Math.Min(
                    job.LastPublishedCount,
                    Math.Max(
                        0,
                        job.Count - PrimaryRowsPublishBatch));
        }
    }

    private static void AdvancePrimaryRowsCacheRevision(long revision)
    {
        foreach (PrimaryRowsCacheEntry entry in primaryRowsCache.Values)
        {
            if (entry != null)
                entry.Revision =
                    revision;
        }
    }

    private static PairDependencyScope BuildPairDependencyScope(
        string primaryId,
        string otherId)
    {
        PairDependencyScope scope =
            new();

        CollectSameOrDescendantTypes(
            primaryId,
            scope.PrimarySide);
        CollectSameOrDescendantTypes(
            otherId,
            scope.OtherSide);

        // Missing/unresolved third-party template IDs still need their literal cache keys invalidated.
        if (!string.IsNullOrEmpty(primaryId))
            scope.PrimarySide.Add(primaryId);
        if (!string.IsNullOrEmpty(otherId))
            scope.OtherSide.Add(otherId);

        return scope;
    }

    private static void CollectSameOrDescendantTypes(
        string rootId,
        HashSet<string> destination)
    {
        if (string.IsNullOrEmpty(rootId) ||
            destination == null)
            return;

        const int MaxAncestorDepth = 128;

        for (int i = 0; i < templateCatalog.Length; i++)
        {
            CreatureTemplate template =
                templateCatalog[i];
            CreatureTemplate cursor =
                template;

            int depth =
                0;
            while (cursor?.type != null &&
                   depth++ < MaxAncestorDepth)
            {
                if (string.Equals(
                        cursor.type.value,
                        rootId,
                        StringComparison.Ordinal))
                {
                    string id =
                        template?.type?.value;
                    if (!string.IsNullOrEmpty(id))
                        destination.Add(id);
                    break;
                }

                cursor =
                    cursor.ancestor;
            }
        }
    }

    private static void InvalidateDirectedDependencyScope(
        PairDependencyScope scope)
    {
        if (scope == null ||
            directedRelationshipCache.Count == 0)
            return;

        List<DirectedRelationshipKey> stale =
            null;

        foreach (KeyValuePair<DirectedRelationshipKey, EditorRelationshipValueSnapshot> pair
                 in directedRelationshipCache)
        {
            if (!scope.AffectsDirected(
                    pair.Key.From,
                    pair.Key.To))
                continue;

            stale ??=
                new List<DirectedRelationshipKey>();
            stale.Add(
                pair.Key);
        }

        if (stale == null)
            return;

        for (int i = 0; i < stale.Count; i++)
            directedRelationshipCache.Remove(
                stale[i]);
    }

    private static void RebasePrimaryRowsCachesForScope(
        PairDependencyScope scope,
        long revision)
    {
        if (scope == null ||
            primaryRowsCache.Count == 0)
            return;

        foreach (KeyValuePair<string, PrimaryRowsCacheEntry> pair in primaryRowsCache)
        {
            string cachedPrimaryId =
                pair.Key;
            PrimaryRowsCacheEntry entry =
                pair.Value;
            if (entry == null)
                continue;

            if (!scope.AffectsPrimary(
                    cachedPrimaryId))
            {
                entry.Revision =
                    revision;
                continue;
            }

            CreatureTemplate cachedPrimary =
                ResolveTemplate(
                    cachedPrimaryId);
            if (cachedPrimary?.type == null)
            {
                // Leave the old revision in place. The normal cache lookup will reject this entry
                // and rebuild it rather than pretending an unresolved third-party template was
                // safely patched.
                continue;
            }

            EditorRelationshipRowSnapshot[] source =
                entry.Rows ??
                Array.Empty<EditorRelationshipRowSnapshot>();
            EditorRelationshipRowSnapshot[] next =
                null;

            for (int i = 0; i < source.Length; i++)
            {
                EditorRelationshipRowSnapshot row =
                    source[i];
                if (row == null ||
                    !scope.AffectsDirected(
                        cachedPrimaryId,
                        row.CreatureType))
                    continue;

                CreatureTemplate other =
                    ResolveTemplate(
                        row.CreatureType);
                if (other?.type == null)
                    continue;

                next ??=
                    (EditorRelationshipRowSnapshot[])source.Clone();
                next[i] =
                    BuildRowSnapshot(
                        cachedPrimary,
                        other);
            }

            if (next != null)
                entry.Rows =
                    next;

            entry.Revision =
                revision;
        }
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

        Dictionary<string, int> rowIndex =
            new(
                rows?.Length ?? 0,
                StringComparer.Ordinal);
        if (rows != null)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string creature =
                    rows[i]?.CreatureType;
                if (!string.IsNullOrEmpty(creature))
                    rowIndex[creature] =
                        i;
            }
        }

        primaryRowsCache[primaryId] =
            new PrimaryRowsCacheEntry
            {
                Revision = revision,
                Rows =
                    rows ??
                    Array.Empty<EditorRelationshipRowSnapshot>(),
                RowIndexByCreature =
                    rowIndex
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
        string fromId =
            from?.type?.value ??
            string.Empty;
        string toId =
            to?.type?.value ??
            string.Empty;

        DirectedRelationshipKey key =
            new(
                fromId,
                toId);
        if (!string.IsNullOrEmpty(fromId) &&
            !string.IsNullOrEmpty(toId) &&
            directedRelationshipCache.TryGetValue(
                key,
                out EditorRelationshipValueSnapshot cached))
            return cached;

        CreatureTemplate.Relationship effective =
            RelationshipEffectiveResolver.Resolve(
                from,
                to);
        bool direct =
            directMap != null &&
            directMap.ContainsKey(to.type);

        EditorRelationshipValueSnapshot captured =
            new()
            {
                Type =
                    effective.type?.value ??
                    string.Empty,
                Intensity =
                    effective.intensity,
                DirectOverride =
                    direct
            };

        if (!string.IsNullOrEmpty(fromId) &&
            !string.IsNullOrEmpty(toId))
            directedRelationshipCache[key] =
                captured;

        return captured;
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