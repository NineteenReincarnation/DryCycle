namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Single lifecycle entry point for DryCycle's Peach Lizard extensions.
/// Keep Quicksand traversal and Desert Batfly predation isolated internally while
/// enabling/disabling them together from the existing Watcher compatibility hook.
/// </summary>
internal static class PeachLizardRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        PeachLizardQuicksandRuntime.Enable();
        PeachLizardDesertBatflyPredation.Enable();
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        PeachLizardDesertBatflyPredation.Disable();
        PeachLizardQuicksandRuntime.Disable();
        _enabled = false;
    }
}
