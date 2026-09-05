using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal static class DesertBatflyTuning
{
    internal const float Radius = 8.5f, Mass = 0.095f;
    internal const int HivePopulation = 7, CurvePopulation = 2;
    internal const float AggressiveThreshold = 0.52f, ThirstPerTick = 0.000065f;
    internal const float AttackThirst = 0.48f, DrainRelief = 0.65f;
    internal const float MealWater = 200f, AttackWater = 30f;
    internal const int AttackSlots = 2, Cooldown = 1800, FailedCooldown = 240;
    internal const int ObserveTicks = 100, AttachTicks = 18, RockStun = 110;
    internal const int ApproachTicks = 45, CircleTicks = 55, DiveTicks = 36;
    internal const int FakeDivePullUpTicks = 14, FakeDiveTicks = 38, InterestTicks = 1000;
    internal const float ObserveThirst = 0.3f, CounterThirst = 0.2f;
    internal const int DocileRoostTicks = 380, AggressiveRoostTicks = 110;
    internal const float DocileRoostChance = 0.03f, AggressiveRoostChance = 0.008f;
    internal const int AttackerMemory = 640, RetreatTicks = 90;
    internal const float LightTargetMass = 0.55f, SightRange = 340f;
    internal const float FakeDiveChance = 0.55f, AlarmRadius = 110f;
    internal const int MaxSpikes = 6, MaxPatterns = 9;
    internal const float WingLength = 23f, WingRate = 0.48f;
    internal const int EmergenceTicks = 65, CurveAttempts = 80;
    internal const float SandMargin = 22f, ScavengerHostility = 0.65f;
}

internal sealed class DesertBatflyPersonality
{
    internal readonly int VisualSeed, PatternSeed, SpikeSeed;
    internal readonly float Temperament, Size, Contrast;
    internal readonly int PatternCount, SpikeCount;
    internal readonly Color BaseColor, WingColor, SecondaryColor;

    internal DesertBatflyPersonality(int seed)
    {
        VisualSeed = seed;
        var random = new System.Random(seed);
        PatternSeed = random.Next();
        SpikeSeed = random.Next();
        Temperament = (float)random.NextDouble();
        Size = Mathf.Lerp(0.94f, 1.08f, (float)random.NextDouble());
        Contrast = Mathf.Lerp(0.2f, 0.82f, Temperament);
        PatternCount = 2 + Mathf.FloorToInt(Temperament * (DesertBatflyTuning.MaxPatterns - 1));
        SpikeCount = 1 + Mathf.FloorToInt(Temperament * DesertBatflyTuning.MaxSpikes);
        BaseColor = Color.Lerp(new Color(0.72f, 0.65f, 0.46f), new Color(0.43f, 0.22f, 0.15f), Temperament);
        BaseColor = Color.Lerp(BaseColor, new Color(0.50f, 0.47f, 0.39f), (float)random.NextDouble() * 0.2f);
        WingColor = Color.Lerp(new Color(0.65f, 0.59f, 0.43f), new Color(0.49f, 0.29f, 0.20f), Temperament);
        SecondaryColor = Color.Lerp(BaseColor, new Color(0.16f, 0.12f, 0.10f), Contrast);
    }

    internal bool Aggressive => Temperament >= DesertBatflyTuning.AggressiveThreshold;
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
