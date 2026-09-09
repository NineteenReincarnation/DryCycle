using UnityEngine;

namespace DryCycle.Thirst;

internal enum PlayerDehydrationStage
{
    Healthy = 0,
    Mild = 1,
    Moderate = 2,
    Severe = 3,
    Critical = 4
}

/// <summary>
/// Stable read-only physiology surface for gameplay systems outside the Thirst domain.
/// Consumers receive stage/strength facts but never gain access to HydrationWeakness state.
/// </summary>
internal readonly struct PlayerDehydrationSnapshot
{
    internal readonly PlayerDehydrationStage Stage;
    internal readonly float Debt;
    internal readonly float GripStrength;
    internal readonly float PickupFailureChance;
    internal readonly float PreyAttraction;

    internal bool FeedingEligible => Stage is PlayerDehydrationStage.Severe or PlayerDehydrationStage.Critical;

    internal PlayerDehydrationSnapshot(
        PlayerDehydrationStage stage,
        float debt,
        float gripStrength,
        float pickupFailureChance,
        float preyAttraction)
    {
        Stage = stage;
        Debt = Mathf.Clamp(debt, 0f, HydrationWeakness.LethalDebt);
        GripStrength = Mathf.Clamp(gripStrength, 0.45f, 1f);
        PickupFailureChance = Mathf.Clamp01(pickupFailureChance);
        PreyAttraction = Mathf.Clamp01(preyAttraction);
    }
}

/// <summary>
/// Cross-domain dehydration contract. This is deliberately the only surface Desert Batfly
/// feeding/grip code needs from the player's hydration implementation.
/// </summary>
internal static class PlayerDehydrationFacts
{
    internal static PlayerDehydrationSnapshot For(Player player)
    {
        float debt = Mathf.Clamp(
            HydrationWeakness.GetDebt(player),
            0f,
            HydrationWeakness.LethalDebt);
        PlayerDehydrationStage stage = StageFor(debt);

        float grip = Piecewise(
            debt,
            1f,
            0.95f,
            0.85f,
            0.70f,
            0.55f,
            0.52f);
        float pickupFailure = Piecewise(
            debt,
            0f,
            0.05f,
            0.12f,
            0.28f,
            0.44f,
            0.46f);

        float attraction = debt < HydrationWeakness.ModerateEndDebt
            ? 0f
            : debt <= HydrationWeakness.SevereEndDebt
                ? Mathf.Lerp(
                    0.42f,
                    0.66f,
                    Mathf.InverseLerp(
                        HydrationWeakness.ModerateEndDebt,
                        HydrationWeakness.SevereEndDebt,
                        debt))
                : Mathf.Lerp(
                    0.66f,
                    1f,
                    Mathf.InverseLerp(
                        HydrationWeakness.SevereEndDebt,
                        HydrationWeakness.LethalDebt,
                        debt));

        return new PlayerDehydrationSnapshot(stage, debt, grip, pickupFailure, attraction);
    }

    /// <summary>
    /// Apply aggregated body-fluid loss from external feeding. HydrationWeakness remains the
    /// sole physiological state owner and decides clamping/death semantics.
    /// </summary>
    internal static bool ApplyPredationStress(Player player, float debtAmount)
        => HydrationWeakness.AddExternalDebt(player, debtAmount);

    private static PlayerDehydrationStage StageFor(float debt)
    {
        if (debt <= 0.0001f) return PlayerDehydrationStage.Healthy;
        if (debt <= HydrationWeakness.MildEndDebt) return PlayerDehydrationStage.Mild;
        if (debt <= HydrationWeakness.ModerateEndDebt) return PlayerDehydrationStage.Moderate;
        if (debt <= HydrationWeakness.SevereEndDebt) return PlayerDehydrationStage.Severe;
        return PlayerDehydrationStage.Critical;
    }

    private static float Piecewise(
        float debt,
        float atZero,
        float at150,
        float at300,
        float at450,
        float at590,
        float at600)
    {
        if (debt <= HydrationWeakness.MildEndDebt)
            return Mathf.Lerp(
                atZero,
                at150,
                Mathf.InverseLerp(0f, HydrationWeakness.MildEndDebt, debt));
        if (debt <= HydrationWeakness.ModerateEndDebt)
            return Mathf.Lerp(
                at150,
                at300,
                Mathf.InverseLerp(
                    HydrationWeakness.MildEndDebt,
                    HydrationWeakness.ModerateEndDebt,
                    debt));
        if (debt <= HydrationWeakness.SevereEndDebt)
            return Mathf.Lerp(
                at300,
                at450,
                Mathf.InverseLerp(
                    HydrationWeakness.ModerateEndDebt,
                    HydrationWeakness.SevereEndDebt,
                    debt));
        if (debt <= HydrationWeakness.FinalStruggleDebt)
            return Mathf.Lerp(
                at450,
                at590,
                Mathf.InverseLerp(
                    HydrationWeakness.SevereEndDebt,
                    HydrationWeakness.FinalStruggleDebt,
                    debt));
        return Mathf.Lerp(
            at590,
            at600,
            Mathf.InverseLerp(
                HydrationWeakness.FinalStruggleDebt,
                HydrationWeakness.LethalDebt,
                debt));
    }
}
