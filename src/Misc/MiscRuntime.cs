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

        // The Room editor has a few catalogs whose source is static for the lifetime of the loaded
        // mod set (danger types and terrain-palette assets). Resolve them during the normal mods-init
        // phase instead of charging their cold filesystem/registry cost to the first O/H UI frame.
        // If AssetManager is not ready yet, RoomSettingsPresentation deliberately leaves the cache
        // invalid so opening the editor can retry safely.
        DryCycle.DevUI.DevTool.Room.RoomSettingsPresentation.WarmStaticCatalogs();

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
