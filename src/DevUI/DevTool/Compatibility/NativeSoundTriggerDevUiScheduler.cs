using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Direct DevUI lifecycle service for rebuilt Sound/Trigger workspaces.
///
/// DevToolRuntime owns the sole DevUI.Update hook and calls this service immediately before vanilla
/// page dispatch. That preserves the previous ownership/fallback timing without stacking another
/// HookGen detour on the same external method.
/// </summary>
internal static class NativeSoundTriggerDevUiScheduler
{
    private static bool enabled;

    internal static void Enable() => enabled = true;

    internal static void Disable() => enabled = false;

    /// <summary>
    /// Returns true when native Sound/Trigger consumed the top-level DevUI update and vanilla must
    /// not run for this frame. Any compatibility failure fails open to vanilla.
    /// </summary>
    internal static bool TryRun(global::DevInterface.DevUI self)
    {
        if (!enabled || self == null)
            return false;

        try
        {
            // Reconcile immediately before page dispatch so a same-frame New UI <-> Vanilla toggle
            // materializes/retires the legacy page before either native or vanilla update runs.
            NativeToolScheduler.SynchronizePresentationOwnership(DevToolRuntime.ActiveSession);
            return LegacyDevUiQuiescenceController.TryRunNativeSoundTriggerTopLevelUpdate(self);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native Sound/Trigger top-level scheduler failed; using vanilla update: " +
                error.Message);
            return false;
        }
    }
}
