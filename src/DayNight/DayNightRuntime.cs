namespace DryCycle.DayNight;

internal static class DayNightRuntime
{
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        DayNightPaletteSettings.Enable();
        DayNightPaletteDevUI.Enable();
        WorldClockHooks.Enable();
        PaletteLighting.Enable();
        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        PaletteLighting.Disable();
        WorldClockHooks.Disable();
        DayNightPaletteDevUI.Disable();
        DayNightPaletteSettings.Disable();
        _enabled = false;
    }
}
