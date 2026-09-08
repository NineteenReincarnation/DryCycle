namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 execution boundary for a real incoming-projectile dodge. Threat Signature memory is
/// only a modifier/input; it never becomes an owner. The executor consumes the Arbiter's
/// resolved local dodge goal and leaves locomotion to native Fly flight.
/// </summary>
internal static class DB_ProjectileEvadeExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.ImmediateProjectileEvade)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))
            return false;
        if (!resolution.FinalGoal.HasValue) return false;
        return DB_ThreatTactics.ApplyProjectileEvadeOwned(bat, resolution.FinalGoal.Value);
    }
}
