namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 execution boundary for Extreme Vengeance. Intimidation state/fear memory may tick
/// independently, but Vengeance locomotion/contact progression runs only for the selected
/// PrimaryOwner.
/// </summary>
internal static class DB_VengeanceExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Vengeance)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))
            return false;

        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=Vengeance");
        bat.DesertAI.CancelAttack();
        return DB_VengeanceRuntime.ExecuteOwned(bat);
    }
}
