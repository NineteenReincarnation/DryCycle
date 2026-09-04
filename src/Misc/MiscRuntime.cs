namespace DryCycle.Misc;

internal static class MiscRuntime
{
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        DryCycleOptions.Register();

        // Core DryCycle DevUI features stay enabled regardless of RegionKit.
        DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Enable();
        PaletteDirectInputRuntime.Enable();
        DevUIShortcutInputGuard.Enable();
        DryCycle.WorldLink.WorldLinkRuntime.Enable();

        // These two utilities only exist as temporary RegionKit fallbacks. They are
        // disabled by default to avoid duplicate hooks/UI once RegionKit is working.
        if (DryCycle.DayNight.RegionDayNightOptions.EnableLegacyIndividualPlacedObjectViewer)
        {
            IndividualPlacedObjectViewer.Enable();
        }

        if (DryCycle.DayNight.RegionDayNightOptions.EnableLegacyFadePaletteCombiner)
        {
            FadePaletteCombiner.Enable();
        }

        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            // WorldLink Enable is transactional, but keep this defensive cleanup so a
            // partially initialized MiscRuntime never leaves room/map hooks behind.
            DevUIShortcutInputGuard.Disable();
            DryCycle.WorldLink.WorldLinkRuntime.Disable();
            return;
        }

        FadePaletteCombiner.Disable();
        IndividualPlacedObjectViewer.Disable();
        DryCycle.WorldLink.WorldLinkRuntime.Disable();
        DevUIShortcutInputGuard.Disable();
        PaletteDirectInputRuntime.Disable();
        DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Disable();
        _enabled = false;
    }
}
