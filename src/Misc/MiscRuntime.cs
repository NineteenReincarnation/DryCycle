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

        // Core DryCycle DevUI features stay enabled regardless of optional editor frontends.
        DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Enable();
        PaletteDirectInputRuntime.Enable();
        DryCycle.WorldLink.WorldLinkRuntime.Enable();
        DryCycle.DevUI.DevTool.Core.DevToolRuntime.Enable();

        // These two utilities only exist as temporary compatibility fallbacks. They are
        // disabled by default to avoid duplicate hooks/UI once their replacement is active.
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
        DryCycle.DevUI.DevTool.Core.DevToolRuntime.Disable();

        if (!_enabled)
        {
            // WorldLink Enable is transactional, but keep this defensive cleanup so a
            // partially initialized MiscRuntime never leaves room/map hooks behind.
            DryCycle.WorldLink.WorldLinkRuntime.Disable();
            return;
        }

        FadePaletteCombiner.Disable();
        IndividualPlacedObjectViewer.Disable();
        DryCycle.WorldLink.WorldLinkRuntime.Disable();
        PaletteDirectInputRuntime.Disable();
        DryCycle.RoomSettingsExt.RoomSettingsExtRuntime.Disable();
        _enabled = false;
    }
}
