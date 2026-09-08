namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 owned execution boundary for realized-room local environmental survival. This layer never
/// owns cross-room travel and never gains a second velocity controller.
/// </summary>
internal static class DB_EnvironmentExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival))
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))
            return false;

        if (resolution.WinningProposal.SuppressSocial)
            DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=" + resolution.PrimaryOwner);
        if (resolution.WinningProposal.SuppressCombat)
            bat.DesertAI.CancelPhysicalAttack();
        return DB_EnvironmentRuntime.ApplyOwnedBehavior(bat);
    }
}
