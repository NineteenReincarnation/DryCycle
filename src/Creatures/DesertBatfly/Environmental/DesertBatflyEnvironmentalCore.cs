using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DesertBatflyEnvironmentalPhase
{
    Calm,
    Advisory,
    Preparation,
    Sheltering,
    Acute,
    Recovery
}

internal enum DesertBatflyEnvironmentalWeather
{
    None,
    LightRain,
    Fog,
    DenseFog,
    HeavyRain,
    HeatWave,
    IntenseHeat,
    Sandstorm,
    DeathSandstorm,
    DeathRain,
    Other
}

internal readonly struct DesertBatflyEnvironmentalRoomContext
{
    internal readonly bool WeatherSourceValid;
    internal readonly DesertBatflyEnvironmentalWeather Weather;
    internal readonly WeatherScheduleEventKind WeatherKind;
    internal readonly string WeatherId;
    internal readonly float ActiveIntensity;
    internal readonly float ImmediateDanger;
    internal readonly float ShelterUrgency;
    internal readonly float TravelExposure;
    internal readonly int ForecastTicks;
    internal readonly DesertBatflyEnvironmentalPhase Phase;
    internal readonly string PhaseReason;

    internal bool ActiveOrForecast => WeatherSourceValid && Weather != DesertBatflyEnvironmentalWeather.None;

    internal DesertBatflyEnvironmentalRoomContext(
        bool weatherSourceValid,
        DesertBatflyEnvironmentalWeather weather,
        WeatherScheduleEventKind weatherKind,
        string weatherId,
        float activeIntensity,
        float immediateDanger,
        float shelterUrgency,
        float travelExposure,
        int forecastTicks,
        DesertBatflyEnvironmentalPhase phase,
        string phaseReason)
    {
        WeatherSourceValid = weatherSourceValid;
        Weather = weather;
        WeatherKind = weatherKind;
        WeatherId = weatherId ?? string.Empty;
        ActiveIntensity = Mathf.Clamp01(activeIntensity);
        ImmediateDanger = Mathf.Clamp01(immediateDanger);
        ShelterUrgency = Mathf.Clamp01(shelterUrgency);
        TravelExposure = Mathf.Clamp01(travelExposure);
        ForecastTicks = forecastTicks < 0 ? 0 : forecastTicks;
        Phase = phase;
        PhaseReason = phaseReason ?? string.Empty;
    }

    internal static DesertBatflyEnvironmentalRoomContext Calm => new(
        false,
        DesertBatflyEnvironmentalWeather.None,
        WeatherScheduleEventKind.Weather,
        string.Empty,
        0f,
        0f,
        0f,
        0f,
        int.MaxValue,
        DesertBatflyEnvironmentalPhase.Calm,
        "no authorized DryCycle environmental weather");
}

internal readonly struct DesertBatflyEnvironmentalInfluence
{
    internal readonly DesertBatflyEnvironmentalPhase Phase;
    internal readonly DesertBatflyEnvironmentalWeather Weather;
    internal readonly float ShelterDrive;
    internal readonly float OpenExposureAversion;
    internal readonly float RoostMultiplier;
    internal readonly float HarassMultiplier;
    internal readonly float SocialMultiplier;
    internal readonly float PlayMultiplier;
    internal readonly float GroupCohesionMultiplier;
    internal readonly float ActivityRadiusMultiplier;
    internal readonly float VisibilityConfidence;
    internal readonly float NavigationUncertainty;
    internal readonly float ObstacleAnticipationScale;
    internal readonly float HomeReturnDrive;
    internal readonly float BurrowDrive;
    internal readonly float MigrationSuppression;
    internal readonly float HeatAgitation;
    internal readonly float HeatShelterDrive;
    internal readonly float ThermalExhaustion;
    internal readonly bool DamageAttackPermission;
    internal readonly bool HardSurvival;
    internal readonly Vector2? PreferredShelterPoint;
    internal readonly float PreferredShelterQuality;
    internal readonly int CommitmentTicks;
    internal readonly float RecoveryProgress;
    internal readonly string DecisionReason;

    internal bool SuppressesNeutralSocial => HardSurvival || SocialMultiplier <= 0.20f || PlayMultiplier <= 0.12f;

    internal DesertBatflyEnvironmentalInfluence(
        DesertBatflyEnvironmentalPhase phase,
        DesertBatflyEnvironmentalWeather weather,
        float shelterDrive,
        float openExposureAversion,
        float roostMultiplier,
        float harassMultiplier,
        float socialMultiplier,
        float playMultiplier,
        float groupCohesionMultiplier,
        float activityRadiusMultiplier,
        float visibilityConfidence,
        float navigationUncertainty,
        float obstacleAnticipationScale,
        float homeReturnDrive,
        float burrowDrive,
        float migrationSuppression,
        float heatAgitation,
        float heatShelterDrive,
        float thermalExhaustion,
        bool damageAttackPermission,
        bool hardSurvival,
        Vector2? preferredShelterPoint,
        float preferredShelterQuality,
        int commitmentTicks,
        float recoveryProgress,
        string decisionReason)
    {
        Phase = phase;
        Weather = weather;
        ShelterDrive = Mathf.Clamp01(shelterDrive);
        OpenExposureAversion = Mathf.Clamp01(openExposureAversion);
        RoostMultiplier = Mathf.Max(0f, roostMultiplier);
        HarassMultiplier = Mathf.Max(0f, harassMultiplier);
        SocialMultiplier = Mathf.Max(0f, socialMultiplier);
        PlayMultiplier = Mathf.Max(0f, playMultiplier);
        GroupCohesionMultiplier = Mathf.Max(0f, groupCohesionMultiplier);
        ActivityRadiusMultiplier = Mathf.Clamp(activityRadiusMultiplier, 0.1f, 1.5f);
        VisibilityConfidence = Mathf.Clamp01(visibilityConfidence);
        NavigationUncertainty = Mathf.Clamp01(navigationUncertainty);
        ObstacleAnticipationScale = Mathf.Clamp(obstacleAnticipationScale, 0.15f, 1f);
        HomeReturnDrive = Mathf.Clamp01(homeReturnDrive);
        BurrowDrive = Mathf.Clamp01(burrowDrive);
        MigrationSuppression = Mathf.Clamp01(migrationSuppression);
        HeatAgitation = Mathf.Clamp01(heatAgitation);
        HeatShelterDrive = Mathf.Clamp01(heatShelterDrive);
        ThermalExhaustion = Mathf.Clamp01(thermalExhaustion);
        DamageAttackPermission = damageAttackPermission;
        HardSurvival = hardSurvival;
        PreferredShelterPoint = preferredShelterPoint;
        PreferredShelterQuality = Mathf.Clamp01(preferredShelterQuality);
        CommitmentTicks = Mathf.Max(0, commitmentTicks);
        RecoveryProgress = Mathf.Clamp01(recoveryProgress);
        DecisionReason = decisionReason ?? string.Empty;
    }

    internal static DesertBatflyEnvironmentalInfluence Neutral => new(
        DesertBatflyEnvironmentalPhase.Calm,
        DesertBatflyEnvironmentalWeather.None,
        0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
        1f, 0f, 1f, 0f, 0f, 0f,
        0f, 0f, 0f,
        false, false, null, 0f, 0, 1f,
        "calm / no Task13 influence");
}

internal readonly struct DB_EnvironmentExposureSample
{
    internal readonly float Exposure;
    internal readonly float RoofShielding;
    internal readonly float SideShielding;
    internal readonly float Enclosure;
    internal readonly float Shade;
    internal readonly float RainExposure;
    internal readonly float VisibilityConfidence;

    internal DB_EnvironmentExposureSample(
        float exposure,
        float roofShielding,
        float sideShielding,
        float enclosure,
        float shade,
        float rainExposure,
        float visibilityConfidence)
    {
        Exposure = Mathf.Clamp01(exposure);
        RoofShielding = Mathf.Clamp01(roofShielding);
        SideShielding = Mathf.Clamp01(sideShielding);
        Enclosure = Mathf.Clamp01(enclosure);
        Shade = Mathf.Clamp01(shade);
        RainExposure = Mathf.Clamp01(rainExposure);
        VisibilityConfidence = Mathf.Clamp01(visibilityConfidence);
    }
}

internal sealed class DesertBatflyShelterAnchor
{
    internal readonly int Id;
    internal readonly IntVector2 Tile;
    internal readonly Vector2 Position;
    internal readonly DB_EnvironmentExposureSample Exposure;
    internal readonly bool RoostCompatible;
    internal readonly bool NearHive;
    internal float Crowding;

    internal DesertBatflyShelterAnchor(
        int id,
        IntVector2 tile,
        Vector2 position,
        DB_EnvironmentExposureSample exposure,
        bool roostCompatible,
        bool nearHive)
    {
        Id = id;
        Tile = tile;
        Position = position;
        Exposure = exposure;
        RoostCompatible = roostCompatible;
        NearHive = nearHive;
    }
}
