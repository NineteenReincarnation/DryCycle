using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Persistent, fixed-size ecology ledger for one DESERTSWARMROOM.
/// Runtime event counters are deliberately bounded and are folded into long-term
/// pressure once per survived cycle; no event history or colony relationship graph exists.
/// </summary>
internal sealed class DB_ColonyState
{
    internal const float BeginMigrationThreshold = 0.70f;
    internal const float ContinueMigrationThreshold = 0.55f;
    internal const float StopMigrationThreshold = 0.40f;

    internal readonly string RegionName;
    internal readonly string RoomName;

    internal int CurrentPopulation;
    internal int PreferredPopulation;
    internal int SoftCapacity;
    internal int HardMinimumPersistence;
    internal int NaturalRecoveryCeiling;

    internal float MortalityPressure;
    internal float PredatorPressure;
    internal float EnvironmentalPressure;
    internal float ShelterFailureMemory;
    internal float MigrationPressure;
    internal float RecentWeatherStress;
    internal float RegionalWeatherStress;
    internal float RelativeHabitatStress;

    internal int LastMigrationCycle = int.MinValue;
    internal int ColonyMigrationCooldown;
    internal int LastOutboundBatch;
    internal int LastInboundBatch;
    internal int ConsecutiveBadWeatherCycles;
    internal string LastSuccessfulRefuge;
    internal bool MigrationActive;

    // Current-cycle accumulators. They are never persisted as event histories.
    internal int CycleDeaths;
    internal int CyclePredationKills;
    internal float CyclePredatorExposure;
    internal float CycleEnvironmentalExposure;
    internal float CycleShelterFailure;

    internal DB_ColonyState(string regionName, string roomName, int preferredPopulation)
    {
        RegionName = NormalizeName(regionName);
        RoomName = NormalizeName(roomName);
        ConfigurePopulation(preferredPopulation);
    }

    internal string Key => RegionName + "|" + RoomName;

    internal void ConfigurePopulation(int preferredPopulation)
    {
        PreferredPopulation = Mathf.Clamp(preferredPopulation, 4, 80);
        HardMinimumPersistence = Mathf.Clamp(
            Mathf.CeilToInt(PreferredPopulation * 0.20f), 2, Mathf.Max(2, PreferredPopulation - 1));
        SoftCapacity = Mathf.Max(PreferredPopulation + 2, Mathf.CeilToInt(PreferredPopulation * 1.32f));
        RecalculateRecoveryCeiling();
    }

    internal void RecalculateRecoveryCeiling()
    {
        int healthyCeiling = Mathf.Max(
            HardMinimumPersistence,
            Mathf.RoundToInt(PreferredPopulation * 0.60f));
        float chronicStress = Mathf.Clamp01(
            EnvironmentalPressure * 0.55f +
            PredatorPressure * 0.20f +
            MortalityPressure * 0.10f +
            ShelterFailureMemory * 0.15f);
        NaturalRecoveryCeiling = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(healthyCeiling, HardMinimumPersistence, chronicStress)),
            HardMinimumPersistence,
            PreferredPopulation);
    }

    internal void RecordDeath(bool predatorKill)
    {
        CycleDeaths = Mathf.Min(CycleDeaths + 1, 64);
        if (predatorKill)
            CyclePredationKills = Mathf.Min(CyclePredationKills + 1, 64);
    }

    internal void SamplePredatorPresence(float seconds, bool guardingHive)
    {
        if (!Finite(seconds) || seconds <= 0f) return;
        float scale = guardingHive ? 1.7f : 1f;
        CyclePredatorExposure = Mathf.Clamp01(
            CyclePredatorExposure + seconds * scale / 240f);
    }

    internal void SampleEnvironment(float migrationStress, float sampleSeconds)
    {
        if (!Finite(migrationStress) || !Finite(sampleSeconds) ||
            migrationStress <= 0f || sampleSeconds <= 0f) return;
        CycleEnvironmentalExposure = Mathf.Clamp01(
            CycleEnvironmentalExposure + Mathf.Clamp01(migrationStress) * sampleSeconds / 180f);
        RecentWeatherStress = Mathf.Max(RecentWeatherStress, Mathf.Clamp01(migrationStress));
    }

    internal void RecordShelterFailure(float severity)
    {
        if (!Finite(severity) || severity <= 0f) return;
        CycleShelterFailure = Mathf.Clamp01(CycleShelterFailure + Mathf.Clamp01(severity) * 0.24f);
    }

    /// <summary>
    /// Fold one survived cycle into the long-term memory. regionalEnvironmental is the
    /// average pressure of comparable colonies after their local environment update,
    /// preventing region-wide disasters from becoming an A->B->C migration carousel.
    /// </summary>
    internal void SettleCycle(int populationAtSettlement, float regionalEnvironmental, int cycleNumber)
    {
        CurrentPopulation = Mathf.Max(0, populationAtSettlement);
        float referencePopulation = Mathf.Max(HardMinimumPersistence, PreferredPopulation);
        float deathRate = Mathf.Clamp01(CycleDeaths / referencePopulation);
        float populationLoss = Mathf.Clamp01(
            (PreferredPopulation - CurrentPopulation) / Mathf.Max(1f, PreferredPopulation));

        float mortalityInput = Mathf.Clamp01(
            deathRate * 0.70f +
            populationLoss * Mathf.Min(0.30f, CycleDeaths * 0.08f));
        MortalityPressure = Mathf.Clamp01(MortalityPressure * 0.88f + mortalityInput * 0.52f);

        float predatorInput = Mathf.Clamp01(
            CyclePredatorExposure * 0.58f +
            Mathf.Clamp01(CyclePredationKills / 3f) * 0.62f);
        PredatorPressure = Mathf.Clamp01(PredatorPressure * 0.82f + predatorInput * 0.46f);

        bool badWeather = CycleEnvironmentalExposure >= 0.18f;
        ConsecutiveBadWeatherCycles = badWeather
            ? Mathf.Min(ConsecutiveBadWeatherCycles + 1, 8)
            : 0;
        float continuity = badWeather
            ? Mathf.Min(1.5f, 1f + ConsecutiveBadWeatherCycles * 0.12f)
            : 1f;
        float environmentInput = Mathf.Clamp01(CycleEnvironmentalExposure * continuity);
        EnvironmentalPressure = Mathf.Clamp01(
            EnvironmentalPressure * 0.80f + environmentInput * 0.44f);

        ShelterFailureMemory = Mathf.Clamp01(
            ShelterFailureMemory * 0.90f + CycleShelterFailure * 0.42f);
        RecentWeatherStress = Mathf.Clamp01(
            Mathf.Max(environmentInput, RecentWeatherStress * 0.55f));

        RegionalWeatherStress = ClampFinite01(regionalEnvironmental);
        RelativeHabitatStress = ComputeRelativeHabitatStress(
            EnvironmentalPressure,
            RegionalWeatherStress,
            ShelterFailureMemory);
        MigrationPressure = ComputeMigrationPressure(
            MortalityPressure,
            PredatorPressure,
            RelativeHabitatStress,
            ShelterFailureMemory,
            CurrentPopulation,
            PreferredPopulation);
        MigrationActive = NextMigrationState(MigrationActive, MigrationPressure);

        if (ColonyMigrationCooldown > 0) ColonyMigrationCooldown--;
        RecalculateRecoveryCeiling();

        CycleDeaths = 0;
        CyclePredationKills = 0;
        CyclePredatorExposure = 0f;
        CycleEnvironmentalExposure = 0f;
        CycleShelterFailure = 0f;
        LastOutboundBatch = 0;
        LastInboundBatch = 0;
    }

    internal static float ComputeRelativeHabitatStress(
        float localEnvironmental,
        float regionalEnvironmental,
        float shelterFailure)
    {
        localEnvironmental = ClampFinite01(localEnvironmental);
        regionalEnvironmental = ClampFinite01(regionalEnvironmental);
        shelterFailure = ClampFinite01(shelterFailure);

        // A small deadband is the anti-carousel core: equally bad colonies do not gain
        // weather migration drive merely because the absolute regional value is high.
        float relative = Mathf.Max(0f, localEnvironmental - regionalEnvironmental - 0.06f);
        float normalized = Mathf.InverseLerp(0f, 0.55f, relative);
        return Mathf.Clamp01(normalized * 0.78f + shelterFailure * relative * 0.40f);
    }

    internal static float ComputeMigrationPressure(
        float mortality,
        float predator,
        float relativeHabitat,
        float shelterFailure,
        int population,
        int preferredPopulation)
    {
        mortality = ClampFinite01(mortality);
        predator = ClampFinite01(predator);
        relativeHabitat = ClampFinite01(relativeHabitat);
        shelterFailure = ClampFinite01(shelterFailure);
        float deficit = preferredPopulation <= 0
            ? 0f
            : Mathf.Clamp01((preferredPopulation - Mathf.Max(0, population)) / (float)preferredPopulation);

        // Population deficit is deliberately weak: deaths already feed MortalityPressure.
        // It only helps distinguish a visibly collapsed colony from one that remains full.
        return Mathf.Clamp01(
            mortality * 0.31f +
            predator * 0.25f +
            relativeHabitat * 0.20f +
            shelterFailure * 0.21f +
            deficit * 0.03f);
    }

    internal static bool NextMigrationState(bool currentlyActive, float pressure)
    {
        pressure = ClampFinite01(pressure);
        if (!currentlyActive) return pressure >= BeginMigrationThreshold;
        if (pressure < StopMigrationThreshold) return false;
        return pressure >= ContinueMigrationThreshold || currentlyActive;
    }

    internal int RecommendedBatchSize()
    {
        int movable = Mathf.Max(0, CurrentPopulation - HardMinimumPersistence);
        if (movable <= 0) return 0;
        int desired = CurrentPopulation < 8
            ? 1
            : Mathf.Clamp(Mathf.RoundToInt(CurrentPopulation * 0.15f), 1, 3);
        return Mathf.Min(movable, desired);
    }

    internal float RecoveryChance()
    {
        if (CurrentPopulation >= NaturalRecoveryCeiling) return 0f;
        float pressure = Mathf.Clamp01(
            MortalityPressure * 0.20f +
            PredatorPressure * 0.25f +
            EnvironmentalPressure * 0.40f +
            ShelterFailureMemory * 0.15f);
        float baseChance = CurrentPopulation < HardMinimumPersistence ? 0.38f : 0.28f;
        // Hard persistence never becomes impossible, even in a chronically bad colony.
        float floor = CurrentPopulation < HardMinimumPersistence ? 0.10f : 0.04f;
        return Mathf.Max(floor, baseChance * Mathf.Lerp(1f, 0.28f, pressure));
    }

    internal string Serialize()
    {
        string[] v =
        {
            Encode(RegionName), Encode(RoomName),
            CurrentPopulation.ToString(CultureInfo.InvariantCulture),
            PreferredPopulation.ToString(CultureInfo.InvariantCulture),
            MortalityPressure.ToString("R", CultureInfo.InvariantCulture),
            PredatorPressure.ToString("R", CultureInfo.InvariantCulture),
            EnvironmentalPressure.ToString("R", CultureInfo.InvariantCulture),
            ShelterFailureMemory.ToString("R", CultureInfo.InvariantCulture),
            MigrationPressure.ToString("R", CultureInfo.InvariantCulture),
            LastMigrationCycle.ToString(CultureInfo.InvariantCulture),
            ColonyMigrationCooldown.ToString(CultureInfo.InvariantCulture),
            ConsecutiveBadWeatherCycles.ToString(CultureInfo.InvariantCulture),
            Encode(LastSuccessfulRefuge),
            MigrationActive ? "1" : "0",
            RecentWeatherStress.ToString("R", CultureInfo.InvariantCulture)
        };
        return string.Join(";", v);
    }

    internal static bool TryDeserialize(string text, out DB_ColonyState state)
    {
        state = null;
        if (string.IsNullOrEmpty(text)) return false;
        string[] v = text.Split(';');
        if (v.Length < 12) return false;
        string region = Decode(v[0]);
        string room = Decode(v[1]);
        if (region.Length == 0 || room.Length == 0) return false;

        int preferred = ParseInt(v[3], 14, 4, 80);
        state = new DB_ColonyState(region, room, preferred)
        {
            CurrentPopulation = ParseInt(v[2], 0, 0, 200),
            MortalityPressure = Parse01(v[4]),
            PredatorPressure = Parse01(v[5]),
            EnvironmentalPressure = Parse01(v[6]),
            ShelterFailureMemory = Parse01(v[7]),
            MigrationPressure = Parse01(v[8]),
            LastMigrationCycle = ParseInt(v[9], int.MinValue, int.MinValue, int.MaxValue),
            ColonyMigrationCooldown = ParseInt(v[10], 0, 0, 20),
            ConsecutiveBadWeatherCycles = ParseInt(v[11], 0, 0, 8),
            LastSuccessfulRefuge = v.Length > 12 ? Decode(v[12]) : string.Empty,
            MigrationActive = v.Length > 13 && v[13] == "1",
            RecentWeatherStress = v.Length > 14 ? Parse01(v[14]) : 0f
        };
        state.RecalculateRecoveryCeiling();
        return true;
    }

    internal static float ClampFinite01(float value) =>
        !Finite(value) ? 0f : Mathf.Clamp01(value);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string NormalizeName(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Mathf.Clamp(value, min, max)
            : fallback;

    private static float Parse01(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? ClampFinite01(value)
            : 0f;

    private static string Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static string Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch { return string.Empty; }
    }
}
