namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 owned execution boundary for Task13 realized-room local survival. Task13 still never
/// owns cross-room travel and never gains a second velocity controller.
/// </summary>
internal static class DB_EnvironmentExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival))
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))
            return false;

        if (resolution.WinningProposal.SuppressSocial)
            DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=" + resolution.PrimaryOwner);
        if (resolution.WinningProposal.SuppressCombat)
            bat.DesertAI.CancelPhysicalAttack();
        return DesertBatflyEnvironmentalBehavior.ApplyOwnedBehavior(bat);
    }
}
