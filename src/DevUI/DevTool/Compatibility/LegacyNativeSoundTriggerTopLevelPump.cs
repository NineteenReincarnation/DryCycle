using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

internal static partial class LegacyDevUiQuiescenceController
{
    /// <summary>
    /// Runs the exact top-level input contract from vanilla DevUI.Update, then either leaves a
    /// page-less native Sound/Trigger workspace completely dormant at its RoomSettingsPage anchor or
    /// pumps the compatibility backend of an already-materialized legacy Sound/Trigger page.
    /// </summary>
    internal static bool TryRunNativeSoundTriggerTopLevelUpdate(global::DevInterface.DevUI owner)
    {
        EditorSession session = DevToolRuntime.ActiveSession;
        if (session != null && object.ReferenceEquals(session.Owner, owner) &&
            NativeToolScheduler.IsVirtualToolActive(session))
        {
            // Native Sound/Trigger owns no DevInterface page work. The RoomSettingsPage exists only
            // to keep room/document lifetime attached to DevUI, so do not run its Update at all.
            if (VanillaTopLevelUpdateIsGated(owner))
                return true;

            UpdateVanillaMouseContract(owner);
            PreserveNativeSoundPreviewContract(session, owner);
            return true;
        }

        // Explicit Vanilla/Legacy ownership must never be swallowed by the native optimization.
        // This is deliberately checked before consulting quiescence profiles so a future profile
        // change cannot accidentally make a visible legacy Sound/Trigger page dormant.
        if (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla ||
            session?.LegacyUiVisible == true)
            return false;

        Page page = owner?.activePage;
        if (page is not SoundPage && page is not TriggersPage)
            return false;
        if (!TryGetQuiescentProfile(page, out PageProfile profile) || !profile.BypassPageOverride)
            return false;
        if (profile.ToolMode != EditorToolMode.Sound && profile.ToolMode != EditorToolMode.Triggers)
            return false;

        // Match DevUI.Update exactly. Returning true when the distribution/devtools gate is closed
        // is correct: vanilla would also perform no page work in that state.
        if (VanillaTopLevelUpdateIsGated(owner))
            return true;

        UpdateVanillaMouseContract(owner);
        PrepareQuiescentFrame(page);
        PumpPageBackend(page, profile);
        return true;
    }

    private static bool VanillaTopLevelUpdateIsGated(global::DevInterface.DevUI owner) =>
        owner?.game?.rainWorld == null ||
        (owner.game.rainWorld.buildType == RainWorld.BuildType.Distribution && !ModManager.DevTools);

    private static void UpdateVanillaMouseContract(global::DevInterface.DevUI owner)
    {
        owner.lastMousePos = owner.mousePos;
        owner.mousePos = Futile.mousePosition;
        owner.mouseDown = Input.GetMouseButton(0);
        owner.mouseClick = owner.mouseDown && !owner.lastMouseDown;
        owner.lastMouseDown = owner.mouseDown;
        owner.draggedNode = null;
    }

    /// <summary>
    /// SoundPage.Update has one non-presentation side effect beyond its legacy drag/trash UI: while
    /// the Sound tool is open it drives the music threat preview from horizontal mouse position.
    /// Preserve that exact Rain World contract without keeping SoundPage alive just for one scalar.
    /// TriggersPage.Update has no equivalent non-UI side effect.
    /// </summary>
    private static void PreserveNativeSoundPreviewContract(
        EditorSession session,
        global::DevInterface.DevUI owner)
    {
        if (session?.ToolMode != EditorToolMode.Sound) return;

        ThreatDetermination threatTracker = owner?.game?.manager?.musicPlayer?.threatTracker;
        if (threatTracker != null)
            threatTracker.currentThreat = Mathf.InverseLerp(0f, 1300f, Futile.mousePosition.x);
    }
}
