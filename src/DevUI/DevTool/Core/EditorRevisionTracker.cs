using System;
using System.Runtime.CompilerServices;
using DevInterface;
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
    private bool presentationOwnershipObserved;
    private bool rebuiltPresentationWasActive;
    private bool suppressMarksUntilFirstRead;

    internal EditorRevisionTracker()
    {
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = 1L;
    }

    internal long Get(EditorRevisionKind kind)
    {
        // Returning from Vanilla/hidden/detached presentation already advances every channel once.
        // CorePresentation may immediately notice a History.Revision edge from the dormant period
        // and attempt to mark Shell + workspace again before it reads either clock. Keep that tiny
        // post-observation window idempotent, then reopen normal marking on the first revision read.
        suppressMarksUntilFirstRead = false;

        int index = (int)kind;
        return index >= 0 && index < revisions.Length ? revisions[index] : 0L;
    }

    internal void Mark(EditorRevisionKind kind)
    {
        if (suppressMarksUntilFirstRead)
            return;

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

    /// <summary>
    /// Called only from DevUI.Update. Frontend attachment / UI-mode / overlay-visibility flags may be
    /// written from the RWImGui side, but revision mutation remains on Rain World's main thread.
    /// Any dormant -> active transition invalidates every channel once, making edits performed while
    /// rebuilt snapshots were intentionally sleeping visible on the first returned frame.
    /// </summary>
    internal void ObservePresentationOwnership(bool rebuiltPresentationActive)
    {
        bool returningToRebuilt =
            presentationOwnershipObserved && !rebuiltPresentationWasActive && rebuiltPresentationActive;

        presentationOwnershipObserved = true;
        rebuiltPresentationWasActive = rebuiltPresentationActive;

        if (returningToRebuilt)
        {
            MarkAll();

            // ObservePresentationMode runs immediately before CorePresentation. Until that first
            // consumer reads a revision, MarkAll is the authoritative invalidation for this return
            // frame. This prevents dormant History changes from double-bumping the same channels.
            suppressMarksUntilFirstRead = true;
        }
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
        session == null ? 0L : Tracker(session).Get(kind);

    internal static void Mark(EditorSession session, EditorRevisionKind kind)
    {
        if (session == null) return;
        Tracker(session).Mark(kind);
    }

    /// <summary>
    /// Invalidates every workspace that can be mutated by the active document's shared history
    /// stack. Room, Objects, Sound and Triggers deliberately share one Room document history; an
    /// Undo executed while another one of those tools is visible can therefore restore data owned by
    /// an inactive presentation hub. Advancing the whole document family keeps those retained caches
    /// correct without rebuilding them until the user actually returns to that tool.
    /// </summary>
    internal static void MarkWorkspace(EditorSession session)
    {
        if (session == null) return;

        EditorRevisionTracker tracker = Tracker(session);
        switch (session.DocumentKey.Kind)
        {
            case EditorDocumentKind.Room:
                tracker.Mark(EditorRevisionKind.Objects);
                tracker.Mark(EditorRevisionKind.Room);
                tracker.Mark(EditorRevisionKind.Sound);
                tracker.Mark(EditorRevisionKind.Triggers);
                break;
            case EditorDocumentKind.RegionMap:
                tracker.Mark(EditorRevisionKind.Map);
                break;
            case EditorDocumentKind.Relationships:
                tracker.Mark(EditorRevisionKind.Relationships);
                break;
            default:
                tracker.MarkWorkspace(session.ToolMode);
                break;
        }
    }

    /// <summary>
    /// Narrow invalidation for a caller that explicitly owns one workspace channel rather than a
    /// document-wide History.Revision edge.
    /// </summary>
    internal static void MarkWorkspace(EditorSession session, EditorToolMode mode)
    {
        if (session == null) return;
        Tracker(session).MarkWorkspace(mode);
    }

    internal static void MarkShellAndWorkspace(EditorSession session) =>
        MarkShellAndWorkspace(session, session?.ToolMode ?? EditorToolMode.Room);

    internal static void MarkShellAndWorkspace(EditorSession session, EditorToolMode mode)
    {
        if (session == null) return;
        Tracker(session).MarkShellAndWorkspace(mode);
    }

    internal static void MarkAll(EditorSession session)
    {
        if (session == null) return;
        Tracker(session).MarkAll();
    }

    /// <summary>
    /// True only while the rebuilt surface is actually drawable. Full Vanilla mode, a detached
    /// RWImGui bridge, and Escape-hidden overlay periods intentionally put immutable snapshot
    /// production to sleep. Re-entry is handled by ObservePresentationMode on the main thread.
    /// </summary>
    internal static bool IsRebuiltPresentationActive(EditorSession session) =>
        session?.Owner != null &&
        EditorInputRouter.FrontendAttached &&
        !EditorUiModeState.UseVanilla &&
        !EditorUiModeState.OverlayHidden;

    /// <summary>
    /// Main-thread ownership observation. Call once per DevUI update before any presentation hub
    /// reads revisions.
    /// </summary>
    internal static void ObservePresentationMode(EditorSession session)
    {
        if (session == null) return;
        Tracker(session).ObservePresentationOwnership(IsRebuiltPresentationActive(session));
    }

    /// <summary>
    /// Reports whether the currently visible rebuilt workspace must be treated as an opaque live
    /// writer. Dormant presentation periods are excluded because all channels are invalidated once
    /// when rebuilt ownership returns. Active pointer/text transactions stay live. An explicitly
    /// visible legacy panel on one of the exact migrated vanilla pages can remain cached while idle
    /// only when no opaque third-party subtree has been observed on that page. Unknown/custom pages
    /// and migrated pages containing foreign controls remain conservative continuous writers.
    /// </summary>
    internal static bool RequiresLiveWorkspaceRefresh(EditorSession session)
    {
        if (!IsRebuiltPresentationActive(session))
            return false;

        if (session.LegacyTransactions.HasPendingTransaction)
            return true;

        Page page = session.Owner?.activePage;
        if (session.LegacyUiVisible &&
            IsExactMigratedPage(session) &&
            !LegacyDevUiQuiescenceController.HasExternalCompatibilityNodes(page))
            return false;

        return !LegacyDevUiQuiescenceController.IsQuiescent(session.Owner);
    }

    private static bool IsExactMigratedPage(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (page == null) return false;

        Type runtimeType = page.GetType();
        return session.ToolMode switch
        {
            EditorToolMode.Room => runtimeType == typeof(RoomSettingsPage),
            EditorToolMode.Objects => runtimeType == typeof(ObjectsPage),
            EditorToolMode.Sound => runtimeType == typeof(SoundPage),
            EditorToolMode.Triggers => runtimeType == typeof(TriggersPage),
            EditorToolMode.Map => runtimeType == typeof(MapPage),
            EditorToolMode.Dialog => runtimeType == typeof(DialogPage),
            EditorToolMode.Relationships => runtimeType == typeof(RelationshipPage),
            _ => false
        };
    }

    internal static void Reset() =>
        trackers = new ConditionalWeakTable<EditorSession, EditorRevisionTracker>();

    private static EditorRevisionTracker Tracker(EditorSession session) =>
        trackers.GetValue(session, _ => new EditorRevisionTracker());
}
