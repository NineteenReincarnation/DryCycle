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
    private const int RoomPageIndex = 0;
    private const int ObjectsPageIndex = 1;
    private const int SoundPageIndex = 2;
    private const int MapPageIndex = 3;
    private const int TriggerPageIndex = 4;
    private const int DialogPageIndex = 5;
    private const int RelationshipsPageIndex = 6;

    internal static bool Supports(EditorToolMode mode) =>
        mode == EditorToolMode.Sound || mode == EditorToolMode.Triggers;

    internal static bool IsVirtualToolActive(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return false;
        if (!CanOwnNativePresentation(session)) return false;
        return IsNativeAnchor(session.Owner.activePage);
    }

    /// <summary>
    /// Handles both sides of the virtual-tool boundary before EditorSession falls back to ordinary
    /// DevUI page switching. Entering Sound/Trigger installs the inert native anchor. Leaving that
    /// anchor for any canonical non-native workspace materializes the requested real page directly,
    /// so the anchor can never be mistaken for Room merely because ResolveToolMode's unknown-page
    /// fallback is Room.
    /// </summary>
    internal static bool TryActivate(EditorSession session, EditorToolMode mode)
    {
        if (session?.Owner == null) return false;

        if (Supports(mode))
        {
            if (!CanOwnNativePresentation(session)) return false;
            return ActivateCore(session, mode);
        }

        if (IsNativeAnchor(session.Owner.activePage))
            return LeaveNativeAnchor(session, mode);

        return false;
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

    private static bool LeaveNativeAnchor(EditorSession session, EditorToolMode mode)
    {
        int pageIndex = CanonicalPageIndex(mode);
        if (pageIndex < 0)
            return false;

        LegacyUiPresentationController.Restore(session.Owner.activePage);
        session.LegacyTransactions.Reset();
        session.Owner.SwitchPage(pageIndex);
        session.Synchronize(session.Owner);
        return !IsNativeAnchor(session.Owner.activePage) && session.ToolMode == mode;
    }

    private static int CanonicalPageIndex(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Room => RoomPageIndex,
        EditorToolMode.Objects => ObjectsPageIndex,
        EditorToolMode.Map => MapPageIndex,
        EditorToolMode.Dialog => DialogPageIndex,
        EditorToolMode.Relationships => RelationshipsPageIndex,
        _ => -1
    };

    private static bool IsNativeAnchor(Page page) =>
        page is NativeToolAnchorPage && page.GetType() == typeof(NativeToolAnchorPage);

    private static bool IsExactLegacyPage(Page page, EditorToolMode mode) => mode switch
    {
        EditorToolMode.Sound => page is SoundPage && page.GetType() == typeof(SoundPage),
        EditorToolMode.Triggers => page is TriggersPage && page.GetType() == typeof(TriggersPage),
        _ => false
    };
}
