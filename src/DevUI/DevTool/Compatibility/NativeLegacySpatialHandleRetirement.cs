using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Removes vanilla built-in Sound/Trigger Handle nodes while the native RWImGui gizmo owns those
/// spatial semantics. This is node retirement, not a Handle.Update hook: the old gizmos simply stop
/// existing on the native path. Returning to Vanilla/Legacy performs the normal deferred page
/// Refresh, which recreates them losslessly.
///
/// Exact runtime types are intentional. A third-party Handle subclass is an unknown contract and is
/// left alive for the legacy compatibility backend until that mod registers a native gizmo provider.
/// </summary>
internal static class NativeLegacySpatialHandleRetirement
{
    private static Page observedPage;
    private static EditorSession observedSession;
    private static long observedRevision;
    private static int observedTopLevelCount = -1;

    internal static void Apply(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (!ShouldOwnNativeSpatialHandles(session, page))
        {
            ResetObservation();
            return;
        }

        long revision = EditorRevisionHub.Get(
            session,
            EditorRevisionTracker.WorkspaceKind(session.ToolMode));
        int topLevelCount = page.subNodes?.Count ?? 0;
        if (ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedPage, page) &&
            observedRevision == revision &&
            observedTopLevelCount == topLevelCount)
            return;

        bool changed = page switch
        {
            SoundPage sound => RetireSoundHandles(sound),
            TriggersPage triggers => RetireTriggerHandles(triggers),
            _ => false
        };

        // Removal changes the relevant nested DevUI topology. Release the page-keyed compiled plan
        // immediately so the next legacy backend pump cannot retain a stale handle root.
        if (changed)
            LegacyDevUiQuiescenceController.ReleasePage(page);

        observedSession = session;
        observedPage = page;
        observedRevision = revision;
        observedTopLevelCount = page.subNodes?.Count ?? 0;
    }

    internal static void Reset() => ResetObservation();

    private static bool ShouldOwnNativeSpatialHandles(EditorSession session, Page page)
    {
        if (session == null || page == null || !EditorInputRouter.FrontendAttached ||
            EditorUiModeState.UseVanilla || session.LegacyUiVisible)
            return false;

        return (session.ToolMode == EditorToolMode.Sound && page.GetType() == typeof(SoundPage)) ||
               (session.ToolMode == EditorToolMode.Triggers && page.GetType() == typeof(TriggersPage));
    }

    private static bool RetireSoundHandles(SoundPage page)
    {
        if (page?.subNodes == null) return false;
        bool changed = false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not AmbientSoundPanel panel || panel.subNodes == null)
                continue;

            for (int child = panel.subNodes.Count - 1; child >= 0; child--)
            {
                DevUINode node = panel.subNodes[child];
                Type type = node?.GetType();
                if (type != typeof(SpotSoundHandle) && type != typeof(DirectionalSoundHandle))
                    continue;

                RetireNode(panel, child, node);
                changed = true;
            }
        }
        return changed;
    }

    private static bool RetireTriggerHandles(TriggersPage page)
    {
        if (page?.subNodes == null) return false;
        bool changed = false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not TriggerPanel panel || panel.subNodes == null)
                continue;

            for (int child = panel.subNodes.Count - 1; child >= 0; child--)
            {
                DevUINode node = panel.subNodes[child];
                if (node?.GetType() != typeof(SpotTriggerHandle))
                    continue;

                RetireNode(panel, child, node);
                changed = true;
            }
        }
        return changed;
    }

    private static void RetireNode(DevUINode parent, int index, DevUINode node)
    {
        try { node?.ClearSprites(); }
        catch { }
        if (parent?.subNodes != null && index >= 0 && index < parent.subNodes.Count &&
            ReferenceEquals(parent.subNodes[index], node))
            parent.subNodes.RemoveAt(index);
    }

    private static void ResetObservation()
    {
        observedPage = null;
        observedSession = null;
        observedRevision = 0L;
        observedTopLevelCount = -1;
    }
}
