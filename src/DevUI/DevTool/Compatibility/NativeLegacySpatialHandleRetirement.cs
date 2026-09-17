using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Removes vanilla built-in Sound/Trigger spatial presentation nodes while native RWImGui gizmos own
/// those semantics. This is node retirement, not a Handle.Update hook: the old Panel/Handle subtree
/// simply stops existing on the native path.
///
/// The first ordinary page materialization is still allowed as a compatibility probe so third-party
/// hooks can attach their own nodes. A page containing any opaque foreign DevUI node is left entirely
/// intact and stays on the conservative Legacy backend. Pure built-in pages retire their vanilla
/// panels wholesale. Returning to Vanilla/Legacy performs the normal deferred Refresh and recreates
/// the original DevInterface presentation losslessly.
/// </summary>
internal static class NativeLegacySpatialHandleRetirement
{
    private static readonly System.Reflection.Assembly VanillaDevUiAssembly = typeof(global::DevInterface.DevUI).Assembly;
    private static readonly System.Reflection.Assembly DryCycleAssembly = typeof(global::DryCycle.Plugin).Assembly;

    private static Page observedPage;
    private static EditorSession observedSession;
    private static long observedRevision;
    private static int observedTopLevelCount = -1;

    internal static void Apply(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (!ShouldOwnNativeSpatialPresentation(session, page))
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
            SoundPage sound => PruneBuiltinSoundNodes(sound),
            TriggersPage triggers => PruneBuiltinTriggerNodes(triggers),
            _ => false
        };

        if (changed)
        {
            LegacyDevUiQuiescenceController.ReleasePage(page);
            // The native workspace deliberately removed vanilla presentation nodes. Mark the page
            // stale so switching to Vanilla/Legacy performs one authoritative Refresh and restores
            // the original Panel/Handle tree before it becomes visible again.
            LegacyDevUiQuiescenceController.TryDeferRefresh(session);
        }

        observedSession = session;
        observedPage = page;
        observedRevision = revision;
        observedTopLevelCount = page.subNodes?.Count ?? 0;
    }

    internal static bool PruneBuiltinSoundNodes(SoundPage page)
    {
        if (page?.subNodes == null || ContainsOpaqueForeignDescendant(page)) return false;
        bool changed = false;

        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not AmbientSoundPanel panel)
                continue;

            // Pure built-in pages contain exact vanilla panels. A custom subtype is still treated as
            // an unknown contract even if a mod happened to emit it from the vanilla assembly path.
            if (panel.GetType() != typeof(AmbientSoundPanel))
                continue;

            RetireTopLevelNode(page, i, panel);
            if (ReferenceEquals(page.draggedObject, panel))
                page.draggedObject = null;
            changed = true;
        }

        return changed;
    }

    internal static bool PruneBuiltinTriggerNodes(TriggersPage page)
    {
        if (page?.subNodes == null || ContainsOpaqueForeignDescendant(page)) return false;
        bool changed = false;

        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not TriggerPanel panel)
                continue;

            if (panel.GetType() != typeof(TriggerPanel))
                continue;

            RetireTopLevelNode(page, i, panel);
            if (ReferenceEquals(page.draggedObject, panel))
                page.draggedObject = null;
            changed = true;
        }

        return changed;
    }

    internal static void Reset() => ResetObservation();

    private static bool ShouldOwnNativeSpatialPresentation(EditorSession session, Page page)
    {
        if (session == null || page == null || !EditorInputRouter.FrontendAttached ||
            EditorUiModeState.UseVanilla || session.LegacyUiVisible)
            return false;

        return (session.ToolMode == EditorToolMode.Sound && page.GetType() == typeof(SoundPage)) ||
               (session.ToolMode == EditorToolMode.Triggers && page.GetType() == typeof(TriggersPage));
    }

    private static bool ContainsOpaqueForeignDescendant(DevUINode parent)
    {
        if (parent?.subNodes == null) return false;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode child = parent.subNodes[i];
            if (child == null) continue;

            System.Reflection.Assembly assembly = child.GetType().Assembly;
            if (assembly != VanillaDevUiAssembly && assembly != DryCycleAssembly)
                return true;

            if (ContainsOpaqueForeignDescendant(child))
                return true;
        }

        return false;
    }

    private static void RetireTopLevelNode(Page page, int index, DevUINode node)
    {
        try { node?.ClearSprites(); }
        catch { }

        if (page?.subNodes != null && index >= 0 && index < page.subNodes.Count &&
            ReferenceEquals(page.subNodes[index], node))
            page.subNodes.RemoveAt(index);

        page?.tempNodes?.Remove(node);
    }

    private static void ResetObservation()
    {
        observedPage = null;
        observedSession = null;
        observedRevision = 0L;
        observedTopLevelCount = -1;
    }
}
