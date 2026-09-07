using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// All OPEN values live here. Times are simulation ticks (40 ticks/second).
internal sealed class DB_State : HealthState
{
    private const string SaveKey = "DCDesertBatflyV1";
    internal DB_Personality Personality;
    internal float Thirst;
    internal float LeftWingInjury, RightWingInjury;
    internal float WingMean => (LeftWingInjury + RightWingInjury) * 0.5f;
    internal float WingAsymmetry => Mathf.Abs(LeftWingInjury - RightWingInjury);
    internal float WingBias => RightWingInjury - LeftWingInjury;

    internal void RecoverWings(float amount)
    {
        if (!alive || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f) return;
        LeftWingInjury = Mathf.Max(0f, LeftWingInjury - amount);
        RightWingInjury = Mathf.Max(0f, RightWingInjury - amount);
    }

    public override void CycleTick()
    {
        base.CycleTick();
        RecoverWings(0.35f);
    }
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

    internal DB_State(AbstractCreature creature) : base(creature)
    {
        Personality = new DB_Personality(creature.ID.RandomSeed);
        Thirst = Mathf.Lerp(0.2f, 0.65f, Personality.Temperament);
    }

    internal EntityID? SocialBondTarget, GriefThreatIdentity;
    internal float SocialBondStrength, GriefStrength;
    internal int GriefTicks;

    internal float GriefAnger => Personality.Temperament * Personality.Nerve;
    internal float GriefAttackScale => 1f - GriefStrength * Mathf.Lerp(0.55f, 0.25f, GriefAnger);
    internal float GriefRoostScale => 1f + GriefStrength * Mathf.Lerp(0.40f, 0.15f, GriefAnger);

    internal float BondStrength(EntityID target) => SocialBondTarget.HasValue &&
        SocialBondTarget.Value.spawner == target.spawner && SocialBondTarget.Value.number == target.number
            ? SocialBondStrength : 0f;

    internal bool StrengthenBond(EntityID candidate, float gain)
    {
        if (float.IsNaN(gain) || float.IsInfinity(gain) || gain <= 0f) return false;
        gain = Mathf.Clamp01(gain);
        if (BondStrength(candidate) > 0f) SocialBondStrength = Mathf.Clamp01(SocialBondStrength + gain);
        else
        {
            if (SocialBondTarget.HasValue && gain < SocialBondStrength + 0.12f) return false;
            SocialBondTarget = candidate;
            SocialBondStrength = gain;
        }
        return true;
    }

    // Clearing the observed dead target also makes repeated delivery idempotent.
    internal float BeginGrief(EntityID victim, EntityID? killer)
    {
        float strength = BondStrength(victim);
        if (strength <= 0f) return 0f;
        SocialBondTarget = null;
        SocialBondStrength = 0f;
        if (strength < 0.30f) return 0f;
        GriefStrength = Mathf.Max(GriefStrength, strength);
        GriefTicks = Mathf.Max(GriefTicks, Mathf.Clamp(Mathf.RoundToInt(
            Mathf.Lerp(1200f, 4000f, strength) * Mathf.Lerp(0.96f, 1.04f, Personality.RoostAffinity)), 1200, 4000));
        GriefThreatIdentity = killer;
        return Mathf.Lerp(0.08f, 0.30f, strength);
    }

    internal void TickGrief()
    {
        if (GriefTicks <= 0) return;
        GriefStrength *= (GriefTicks - 1f) / GriefTicks;
        if (--GriefTicks == 0) { GriefStrength = 0f; GriefThreatIdentity = null; }
    }

    private static string SaveIdentity(EntityID? id) => id.HasValue
        ? id.Value.spawner.ToString(CultureInfo.InvariantCulture) + "," + id.Value.number.ToString(CultureInfo.InvariantCulture) : "";

    private static EntityID? LoadIdentity(string text)
    {
        string[] parts = text.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spawner) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? new EntityID(spawner, number) : (EntityID?)null;
    }

    private static float LoadStrength(string text) => float.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out float value) && !float.IsNaN(value) && !float.IsInfinity(value)
            ? Mathf.Clamp01(value) : 0f;

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
            PredatorTraumaTicks.ToString(CultureInfo.InvariantCulture),
            SaveIdentity(SocialBondTarget), SocialBondStrength.ToString("R", CultureInfo.InvariantCulture),
            GriefStrength.ToString("R", CultureInfo.InvariantCulture), GriefTicks.ToString(CultureInfo.InvariantCulture),
            SaveIdentity(GriefThreatIdentity),
            LeftWingInjury.ToString("R", CultureInfo.InvariantCulture),
            RightWingInjury.ToString("R", CultureInfo.InvariantCulture) });
        return base.ToString();
    }

    public override void LoadFromString(string[] data)
    {
        LeftWingInjury = RightWingInjury = 0f;
        SocialBondTarget = GriefThreatIdentity = null;
        SocialBondStrength = GriefStrength = 0f;
        GriefTicks = 0;
        base.LoadFromString(data);
        if (!unrecognizedSaveStrings.TryGetValue(SaveKey, out string saved)) return;
        string[] values = saved.Split(';');
        if (values.Length < 5) return;
        if (int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            Personality = new DB_Personality(seed);
        if (float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float thirst) &&
            !float.IsNaN(thirst) && !float.IsInfinity(thirst)) Thirst = Mathf.Clamp01(thirst);
        if (int.TryParse(values[2], out int cooldown)) Cooldown = Mathf.Clamp(cooldown, 0, DB_Tuning.Cooldown);
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
            GrabMemoryTicks = Mathf.Clamp(memoryTicks, 0, DB_Tuning.GrabMemoryMaxTicks);

        if (values.Length > 9 && int.TryParse(values[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int traumaPlayer))
            PlayerTraumaPlayer = Mathf.Max(-1, traumaPlayer);
        if (values.Length > 10 && float.TryParse(values[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float playerTrauma) &&
            !float.IsNaN(playerTrauma) && !float.IsInfinity(playerTrauma))
            PlayerTraumaStrength = Mathf.Clamp01(playerTrauma);
        if (values.Length > 11 && int.TryParse(values[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playerTraumaTicks))
            PlayerTraumaTicks = Mathf.Clamp(playerTraumaTicks, 0, DB_Tuning.TraumaMaxTicks);

        if (values.Length > 12 && int.TryParse(values[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out int predatorTraumaId))
            PredatorTraumaId = predatorTraumaId;
        if (values.Length > 13 && float.TryParse(values[13], NumberStyles.Float, CultureInfo.InvariantCulture, out float predatorTrauma) &&
            !float.IsNaN(predatorTrauma) && !float.IsInfinity(predatorTrauma))
            PredatorTraumaStrength = Mathf.Clamp01(predatorTrauma);
        if (values.Length > 14 && int.TryParse(values[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out int predatorTraumaTicks))
            PredatorTraumaTicks = Mathf.Clamp(predatorTraumaTicks, 0, DB_Tuning.TraumaMaxTicks);

        if (values.Length > 16)
        {
            SocialBondTarget = LoadIdentity(values[15]);
            SocialBondStrength = LoadStrength(values[16]);
            if (!SocialBondTarget.HasValue || SocialBondStrength <= 0f)
            { SocialBondTarget = null; SocialBondStrength = 0f; }
        }
        if (values.Length > 18)
        {
            GriefStrength = LoadStrength(values[17]);
            if (int.TryParse(values[18], out int griefTicks)) GriefTicks = Mathf.Clamp(griefTicks, 0, 4000);
            if (values.Length > 19) GriefThreatIdentity = LoadIdentity(values[19]);
            if (GriefTicks <= 0 || GriefStrength <= 0f)
            { GriefStrength = 0f; GriefTicks = 0; GriefThreatIdentity = null; }
        }

        if (values.Length > 20) LeftWingInjury = LoadStrength(values[20]);
        if (values.Length > 21) RightWingInjury = LoadStrength(values[21]);

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
