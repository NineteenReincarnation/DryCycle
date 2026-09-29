using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Owns page-less rebuilt tools whose authoring/runtime state no longer requires a matching
/// DevInterface Page. Objects, Sound and Triggers use a minimal DryCycle Page only as a room/document
/// lifetime anchor. Objects diagnostics are fully headless; its concrete legacy page is materialized
/// only for explicit Vanilla/Legacy ownership. Sound/Trigger diagnostics may still require their
/// concrete backend pages until those subsystems reach the same headless audit boundary.
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
        mode == EditorToolMode.Objects ||
        mode == EditorToolMode.Sound ||
        mode == EditorToolMode.Triggers;

    internal static bool IsVirtualToolActive(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return false;
        if (!CanOwnNativePresentation(session)) return false;
        return IsNativeAnchor(session.Owner.activePage);
    }

    /// <summary>
    /// Owns every transition into or out of page-less native workspaces. Supported tools never fall
    /// through to EditorSession's raw SwitchPage path: the scheduler either installs the inert native
    /// anchor or materializes the concrete backend page according to current ownership policy.
    /// </summary>
    internal static bool SwitchTool(EditorSession session, EditorToolMode mode)
    {
        if (session?.Owner == null)
            return false;

        if (Supports(mode))
        {
            if (CanOwnNativePresentation(session))
                return ActivateCore(session, mode);

            return MaterializeLegacyToolCore(
                session,
                mode,
                explicitLegacyUi: session.LegacyUiVisible);
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
                MaterializeLegacyToolCore(session, session.ToolMode, explicitLegacyUi: false);
            return;
        }

        if (IsExactLegacyPage(session.Owner.activePage, session.ToolMode))
            ActivateCore(session, session.ToolMode);
    }

    internal static bool ShowExplicitLegacyTool(
        EditorSession session,
        EditorToolMode mode) =>
        MaterializeLegacyToolCore(session, mode, explicitLegacyUi: true);

    private static bool MaterializeLegacyToolCore(
        EditorSession session,
        EditorToolMode mode,
        bool explicitLegacyUi)
    {
        if (session?.Owner == null || !Supports(mode)) return false;

        if (!IsExactLegacyPage(session.Owner.activePage, mode))
        {
            LegacyUiPresentationController.Restore(session.Owner.activePage);
            session.LegacyTransactions.Reset();
            session.Owner.SwitchPage(LegacyPageIndex(mode));
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

        EditorToolMode mode = session.ToolMode;

        // LegacyUiVisible is expected to be true on this transition, so do not reuse
        // CanOwnNativePresentation here. Only external ownership gates can refuse the hand-off.
        if (!EditorInputRouter.FrontendAttached ||
            EditorUiModeState.UseVanilla ||
            DiagnosticsRequireLegacyPage(mode))
            return false;

        session.AdoptMaterializedToolMode(mode, legacyUiVisible: false);
        return ActivateCore(session, mode);
    }

    private static bool CanOwnNativePresentation(EditorSession session) =>
        session != null &&
        EditorInputRouter.FrontendAttached &&
        !EditorUiModeState.UseVanilla &&
        !session.LegacyUiVisible &&
        !DiagnosticsRequireLegacyPage(session.ToolMode);

    private static bool DiagnosticsRequireLegacyPage(EditorToolMode mode) =>
        DevUiDiagnosticsPolicy.Enabled &&
        mode != EditorToolMode.Objects;

    private static bool ActivateCore(EditorSession session, EditorToolMode mode)
    {
        if (session?.Owner == null || !Supports(mode)) return false;

        if (!IsNativeAnchor(session.Owner.activePage))
        {
            LegacyUiPresentationController.Restore(session.Owner.activePage);
            session.LegacyTransactions.Reset();

            // DevUI.SwitchPage only constructs canonical legacy pages. Native workspaces instead
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

    private static int LegacyPageIndex(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Objects => ObjectsPageIndex,
        EditorToolMode.Sound => SoundPageIndex,
        EditorToolMode.Triggers => TriggerPageIndex,
        _ => -1
    };

    private static int CanonicalPageIndex(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Room => RoomPageIndex,
        EditorToolMode.Map => MapPageIndex,
        EditorToolMode.Dialog => DialogPageIndex,
        EditorToolMode.Relationships => RelationshipsPageIndex,
        _ => -1
    };

    private static bool IsNativeAnchor(Page page) =>
        page is NativeToolAnchorPage && page.GetType() == typeof(NativeToolAnchorPage);

    private static bool IsExactLegacyPage(Page page, EditorToolMode mode) => mode switch
    {
        EditorToolMode.Objects => page is ObjectsPage && page.GetType() == typeof(ObjectsPage),
        EditorToolMode.Sound => page is SoundPage && page.GetType() == typeof(SoundPage),
        EditorToolMode.Triggers => page is TriggersPage && page.GetType() == typeof(TriggersPage),
        _ => false
    };
}