using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal static class DesertBatflyTuning
{
    internal const float Radius = 6f, Mass = 0.05f;
    internal const int HivePopulation = 11, CurvePopulation = 3;
    internal const float AggressiveThreshold = 0.52f, ThirstPerTick = 0.000065f;
    internal const float AttackThirst = 0.48f, DrainRelief = 0.65f;

    internal const float MealWater = 50f, AttackWaterPerSecond = 50f;

    internal const int AttackSlots = 2, Cooldown = 1800, FailedCooldown = 240;
    internal const int ObserveTicks = 100, AttachTicks = 180, RockStun = 110;
    internal const int DrainStartTicks = 20, DrainEndTicks = 160;
    internal const int ApproachTicks = 45, CircleTicks = 55, DiveTicks = 36;
    internal const int FakeDivePullUpTicks = 14, FakeDiveTicks = 38, InterestTicks = 1000;
    internal const float ObserveThirst = 0.3f, CounterThirst = 0.2f;

    internal const int RetaliationChargeTicks = 42;
    internal const int RetaliationContactMinTicks = 16, RetaliationContactMaxTicks = 36;
    internal const int RetaliationCooldown = 300;
    internal const float RetaliationMinSpeed = 11.5f, RetaliationMaxSpeed = 15.5f;
    internal const float RetaliationMinImpact = 1.4f, RetaliationMaxImpact = 2.8f;
    internal const float RetaliationMinDrag = 0.025f, RetaliationMaxDrag = 0.065f;
    internal const float RetaliationMinPush = 0.03f, RetaliationMaxPush = 0.08f;

    // The threshold is calibrated against the exact System.Random personality pipeline:
    // seeds 0..9999 yield 494 true avengers (4.94%). Temperament/Nerve remain hard
    // eligibility gates, so the population rate cannot turn every nasty bat into one.
    internal const float VengeanceTraitThreshold = 0.715f;
    internal const float VengeanceMinTemperament = 0.70f;
    internal const float VengeanceMinNerve = 0.58f;

    // Conformity is an independent social personality axis. It never replaces the bat's
    // own decision logic; it only changes how strongly observed flock behavior weighs in.
    internal const float SocialFollowerMinConformity = 0.48f;
    internal const float SocialFollowerRange = 235f;
    internal const int SocialVengeanceGroupCap = 3;

    // PTSD is intentionally much longer lived than immediate fear. These values are
    // persisted in CreatureState so a surviving bat can remain afraid after room changes.
    internal const int TraumaMinTicks = 2400, TraumaMaxTicks = 12000;
    internal const float TraumaAggressionBlock = 0.42f;
    internal const float TraumaSevere = 0.68f;
    internal const float TraumaFearMinDistance = 210f, TraumaFearMaxDistance = 410f;

    internal const float GrabMemoryGain = 0.28f, GrabThrowBonus = 0.24f;
    internal const float GrabThrowSpeed = 6f;
    internal const int GrabMemoryMinTicks = 1200, GrabMemoryMaxTicks = 3600;
    internal const float GrabFearMinDistance = 145f, GrabFearMaxDistance = 270f;

    internal const float SandSpitTraitThreshold = 0.58f;
    internal const float SandSpitMeterMinRate = 0.0032f, SandSpitMeterMaxRate = 0.0064f;
    internal const float SandSpitMovementBonus = 0.0015f;
    internal const float SandSpitThresholdMin = 0.82f, SandSpitThresholdMax = 1.16f;
    internal const int SandSpitWindupTicks = 8;
    internal const int SandSpitCooldownMinTicks = 90, SandSpitCooldownMaxTicks = 150;
    internal const int SandWorldParticleMin = 5, SandWorldParticleMax = 8;
    internal const int SandScreenMarkMin = 4, SandScreenMarkMax = 6;
    internal const int SandScreenLifeMin = 48, SandScreenLifeMax = 78;
    internal const int SandScreenMaxConcurrentBursts = 2;

    internal const int RoostMinTicks = 160, RoostMaxTicks = 520;
    internal const float RoostMinChance = 0.012f, RoostMaxChance = 0.045f;

    internal const int AttackerMemory = 640, RetreatTicks = 90, ApproachRetreatTicks = 55;
    internal const float LightTargetMass = 0.55f, SightRange = 340f;
    internal const float AlarmRadius = 110f;
    internal const int MaxSpikes = 4, MaxPatterns = 14;
    internal const int EmergenceTicks = 65, CurveAttempts = 80;
    internal const float SandMargin = 22f, ScavengerHostility = 0.65f;
}

internal sealed class DesertBatflyPersonality
{
    internal readonly int VisualSeed, PatternSeed, SpikeSeed;
    internal readonly float Temperament, Size, Contrast;
    internal readonly float Nerve, RoostAffinity, SandSpitAffinity, VengeanceAffinity, Conformity;
    internal readonly int PatternCount, SpikeCount;
    internal readonly Color BaseColor, WingColor, SecondaryColor;

    internal DesertBatflyPersonality(int seed)
    {
        VisualSeed = seed;
        var random = new System.Random(seed);
        PatternSeed = random.Next();
        SpikeSeed = random.Next();
        Temperament = (float)random.NextDouble();

        var nerveRandom = new System.Random(seed ^ 0x5A17B1D3);
        Nerve = Mathf.Clamp01(Mathf.Lerp((float)nerveRandom.NextDouble(), Temperament, 0.25f));

        var roostRandom = new System.Random(seed ^ 0x3C6EF372);
        RoostAffinity = (float)roostRandom.NextDouble();

        var sandRandom = new System.Random(seed ^ 0x6D2B79F5);
        float innateSand = (float)sandRandom.NextDouble();
        SandSpitAffinity = Mathf.Clamp01(
            innateSand * 0.62f + Temperament * 0.28f + Nerve * 0.10f);

        var vengeanceRandom = new System.Random(seed ^ 0x2C1B3C6D);
        float innateVengeance = (float)vengeanceRandom.NextDouble();
        VengeanceAffinity = Mathf.Clamp01(
            innateVengeance * 0.55f + Temperament * 0.30f + Nerve * 0.15f);

        // Deliberately independent from aggression and courage. A vicious individual can
        // be highly individualistic, while a calm one can be strongly social.
        var conformityRandom = new System.Random(seed ^ 0x7F4A7C15);
        Conformity = (float)conformityRandom.NextDouble();

        Size = Mathf.Lerp(1f, 1.25f, Temperament);
        Contrast = Mathf.Lerp(0.24f, 0.88f, Temperament);
        PatternCount = 5 + Mathf.FloorToInt(Temperament * (DesertBatflyTuning.MaxPatterns - 5));
        SpikeCount = Mathf.Clamp(
            Mathf.FloorToInt(Mathf.InverseLerp(0.42f, 1f, Temperament) * (DesertBatflyTuning.MaxSpikes + 0.99f)),
            0,
            DesertBatflyTuning.MaxSpikes);

        BaseColor = Color.Lerp(new Color(0.73f, 0.66f, 0.47f), new Color(0.45f, 0.23f, 0.15f), Temperament);
        BaseColor = Color.Lerp(BaseColor, new Color(0.50f, 0.47f, 0.39f), (float)random.NextDouble() * 0.18f);
        WingColor = Color.Lerp(new Color(0.67f, 0.60f, 0.43f), new Color(0.52f, 0.30f, 0.20f), Temperament);
        SecondaryColor = Color.Lerp(BaseColor, new Color(0.15f, 0.105f, 0.08f), Contrast);
    }

    internal bool Aggressive => Temperament >= DesertBatflyTuning.AggressiveThreshold;
    internal float AggressionDrive => Mathf.InverseLerp(DesertBatflyTuning.AggressiveThreshold, 1f, Temperament);

    internal bool CanExtremeVengeance =>
        Temperament >= DesertBatflyTuning.VengeanceMinTemperament &&
        Nerve >= DesertBatflyTuning.VengeanceMinNerve &&
        VengeanceAffinity >= DesertBatflyTuning.VengeanceTraitThreshold;

    internal float VengeanceDrive => Mathf.Clamp01(
        Mathf.InverseLerp(
            DesertBatflyTuning.VengeanceTraitThreshold,
            1f,
            VengeanceAffinity) * 0.62f +
        Mathf.InverseLerp(
            DesertBatflyTuning.VengeanceMinTemperament,
            1f,
            Temperament) * 0.23f +
        Mathf.InverseLerp(
            DesertBatflyTuning.VengeanceMinNerve,
            1f,
            Nerve) * 0.15f);

    internal float SocialFearScale => Mathf.Lerp(0.72f, 1.48f, Conformity);

    internal bool CanSandSpit => SandSpitAffinity >= DesertBatflyTuning.SandSpitTraitThreshold;
    internal float SandSpitDrive => Mathf.InverseLerp(
        DesertBatflyTuning.SandSpitTraitThreshold,
        1f,
        SandSpitAffinity);
    internal float SandSpitMeterRate => Mathf.Lerp(
        DesertBatflyTuning.SandSpitMeterMinRate,
        DesertBatflyTuning.SandSpitMeterMaxRate,
        SandSpitDrive) * Mathf.Lerp(0.92f, 1.12f, Nerve);
    internal float SandSpitIntensity => Mathf.Clamp01(
        Mathf.Lerp(0.35f, 1f, SandSpitDrive) * Mathf.Lerp(0.9f, 1.08f, Temperament));

    internal float FakeDiveChance => Mathf.Lerp(0.68f, 0.26f, AggressionDrive);
    internal float RetaliationChance => Mathf.Lerp(0.38f, 0.92f, AggressionDrive);
    internal int ObserveDuration => Mathf.RoundToInt(Mathf.Lerp(120f, 72f, AggressionDrive));
    internal int RetaliationContactDuration => Mathf.RoundToInt(Mathf.Lerp(
        DesertBatflyTuning.RetaliationContactMinTicks,
        DesertBatflyTuning.RetaliationContactMaxTicks,
        AggressionDrive));
    internal float RetaliationSpeed => Mathf.Lerp(
        DesertBatflyTuning.RetaliationMinSpeed,
        DesertBatflyTuning.RetaliationMaxSpeed,
        AggressionDrive);
    internal float RetaliationImpact => Mathf.Lerp(
        DesertBatflyTuning.RetaliationMinImpact,
        DesertBatflyTuning.RetaliationMaxImpact,
        AggressionDrive);
    internal float RetaliationDrag => Mathf.Lerp(
        DesertBatflyTuning.RetaliationMinDrag,
        DesertBatflyTuning.RetaliationMaxDrag,
        AggressionDrive);
    internal float RetaliationPush => Mathf.Lerp(
        DesertBatflyTuning.RetaliationMinPush,
        DesertBatflyTuning.RetaliationMaxPush,
        AggressionDrive);

    internal float RoostChance
    {
        get
        {
            float calmness = 1f - Temperament;
            float baseChance = Mathf.Lerp(
                DesertBatflyTuning.RoostMinChance,
                DesertBatflyTuning.RoostMaxChance,
                calmness);
            // Social animals are slightly more willing to join a roosting culture, but
            // this is intentionally weak: RoostAffinity and the bat's own AI still lead.
            float social = Mathf.Lerp(0.94f, 1.10f, Conformity);
            return baseChance * Mathf.Lerp(0.85f, 1.25f, RoostAffinity) * social;
        }
    }

    internal int RoostDuration
    {
        get
        {
            float calmness = 1f - Temperament;
            float baseTicks = Mathf.Lerp(
                DesertBatflyTuning.RoostMinTicks,
                DesertBatflyTuning.RoostMaxTicks,
                calmness);
            return Mathf.RoundToInt(baseTicks * Mathf.Lerp(0.90f, 1.25f, RoostAffinity));
        }
    }
}

internal sealed class DesertBatflyState : HealthState
{
    private const string SaveKey = "DCDesertBatflyV1";
    internal DesertBatflyPersonality Personality;
    internal float Thirst;
    internal int Cooldown, Bites = 3;
    internal bool MealConsumed, InHive;

    internal int GrabMemoryPlayer = -1, GrabMemoryTicks;
    internal float GrabMemoryStrength;

    // Persistent PTSD-like memories. Player identity uses co-op player number; Peach
    // identity uses its AbstractCreature EntityID.number. Only one of each is retained,
    // keeping state fixed-size and save-safe.
    internal int PlayerTraumaPlayer = -1, PlayerTraumaTicks;
    internal float PlayerTraumaStrength;
    internal int PredatorTraumaId = int.MinValue, PredatorTraumaTicks;
    internal float PredatorTraumaStrength;

    internal DesertBatflyState(AbstractCreature creature) : base(creature)
    {
        Personality = new DesertBatflyPersonality(creature.ID.RandomSeed);
        Thirst = Mathf.Lerp(0.2f, 0.65f, Personality.Temperament);
    }

    internal bool HasTrauma =>
        (PlayerTraumaTicks > 0 && PlayerTraumaStrength > 0f) ||
        (PredatorTraumaTicks > 0 && PredatorTraumaStrength > 0f);

    internal void TickTrauma()
    {
        if (PlayerTraumaTicks > 0 && --PlayerTraumaTicks <= 0)
        {
            PlayerTraumaPlayer = -1;
            PlayerTraumaStrength = 0f;
            PlayerTraumaTicks = 0;
        }

        if (PredatorTraumaTicks > 0 && --PredatorTraumaTicks <= 0)
        {
            PredatorTraumaId = int.MinValue;
            PredatorTraumaStrength = 0f;
            PredatorTraumaTicks = 0;
        }
    }

    public override string ToString()
    {
        unrecognizedSaveStrings[SaveKey] = string.Join(";", new[] {
            Personality.VisualSeed.ToString(CultureInfo.InvariantCulture),
            Thirst.ToString("R", CultureInfo.InvariantCulture),
            Cooldown.ToString(CultureInfo.InvariantCulture),
            Bites.ToString(CultureInfo.InvariantCulture),
            MealConsumed ? "1" : "0",
            InHive ? "1" : "0",
            GrabMemoryPlayer.ToString(CultureInfo.InvariantCulture),
            GrabMemoryStrength.ToString("R", CultureInfo.InvariantCulture),
            GrabMemoryTicks.ToString(CultureInfo.InvariantCulture),
            PlayerTraumaPlayer.ToString(CultureInfo.InvariantCulture),
            PlayerTraumaStrength.ToString("R", CultureInfo.InvariantCulture),
            PlayerTraumaTicks.ToString(CultureInfo.InvariantCulture),
            PredatorTraumaId.ToString(CultureInfo.InvariantCulture),
            PredatorTraumaStrength.ToString("R", CultureInfo.InvariantCulture),
            PredatorTraumaTicks.ToString(CultureInfo.InvariantCulture) });
        return base.ToString();
    }

    public override void LoadFromString(string[] data)
    {
        base.LoadFromString(data);
        if (!unrecognizedSaveStrings.TryGetValue(SaveKey, out string saved)) return;
        string[] values = saved.Split(';');
        if (values.Length < 5) return;
        if (int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            Personality = new DesertBatflyPersonality(seed);
        if (float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float thirst) &&
            !float.IsNaN(thirst) && !float.IsInfinity(thirst)) Thirst = Mathf.Clamp01(thirst);
        if (int.TryParse(values[2], out int cooldown)) Cooldown = Mathf.Clamp(cooldown, 0, DesertBatflyTuning.Cooldown);
        if (int.TryParse(values[3], out int bites)) Bites = Mathf.Clamp(bites, 0, 3);
        MealConsumed = values[4] == "1";
        InHive = values.Length > 5 && values[5] == "1";

        GrabMemoryPlayer = -1;
        GrabMemoryStrength = 0f;
        GrabMemoryTicks = 0;
        PlayerTraumaPlayer = -1;
        PlayerTraumaStrength = 0f;
        PlayerTraumaTicks = 0;
        PredatorTraumaId = int.MinValue;
        PredatorTraumaStrength = 0f;
        PredatorTraumaTicks = 0;

        if (values.Length > 6 && int.TryParse(values[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int player))
            GrabMemoryPlayer = Mathf.Max(-1, player);
        if (values.Length > 7 && float.TryParse(values[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float strength) &&
            !float.IsNaN(strength) && !float.IsInfinity(strength))
            GrabMemoryStrength = Mathf.Clamp01(strength);
        if (values.Length > 8 && int.TryParse(values[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int memoryTicks))
            GrabMemoryTicks = Mathf.Clamp(memoryTicks, 0, DesertBatflyTuning.GrabMemoryMaxTicks);

        if (values.Length > 9 && int.TryParse(values[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int traumaPlayer))
            PlayerTraumaPlayer = Mathf.Max(-1, traumaPlayer);
        if (values.Length > 10 && float.TryParse(values[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float playerTrauma) &&
            !float.IsNaN(playerTrauma) && !float.IsInfinity(playerTrauma))
            PlayerTraumaStrength = Mathf.Clamp01(playerTrauma);
        if (values.Length > 11 && int.TryParse(values[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playerTraumaTicks))
            PlayerTraumaTicks = Mathf.Clamp(playerTraumaTicks, 0, DesertBatflyTuning.TraumaMaxTicks);

        if (values.Length > 12 && int.TryParse(values[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out int predatorTraumaId))
            PredatorTraumaId = predatorTraumaId;
        if (values.Length > 13 && float.TryParse(values[13], NumberStyles.Float, CultureInfo.InvariantCulture, out float predatorTrauma) &&
            !float.IsNaN(predatorTrauma) && !float.IsInfinity(predatorTrauma))
            PredatorTraumaStrength = Mathf.Clamp01(predatorTrauma);
        if (values.Length > 14 && int.TryParse(values[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out int predatorTraumaTicks))
            PredatorTraumaTicks = Mathf.Clamp(predatorTraumaTicks, 0, DesertBatflyTuning.TraumaMaxTicks);

        if (GrabMemoryTicks <= 0 || GrabMemoryStrength <= 0f)
        {
            GrabMemoryPlayer = -1;
            GrabMemoryStrength = 0f;
            GrabMemoryTicks = 0;
        }
        if (PlayerTraumaTicks <= 0 || PlayerTraumaStrength <= 0f)
        {
            PlayerTraumaPlayer = -1;
            PlayerTraumaStrength = 0f;
            PlayerTraumaTicks = 0;
        }
        if (PredatorTraumaTicks <= 0 || PredatorTraumaStrength <= 0f)
        {
            PredatorTraumaId = int.MinValue;
            PredatorTraumaStrength = 0f;
            PredatorTraumaTicks = 0;
        }
    }
}
