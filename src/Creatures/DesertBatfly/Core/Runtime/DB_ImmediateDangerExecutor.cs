namespace DryCycle.Creatures.DesertBatfly;

/// <summary>R3 boundary for direct local escape movement.</summary>
internal static class DB_ImmediateDangerExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.ImmediateDanger ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateDanger))
            return false;
        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateDanger");
        return bat.DesertAI.ExecuteImmediateDangerOwned();
    }
}
