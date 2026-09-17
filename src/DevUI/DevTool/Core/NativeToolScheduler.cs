using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Owns page-less rebuilt tools whose authoring/runtime state no longer requires a matching
/// DevInterface Page. Sound and Triggers use a minimal DryCycle Page only as a room/document lifetime
/// anchor; their concrete legacy pages are materialized solely for Vanilla/Legacy mode or diagnostics
/// that explicitly need to inspect the real DevInterface control tree.
/// </summary>
internal static class NativeToolScheduler
{
    private const int SoundPageIndex = 2;
    private const int TriggerPageIndex = 4;

    internal static bool Supports(EditorToolMode mode) =>
        mode == EditorToolMode.Sound || mode == EditorToolMode.Triggers;

    internal static bool IsVirtualToolActive(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return false;
        if (!CanOwnNativePresentation(session)) return false;
        return IsNativeAnchor(session.Owner.activePage);
    }

    /// <summary>
    /// Activates a rebuilt Sound/Trigger workspace without constructing any legacy business page.
    /// Keeping Objects would leak old gizmos and Map/Relationships would retain the wrong document,
    /// so native tools always use their dedicated empty Page anchor.
    /// </summary>
    internal static bool TryActivate(EditorSession session, EditorToolMode mode)
    {
        if (session?.Owner == null || !Supports(mode)) return false;
        if (!CanOwnNativePresentation(session)) return false;

        return ActivateCore(session, mode);
    }

    /// <summary>
    /// Reconciles global New UI / Vanilla / diagnostics ownership on Rain World's main thread.
    /// EditorUiModeState and diagnostics flags can change outside the core scheduler, so actual Page
    /// construction or retirement happens here rather than inside presentation setters.
    /// </summary>
    internal static void SynchronizePresentationOwnership(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return;

        if (!CanOwnNativePresentation(session))
        {
            if (!session.LegacyUiVisible && IsNativeAnchor(session.Owner.activePage))
                MaterializeLegacyTool(session, session.ToolMode, explicitLegacyUi: false);
            return;
        }

        if (IsExactLegacyPage(session.Owner.activePage, session.ToolMode))
            ActivateCore(session, session.ToolMode);
    }

    internal static bool MaterializeLegacyTool(
        EditorSession session,
        EditorToolMode mode,
        bool explicitLegacyUi)
    {
        if (session?.Owner == null || !Supports(mode)) return false;

        if (!IsExactLegacyPage(session.Owner.activePage, mode))
        {
            LegacyUiPresentationController.Restore(session.Owner.activePage);
            session.LegacyTransactions.Reset();
            session.Owner.SwitchPage(mode == EditorToolMode.Sound ? SoundPageIndex : TriggerPageIndex);
            session.Synchronize(session.Owner);
        }

        if (!IsExactLegacyPage(session.Owner.activePage, mode))
            return false;

        session.AdoptMaterializedToolMode(mode, explicitLegacyUi);
        LegacyUiPresentationController.Restore(session.Owner.activePage);
        return true;
    }

    internal static bool ReturnToNativeTool(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return false;

        // LegacyUiVisible is expected to be true on this transition, so do not reuse
        // CanOwnNativePresentation here. Only external ownership gates can refuse the hand-off.
        if (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla ||
            DevUiDiagnosticsPolicy.Enabled)
            return false;

        EditorToolMode mode = session.ToolMode;
        session.AdoptMaterializedToolMode(mode, legacyUiVisible: false);
        return ActivateCore(session, mode);
    }

    private static bool CanOwnNativePresentation(EditorSession session) =>
        session != null &&
        EditorInputRouter.FrontendAttached &&
        !EditorUiModeState.UseVanilla &&
        !session.LegacyUiVisible &&
        !DevUiDiagnosticsPolicy.Enabled;

    private static bool ActivateCore(EditorSession session, EditorToolMode mode)
    {
        if (session?.Owner == null || !Supports(mode)) return false;

        if (!IsNativeAnchor(session.Owner.activePage))
        {
            LegacyUiPresentationController.Restore(session.Owner.activePage);
            session.LegacyTransactions.Reset();

            // DevUI.SwitchPage only constructs canonical legacy pages. Native Sound/Trigger instead
            // mirror its ClearSprites ownership boundary and install one tiny inert Page directly.
            session.Owner.ClearSprites();
            session.Owner.activePage = new NativeToolAnchorPage(session.Owner);
            session.Synchronize(session.Owner);
        }

        if (!IsNativeAnchor(session.Owner.activePage))
            return false;

        session.AdoptVirtualToolMode(mode);
        return true;
    }

    private static bool IsNativeAnchor(Page page) =>
        page is NativeToolAnchorPage && page.GetType() == typeof(NativeToolAnchorPage);

    private static bool IsExactLegacyPage(Page page, EditorToolMode mode) => mode switch
    {
        EditorToolMode.Sound => page is SoundPage && page.GetType() == typeof(SoundPage),
        EditorToolMode.Triggers => page is TriggersPage && page.GetType() == typeof(TriggersPage),
        _ => false
    };
}
