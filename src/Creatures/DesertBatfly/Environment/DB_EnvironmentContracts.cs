using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_EnvironmentPhase
{
    Calm,
    Advisory,
    Preparation,
    Sheltering,
    Acute,
    Recovery
}

internal enum DB_EnvironmentWeather
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

internal readonly struct DB_EnvironmentContext
{
    internal readonly bool WeatherSourceValid;
    internal readonly DB_EnvironmentWeather Weather;
    internal readonly WeatherScheduleEventKind WeatherKind;
    internal readonly string WeatherId;
    internal readonly float ActiveIntensity;
    internal readonly float ImmediateDanger;
    internal readonly float ShelterUrgency;
    internal readonly float TravelExposure;
    internal readonly int ForecastTicks;
    internal readonly DB_EnvironmentPhase Phase;
    internal readonly string PhaseReason;

    internal bool ActiveOrForecast => WeatherSourceValid && Weather != DB_EnvironmentWeather.None;

    internal DB_EnvironmentContext(
        bool weatherSourceValid,
        DB_EnvironmentWeather weather,
        WeatherScheduleEventKind weatherKind,
        string weatherId,
        float activeIntensity,
        float immediateDanger,
        float shelterUrgency,
        float travelExposure,
        int forecastTicks,
        DB_EnvironmentPhase phase,
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

    internal static DB_EnvironmentContext Calm => new(
        false,
        DB_EnvironmentWeather.None,
        WeatherScheduleEventKind.Weather,
        string.Empty,
        0f,
        0f,
        0f,
        0f,
        int.MaxValue,
        DB_EnvironmentPhase.Calm,
        "no authorized DryCycle environmental weather");
}

internal readonly struct DB_EnvironmentInfluence
{
    internal readonly DB_EnvironmentPhase Phase;
    internal readonly DB_EnvironmentWeather Weather;
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

    internal DB_EnvironmentInfluence(
        DB_EnvironmentPhase phase,
        DB_EnvironmentWeather weather,
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

    internal static DB_EnvironmentInfluence Neutral => new(
        DB_EnvironmentPhase.Calm,
        DB_EnvironmentWeather.None,
        0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
        1f, 0f, 1f, 0f, 0f, 0f,
        0f, 0f, 0f,
        false, false, null, 0f, 0, 1f,
        "calm / no Task13 influence");
}

