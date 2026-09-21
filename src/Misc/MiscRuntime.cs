using System;

namespace DryCycle.Misc;

internal static class MiscRuntime
{
    private static bool _enabled;
    private static bool _devToolEnabled;
    private static bool _soundFormatSupportEnabled;

    public static void Enable()
    {
        if (_enabled)
            return;

        DryCycleOptions.Register();

        // Extra loose-audio formats are useful, but they are not allowed to decide whether Rain
        // World can finish booting. Hook/API mismatches disable only this optional feature.
        TryEnableSoundFormatSupport();

        // Core DryCycle runtime facilities. Failures here are still propagated to Plugin's guarded
        // post-mod transaction because gameplay systems can depend on these services.
        DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Enable();
        PaletteDirectInputRuntime.Enable();
        DryCycle.WorldLink.WorldLinkRuntime.Enable();

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
        _soundFormatSupportEnabled = false;

        SafeDisable("legacy fade palette combiner", FadePaletteCombiner.Disable);
        SafeDisable("legacy individual object viewer", IndividualPlacedObjectViewer.Disable);
        SafeDisable("WorldLink runtime", DryCycle.WorldLink.WorldLinkRuntime.Disable);
        SafeDisable("palette direct input", PaletteDirectInputRuntime.Disable);
        SafeDisable("RoomSettingsExt runtime", DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Disable);

        _enabled = false;
    }

    private static void TryEnableSoundFormatSupport()
    {
        try
        {
            DryCycle.Misc.SoundFormatSupport.SoundFormatSupportRuntime.Enable();
            _soundFormatSupportEnabled = true;
        }
        catch (Exception error)
        {
            _soundFormatSupportEnabled = false;
            Plugin.Logger?.LogError(
                "Optional sound-format support failed to initialize and has been disabled; Rain World startup will continue.");
            Plugin.Logger?.LogError(error);
            SafeDisable(
                "partially initialized sound format support",
                DryCycle.Misc.SoundFormatSupport.SoundFormatSupportRuntime.Disable);
        }
    }

    private static void TryEnableDevToolBackend()
    {
        try
        {
            // Quiescence is part of the DevTool backend architecture, not a separately-discovered
            // BepInEx feature. Own its hook lifetime explicitly with the rebuilt editor runtime.
            DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.Enable();

            // DevToolRuntime owns the single DevUI.Update hook and invokes the native Sound/Trigger
            // scheduler directly at the vanilla-dispatch boundary.
            DryCycle.DevUI.DevTool.Compatibility.NativeSoundTriggerDevUiScheduler.Enable();
            DryCycle.DevUI.DevTool.Core.DevToolRuntime.Enable();

            // Player Map is a first-class DevTool subsystem and follows the same optional lifetime.
            DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapBackendLifecycle.Enable(
                global::DryCycle.Plugin.Logger);

            // Static catalogs are a cold-start optimization only. They belong to the optional editor
            // transaction so a bad asset/catalog scan cannot block normal gameplay startup.
            DryCycle.DevUI.DevTool.Room.RoomSettingsPresentation.WarmStaticCatalogs();

            _devToolEnabled = true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogError(
                "DryCycle DevTool backend failed to initialize and has been disabled; gameplay startup will continue.");
            Plugin.Logger?.LogError(error);
            DisableDevToolBackendSafely();
        }
    }

    private static void DisableDevToolBackendSafely()
    {
        // Always run the full reverse-order cleanup, even when _devToolEnabled is false: an exception
        // may have occurred midway through Enable before the success flag was set.
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

        _devToolEnabled = false;
    }

    private static void SafeDisable(string name, Action disable)
    {
        try
        {
            disable?.Invoke();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DryCycle cleanup failed for " + name + ": " + error);
        }
    }
}
