using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Inner DevUI lifecycle layer for rebuilt Sound/Trigger workspaces. It is installed after
/// quiescence and before DevToolRuntime so the main runtime remains the single outer editor hook.
/// Native Sound/Trigger can run without matching legacy pages; this layer owns the final
/// main-thread ownership hand-off immediately before vanilla would update activePage.
///
/// If native ownership is not valid (Vanilla UI, diagnostics, legacy transaction, custom page,
/// etc.), the call is passed through untouched to the original DevUI implementation.
/// </summary>
internal static class NativeSoundTriggerDevUiScheduler
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
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
            // DevToolRuntime is the outer hook and has already synchronized EditorSession and run
            // editor shortcuts before control reaches this inner layer. Reconcile ownership again
            // here so a same-frame New UI <-> Vanilla toggle materializes/retires the legacy page
            // before any page Update is dispatched.
            NativeToolScheduler.SynchronizePresentationOwnership(DevToolRuntime.ActiveSession);

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
