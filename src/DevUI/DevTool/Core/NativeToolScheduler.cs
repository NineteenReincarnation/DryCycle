using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Owns page-less rebuilt tools whose authoring/runtime state no longer requires a matching
/// DevInterface Page. Sound and Triggers use an exact RoomSettingsPage only as a lightweight room
/// lifetime anchor; their concrete legacy pages are materialized solely for Vanilla/Legacy mode or
/// diagnostics that explicitly need to inspect the real DevInterface control tree.
/// </summary>
internal static class NativeToolScheduler
{
    private const int RoomAnchorPageIndex = 0;
    private const int SoundPageIndex = 2;
    private const int TriggerPageIndex = 4;

    internal static bool Supports(EditorToolMode mode) =>
        mode == EditorToolMode.Sound || mode == EditorToolMode.Triggers;

    internal static bool IsVirtualToolActive(EditorSession session)
    {
        if (session?.Owner == null || !Supports(session.ToolMode)) return false;
        if (!CanOwnNativePresentation(session)) return false;

        Page page = session.Owner.activePage;
        return page is RoomSettingsPage && page.GetType() == typeof(RoomSettingsPage);
    }

    /// <summary>
    /// Activates a rebuilt Sound/Trigger workspace without constructing its legacy page. A room page
    /// is the only valid anchor: keeping Objects would leak old gizmos, while Map/Relationships would
    /// keep the wrong document identity alive.
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
            if (!session.LegacyUiVisible && IsRoomAnchor(session.Owner.activePage))
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

        if (!IsRoomAnchor(session.Owner.activePage))
        {
            LegacyUiPresentationController.Restore(session.Owner.activePage);
            session.LegacyTransactions.Reset();
            session.Owner.SwitchPage(RoomAnchorPageIndex);
            session.Synchronize(session.Owner);
        }

        if (!IsRoomAnchor(session.Owner.activePage))
            return false;

        session.AdoptVirtualToolMode(mode);
        return true;
    }

    private static bool IsRoomAnchor(Page page) =>
        page is RoomSettingsPage && page.GetType() == typeof(RoomSettingsPage);

    private static bool IsExactLegacyPage(Page page, EditorToolMode mode) => mode switch
    {
        EditorToolMode.Sound => page is SoundPage && page.GetType() == typeof(SoundPage),
        EditorToolMode.Triggers => page is TriggersPage && page.GetType() == typeof(TriggersPage),
        _ => false
    };
}
