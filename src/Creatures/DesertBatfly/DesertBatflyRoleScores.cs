namespace DryCycle.Creatures.DesertBatfly;

// REJECTED FEATURE COMPATIBILITY TYPES
// ------------------------------------
// Emergent Social Roles (Sentinel / Bully / Opportunist) were rejected after
// playtesting.  The old enum/type names remain temporarily so older debug and
// injury call sites can be migrated without coupling gameplay to the removed
// feature.  Runtime code must never emit a role other than None and all scores
// are permanently neutral.
//
// Do not add role selection, thresholds, population budgets, steering or combat
// modifiers here.  Individual variation belongs to Personality and the existing
// general-purpose social/fear/vengeance systems.
internal enum ExpressedSocialRole
{
    None,

    // Legacy symbols retained only until dependent diagnostics/helpers are
    // structurally cleaned up. They are never selected or expressed.
    Sentinel,
    Bully,
    Opportunist
}

internal readonly struct DesertBatflyRoleScores
{
    internal readonly float Sentinel;
    internal readonly float Bully;
    internal readonly float Opportunist;

    internal DesertBatflyRoleScores(float sentinel, float bully, float opportunist)
    {
        Sentinel = 0f;
        Bully = 0f;
        Opportunist = 0f;
    }

    internal float For(ExpressedSocialRole role) => 0f;

    // These two helpers describe ordinary personality tendencies, not jobs.
    // They are retained as compatibility helpers only; callers should prefer
    // reading the underlying Personality axes directly.
    internal static float FollowerLike(DesertBatflyPersonality personality) => personality.Conformity;
    internal static float LonerLike(DesertBatflyPersonality personality) =>
        (1f - personality.Conformity) * (1f - personality.RoostAffinity);

    internal static DesertBatflyRoleScores Calculate(
        DesertBatflyPersonality personality,
        float panic = 0f,
        float grief = 0f,
        float trauma = 0f,
        float opportunity = 0f) => default;

    internal static float EntryThreshold(int activeCount, int roleCount) => 1f;
    internal ExpressedSocialRole Select(int activeCount, int roleCount) => ExpressedSocialRole.None;
}
