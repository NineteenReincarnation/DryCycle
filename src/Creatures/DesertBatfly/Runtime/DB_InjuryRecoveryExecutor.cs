namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 owned execution boundary for severe-injury recovery. The executor never decides
/// whether InjuryRecovery should win; it only runs after DB_BehaviorArbiter selected it.
/// </summary>
internal static class DB_InjuryRecoveryExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.InjuryRecovery)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.InjuryRecovery))
            return false;

        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=InjuryRecovery");
        bat.DesertAI.CancelPhysicalAttack();
        return bat.DesertAI.ExecuteInjuryRecoveryOwned();
    }
}
