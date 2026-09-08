namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R3 fear/PTSD execution boundary. Current acute danger may supply a retreat goal; passive
/// fear/PTSD can simply suppress lower owners while preserving the current native goal.
/// </summary>
internal static class DB_FearExecutor
{
    internal static bool TryExecute(DesertBatfly bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.FearResponse ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.FearResponse))
            return false;
        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=FearResponse");
        return bat.DesertAI.ExecuteFearOwned(resolution);
    }
}
