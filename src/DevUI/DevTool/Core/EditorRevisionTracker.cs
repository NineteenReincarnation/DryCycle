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
    private bool presentationModeObserved;
    private bool vanillaOwnedPresentation;

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

    /// <summary>
    /// Called only from DevUI.Update. The volatile presentation flag may be written by RWImGui's
    /// render callback, but revision mutation stays on Rain World's main thread. Returning from full
    /// vanilla presentation invalidates every channel exactly once so edits performed while rebuilt
    /// snapshots were intentionally dormant become visible immediately.
    /// </summary>
    internal void ObservePresentationMode(bool useVanilla)
    {
        bool returningToRebuilt =
            presentationModeObserved && vanillaOwnedPresentation && !useVanilla;

        presentationModeObserved = true;
        vanillaOwnedPresentation = useVanilla;

        if (returningToRebuilt)
            MarkAll();
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

    internal static void MarkWorkspace(EditorSession session) =>
        MarkWorkspace(session, session?.ToolMode ?? EditorToolMode.Room);

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
    /// Main-thread ownership observation. Call once per DevUI update before any presentation hub
    /// reads revisions.
    /// </summary>
    internal static void ObservePresentationMode(EditorSession session)
    {
        if (session == null) return;
        Tracker(session).ObservePresentationMode(EditorUiModeState.UseVanilla);
    }

    /// <summary>
    /// Reports whether the current rebuilt workspace must be treated as an opaque live writer.
    /// Full vanilla presentation is intentionally excluded: rebuilt windows are not drawn there,
    /// and ObservePresentationMode invalidates every channel once ownership returns to New UI.
    /// Explicit legacy panels inside New UI, legacy transactions, diagnostics and unknown custom
    /// Pages remain live because the rebuilt surface is visible while those writers are active.
    /// </summary>
    internal static bool RequiresLiveWorkspaceRefresh(EditorSession session)
    {
        if (session?.Owner == null || !EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla)
            return false;

        if (session.LegacyUiVisible || session.LegacyTransactions.HasPendingTransaction)
            return true;

        return !LegacyDevUiQuiescenceController.IsQuiescent(session.Owner);
    }

    internal static void Reset() =>
        trackers = new ConditionalWeakTable<EditorSession, EditorRevisionTracker>();

    private static EditorRevisionTracker Tracker(EditorSession session) =>
        trackers.GetValue(session, _ => new EditorRevisionTracker());
}
