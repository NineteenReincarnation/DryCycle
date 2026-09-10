namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Temporary compatibility facade for older managed probes. Background peer/flock/short-swarm
/// behavior now belongs to the independent NeutralEcology owner and is implemented entirely by
/// DB_NeutralBehaviorRuntime. Production arbitration/execution no longer calls this type.
/// </summary>
internal static class DB_AmbientSocialRuntime
{
    internal const int AmbientSampleLimit = DB_NeutralBehaviorRuntime.SampleLimit;
    internal const int ParticipationEpochTicks = 0;

    internal static float ParticipationProbability(DB_Personality personality)
        => DB_NeutralBehaviorRuntime.ParticipationProbability(personality);

    internal static float LooseFlockPreference(DB_Personality personality, int nearbyCandidates)
        => DB_NeutralBehaviorRuntime.LooseFlockPreference(personality, nearbyCandidates);

    internal static float Commitment(DB_Personality personality)
        => DB_NeutralBehaviorRuntime.Commitment(personality);

    internal static bool ShouldOwn(in DB_FrameContext frame)
        => DB_NeutralBehaviorRuntime.ShouldOwn(frame);

    internal static bool ApplyOwnedBehavior(DB_Creature bat)
        => DB_NeutralBehaviorRuntime.ApplyOwnedBehavior(bat);
}
