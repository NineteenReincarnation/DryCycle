namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Compatibility boundary between the current Social owner slot and the neutral-ecology
/// state machine. Formal interactions remain in DB_SocialRuntime; all background peer/flock/
/// short-swarm decisions are centralized in DB_NeutralBehaviorRuntime.
///
/// This type intentionally contains no independent timers, gates or steering code. It can be
/// removed when NeutralEcology receives its own top-level owner in the next owner-enum migration.
/// </summary>
internal static class DB_AmbientSocialRuntime
{
    internal const int AmbientSampleLimit = DB_NeutralBehaviorRuntime.SampleLimit;

    // Kept only for managed reflection compatibility with older probes. Neutral ecology no
    // longer uses long participation epochs or a persistent identity gate.
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
