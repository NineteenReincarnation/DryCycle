namespace DryCycle.Token;

/// <summary>
/// Registers DryCycle's sandbox-unlock IDs. The actual world token remains Rain
/// World's native BlueToken, so collection, save persistence, HUD feedback and the
/// DevTools token selector all continue to use the vanilla token pipeline.
/// </summary>
internal static class DryCycleTokenRuntime
{
    internal const string RopeSpearTokenValue = "RopeSpear";

    internal static MultiplayerUnlocks.SandboxUnlockID RopeSpearUnlock { get; private set; }

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        RopeSpearUnlock = new MultiplayerUnlocks.SandboxUnlockID(
            RopeSpearTokenValue,
            register: true);
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        RopeSpearUnlock?.Unregister();
        RopeSpearUnlock = null;
        _enabled = false;
    }
}
