using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Inner DevUI lifecycle layer for rebuilt Sound/Trigger workspaces. It is installed after
/// quiescence and before DevToolRuntime so the main runtime remains the single outer editor hook,
/// while this layer replaces only vanilla activePage.Update for native Sound/Trigger pages.
///
/// This reduces two derived-page Update detours to one existing lifecycle level. If native ownership
/// is not valid (Vanilla UI, diagnostics, legacy transaction, custom page, etc.), the call is passed
/// through untouched to the original DevUI implementation.
/// </summary>
internal static class NativeSoundTriggerDevUiScheduler
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;

        // LegacyDevUiQuiescenceController.Enable() runs immediately before this method. Remove its
        // now-redundant derived Sound/Trigger page detours before installing the top-level pump.
        LegacyDevUiQuiescenceController.RetireNativeSoundTriggerPageUpdateHooks();
        On.DevInterface.DevUI.Update += DevUI_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.DevUI.Update -= DevUI_Update;
        enabled = false;
    }

    private static void DevUI_Update(
        On.DevInterface.DevUI.orig_Update orig,
        global::DevInterface.DevUI self)
    {
        try
        {
            if (LegacyDevUiQuiescenceController.TryRunNativeSoundTriggerTopLevelUpdate(self))
                return;
        }
        catch (Exception error)
        {
            // Fail open to vanilla. A compatibility optimization must never make DevTools unusable.
            Plugin.Logger?.LogWarning(
                "DevTool native Sound/Trigger top-level scheduler failed; using vanilla update: " +
                error.Message);
        }

        orig(self);
    }
}
