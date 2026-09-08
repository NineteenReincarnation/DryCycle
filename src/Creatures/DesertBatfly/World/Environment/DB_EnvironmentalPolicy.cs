using System;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Explicit environmental policy surface for soft modifiers consumed by other Desert Batfly
/// domains. This class never owns locomotion, never mutates Personality/Thirst to spoof a
/// decision, and never reaches another domain through Reflection or RuntimeDetour.
/// </summary>
internal static class DB_EnvironmentalPolicy
{
    private const float SandstormBlockerRadius = 120f;
    private const float SandstormReturningBatRadius = 340f;
    private const float MinimumActivityRangeScale = 0.28f;

    internal const int SandstormHomeRecallMinimumLeadTicks = 2800;
    internal const int DeathSandstormHomeRecallMinimumLeadTicks = 3600;
    internal const int SandstormEmergencyMinimumLeadTicks = 4200;
    internal const int DeathSandstormEmergencyMinimumLeadTicks = 5200;
    internal const int SandstormEmergencyMaxHops = 2;
    internal const int DeathSandstormEmergencyMaxHops = 1;

    internal static bool AggressionAuthorized(DB_Creature bat)
        => bat != null && (bat.Personality.Aggressive ||
            DB_EnvironmentRuntime.AllowsEnvironmentalDamageAttack(bat));

    internal static float CombatMotivation(DB_Creature bat)
    {
        if (bat == null) return 0f;
        float value = Mathf.Clamp01(bat.DesertState.Thirst);
        if (!DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence))
            return value;

        if (influence.HarassMultiplier < 1f)
            value *= Mathf.Lerp(0.28f, 1f, influence.HarassMultiplier);
        if (influence.HeatAgitation > 0f && !influence.HardSurvival)
        {
            float heatMotivation = influence.HeatAgitation *
                (1f - influence.ThermalExhaustion) * 0.38f;
            value = Mathf.Max(value, bat.DesertState.Thirst + heatMotivation);
        }
        return Mathf.Clamp01(value);
    }

    internal static bool AllowsHarassCandidate(DB_Creature bat, Creature creature)
    {
        if (bat == null || creature == null) return false;
        if (!DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence))
            return true;
        if (influence.HardSurvival) return false;

        float scale = influence.HarassMultiplier;
        if (creature is Player player && IsSandstormHomeAccessBlocker(bat, player, influence))
        {
            float defensiveDrive = Mathf.Clamp01(
                bat.Personality.Temperament * 0.55f +
                bat.Personality.Nerve * 0.25f +
                Mathf.Max(influence.HomeReturnDrive, influence.BurrowDrive) * 0.20f);
            scale = Mathf.Max(scale, Mathf.Lerp(0.62f, 0.86f, defensiveDrive));
        }

        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int clockBucket = (bat.room?.game?.clock ?? 0) / 80;
        int targetKey = creature.abstractCreature?.ID.number ?? 0;
        return Stable01(bat.Personality.VisualSeed ^ targetKey * 397 ^ clockBucket * 7919) <= scale;
    }

    internal static float RoostChanceScale(DB_Creature bat)
        => DB_EnvironmentRuntime.RoostScale(bat);

    internal static int AdjustRoostDuration(DB_Creature bat, int baseDuration)
    {
        if (bat == null || baseDuration <= 0 ||
            !DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence))
            return baseDuration;
        float scale = Mathf.Clamp(influence.RoostMultiplier, 1f, 4.5f);
        int adjusted = Mathf.RoundToInt(baseDuration * scale);
        if (DB_EnvironmentRuntime.ShouldHoldEnvironmentalRoost(bat))
            adjusted = Mathf.Max(adjusted, influence.HardSurvival ? 2400 : 1200);
        return Mathf.Clamp(adjusted, baseDuration, 4200);
    }

    internal static bool BlocksNeutralSocial(DB_Creature bat)
    {
        if (bat == null) return true;
        if (DB_EnvironmentRuntime.SuppressNeutralSocial(bat)) return true;
        if (!DB_EnvironmentRuntime.TryGetInfluence(
                bat, out DB_EnvironmentInfluence influence))
            return false;
        return ShouldSeekHome(influence) || ShouldBurrow(influence);
    }

    internal static float SocialDriveScale(DB_Creature bat)
        => DB_EnvironmentRuntime.SocialScale(bat);

    internal static float GroupCohesionScale(DB_Creature bat)
        => DB_EnvironmentRuntime.GroupCohesionScale(bat);

    internal static bool AllowsPlayChase(DB_Creature bat)
    {
        if (bat == null) return false;
        float scale = DB_EnvironmentRuntime.PlayScale(bat);
        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int bucket = (bat.room?.game?.clock ?? 0) / 90;
        return Stable01(bat.Personality.VisualSeed ^ bucket * 0x632BE5AB) <= scale;
    }

    internal static bool WithinActivityRange(DB_Creature source, DB_Creature candidate, float baseRange)
    {
        if (source?.mainBodyChunk == null || candidate?.mainBodyChunk == null) return false;
        if (!DB_EnvironmentRuntime.TryGetInfluence(
                source, out DB_EnvironmentInfluence influence))
            return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange;
        float scale = Mathf.Clamp(influence.ActivityRadiusMultiplier, MinimumActivityRangeScale, 1f);
        return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange * scale;
    }

    internal static bool ShouldSeekHome(in DB_EnvironmentInfluence influence)
    {
        if (influence.HardSurvival && influence.HomeReturnDrive >= 0.35f) return true;
        if (influence.HomeReturnDrive < 0.58f) return false;
        if (influence.Weather is DB_EnvironmentWeather.HeatWave or
            DB_EnvironmentWeather.IntenseHeat)
        {
            float retreat = Mathf.Max(influence.HeatShelterDrive, influence.ThermalExhaustion);
            return retreat + 0.10f >= influence.HeatAgitation;
        }
        return true;
    }

    internal static bool ShouldBurrow(in DB_EnvironmentInfluence influence)
    {
        if (influence.HardSurvival && influence.BurrowDrive >= 0.30f) return true;
        return influence.BurrowDrive >= 0.68f;
    }


    internal static bool ShouldSuppressNewMigration(World world, DB_ColonyState source)
    {
        if (world == null || source == null) return false;
        AbstractRoom room = DB_ColonyRuntime.FindRoom(world, source.RoomName);
        if (room == null) return false;

        DB_WeatherEcologySample sample = DB_WeatherEcology.Sample(world, room);
        DB_EnvironmentWeather weather = DB_EnvironmentProfile.Classify(sample);
        if (!IsSandstorm(weather)) return false;

        float suppression = DB_EnvironmentProfile.SandstormMigrationSuppression(weather, sample);
        return suppression >= 0.70f;
    }

    internal static bool ShouldRecallHomeForSandstorm(
        DB_EnvironmentWeather weather,
        in DB_WeatherEcologySample sample)
    {
        if (!IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;
        if (sample.ActiveIntensity > 0.08f || sample.ImmediateDanger >= 0.48f)
            return false;

        int minimumLead = weather == DB_EnvironmentWeather.DeathSandstorm
            ? DeathSandstormHomeRecallMinimumLeadTicks
            : SandstormHomeRecallMinimumLeadTicks;
        return sample.TimeUntilDangerTicks >= minimumLead &&
               sample.TimeUntilDangerTicks <= DB_EnvironmentProfile.SandstormAdvisoryTicks;
    }

    internal static bool CanConsiderSandstormOutwardRefuge(
        AbstractRoom home,
        DB_EnvironmentWeather weather,
        in DB_WeatherEcologySample sample,
        out float homeQuality)
    {
        homeQuality = 1f;
        if (home == null || !IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;
        if (sample.ActiveIntensity > 0.04f || sample.ImmediateDanger >= 0.38f)
            return false;

        int minimumLead = weather == DB_EnvironmentWeather.DeathSandstorm
            ? DeathSandstormEmergencyMinimumLeadTicks
            : SandstormEmergencyMinimumLeadTicks;
        if (sample.TimeUntilDangerTicks < minimumLead) return false;

        homeQuality = DB_RefugePolicy.HomeHiveShelterQuality(
            home, sample.HazardKind, sample.HazardId);
        float maximumAcceptableHome = weather == DB_EnvironmentWeather.DeathSandstorm
            ? 0.24f
            : 0.30f;
        return homeQuality < maximumAcceptableHome;
    }

    internal static bool AcceptSandstormEmergencyRefuge(
        DB_EnvironmentWeather weather,
        in DB_WeatherEcologySample sample,
        float homeQuality,
        in DB_RefugeTarget target)
    {
        if (!target.Valid || !IsSandstorm(weather)) return false;
        int maxHops = weather == DB_EnvironmentWeather.DeathSandstorm
            ? DeathSandstormEmergencyMaxHops
            : SandstormEmergencyMaxHops;
        float minimumImprovement = weather == DB_EnvironmentWeather.DeathSandstorm
            ? 0.24f
            : 0.18f;
        int extraMargin = weather == DB_EnvironmentWeather.DeathSandstorm
            ? 1300
            : 900;

        if (target.Route.HopCount > maxHops ||
            target.ShelterQuality < homeQuality + minimumImprovement ||
            !sample.ForecastDanger || sample.TimeUntilDangerTicks == int.MaxValue)
            return false;

        long required = (long)target.EstimatedTravelTicks + extraMargin;
        return required < sample.TimeUntilDangerTicks;
    }

    internal static float FogNavigationFamiliarityScale(
        DB_Creature bat,
        DB_EnvironmentWeather weather)
    {
        if (bat?.room?.abstractRoom == null ||
            weather is not (DB_EnvironmentWeather.Fog or DB_EnvironmentWeather.DenseFog))
            return 1f;

        DB_ColonyRuntime.IndividualRecord record =
            DB_ColonyRuntime.RecordFor(bat.abstractCreature, false);
        bool homeRoom = record != null && !string.IsNullOrEmpty(record.CurrentColony) &&
            string.Equals(record.CurrentColony, bat.room.abstractRoom.name, StringComparison.OrdinalIgnoreCase);
        if (!homeRoom) return 1f;

        bool nearHive = DB_EnvironmentExposure.NearHive(
            bat.room, bat.room.GetTilePosition(bat.mainBodyChunk.pos), 10);
        if (weather == DB_EnvironmentWeather.DenseFog)
            return nearHive ? 0.52f : 0.66f;
        return nearHive ? 0.72f : 0.82f;
    }

    internal static bool IsSandstormHomeAccessBlocker(
        DB_Creature bat,
        Player player,
        in DB_EnvironmentInfluence influence)
    {
        if (bat?.room == null || player?.mainBodyChunk == null || player.room != bat.room ||
            influence.HardSurvival ||
            influence.Weather is not (DB_EnvironmentWeather.Sandstorm or
                DB_EnvironmentWeather.DeathSandstorm) ||
            (influence.HomeReturnDrive < 0.45f && influence.BurrowDrive < 0.45f) ||
            bat.room.hives == null || bat.room.hives.Length == 0)
            return false;

        float playerRadiusSq = SandstormBlockerRadius * SandstormBlockerRadius;
        float batRadiusSq = SandstormReturningBatRadius * SandstormReturningBatRadius;
        Vector2 playerPos = player.mainBodyChunk.pos;
        Vector2 batPos = bat.mainBodyChunk.pos;
        for (int h = 0; h < bat.room.hives.Length; h++)
        {
            IntVector2[] hive = bat.room.hives[h];
            if (hive == null || hive.Length == 0) continue;
            for (int i = 0; i < hive.Length; i++)
            {
                Vector2 point = bat.room.MiddleOfTile(hive[i]);
                if ((playerPos - point).sqrMagnitude > playerRadiusSq) continue;
                if ((batPos - point).sqrMagnitude <= batRadiusSq) return true;
            }
        }
        return false;
    }

    private static bool IsSandstorm(DB_EnvironmentWeather weather)
        => weather is DB_EnvironmentWeather.Sandstorm or
                      DB_EnvironmentWeather.DeathSandstorm;

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
