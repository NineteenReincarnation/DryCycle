using System;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Logical data channels for the rebuilt DevTool. A revision changes only when the semantic data
/// consumed by that presentation channel may have changed. Presentation hubs can therefore skip
/// rebuilding immutable snapshots on stable frames without polling every model field.
/// </summary>
internal enum EditorRevisionKind
{
    Shell,
    Objects,
    Room,
    Sound,
    Triggers,
    Map,
    Dialog,
    Relationships,
    Count
}

/// <summary>
/// Main-thread revision clock owned by one EditorSession.
///
/// Revisions are intentionally session-local rather than global. Reopening DevUI creates a fresh
/// session with a fresh set of revisions, so presentation caches can use session identity plus one
/// channel number as a complete invalidation key. Values never use zero: zero is reserved by
/// consumers as the "not observed" sentinel.
/// </summary>
internal sealed class EditorRevisionTracker
{
    private readonly long[] revisions = new long[(int)EditorRevisionKind.Count];

    internal EditorRevisionTracker()
    {
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = 1L;
    }

    internal long Get(EditorRevisionKind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < revisions.Length ? revisions[index] : 0L;
    }

    internal void Mark(EditorRevisionKind kind)
    {
        int index = (int)kind;
        if (index < 0 || index >= revisions.Length)
            return;

        revisions[index] = Next(revisions[index]);
    }

    internal void MarkWorkspace(EditorToolMode mode) => Mark(WorkspaceKind(mode));

    internal void MarkShellAndWorkspace(EditorToolMode mode)
    {
        Mark(EditorRevisionKind.Shell);
        MarkWorkspace(mode);
    }

    internal void MarkAll()
    {
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = Next(revisions[i]);
    }

    internal static EditorRevisionKind WorkspaceKind(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Objects => EditorRevisionKind.Objects,
        EditorToolMode.Room => EditorRevisionKind.Room,
        EditorToolMode.Sound => EditorRevisionKind.Sound,
        EditorToolMode.Triggers => EditorRevisionKind.Triggers,
        EditorToolMode.Map => EditorRevisionKind.Map,
        EditorToolMode.Dialog => EditorRevisionKind.Dialog,
        EditorToolMode.Relationships => EditorRevisionKind.Relationships,
        _ => EditorRevisionKind.Shell
    };

    private static long Next(long value) => value >= long.MaxValue ? 1L : value + 1L;
}

/// <summary>
/// Associates revision clocks with EditorSession without expanding the public session surface.
/// All mutation calls happen on Rain World's main thread; the ConditionalWeakTable only provides
/// lifetime ownership so closing DevUI cannot leave revision state rooted forever.
/// </summary>
internal static class EditorRevisionHub
{
    private static ConditionalWeakTable<EditorSession, EditorRevisionTracker> trackers = new();

    internal static long Get(EditorSession session, EditorRevisionKind kind) =>
        session == null ? 0L : trackers.GetValue(session, _ => new EditorRevisionTracker()).Get(kind);

    internal static void Mark(EditorSession session, EditorRevisionKind kind)
    {
        if (session == null) return;
        trackers.GetValue(session, _ => new EditorRevisionTracker()).Mark(kind);
    }

    internal static void MarkWorkspace(EditorSession session) =>
        MarkWorkspace(session, session?.ToolMode ?? EditorToolMode.Room);

    internal static void MarkWorkspace(EditorSession session, EditorToolMode mode)
    {
        if (session == null) return;
        trackers.GetValue(session, _ => new EditorRevisionTracker()).MarkWorkspace(mode);
    }

    internal static void MarkShellAndWorkspace(EditorSession session) =>
        MarkShellAndWorkspace(session, session?.ToolMode ?? EditorToolMode.Room);

    internal static void MarkShellAndWorkspace(EditorSession session, EditorToolMode mode)
    {
        if (session == null) return;
        trackers.GetValue(session, _ => new EditorRevisionTracker()).MarkShellAndWorkspace(mode);
    }

    internal static void MarkAll(EditorSession session)
    {
        if (session == null) return;
        trackers.GetValue(session, _ => new EditorRevisionTracker()).MarkAll();
    }

    /// <summary>
    /// Reports whether the current workspace must be treated as an opaque live writer. Normally the
    /// rebuilt UI is revision-driven and the known vanilla backend is quiescent. Full vanilla UI,
    /// explicit legacy UI, legacy transactions, diagnostics or an unknown custom Page can mutate
    /// authoritative state outside DryCycle's command queues; those cases deliberately trade some
    /// rebuilding for compatibility correctness.
    /// </summary>
    internal static bool RequiresLiveWorkspaceRefresh(EditorSession session)
    {
        if (session?.Owner == null || !EditorInputRouter.FrontendAttached)
            return false;

        if (EditorUiModeState.UseVanilla ||
            session.LegacyUiVisible ||
            session.LegacyTransactions.HasPendingTransaction)
            return true;

        // Exact known migrated pages are pruned by the quiescence backend. If the backend refuses
        // to quiesce a page (for example a third-party Page subclass or diagnostics mode), regard
        // the full legacy lifecycle as an unknown writer rather than risking a stale new-UI view.
        return !LegacyDevUiQuiescenceController.IsQuiescent(session.Owner);
    }

    internal static void Reset() =>
        trackers = new ConditionalWeakTable<EditorSession, EditorRevisionTracker>();
}
