using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Pure decision math for Task 09. Selection is continuous and personality-driven;
/// there is intentionally no Migrator/Scout/Leader role or runtime job state.
/// </summary>
internal static class DB_MigrationPolicy
{
    internal const int IndividualCooldownCycles = 3;
    internal const int ColonyCooldownCycles = 2;
    internal const float DestinationSwitchMargin = 0.14f;

    internal static bool CanScheduleBatch(DB_ColonyState colony)
    {
        if (colony == null || colony.ColonyMigrationCooldown > 0 ||
            colony.CurrentPopulation <= colony.HardMinimumPersistence)
            return false;
        if (!colony.MigrationActive)
            return colony.MigrationPressure >= DB_ColonyState.BeginMigrationThreshold;
        return colony.MigrationPressure >= DB_ColonyState.ContinueMigrationThreshold;
    }

    internal static float IndividualPropensity(
        DB_Personality personality,
        float physicalCapability,
        bool severeInjury,
        bool injuryRecovery,
        float activeTrauma,
        float bondStrength,
        float shelterFailure,
        int currentCycle,
        int lastMigrationCycle)
    {
        if (personality == null || severeInjury || injuryRecovery) return 0f;
        if (lastMigrationCycle != int.MinValue &&
            currentCycle - lastMigrationCycle >= 0 &&
            currentCycle - lastMigrationCycle < IndividualCooldownCycles)
            return 0f;

        physicalCapability = DB_ColonyState.ClampFinite01(physicalCapability);
        activeTrauma = DB_ColonyState.ClampFinite01(activeTrauma);
        bondStrength = DB_ColonyState.ClampFinite01(bondStrength);
        shelterFailure = DB_ColonyState.ClampFinite01(shelterFailure);

        float individualism = 1f - personality.Conformity;
        float lowRoost = 1f - personality.RoostAffinity;
        float score =
            0.16f +
            lowRoost * 0.24f +
            individualism * 0.15f +
            activeTrauma * 0.13f +
            shelterFailure * 0.16f +
            physicalCapability * 0.22f -
            bondStrength * 0.15f;

        // Nerve has only a modest effect: timid bats may want to leave but are less able
        // to commit to a long route, while very bold bats are more willing to travel.
        score += Mathf.Lerp(-0.05f, 0.06f, personality.Nerve);
        return Mathf.Clamp01(score);
    }

    internal static float DestinationSuitability(
        DB_ColonyState destination,
        float habitatSuitability,
        float travelCost01,
        float formerColonyFamiliarity,
        float bondPartnerPresence)
    {
        if (destination == null) return float.NegativeInfinity;
        habitatSuitability = DB_ColonyState.ClampFinite01(habitatSuitability);
        travelCost01 = DB_ColonyState.ClampFinite01(travelCost01);
        formerColonyFamiliarity = DB_ColonyState.ClampFinite01(formerColonyFamiliarity);
        bondPartnerPresence = DB_ColonyState.ClampFinite01(bondPartnerPresence);

        float freeCapacity = destination.SoftCapacity <= 0
            ? 0f
            : Mathf.Clamp(
                (destination.SoftCapacity - destination.CurrentPopulation) /
                (float)Mathf.Max(1, destination.SoftCapacity),
                -1f,
                1f);
        float overcrowding = destination.CurrentPopulation <= destination.SoftCapacity
            ? 0f
            : Mathf.Clamp01(
                (destination.CurrentPopulation - destination.SoftCapacity) /
                (float)Mathf.Max(1, destination.SoftCapacity));

        return
            habitatSuitability * 0.28f +
            freeCapacity * 0.17f +
            formerColonyFamiliarity * 0.05f +
            bondPartnerPresence * 0.05f -
            destination.PredatorPressure * 0.13f -
            destination.MortalityPressure * 0.10f -
            destination.EnvironmentalPressure * 0.11f -
            destination.ShelterFailureMemory * 0.08f -
            travelCost01 * 0.16f -
            overcrowding * 0.18f;
    }

    internal static bool ShouldSwitchDestination(float currentScore, float candidateScore,
        float margin = DestinationSwitchMargin)
    {
        if (float.IsNaN(candidateScore) || float.IsNegativeInfinity(candidateScore)) return false;
        if (float.IsNaN(currentScore) || float.IsNegativeInfinity(currentScore)) return true;
        margin = Mathf.Clamp(margin, 0.05f, 0.30f);
        return candidateScore >= currentScore + margin;
    }

    internal static float RegionalEnvironmentalAverage(
        System.Collections.Generic.IEnumerable<DB_ColonyState> colonies)
    {
        if (colonies == null) return 0f;
        float sum = 0f;
        int count = 0;
        foreach (DB_ColonyState colony in colonies)
        {
            if (colony == null) continue;
            sum += DB_ColonyState.ClampFinite01(colony.EnvironmentalPressure);
            count++;
        }
        return count == 0 ? 0f : Mathf.Clamp01(sum / count);
    }

    internal static float ActiveTrauma(DB_State state)
    {
        if (state == null) return 0f;
        return Mathf.Max(
            state.PlayerTraumaTicks > 0 ? state.PlayerTraumaStrength : 0f,
            state.PredatorTraumaTicks > 0 ? state.PredatorTraumaStrength : 0f);
    }
}
