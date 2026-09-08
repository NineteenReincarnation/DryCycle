namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Formal Extreme Vengeance domain boundary.
///
/// Fear and Vengeance intentionally still share one runtime state lifecycle during the
/// behavior-preserving migration: fear collapse and persistent trauma must be able to
/// cancel an armed vengeance state synchronously. External domains use this type for
/// Vengeance facts/execution; DB_FearRuntime remains the shared-state owner until that
/// coupling is extracted with dedicated state-contract tests.
/// </summary>
internal static class DB_VengeanceRuntime
{
    internal static bool IsActive(DesertBatfly bat)
        => DB_FearRuntime.IsExtremeVengeanceActive(bat);

    internal static bool IsAvenger(DesertBatfly bat)
        => DB_FearRuntime.IsVengeanceAvenger(bat);

    internal static bool TryGetTarget(DesertBatfly bat, out Creature target)
        => DB_FearRuntime.TryGetVengeanceTarget(bat, out target);

    internal static bool ExecuteOwned(DesertBatfly bat)
        => DB_FearRuntime.ExecuteVengeanceOwned(bat);
}
