using System;

namespace DryCycle.Misc;

internal static class MiscRuntime
{
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
            return;

        StartupDiagnostics.Step("MiscRuntime/DryCycleOptions.Register", DryCycleOptions.Register);

        // Extended loose-audio decoding is a required DryCycle feature. If its hook/API contract
        // is broken, propagate the failure into Plugin's guarded post-mod transaction instead of
        // pretending the mod started successfully with the feature silently disabled.
        StartupDiagnostics.Step(
            "MiscRuntime/SoundFormatSupportRuntime.Enable",
            DryCycle.Misc.SoundFormatSupport.SoundFormatSupportRuntime.Enable);

        // Core DryCycle runtime facilities. Failures here are propagated to Plugin's guarded
        // post-mod transaction because gameplay systems can depend on these services.
        StartupDiagnostics.Step(
            "MiscRuntime/RoomSettingsExtRuntime.Enable",
            DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Enable);
        StartupDiagnostics.Step(
            "MiscRuntime/PaletteDirectInputRuntime.Enable",
            PaletteDirectInputRuntime.Enable);
        StartupDiagnostics.Step(
            "MiscRuntime/WorldLinkRuntime.Enable",
            DryCycle.WorldLink.WorldLinkRuntime.Enable);

        // The rebuilt editor is an optional development surface. A broken DevTool hook, Player Map
        // backend, or catalog warm-up must never take the whole gameplay mod (or Rain World) down.
        TryEnableDevToolBackend();

        // These two utilities only exist as temporary compatibility fallbacks. They are disabled by
        // default to avoid duplicate hooks/UI once their replacement is active.
        if (DryCycle.DayNight.RegionDayNightOptions.EnableLegacyIndividualPlacedObjectViewer)
            IndividualPlacedObjectViewer.Enable();

        if (DryCycle.DayNight.RegionDayNightOptions.EnableLegacyFadePaletteCombiner)
            FadePaletteCombiner.Enable();

        _enabled = true;
    }

    public static void Disable()
    {
        // Disable is deliberately best-effort and non-throwing. Plugin can call it while unwinding a
        // partially completed startup transaction, so one cleanup failure must not prevent the rest
        // of the hooks/services from being released.
        DisableDevToolBackendSafely();

        SafeDisable(
            "sound format support",
            DryCycle.Misc.SoundFormatSupport.SoundFormatSupportRuntime.Disable);

        SafeDisable("legacy fade palette combiner", FadePaletteCombiner.Disable);
        SafeDisable("legacy individual object viewer", IndividualPlacedObjectViewer.Disable);
        SafeDisable("WorldLink runtime", DryCycle.WorldLink.WorldLinkRuntime.Disable);
        SafeDisable("palette direct input", PaletteDirectInputRuntime.Disable);
        SafeDisable("RoomSettingsExt runtime", DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Disable);

        _enabled = false;
    }

    private static void TryEnableDevToolBackend()
    {
        try
        {
            // Quiescence is part of the DevTool backend architecture, not a separately-discovered
            // BepInEx feature. Own its hook lifetime explicitly with the rebuilt editor runtime.
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/LegacyDevUiQuiescenceController.Enable",
                DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.Enable);

            // DevToolRuntime owns the single DevUI.Update hook and invokes the native Sound/Trigger
            // scheduler directly at the vanilla-dispatch boundary.
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/NativeSoundTriggerDevUiScheduler.Enable",
                DryCycle.DevUI.DevTool.Compatibility.NativeSoundTriggerDevUiScheduler.Enable);
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/DevToolRuntime.Enable",
                DryCycle.DevUI.DevTool.Core.DevToolRuntime.Enable);

            // Player Map is a first-class DevTool subsystem and follows the same optional lifetime.
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/PlayerMapBackendLifecycle.Enable",
                () => DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapBackendLifecycle.Enable(
                    global::DryCycle.Plugin.Logger));

            // Static catalogs are a cold-start optimization only. They belong to the optional editor
            // transaction so a bad asset/catalog scan cannot block normal gameplay startup.
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/RoomSettingsPresentation.WarmStaticCatalogs",
                DryCycle.DevUI.DevTool.Room.RoomSettingsPresentation.WarmStaticCatalogs);

        }
        catch (Exception error)
        {
            StartupDiagnostics.Failure("MiscRuntime/DevToolBackend", error);
            Plugin.Logger?.LogError(
                "DryCycle DevTool backend failed to initialize and has been disabled; gameplay startup will continue.");
            DisableDevToolBackendSafely();
        }
    }

    private static void DisableDevToolBackendSafely()
    {
        // Always run the full reverse-order cleanup: an exception may have occurred midway through
        // Enable, so cleanup must not depend on a separate success flag.
        SafeDisable(
            "Player Map backend",
            DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapBackendLifecycle.Disable);
        SafeDisable(
            "DevTool runtime",
            DryCycle.DevUI.DevTool.Core.DevToolRuntime.Disable);
        SafeDisable(
            "native Sound/Trigger scheduler",
            DryCycle.DevUI.DevTool.Compatibility.NativeSoundTriggerDevUiScheduler.Disable);
        SafeDisable(
            "legacy DevUI quiescence",
            DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.Disable);
        SafeDisable(
            "DevTool revision hub",
            DryCycle.DevUI.DevTool.Core.EditorRevisionHub.Reset);

    }

    private static void SafeDisable(string name, Action disable)
    {
        if (disable == null)
        {
            return;
        }

        if (!StartupDiagnostics.RollbackStep("MiscRuntime.Cleanup/" + name, disable))
        {
            Plugin.Logger?.LogWarning(
                "DryCycle cleanup failed for '" + name +
                "'. See the preceding [ROLLBACK-FAIL] entry for the full exception.");
        }
    }
}
