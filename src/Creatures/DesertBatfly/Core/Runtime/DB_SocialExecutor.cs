namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 execution boundary for neutral social locomotion and roost-join transitions.
/// Social state may be prepared before arbitration; active interaction movement progresses
/// only while Social is the current PrimaryOwner.
/// </summary>
internal static class DB_SocialExecutor
{
    internal static bool TryExecute(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Social)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social))
            return false;
        return DB_SocialRuntime.ApplyOwnedBehavior(bat);
    }
}
