using DryCycle.Weather.Spatial;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflyEnvironmentalProfile
{
    internal const int SandstormAdvisoryTicks = 6800;
    internal const int SandstormPreparationTicks = 4700;
    internal const int SandstormStrongPreparationTicks = 2600;
    internal const int DeathRainPreparationLeadTicks = 3600;
    internal const int DeathRainShelterLeadTicks = 1800;

    internal static DesertBatflyEnvironmentalWeather Classify(in DesertBatflyWeatherEcologySample sample)
    {
        if (!sample.HasHazard) return DesertBatflyEnvironmentalWeather.None;
        string id = WeatherSpatialCatalog.NormalizeId(sample.HazardId);
        return id switch
        {
            "LIGHTRAIN" => DesertBatflyEnvironmentalWeather.LightRain,
            "FOG" => DesertBatflyEnvironmentalWeather.Fog,
            "DENSEFOG" => DesertBatflyEnvironmentalWeather.DenseFog,
            "HEAVYRAIN" => DesertBatflyEnvironmentalWeather.HeavyRain,
            "HEATWAVE" => DesertBatflyEnvironmentalWeather.HeatWave,
            "INTENSEHEAT" => DesertBatflyEnvironmentalWeather.IntenseHeat,
            "SANDSTORM" => DesertBatflyEnvironmentalWeather.Sandstorm,
            "DEATHSANDSTORM" => DesertBatflyEnvironmentalWeather.DeathSandstorm,
            "DEATHRAIN" => DesertBatflyEnvironmentalWeather.DeathRain,
            _ => DesertBatflyEnvironmentalWeather.Other
        };
    }

    internal static DesertBatflyEnvironmentalPhase ResolvePhase(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample,
        DesertBatflyEnvironmentalPhase previous,
        out string reason)
    {
        if (weather == DesertBatflyEnvironmentalWeather.None)
        {
            reason = "no authorized DryCycle environmental weather";
            return previous == DesertBatflyEnvironmentalPhase.Calm
                ? DesertBatflyEnvironmentalPhase.Calm
                : DesertBatflyEnvironmentalPhase.Recovery;
        }

        float active = sample.ActiveIntensity;
        float danger = sample.ImmediateDanger;
        int forecast = sample.TimeUntilDangerTicks;

        if (weather == DesertBatflyEnvironmentalWeather.LightRain)
        {
            reason = "LightRain phase ceiling = Advisory";
            return DesertBatflyEnvironmentalPhase.Advisory;
        }

        if (weather == DesertBatflyEnvironmentalWeather.Sandstorm ||
            weather == DesertBatflyEnvironmentalWeather.DeathSandstorm)
        {
            if (weather == DesertBatflyEnvironmentalWeather.DeathSandstorm &&
                (danger >= 0.72f && active >= 0.50f))
            {
                reason = "DeathSandstorm hard survival";
                return DesertBatflyEnvironmentalPhase.Acute;
            }
            if (active >= 0.60f)
            {
                reason = "active Sandstorm shelter phase";
                return DesertBatflyEnvironmentalPhase.Sheltering;
            }
            if (forecast <= SandstormStrongPreparationTicks)
            {
                reason = "species-specific strong Sandstorm anticipation";
                return DesertBatflyEnvironmentalPhase.Preparation;
            }
            if (forecast <= SandstormPreparationTicks)
            {
                reason = "species-specific Sandstorm preparation";
                return DesertBatflyEnvironmentalPhase.Preparation;
            }
            if (forecast <= SandstormAdvisoryTicks || active > 0f)
            {
                reason = "species-specific early Sandstorm advisory";
                return DesertBatflyEnvironmentalPhase.Advisory;
            }
        }

        if (weather == DesertBatflyEnvironmentalWeather.DeathRain)
        {
            if (danger >= 0.72f && active >= 0.50f)
            {
                reason = "DeathRain lethal local hard survival";
                return DesertBatflyEnvironmentalPhase.Acute;
            }
            if (active >= 0.30f || forecast <= DeathRainShelterLeadTicks)
            {
                reason = "DeathRain local sheltering while Travel owns cross-room safety";
                return DesertBatflyEnvironmentalPhase.Sheltering;
            }
            if (active > 0f || forecast <= DeathRainPreparationLeadTicks)
            {
                reason = "DeathRain pre-onset local preparation; Travel retains cross-room ownership";
                return DesertBatflyEnvironmentalPhase.Preparation;
            }
            reason = "DeathRain bounded forecast advisory";
            return DesertBatflyEnvironmentalPhase.Advisory;
        }

        if (weather == DesertBatflyEnvironmentalWeather.IntenseHeat)
        {
            if (danger >= 0.72f && active >= 0.50f)
            {
                reason = "DangerType hard survival";
                return DesertBatflyEnvironmentalPhase.Acute;
            }
            if (active >= 0.30f || forecast <= 1800)
            {
                reason = "DangerType shelter preparation";
                return DesertBatflyEnvironmentalPhase.Sheltering;
            }
            if (forecast < int.MaxValue)
            {
                reason = "DangerType forecast preparation";
                return DesertBatflyEnvironmentalPhase.Preparation;
            }
        }

        switch (weather)
        {
            case DesertBatflyEnvironmentalWeather.Fog:
                // Ordinary Fog is intentionally an activity/visibility modifier only.
                // DenseFog owns the qualitative transition into Home/Roost/refuge behavior.
                reason = "Fog phase ceiling = Advisory; visual confidence/activity only";
                return DesertBatflyEnvironmentalPhase.Advisory;

            case DesertBatflyEnvironmentalWeather.DenseFog:
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Sheltering, 0.78f, 0.62f))
                {
                    reason = "DenseFog severely limits navigation";
                    return DesertBatflyEnvironmentalPhase.Sheltering;
                }
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Preparation, 0.35f, 0.24f) ||
                    forecast <= 1800)
                {
                    reason = "DenseFog preparation / navigation confidence loss";
                    return DesertBatflyEnvironmentalPhase.Preparation;
                }
                reason = "DenseFog advisory";
                return DesertBatflyEnvironmentalPhase.Advisory;

            case DesertBatflyEnvironmentalWeather.HeavyRain:
                // HeavyRain is deliberately non-lethal Environment shelter ecology. It may
                // contract the room strongly, but it never escalates itself into Acute.
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Sheltering, 0.58f, 0.44f))
                {
                    reason = "HeavyRain covered-space sheltering; Acute reserved for DeathRain";
                    return DesertBatflyEnvironmentalPhase.Sheltering;
                }
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Preparation, 0.18f, 0.12f) ||
                    forecast <= 2200)
                {
                    reason = "HeavyRain exposure-aware covered-space preparation";
                    return DesertBatflyEnvironmentalPhase.Preparation;
                }
                reason = "HeavyRain advisory";
                return DesertBatflyEnvironmentalPhase.Advisory;

            case DesertBatflyEnvironmentalWeather.HeatWave:
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Sheltering, 0.82f, 0.68f))
                {
                    reason = "sustained HeatWave shelter/exhaustion";
                    return DesertBatflyEnvironmentalPhase.Sheltering;
                }
                if (active >= EnterThreshold(previous, DesertBatflyEnvironmentalPhase.Preparation, 0.46f, 0.32f))
                {
                    reason = "HeatWave agitation + rising shelter demand";
                    return DesertBatflyEnvironmentalPhase.Preparation;
                }
                reason = "early HeatWave agitation";
                return DesertBatflyEnvironmentalPhase.Advisory;
        }

        if (sample.LethalNow)
        {
            reason = "generic hard environmental danger";
            return DesertBatflyEnvironmentalPhase.Acute;
        }
        if (active >= 0.60f)
        {
            reason = "generic active shelter weather";
            return DesertBatflyEnvironmentalPhase.Sheltering;
        }
        if (active > 0f || forecast < int.MaxValue)
        {
            reason = "generic environmental advisory";
            return DesertBatflyEnvironmentalPhase.Advisory;
        }

        reason = "environment recovered";
        return DesertBatflyEnvironmentalPhase.Recovery;
    }

    /// <summary>
    /// Local realized HeavyRain burden. Roof shelter matters more than side walls;
    /// injury/shock make exposed travel costly, while personality never turns rain into
    /// a lethal profile. The result is realized-only and is not persistent memory.
    /// </summary>
    internal static float HeavyRainBurden(
        float activeIntensity,
        float rainExposure,
        float roofShielding,
        float shelterUrgency,
        float physicalCapability,
        float postStunShock,
        float nerve)
    {
        float active = Mathf.Clamp01(activeIntensity);
        float rain = Mathf.Clamp01(rainExposure);
        float roof = Mathf.Clamp01(roofShielding);
        float injury = 1f - Mathf.Clamp01(physicalCapability);
        float sensitivity = injury * 0.24f + Mathf.Clamp01(postStunShock) * 0.10f + (1f - Mathf.Clamp01(nerve)) * 0.08f;
        float raw = active * Mathf.Lerp(0.28f, 1f, rain) + Mathf.Clamp01(shelterUrgency) * 0.16f + sensitivity;
        float roofRelief = Mathf.Lerp(1f, 0.18f, roof);
        return Mathf.Clamp01(raw * roofRelief);
    }

    internal static float VisibilityConfidence(DesertBatflyEnvironmentalWeather weather, float intensity)
    {
        float i = Mathf.Clamp01(intensity);
        return weather switch
        {
            DesertBatflyEnvironmentalWeather.Fog => Mathf.Lerp(0.82f, 0.62f, i),
            DesertBatflyEnvironmentalWeather.DenseFog => Mathf.Lerp(0.58f, 0.26f, i),
            _ => 1f
        };
    }

    internal static float NavigationUncertainty(DesertBatflyEnvironmentalWeather weather, float intensity)
    {
        float i = Mathf.Clamp01(intensity);
        return weather switch
        {
            DesertBatflyEnvironmentalWeather.Fog => Mathf.Lerp(0.04f, 0.16f, i),
            DesertBatflyEnvironmentalWeather.DenseFog => Mathf.Lerp(0.32f, 0.78f, i),
            _ => 0f
        };
    }

    internal static float ObstacleAnticipationScale(DesertBatflyEnvironmentalWeather weather, float intensity)
    {
        float uncertainty = NavigationUncertainty(weather, intensity);
        return Mathf.Clamp(1f - uncertainty * 0.72f, 0.30f, 1f);
    }

    internal static float HeatAgitation(float intensity)
    {
        float i = Mathf.Clamp01(intensity);
        return Mathf.Clamp01(Mathf.SmoothStep(0f, 1f, i * 1.18f));
    }

    internal static float HeatShelterDrive(float intensity)
    {
        float i = Mathf.Clamp01(intensity);
        return Mathf.Clamp01(Mathf.InverseLerp(0.24f, 1f, i) * Mathf.Lerp(0.35f, 1f, i));
    }

    internal static float ThermalExhaustion(float intensity, float exposureTicks01)
    {
        float i = Mathf.Clamp01(intensity);
        float exposure = Mathf.Clamp01(exposureTicks01);
        return Mathf.Clamp01(Mathf.InverseLerp(0.35f, 1f, i) * Mathf.Lerp(0.25f, 1f, exposure));
    }

    internal static float SandstormMigrationSuppression(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample)
    {
        if (weather != DesertBatflyEnvironmentalWeather.Sandstorm &&
            weather != DesertBatflyEnvironmentalWeather.DeathSandstorm)
            return 0f;

        if (weather == DesertBatflyEnvironmentalWeather.DeathSandstorm && sample.ActiveIntensity > 0.25f)
            return 1f;
        if (sample.ActiveIntensity > 0.05f) return Mathf.Lerp(0.80f, 0.98f, sample.ActiveIntensity);
        if (sample.TimeUntilDangerTicks <= SandstormStrongPreparationTicks) return 0.88f;
        if (sample.TimeUntilDangerTicks <= SandstormPreparationTicks) return 0.72f;
        if (sample.TimeUntilDangerTicks <= SandstormAdvisoryTicks) return 0.42f;
        return 0f;
    }

    private static float EnterThreshold(
        DesertBatflyEnvironmentalPhase previous,
        DesertBatflyEnvironmentalPhase phase,
        float enter,
        float exit)
        => previous == phase ? exit : enter;
}
