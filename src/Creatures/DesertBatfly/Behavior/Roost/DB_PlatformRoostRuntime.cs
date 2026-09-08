namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Transitional lifecycle shell retained only until the Integration hook list is flattened.
/// Floor roost legality and geometry are owned exclusively by DB_RoostPolicy; this type must
/// never patch or reinterpret FlyAI.ChainTile.
/// </summary>
internal static class DB_PlatformRoostRuntime
{
    internal static void Enable()
    {
        // Intentionally empty. DB_RoostPolicy is the sole Floor-roost authority.
    }

    internal static void Disable()
    {
        // Intentionally empty. No vanilla hook was installed.
    }
}
