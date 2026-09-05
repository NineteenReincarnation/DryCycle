using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Expression is temporary. No role, score or jitter is written to CreatureState.
internal enum ExpressedSocialRole { None, Sentinel, Bully, Opportunist }

internal readonly struct DesertBatflyRoleScores
{
    internal readonly float Sentinel, Bully, Opportunist;
    internal DesertBatflyRoleScores(float sentinel, float bully, float opportunist)
    {
        Sentinel = Unit(sentinel); Bully = Unit(bully); Opportunist = Unit(opportunist);
    }
    private static float Unit(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
    internal float For(ExpressedSocialRole role) => role switch
    {
        ExpressedSocialRole.Sentinel => Sentinel,
        ExpressedSocialRole.Bully => Bully,
        ExpressedSocialRole.Opportunist => Opportunist,
        _ => 0f
    };
    internal static float FollowerLike(DesertBatflyPersonality p) => p.Conformity;
    internal static float LonerLike(DesertBatflyPersonality p) => (1f - p.Conformity) * (1f - p.RoostAffinity);

    internal static DesertBatflyRoleScores Calculate(DesertBatflyPersonality p,
        float panic = 0f, float grief = 0f, float trauma = 0f, float opportunity = 0f)
    {
        // Independent salts: never consume or mutate the existing personality stream.
        float Jitter(int salt) => (float)new System.Random(p.VisualSeed ^ salt).NextDouble();
        float middle = 1f - Mathf.Abs(p.Temperament - 0.5f) * 2f;
        float quiet = 1f - Mathf.Abs(p.Temperament - 0.38f) / 0.62f;
        float pressure = Unit(panic) * 0.18f + Unit(trauma) * 0.30f;
        return new DesertBatflyRoleScores(
            1.15f * (0.35f * p.Nerve + 0.20f * p.Conformity + 0.15f * middle +
                     0.10f * p.RoostAffinity + 0.20f * Jitter(0x1937AC51)) - pressure - Unit(grief) * 0.20f,
            1.15f * (0.50f * p.Temperament + 0.30f * p.Nerve + 0.20f * Jitter(0x35AC1497)) -
                pressure - Unit(grief) * 0.40f,
            1.15f * (0.30f * p.Nerve + 0.25f * quiet + 0.20f * (1f - p.Conformity) +
                     0.25f * Jitter(0x271DA653)) - pressure - Unit(grief) * 0.30f + Unit(opportunity) * 0.04f);
    }

    internal static float EntryThreshold(int activeCount, int roleCount)
    {
        int budget = Mathf.Max(1, Mathf.RoundToInt(activeCount * 0.24f));
        // Pressure, not a cap: exceptional scores can still enter beyond the budget.
        return Mathf.Min(0.96f, 0.79f + Mathf.Max(0, roleCount - budget + 1) * 0.035f);
    }

    internal ExpressedSocialRole Select(int activeCount, int roleCount)
    {
        ExpressedSocialRole best = ExpressedSocialRole.Sentinel;
        if (Bully > For(best)) best = ExpressedSocialRole.Bully;
        if (Opportunist > For(best)) best = ExpressedSocialRole.Opportunist;
        float second = best switch
        {
            ExpressedSocialRole.Sentinel => Mathf.Max(Bully, Opportunist),
            ExpressedSocialRole.Bully => Mathf.Max(Sentinel, Opportunist),
            _ => Mathf.Max(Sentinel, Bully)
        };
        return For(best) >= EntryThreshold(activeCount, roleCount) && For(best) - second >= 0.12f
            ? best : ExpressedSocialRole.None;
    }
}
