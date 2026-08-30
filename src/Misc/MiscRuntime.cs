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

        PaletteNumberInput.Enable();
        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        PaletteNumberInput.Disable();
        _enabled = false;
    }
}
