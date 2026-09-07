using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using DryCycle.Weather.Spatial;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DesertBatflyWeatherEcologySample
{
    internal readonly WeatherScheduleEventKind HazardKind;
    internal readonly string HazardId;
    internal readonly float ActiveIntensity;
    internal readonly float ImmediateDanger;
    internal readonly float ShelterUrgency;
    internal readonly float MigrationStress;
    internal readonly float TravelExposure;
    internal readonly int TimeUntilDangerTicks;

    internal bool HasHazard => !string.IsNullOrEmpty(HazardId) &&
        (ActiveIntensity > 0f || TimeUntilDangerTicks < int.MaxValue);
    internal bool ForecastDanger => TimeUntilDangerTicks < int.MaxValue;
    internal bool LethalNow => ImmediateDanger >= 0.72f && ActiveIntensity >= 0.55f;

    internal DesertBatflyWeatherEcologySample(
        WeatherScheduleEventKind hazardKind,
        string hazardId,
        float activeIntensity,
        float immediateDanger,
        float shelterUrgency,
        float migrationStress,
        float travelExposure,
        int timeUntilDangerTicks)
    {
        HazardKind = hazardKind;
        HazardId = hazardId ?? string.Empty;
        ActiveIntensity = Mathf.Clamp01(activeIntensity);
        ImmediateDanger = Mathf.Clamp01(immediateDanger);
        ShelterUrgency = Mathf.Clamp01(shelterUrgency);
        MigrationStress = Mathf.Clamp01(migrationStress);
        TravelExposure = Mathf.Clamp01(travelExposure);
        TimeUntilDangerTicks = timeUntilDangerTicks < 0 ? 0 : timeUntilDangerTicks;
    }

    internal static DesertBatflyWeatherEcologySample None => new(
        WeatherScheduleEventKind.Weather, string.Empty, 0f, 0f, 0f, 0f, 0f, int.MaxValue);
}

/// <summary>
/// Single semantic bridge between DryCycle weather and Desert Batfly ecology.
/// Future schedule information is exposed only as TimeUntilDanger/ShelterUrgency;
/// MigrationStress is accumulated only from an event that is actually active and
/// spatially allowed in the queried room.
/// </summary>
internal static class DesertBatflyWeatherEcology
{
    // Bats should not evacuate at the start of a half-cycle merely because the scheduler
    // already knows a disaster exists later. Three minutes is enough to evaluate a route,
    // stagger departure and still preserve the idea of a near-term observable forecast.
    private const int ForecastPlanningHorizonTicks = 7200;

    private readonly struct Profile
    {
        internal readonly float Immediate, Shelter, Migration, Travel;
        internal Profile(float immediate, float shelter, float migration, float travel)
        {
            Immediate = immediate;
            Shelter = shelter;
            Migration = migration;
            Travel = travel;
        }
    }

    internal static DesertBatflyWeatherEcologySample Sample(World world, AbstractRoom room)
    {
        if (world == null || room == null || world.region == null ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock) ||
            !WeatherScheduleRuntime.TryGetCurrentSchedule(world, out WeatherPhaseSchedule schedule) ||
            schedule == null)
            return DesertBatflyWeatherEcologySample.None;

        long phaseTicks = CurrentPhaseTicks(clock);
        string region = world.region.name;

        float active = 0f;
        float immediate = 0f;
        float shelter = 0f;
        float migration = 0f;
        float travel = 0f;
        int nearestDanger = int.MaxValue;
        string hazardId = string.Empty;
        WeatherScheduleEventKind hazardKind = WeatherScheduleEventKind.Weather;
        float hazardPriority = -1f;

        for (int i = 0; i < schedule.Events.Count; i++)
        {
            ScheduledWeatherEvent scheduled = schedule.Events[i];
            if (scheduled?.Candidate == null ||
                !WeatherSpatialRegistry.IsAllowed(region, room.name,
                    scheduled.Candidate.Kind, scheduled.Candidate.Id))
                continue;

            string normalizedId = WeatherSpatialCatalog.NormalizeId(scheduled.Candidate.Id);
            Profile profile = ProfileFor(scheduled.Candidate.Kind, normalizedId);
            float intensity = EventEnvelope(scheduled, phaseTicks);
            if (intensity > 0f)
            {
                float eventImmediate = profile.Immediate * intensity;
                float eventShelter = profile.Shelter * intensity;
                float eventMigration = profile.Migration * intensity;
                float eventTravel = profile.Travel * intensity;

                active = Mathf.Max(active, intensity);
                immediate = Mathf.Max(immediate, eventImmediate);
                shelter = Mathf.Max(shelter, eventShelter);
                migration = Mathf.Clamp01(migration + eventMigration * (1f - migration * 0.35f));
                travel = Mathf.Max(travel, eventTravel);

                float priority = eventImmediate * 1.3f + eventShelter + eventTravel * 0.5f;
                if (priority > hazardPriority)
                {
                    hazardPriority = priority;
                    hazardId = scheduled.Candidate.Id;
                    hazardKind = scheduled.Candidate.Kind;
                }
            }

            // Forecast only hazards that materially require shelter. Forecasts may raise
            // shelter urgency, but never MigrationStress: permanent migration still needs
            // weather that actually happened in this room. DenseFog is deliberately NOT
            // forecast here: its outward displacement is a reaction to actual severe loss
            // of visibility, while Sandstorm is the species-specific early-warning weather.
            bool reactiveDenseFog =
                scheduled.Candidate.Kind == WeatherScheduleEventKind.Weather &&
                normalizedId == "DENSEFOG";
            if (profile.Shelter >= 0.50f && !reactiveDenseFog)
            {
                long start = EffectStart(scheduled);
                if (phaseTicks < start)
                {
                    long delta = start - phaseTicks;
                    int ticks = delta >= int.MaxValue ? int.MaxValue : (int)delta;
                    if (ticks <= ForecastPlanningHorizonTicks && ticks < nearestDanger)
                    {
                        nearestDanger = ticks;
                        shelter = Mathf.Max(shelter, profile.Shelter);
                        float forecastPriority = profile.Shelter + profile.Travel * 0.5f;
                        if (hazardPriority < 0f || forecastPriority > hazardPriority)
                        {
                            hazardPriority = forecastPriority;
                            hazardId = scheduled.Candidate.Id;
                            hazardKind = scheduled.Candidate.Kind;
                        }
                    }
                }
                else if (intensity > 0f)
                {
                    nearestDanger = 0;
                }
            }
        }

        return new DesertBatflyWeatherEcologySample(
            hazardKind, hazardId, active, immediate, shelter, migration, travel, nearestDanger);
    }

    internal static float RegionalStress(World world,
        System.Collections.Generic.IEnumerable<AbstractRoom> colonyRooms)
    {
        if (world == null || colonyRooms == null) return 0f;
        float sum = 0f;
        int count = 0;
        foreach (AbstractRoom room in colonyRooms)
        {
            if (room == null) continue;
            DesertBatflyWeatherEcologySample sample = Sample(world, room);
            sum += sample.MigrationStress;
            count++;
        }
        return count == 0 ? 0f : Mathf.Clamp01(sum / count);
    }

    internal static float HazardShelterDemand(WeatherScheduleEventKind kind, string id)
    {
        return ProfileFor(kind, id).Shelter;
    }

    private static Profile ProfileFor(WeatherScheduleEventKind kind, string id)
    {
        string n = WeatherSpatialCatalog.NormalizeId(id);
        if (kind == WeatherScheduleEventKind.DangerType)
        {
            return n switch
            {
                "INTENSEHEAT" => new Profile(0.98f, 1f, 0.92f, 0.98f),
                "DEATHRAIN" => new Profile(1f, 1f, 0.82f, 1f),
                "DEATHSANDSTORM" => new Profile(1f, 1f, 0.94f, 1f),
                "SANDSTORM" => new Profile(0.82f, 0.96f, 0.78f, 0.92f),
                _ => new Profile(0.70f, 0.80f, 0.45f, 0.78f)
            };
        }

        return n switch
        {
            "LIGHTRAIN" => new Profile(0.04f, 0.10f, 0f, 0.06f),
            "FOG" => new Profile(0.02f, 0.05f, 0f, 0.05f),
            // DenseFog is a temporary usability/shelter problem, not a permanent-habitat
            // migration signal. High active Shelter lets Task09 consider a nearby clearer
            // Refuge once the fog is materially present; near-zero Migration prevents one
            // fog event from becoming a ColonyMigration.
            "DENSEFOG" => new Profile(0.06f, 0.90f, 0.01f, 0.12f),
            "HEAVYRAIN" => new Profile(0.24f, 0.66f, 0.15f, 0.42f),
            "HEATWAVE" => new Profile(0.28f, 0.55f, 0.55f, 0.44f),
            "SANDSTORM" => new Profile(0.62f, 0.88f, 0.72f, 0.82f),
            _ => new Profile(0.08f, 0.15f, 0.04f, 0.12f)
        };
    }

    private static long CurrentPhaseTicks(WorldClock clock)
    {
        if (clock == null) return 0;
        return (long)Math.Round(
            Math.Max(0f, Math.Min(1f, clock.HalfProgress)) * clock.CurrentHalfLength);
    }

    private static long EffectStart(ScheduledWeatherEvent scheduled) =>
        (long)scheduled.StartPip * WeatherPhaseScheduler.PipTicks -
        WeatherPhaseScheduler.EventTransitionTicks;

    private static float EventEnvelope(ScheduledWeatherEvent scheduled, long phaseTicks)
    {
        long mainStart = (long)scheduled.StartPip * WeatherPhaseScheduler.PipTicks;
        long mainEnd = (long)scheduled.EndPipExclusive * WeatherPhaseScheduler.PipTicks;
        long transition = WeatherPhaseScheduler.EventTransitionTicks;
        long effectStart = mainStart - transition;
        long effectEnd = mainEnd + transition;
        if (phaseTicks < effectStart || phaseTicks >= effectEnd) return 0f;
        if (phaseTicks < mainStart)
        {
            float t = transition <= 0 ? 1f : (phaseTicks - effectStart) / (float)transition;
            return Smooth01(t);
        }
        if (phaseTicks < mainEnd) return 1f;
        float tail = transition <= 0 ? 0f : (effectEnd - phaseTicks) / (float)transition;
        return Smooth01(tail);
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
