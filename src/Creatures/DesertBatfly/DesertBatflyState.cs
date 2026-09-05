using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal static class DesertBatflyTuning
{
    // Vanilla Fly uses radius 6 and mass 0.05. Personality.Size is deliberately
    // constrained to 1.00x-1.25x so both physics and graphics stay Batfly-like.
    internal const float Radius = 6f, Mass = 0.05f;
    internal const int HivePopulation = 11, CurvePopulation = 3;
    internal const float AggressiveThreshold = 0.52f, ThirstPerTick = 0.000065f;
    internal const float AttackThirst = 0.48f, DrainRelief = 0.65f;

    // Water values are raw DryCycle hydration points, not HUD pips. Eating a
    // Desert Batfly now costs 50 points. Once an attached bat starts drinking it
    // removes 50 points per second for as long as the drain window remains active.
    internal const float MealWater = 50f, AttackWaterPerSecond = 50f;

    internal const int AttackSlots = 2, Cooldown = 1800, FailedCooldown = 240;
    internal const int ObserveTicks = 100, AttachTicks = 180, RockStun = 110;
    internal const int DrainStartTicks = 20, DrainEndTicks = 160;
    internal const int ApproachTicks = 45, CircleTicks = 55, DiveTicks = 36;
    internal const int FakeDivePullUpTicks = 14, FakeDiveTicks = 38, InterestTicks = 1000;
    internal const float ObserveThirst = 0.3f, CounterThirst = 0.2f;

    // Desert Batflies spend a little more of their idle time hanging than vanilla.
    // Personality then spreads individuals across this range instead of using one
    // species-wide timer/chance.
    internal const int RoostMinTicks = 160, RoostMaxTicks = 520;
    internal const float RoostMinChance = 0.012f, RoostMaxChance = 0.045f;

    internal const int AttackerMemory = 640, RetreatTicks = 90, ApproachRetreatTicks = 55;
    internal const float LightTargetMass = 0.55f, SightRange = 340f;
    internal const float FakeDiveChance = 0.55f, AlarmRadius = 110f;
    internal const int MaxSpikes = 4, MaxPatterns = 14;
    internal const int EmergenceTicks = 65, CurveAttempts = 80;
    internal const float SandMargin = 22f, ScavengerHostility = 0.65f;
}

internal sealed class DesertBatflyPersonality
{
    internal readonly int VisualSeed, PatternSeed, SpikeSeed;
    internal readonly float Temperament, Size, Contrast;
    internal readonly float Nerve, RoostAffinity;
    internal readonly int PatternCount, SpikeCount;
    internal readonly Color BaseColor, WingColor, SecondaryColor;

    internal DesertBatflyPersonality(int seed)
    {
        VisualSeed = seed;
        var random = new System.Random(seed);
        PatternSeed = random.Next();
        SpikeSeed = random.Next();
        Temperament = (float)random.NextDouble();

        // Nerve is a separate stable personality factor. High-Nerve animals are
        // less disturbed by mere proximity/approach, while actual attacks, weapons
        // and grabs still bypass this tolerance. A small temperament bias makes
        // harsher animals somewhat more likely to be bold without making it binary.
        var nerveRandom = new System.Random(seed ^ 0x5A17B1D3);
        Nerve = Mathf.Clamp01(Mathf.Lerp((float)nerveRandom.NextDouble(), Temperament, 0.25f));

        // RoostAffinity is intentionally independent from aggression. Two equally
        // calm/aggressive individuals can still differ in how often and how long
        // they prefer to hang.
        var roostRandom = new System.Random(seed ^ 0x3C6EF372);
        RoostAffinity = (float)roostRandom.NextDouble();

        // Size is now a readable personality trait rather than unrelated random
        // scaling: calm individuals stay near vanilla size, harsher individuals
        // trend toward the 1.25x upper bound.
        Size = Mathf.Lerp(1f, 1.25f, Temperament);
        Contrast = Mathf.Lerp(0.24f, 0.88f, Temperament);
        PatternCount = 5 + Mathf.FloorToInt(Temperament * (DesertBatflyTuning.MaxPatterns - 5));
        SpikeCount = Mathf.Clamp(
            Mathf.FloorToInt(Mathf.InverseLerp(0.42f, 1f, Temperament) * (DesertBatflyTuning.MaxSpikes + 0.99f)),
            0,
            DesertBatflyTuning.MaxSpikes);

        // Keep a sandy common ancestry while letting aggressive individuals drift
        // into ochre/red-brown. A small stable grey variation avoids clone-like
        // swarms without changing the temperament gradient.
        BaseColor = Color.Lerp(new Color(0.73f, 0.66f, 0.47f), new Color(0.45f, 0.23f, 0.15f), Temperament);
        BaseColor = Color.Lerp(BaseColor, new Color(0.50f, 0.47f, 0.39f), (float)random.NextDouble() * 0.18f);
        WingColor = Color.Lerp(new Color(0.67f, 0.60f, 0.43f), new Color(0.52f, 0.30f, 0.20f), Temperament);
        SecondaryColor = Color.Lerp(BaseColor, new Color(0.15f, 0.105f, 0.08f), Contrast);
    }

    internal bool Aggressive => Temperament >= DesertBatflyTuning.AggressiveThreshold;

    // Calm animals trend toward the high end, but RoostAffinity keeps the result
    // individual rather than turning it into another docile/aggressive switch.
    internal float RoostChance
    {
        get
        {
            float calmness = 1f - Temperament;
            float baseChance = Mathf.Lerp(
                DesertBatflyTuning.RoostMinChance,
                DesertBatflyTuning.RoostMaxChance,
                calmness);
            return baseChance * Mathf.Lerp(0.85f, 1.25f, RoostAffinity);
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

    internal DesertBatflyState(AbstractCreature creature) : base(creature)
    {
        Personality = new DesertBatflyPersonality(creature.ID.RandomSeed);
        Thirst = Mathf.Lerp(0.2f, 0.65f, Personality.Temperament);
    }

    public override string ToString()
    {
        unrecognizedSaveStrings[SaveKey] = string.Join(";", new[] {
            Personality.VisualSeed.ToString(CultureInfo.InvariantCulture),
            Thirst.ToString("R", CultureInfo.InvariantCulture),
            Cooldown.ToString(CultureInfo.InvariantCulture), Bites.ToString(CultureInfo.InvariantCulture),
            MealConsumed ? "1" : "0", InHive ? "1" : "0" });
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
    }
}
