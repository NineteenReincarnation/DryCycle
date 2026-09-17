using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

internal static partial class LegacyDevUiQuiescenceController
{
    /// <summary>
    /// Runs the exact top-level input contract from vanilla DevUI.Update, then pumps only the
    /// compatibility backend required by a rebuilt Sound/Trigger workspace. This replaces the need
    /// for dedicated SoundPage.Update and TriggersPage.Update detours while preserving opaque
    /// third-party subtrees through the existing compiled backend plan.
    /// </summary>
    internal static bool TryRunNativeSoundTriggerTopLevelUpdate(global::DevInterface.DevUI owner)
    {
        Page page = owner?.activePage;
        if (page is not SoundPage && page is not TriggersPage)
            return false;
        if (!TryGetQuiescentProfile(page, out PageProfile profile) || !profile.BypassPageOverride)
            return false;
        if (profile.ToolMode != EditorToolMode.Sound && profile.ToolMode != EditorToolMode.Triggers)
            return false;

        // Match DevUI.Update exactly. Returning true when the distribution/devtools gate is closed
        // is correct: vanilla would also perform no page work in that state.
        if (owner.game?.rainWorld == null ||
            (owner.game.rainWorld.buildType == RainWorld.BuildType.Distribution && !ModManager.DevTools))
            return true;

        owner.lastMousePos = owner.mousePos;
        owner.mousePos = Futile.mousePosition;
        owner.mouseDown = Input.GetMouseButton(0);
        owner.mouseClick = owner.mouseDown && !owner.lastMouseDown;
        owner.lastMouseDown = owner.mouseDown;
        owner.draggedNode = null;

        PrepareQuiescentFrame(page);
        PumpPageBackend(page, profile);
        return true;
    }

    /// <summary>
    /// Quiescence historically intercepted each derived page Update because DevUI.Update always
    /// dispatched into the active page. Native Sound/Trigger now stop that dispatch one level higher,
    /// so their dedicated detours are redundant and are explicitly removed after Enable().
    /// </summary>
    internal static void RetireNativeSoundTriggerPageUpdateHooks()
    {
        On.DevInterface.SoundPage.Update -= SoundPage_Update;
        On.DevInterface.TriggersPage.Update -= TriggersPage_Update;
    }
}
