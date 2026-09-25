using System;

namespace DryCycle.Misc;

internal static class MiscRuntime
{
    private static bool _enabled;
    private static bool _devToolCoreEnabled;
    private static bool _devToolExtrasAttempted;

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

        // DevTool lifetime is owned by Plugin, not this gameplay transaction. The core editor is
        // enabled from Plugin.OnEnable and heavy extras are attempted immediately after OnModsInit.
        // MiscRuntime rollback therefore cannot accidentally remove the developer UI.

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
        // of the hooks/services from being released. DevTool has an independent Plugin-owned lifetime.

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

    internal static void EnableDevToolCore()
    {
        if (_devToolCoreEnabled)
            return;

        // Compatibility helpers are optional; the single DevUI.Update producer is the core.
        TryEnableOptionalDevToolFeature(
            "LegacyDevUiQuiescenceController",
            DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.Enable,
            DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.Disable);

        TryEnableOptionalDevToolFeature(
            "NativeSoundTriggerDevUiScheduler",
            DryCycle.DevUI.DevTool.Compatibility.NativeSoundTriggerDevUiScheduler.Enable,
            DryCycle.DevUI.DevTool.Compatibility.NativeSoundTriggerDevUiScheduler.Disable);

        try
        {
            StartupDiagnostics.Step(
                "MiscRuntime/DevTool/DevToolRuntime.Enable",
                DryCycle.DevUI.DevTool.Core.DevToolRuntime.Enable);
            _devToolCoreEnabled = true;
            StartupDiagnostics.Marker("MiscRuntime/DevTool/CoreBackend", "ENABLED");
        }
        catch (Exception error)
        {
            StartupDiagnostics.Failure("MiscRuntime/DevTool/CoreBackend", error);
            StartupDiagnostics.Marker(
                "MiscRuntime/DevTool/CoreBackend",
                "DEGRADED",
                "core DevUI.Update hook failed; gameplay remains available");
            DisableDevToolBackendSafely();
        }
    }

    internal static void EnableDevToolExtras()
    {
        if (_devToolExtrasAttempted)
            return;

        _devToolExtrasAttempted = true;
        if (!_devToolCoreEnabled)
            EnableDevToolCore();
        if (!_devToolCoreEnabled)
            return;

        // Player Map and static catalog warm-up are intentionally delayed until RainWorld.OnModsInit
        // has completed. They are optional and cannot tear down the already-running core editor.
        TryEnableOptionalDevToolFeature(
            "PlayerMapBackendLifecycle",
            () => DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapBackendLifecycle.Enable(
                global::DryCycle.Plugin.Logger),
            DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapBackendLifecycle.Disable);

        TryEnableOptionalDevToolFeature(
            "RoomSettingsPresentation.WarmStaticCatalogs",
            DryCycle.DevUI.DevTool.Room.RoomSettingsPresentation.WarmStaticCatalogs,
            null);
    }

    internal static void DisableDevToolBackend()
    {
        DisableDevToolBackendSafely();
        _devToolCoreEnabled = false;
        _devToolExtrasAttempted = false;
    }

    private static void TryEnableOptionalDevToolFeature(
        string name,
        Action enable,
        Action rollback)
    {
        try
        {
            enable?.Invoke();
            StartupDiagnostics.Marker("MiscRuntime/DevTool/" + name, "ENABLED");
        }
        catch (Exception error)
        {
            StartupDiagnostics.Failure("MiscRuntime/DevTool/" + name, error);
            StartupDiagnostics.Marker(
                "MiscRuntime/DevTool/" + name,
                "DEGRADED",
                "optional DevTool feature failed; core editor remains enabled");

            if (rollback != null)
                SafeDisable("optional " + name, rollback);
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
            StartupDiagnostics.Marker(
                "MiscRuntime.Cleanup/" + name,
                "ROLLBACK-INCOMPLETE",
                "see preceding ROLLBACK-FAIL entry for the full exception");
        }
    }
}
