using System;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Explicit R5 policy surface for Task13 soft modifiers consumed by other Desert Batfly
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

    internal static bool AggressionAuthorized(DesertBatfly bat)
        => bat != null && (bat.Personality.Aggressive ||
            DesertBatflyEnvironmentalBehavior.AllowsEnvironmentalDamageAttack(bat));

    internal static float CombatMotivation(DesertBatfly bat)
    {
        if (bat == null) return 0f;
        float value = Mathf.Clamp01(bat.DesertState.Thirst);
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
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

    internal static bool AllowsHarassCandidate(DesertBatfly bat, Creature creature)
    {
        if (bat == null || creature == null) return false;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
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

    internal static float RoostChanceScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.RoostScale(bat);

    internal static int AdjustRoostDuration(DesertBatfly bat, int baseDuration)
    {
        if (bat == null || baseDuration <= 0 ||
            !DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return baseDuration;
        float scale = Mathf.Clamp(influence.RoostMultiplier, 1f, 4.5f);
        int adjusted = Mathf.RoundToInt(baseDuration * scale);
        if (DesertBatflyEnvironmentalBehavior.ShouldHoldEnvironmentalRoost(bat))
            adjusted = Mathf.Max(adjusted, influence.HardSurvival ? 2400 : 1200);
        return Mathf.Clamp(adjusted, baseDuration, 4200);
    }

    internal static bool BlocksNeutralSocial(DesertBatfly bat)
    {
        if (bat == null) return true;
        if (DesertBatflyEnvironmentalBehavior.SuppressNeutralSocial(bat)) return true;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return false;
        return ShouldSeekHome(influence) || ShouldBurrow(influence);
    }

    internal static float SocialDriveScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.SocialScale(bat);

    internal static float GroupCohesionScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.GroupCohesionScale(bat);

    internal static bool AllowsPlayChase(DesertBatfly bat)
    {
        if (bat == null) return false;
        float scale = DesertBatflyEnvironmentalBehavior.PlayScale(bat);
        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int bucket = (bat.room?.game?.clock ?? 0) / 90;
        return Stable01(bat.Personality.VisualSeed ^ bucket * 0x632BE5AB) <= scale;
    }

    internal static bool WithinActivityRange(DesertBatfly source, DesertBatfly candidate, float baseRange)
    {
        if (source?.mainBodyChunk == null || candidate?.mainBodyChunk == null) return false;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                source, out DesertBatflyEnvironmentalInfluence influence))
            return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange;
        float scale = Mathf.Clamp(influence.ActivityRadiusMultiplier, MinimumActivityRangeScale, 1f);
        return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange * scale;
    }

    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.HomeReturnDrive >= 0.35f) return true;
        if (influence.HomeReturnDrive < 0.58f) return false;
        if (influence.Weather is DesertBatflyEnvironmentalWeather.HeatWave or
            DesertBatflyEnvironmentalWeather.IntenseHeat)
        {
            float retreat = Mathf.Max(influence.HeatShelterDrive, influence.ThermalExhaustion);
            return retreat + 0.10f >= influence.HeatAgitation;
        }
        return true;
    }

    internal static bool ShouldBurrow(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.BurrowDrive >= 0.30f) return true;
        return influence.BurrowDrive >= 0.68f;
    }


    internal static bool ShouldSuppressNewMigration(World world, DB_ColonyState source)
    {
        if (world == null || source == null) return false;
        AbstractRoom room = DB_ColonyRuntime.FindRoom(world, source.RoomName);
        if (room == null) return false;

        DesertBatflyWeatherEcologySample sample = DesertBatflyWeatherEcology.Sample(world, room);
        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(sample);
        if (!IsSandstorm(weather)) return false;

        float suppression = DesertBatflyEnvironmentalProfile.SandstormMigrationSuppression(weather, sample);
        return suppression >= 0.70f;
    }

    internal static bool ShouldRecallHomeForSandstorm(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample)
    {
        if (!IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;
        if (sample.ActiveIntensity > 0.08f || sample.ImmediateDanger >= 0.48f)
            return false;

        int minimumLead = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormHomeRecallMinimumLeadTicks
            : SandstormHomeRecallMinimumLeadTicks;
        return sample.TimeUntilDangerTicks >= minimumLead &&
               sample.TimeUntilDangerTicks <= DesertBatflyEnvironmentalProfile.SandstormAdvisoryTicks;
    }

    internal static bool CanConsiderSandstormOutwardRefuge(
        AbstractRoom home,
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample,
        out float homeQuality)
    {
        homeQuality = 1f;
        if (home == null || !IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;
        if (sample.ActiveIntensity > 0.04f || sample.ImmediateDanger >= 0.38f)
            return false;

        int minimumLead = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormEmergencyMinimumLeadTicks
            : SandstormEmergencyMinimumLeadTicks;
        if (sample.TimeUntilDangerTicks < minimumLead) return false;

        homeQuality = DesertBatflyRefuge.HomeHiveShelterQuality(
            home, sample.HazardKind, sample.HazardId);
        float maximumAcceptableHome = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? 0.24f
            : 0.30f;
        return homeQuality < maximumAcceptableHome;
    }

    internal static bool AcceptSandstormEmergencyRefuge(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample,
        float homeQuality,
        in DesertBatflyRefugeTarget target)
    {
        if (!target.Valid || !IsSandstorm(weather)) return false;
        int maxHops = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormEmergencyMaxHops
            : SandstormEmergencyMaxHops;
        float minimumImprovement = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? 0.24f
            : 0.18f;
        int extraMargin = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
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
        DesertBatfly bat,
        DesertBatflyEnvironmentalWeather weather)
    {
        if (bat?.room?.abstractRoom == null ||
            weather is not (DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog))
            return 1f;

        DB_ColonyRuntime.IndividualRecord record =
            DB_ColonyRuntime.RecordFor(bat.abstractCreature, false);
        bool homeRoom = record != null && !string.IsNullOrEmpty(record.CurrentColony) &&
            string.Equals(record.CurrentColony, bat.room.abstractRoom.name, StringComparison.OrdinalIgnoreCase);
        if (!homeRoom) return 1f;

        bool nearHive = DesertBatflyEnvironmentalExposure.NearHive(
            bat.room, bat.room.GetTilePosition(bat.mainBodyChunk.pos), 10);
        if (weather == DesertBatflyEnvironmentalWeather.DenseFog)
            return nearHive ? 0.52f : 0.66f;
        return nearHive ? 0.72f : 0.82f;
    }

    internal static bool IsSandstormHomeAccessBlocker(
        DesertBatfly bat,
        Player player,
        in DesertBatflyEnvironmentalInfluence influence)
    {
        if (bat?.room == null || player?.mainBodyChunk == null || player.room != bat.room ||
            influence.HardSurvival ||
            influence.Weather is not (DesertBatflyEnvironmentalWeather.Sandstorm or
                DesertBatflyEnvironmentalWeather.DeathSandstorm) ||
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

    private static bool IsSandstorm(DesertBatflyEnvironmentalWeather weather)
        => weather is DesertBatflyEnvironmentalWeather.Sandstorm or
                      DesertBatflyEnvironmentalWeather.DeathSandstorm;

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
