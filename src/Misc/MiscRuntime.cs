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

        PaletteDirectInputRuntime.Enable();
        IndividualPlacedObjectViewer.Enable();
        FadePaletteCombiner.Enable();
        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        FadePaletteCombiner.Disable();
        IndividualPlacedObjectViewer.Disable();
        PaletteDirectInputRuntime.Disable();
        _enabled = false;
    }
}
