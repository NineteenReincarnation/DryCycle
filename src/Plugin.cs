using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Creatures;
using DryCycle.Creatures.MossySpider;
using DryCycle.DayNight;
using DryCycle.HUD;
using DryCycle.Items.DewPod;
using DryCycle.Items.KingVultureSpear;
using DryCycle.Items.RopeSpear;
using DryCycle.Misc;
using DryCycle.OptimizedVanilla;
using DryCycle.PlayerAbility.SlugCatKarmicArmor;
using DryCycle.Registration;
using DryCycle.Rendering;
using DryCycle.ShelterExts;
using DryCycle.TemperatureSystem;
using DryCycle.TerrainExt.QuicksandZone;
using DryCycle.Thirst;
using DryCycle.Weather;
using DryCycle.Weather.Climate;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using DryCycle.Weather.Scheduling;
using DryCycle.WorldLink.InternalGate;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
[BepInDependency("slime-cubed.devconsole", BepInDependency.DependencyFlags.SoftDependency)]
internal sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "Anno";
    public const string RainWorldModId = "NR.B5";
    public const string ModName = "DryCycle";
    public const string Version = "0.2.118";

    internal new static ManualLogSource Logger;
    private static bool _contentRegistered;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;

        if (!_contentRegistered)
        {
            DryCycleContent.Register(new MossySpiderDefinition());
            _contentRegistered = true;
        }

        DryCycleShaderAssets.Enable();

        DryCycleContent.Enable();
        MossySpiderBackPlatform.Enable();
        SpinebackLizardHooks.Enable();
        DewPodAudioHooks.InitializeSoundIds();

        On.RainWorld.PreModsInit += RainWorld_PreModsInit;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        On.RainWorld.PostModsInit += RainWorld_PostModsInit;
    }

    public void OnDisable()
    {
        On.RainWorld.PreModsInit -= RainWorld_PreModsInit;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        On.RainWorld.PostModsInit -= RainWorld_PostModsInit;
        DryCycleContent.Disable();
        MossySpiderBackPlatform.Disable();
        CreatureDevConsoleSupport.ResetRegistration();
        RopeSpearDevConsoleSupport.ResetRegistration();
        SpinebackLizardHooks.Disable();
        SpinebackLizardDevConsoleSupport.ResetRegistration();

        if (_initialized)
        {
            InternalGateRuntime.Disable();
            VanillaDevUIShortcutRuntime.Disable();
            MiscRuntime.Disable();
            OpenShelterSleepRuntime.Disable();
            RainDrinkingRuntime.Disable();
            RainMeterFastForwardForecastFix.Disable();
            FogForecastFlowRuntime.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            WeatherCameraEffectsRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
            IntenseHeatWeatherRuntime.Disable();
            HeatWaveWeatherRuntime.Disable();
            FogWeatherRuntime.Disable();
            WeatherScheduleRuntime.Disable();
            ShelterCycleResetRuntime.Disable();
            WorldClockRegionContinuityRuntime.Disable();
            DayNightRuntime.Disable();
            HydrationDivider.Disable();
            DehydrationVisualRuntime.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
            RopeSpearWallStickRuntime.Disable();
            RopeSpearAimController.Disable();
            RopeSpearHooks.Disable();
            QuicksandSubmersionCleanup.Disable();
            QuicksandCreatureEscape.Disable();
            QuicksandAIHazard.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerShoreConstraint.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandLooseObjectSinkEase.Disable();
            QuicksandSinkRateLimiter.Disable();
            QuicksandPlayerHorizontalStability.Disable();
            QuicksandDrillCrabCompatibility.Disable();
            QuicksandZoneHooks.Disable();
            DewPodAudioHooks.Disable();
            DewPodRuntimeTuningHooks.Disable();
            DewPodClassicVisualHooks.Disable();
            DewPodPlantCollisionHooks.Disable();
            DewPodPlantHooks.Disable();
            DewPodHooks.Disable();
            TemperatureSystemRuntime.Disable();
            SlugCatKarmicArmorRuntime.Disable();
            ThirstHooks.Disable();
            KingVultureSpearFeedback.Disable();
            KingVultureSpearPlayerEffects.Disable();
            KingVultureSpearHooks.Disable();
            _initialized = false;
        }

        DryCycleShaderAssets.Disable();
    }

    private static void RainWorld_PreModsInit(On.RainWorld.orig_PreModsInit orig, RainWorld self)
    {
        CreatureDevConsoleSupport.ResetRegistration();
        RopeSpearDevConsoleSupport.ResetRegistration();
        SpinebackLizardDevConsoleSupport.ResetRegistration();
        SlugBaseHydrationFeatures.Initialize();
        orig(self);
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        SlugBaseHydrationFeatures.Initialize();
        orig(self);

        DryCycleShaderAssets.EnsureLoaded(self);
        RegionDayNightOptions.Register();

        if (_initialized)
        {
            return;
        }

        try
        {
            DryCycleContent.LoadResources(self);
            KingVultureSpearHooks.Enable();
            RopeSpearHooks.Enable();
            RopeSpearWallStickRuntime.Enable();
            RopeSpearAimController.Enable();
            KingVultureSpearPlayerEffects.Enable();
            KingVultureSpearFeedback.Enable();
            ThirstHooks.Enable();
            SlugCatKarmicArmorRuntime.Enable();
            TemperatureSystemRuntime.Enable();
            DewPodHooks.Enable();

            QuicksandZoneHooks.Enable();
            QuicksandDrillCrabCompatibility.EnsureEnabled();
            QuicksandAIHazard.Enable();
            QuicksandCreatureEscape.Enable();
            QuicksandPlayerStruggleControl.EnableNativeCapture();
            QuicksandPlayerHorizontalStability.Enable();
            QuicksandSinkRateLimiter.Enable();
            QuicksandLooseObjectSinkEase.Enable();
            QuicksandPlayerLocomotionSupport.Enable();
            QuicksandPlayerStruggleControl.Enable();
            QuicksandPlayerShoreConstraint.Enable();
            QuicksandWeaponSettling.Enable();
            QuicksandSubmersionCleanup.Enable();

            DewPodPlantHooks.Enable();
            DewPodPlantCollisionHooks.Enable();
            DewPodClassicVisualHooks.Enable();
            DewPodRuntimeTuningHooks.Enable();
            DewPodAudioHooks.Enable();
            KingVultureSpearCombat.Enable();
            HydrationWeakness.Enable();
            DehydrationVisualRuntime.Enable();
            HydrationDivider.Enable();

            WorldClockHooks.TestScheduleEnabled = false;
            WeatherTypeRegistry.ResetWarnings();
            RegionClimateRegistry.Reload();
            DayNightRuntime.Enable();
            WorldClockRegionContinuityRuntime.Enable();
            ShelterCycleResetRuntime.Enable();
            OpenShelterSleepRuntime.Enable();
            WeatherScheduleRuntime.Enable();
            FogWeatherRuntime.Enable();
            HeatWaveWeatherRuntime.Enable();
            IntenseHeatWeatherRuntime.Enable();
            SandstormWeatherRuntime.Enable();
            RainWeatherRuntime.Enable();

            ScheduledHeavyRainTraversalRuntime.Enable();
            ScheduledHeavyRainImpactGuardRuntime.Enable();
            SyntheticRoomRainTakeoverRuntime.Enable();

            RainDrinkingRuntime.Enable();
            WeatherCameraEffectsRuntime.Enable();
            RainMeterRoundPipRuntime.Enable();
            FogForecastFlowRuntime.Enable();
            RainMeterFastForwardForecastFix.Enable();

            InternalGateRuntime.Enable();
            VanillaDevUIShortcutRuntime.Enable();
            MiscRuntime.Enable();
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: systems enabled.");
        }
        catch (Exception ex)
        {
            InternalGateRuntime.Disable();
            VanillaDevUIShortcutRuntime.Disable();
            MiscRuntime.Disable();
            OpenShelterSleepRuntime.Disable();
            RainDrinkingRuntime.Disable();
            RainMeterFastForwardForecastFix.Disable();
            FogForecastFlowRuntime.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            WeatherCameraEffectsRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
            IntenseHeatWeatherRuntime.Disable();
            HeatWaveWeatherRuntime.Disable();
            FogWeatherRuntime.Disable();
            WeatherScheduleRuntime.Disable();
            ShelterCycleResetRuntime.Disable();
            WorldClockRegionContinuityRuntime.Disable();
            DayNightRuntime.Disable();
            HydrationDivider.Disable();
            DehydrationVisualRuntime.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
            RopeSpearWallStickRuntime.Disable();
            RopeSpearAimController.Disable();
            RopeSpearHooks.Disable();
            QuicksandSubmersionCleanup.Disable();
            QuicksandCreatureEscape.Disable();
            QuicksandAIHazard.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerShoreConstraint.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandLooseObjectSinkEase.Disable();
            QuicksandSinkRateLimiter.Disable();
            QuicksandPlayerHorizontalStability.Disable();
            QuicksandDrillCrabCompatibility.Disable();
            QuicksandZoneHooks.Disable();
            DewPodAudioHooks.Disable();
            DewPodRuntimeTuningHooks.Disable();
            DewPodClassicVisualHooks.Disable();
            DewPodPlantCollisionHooks.Disable();
            DewPodPlantHooks.Disable();
            DewPodHooks.Disable();
            TemperatureSystemRuntime.Disable();
            SlugCatKarmicArmorRuntime.Disable();
            ThirstHooks.Disable();
            KingVultureSpearFeedback.Disable();
            KingVultureSpearPlayerEffects.Disable();
            KingVultureSpearHooks.Disable();
            SpinebackLizardHooks.Disable();
            Logger.LogError(ex);
            throw;
        }
    }

    private static void RainWorld_PostModsInit(
        On.RainWorld.orig_PostModsInit orig,
        RainWorld self)
    {
        orig(self);
        CreatureDevConsoleSupport.TryRegisterAll();
        RopeSpearDevConsoleSupport.TryRegister();
        SpinebackLizardDevConsoleSupport.TryRegister();
    }
}
