namespace DryCycle.Creatures.DesertBatfly;

/// <summary>R3 owner boundary for species/native Chain roost behavior.</summary>
internal static class DB_RoostExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Roost ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Roost))
            return false;
        return bat.DesertAI.ExecuteRoostOwned();
    }
}
