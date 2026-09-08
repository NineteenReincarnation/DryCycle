using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal enum DB_Sex { Male, Female }

internal sealed class DB_Personality
{
    internal readonly DB_Sex Sex;
    internal float BodyVisualScale => Sex == DB_Sex.Male ? 0.98f : 1.035f;
    internal float WingLengthScale => Sex == DB_Sex.Male ? 1.055f : 1f;
    internal float WingWidthScale => Sex == DB_Sex.Female ? 1.04f : 1f;
    internal float MarkProminence => Sex == DB_Sex.Male ? 1.10f : 1f;
    internal readonly int VisualSeed, PatternSeed, SpikeSeed;
    internal readonly float Temperament, Size, Contrast;
    internal readonly float Nerve, RoostAffinity, SandSpitAffinity, VengeanceAffinity, Conformity;
    internal readonly int PatternCount, SpikeCount;
    internal readonly Color BaseColor, WingColor, SecondaryColor;

    internal DB_Personality(int seed)
    {
        Sex = new System.Random(seed ^ 0x147AD639).NextDouble() < 0.48
            ? DB_Sex.Male : DB_Sex.Female;
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
        PatternCount = 5 + Mathf.FloorToInt(Temperament * (DB_Tuning.MaxPatterns - 5));
        SpikeCount = Mathf.Clamp(
            Mathf.FloorToInt(Mathf.InverseLerp(0.42f, 1f, Temperament) * (DB_Tuning.MaxSpikes + 0.99f)),
            0,
            DB_Tuning.MaxSpikes);

        BaseColor = Color.Lerp(new Color(0.73f, 0.66f, 0.47f), new Color(0.45f, 0.23f, 0.15f), Temperament);
        BaseColor = Color.Lerp(BaseColor, new Color(0.50f, 0.47f, 0.39f), (float)random.NextDouble() * 0.18f);
        WingColor = Color.Lerp(new Color(0.67f, 0.60f, 0.43f), new Color(0.52f, 0.30f, 0.20f), Temperament);
        SecondaryColor = Color.Lerp(BaseColor, new Color(0.15f, 0.105f, 0.08f), Contrast);
    }

    internal bool Aggressive => Temperament >= DB_Tuning.AggressiveThreshold;
    internal float AggressionDrive => Mathf.InverseLerp(DB_Tuning.AggressiveThreshold, 1f, Temperament);

    internal bool CanExtremeVengeance =>
        Temperament >= DB_Tuning.VengeanceMinTemperament &&
        Nerve >= DB_Tuning.VengeanceMinNerve &&
        VengeanceAffinity >= DB_Tuning.VengeanceTraitThreshold;

    internal float VengeanceDrive => Mathf.Clamp01(
        Mathf.InverseLerp(
            DB_Tuning.VengeanceTraitThreshold,
            1f,
            VengeanceAffinity) * 0.62f +
        Mathf.InverseLerp(
            DB_Tuning.VengeanceMinTemperament,
            1f,
            Temperament) * 0.23f +
        Mathf.InverseLerp(
            DB_Tuning.VengeanceMinNerve,
            1f,
            Nerve) * 0.15f);

    internal float SocialFearScale => Mathf.Lerp(0.72f, 1.48f, Conformity);

    // Computed from the established personality axes: no extra random trait stream is added.
    // Temperament drives raw struggle, Nerve drives effective escape attempts, and larger
    // individuals are somewhat harder for the player to control.
    internal float EscapeDrive => Mathf.Clamp01(
        Temperament * 0.45f +
        Nerve * 0.35f +
        Mathf.InverseLerp(1f, 1.25f, Size) * 0.20f);

    // Rescue willingness is social courage, not ordinary aggression. Conformity and Nerve lead,
    // while Temperament contributes willingness to physically challenge the holder.
    internal float RescueDrive => Mathf.Clamp01(
        Conformity * 0.38f +
        Nerve * 0.36f +
        Temperament * 0.26f);

    internal bool CanSandSpit => SandSpitAffinity >= DB_Tuning.SandSpitTraitThreshold;
    internal float SandSpitDrive => Mathf.InverseLerp(
        DB_Tuning.SandSpitTraitThreshold,
        1f,
        SandSpitAffinity);
    internal float SandSpitMeterRate => Mathf.Lerp(
        DB_Tuning.SandSpitMeterMinRate,
        DB_Tuning.SandSpitMeterMaxRate,
        SandSpitDrive) * Mathf.Lerp(0.92f, 1.12f, Nerve);
    internal float SandSpitIntensity => Mathf.Clamp01(
        Mathf.Lerp(0.35f, 1f, SandSpitDrive) * Mathf.Lerp(0.9f, 1.08f, Temperament));

    internal float FakeDiveChance => Mathf.Lerp(0.68f, 0.26f, AggressionDrive);
    internal float RetaliationChance => Mathf.Lerp(0.38f, 0.92f, AggressionDrive);
    internal int ObserveDuration => Mathf.RoundToInt(Mathf.Lerp(120f, 72f, AggressionDrive));
    internal int RetaliationContactDuration => Mathf.RoundToInt(Mathf.Lerp(
        DB_Tuning.RetaliationContactMinTicks,
        DB_Tuning.RetaliationContactMaxTicks,
        AggressionDrive));
    internal float RetaliationSpeed => Mathf.Lerp(
        DB_Tuning.RetaliationMinSpeed,
        DB_Tuning.RetaliationMaxSpeed,
        AggressionDrive);
    internal float RetaliationImpact => Mathf.Lerp(
        DB_Tuning.RetaliationMinImpact,
        DB_Tuning.RetaliationMaxImpact,
        AggressionDrive);
    internal float RetaliationDrag => Mathf.Lerp(
        DB_Tuning.RetaliationMinDrag,
        DB_Tuning.RetaliationMaxDrag,
        AggressionDrive);
    internal float RetaliationPush => Mathf.Lerp(
        DB_Tuning.RetaliationMinPush,
        DB_Tuning.RetaliationMaxPush,
        AggressionDrive);

    internal float RoostChance
    {
        get
        {
            float calmness = 1f - Temperament;
            float baseChance = Mathf.Lerp(
                DB_Tuning.RoostMinChance,
                DB_Tuning.RoostMaxChance,
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
                DB_Tuning.RoostMinTicks,
                DB_Tuning.RoostMaxTicks,
                calmness);
            return Mathf.RoundToInt(baseTicks * Mathf.Lerp(0.90f, 1.25f, RoostAffinity));
        }
    }
}
