namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 owner boundary for the existing DB_AI combat state machine. R4 may later move
/// these responsibilities into Combat/ and FlightMotor; R3 only guarantees one owner.
/// </summary>
internal static class DB_CombatExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Combat ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=Combat");
        if (!bat.DesertAI.Combat.TryExecuteOwned()) return false;
        DesertBatflyThreatRuntime.ApplyOwnedTacticalModifier(bat);
        return true;
    }
}
