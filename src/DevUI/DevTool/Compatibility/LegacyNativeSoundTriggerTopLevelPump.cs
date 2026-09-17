using DevInterface;
using DryCycle.DevUI.DevTool.Core;
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
        if (session != null && ReferenceEquals(session.Owner, owner) &&
            NativeToolScheduler.IsVirtualToolActive(session))
        {
            // Native Sound/Trigger owns no DevInterface page work. The RoomSettingsPage exists only
            // to keep room/document lifetime attached to DevUI, so do not run its Update at all.
            if (VanillaTopLevelUpdateIsGated(owner))
                return true;

            UpdateVanillaMouseContract(owner);
            return true;
        }

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
    /// Quiescence historically intercepted each derived page Update because DevUI.Update always
    /// dispatched into the active page. Native Sound/Trigger stop that dispatch one level higher, so
    /// their dedicated detours are redundant and are explicitly removed after Enable().
    /// </summary>
    internal static void RetireNativeSoundTriggerPageUpdateHooks()
    {
        On.DevInterface.SoundPage.Update -= SoundPage_Update;
        On.DevInterface.TriggersPage.Update -= TriggersPage_Update;
    }
}
