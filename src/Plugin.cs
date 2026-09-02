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
using DryCycle.Misc;
using DryCycle.Registration;
using DryCycle.Rendering;
using DryCycle.ShelterExts;
using DryCycle.TemperatureSystem;
using DryCycle.TerrainExt.QuicksandZone;
using DryCycle.Thirst;
using DryCycle.Weather;
using DryCycle.Weather.Climate;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.Scheduling;

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
    public const string Version = "0.2.111";

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

        // This only installs RainWorld.LoadResources. Unity shader assets themselves
        // are intentionally not touched until Rain World executes that hook.
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
        SpinebackLizardHooks.Disable();
        SpinebackLizardDevConsoleSupport.ResetRegistration();

        if (_initialized)
        {
            MiscRuntime.Disable();
            OpenShelterSleepRuntime.Disable();
            RainDrinkingRuntime.Disable();
            RainMeterFastForwardForecastFix.Disable();
            FogForecastFlowRuntime.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            WeatherCameraEffectsRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            RoomDangerTypeTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
            HeatWaveWeatherRuntime.Disable();
            FogWeatherRuntime.Disable();
            WeatherScheduleRuntime.Disable();
            ShelterCycleResetRuntime.Disable();
            WorldClockRegionContinuityRuntime.Disable();
            DayNightRuntime.Disable();
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
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
        SpinebackLizardDevConsoleSupport.ResetRegistration();
        SlugBaseHydrationFeatures.Initialize();
        orig(self);
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        SlugBaseHydrationFeatures.Initialize();
        orig(self);

        RegionDayNightOptions.Register();

        if (_initialized)
        {
            return;
        }

        try
        {
            DryCycleContent.LoadResources(self);
            KingVultureSpearHooks.Enable();
            KingVultureSpearPlayerEffects.Enable();
            KingVultureSpearFeedback.Enable();
            ThirstHooks.Enable();
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
            SandstormWeatherRuntime.Enable();
            RainWeatherRuntime.Enable();

            // One GlobalRain layer owns the entire Scheduled HeavyRain split: it first
            // records native/authored intensity, then overlays the nonlethal regional
            // contribution. All impact/DangerType guards read that same baseline.
            ScheduledHeavyRainTraversalRuntime.Enable();

            // Creature.TerrainImpact has a separate rainDeath path through
            // RoomRain.CreatureSmashedInGround; isolate Scheduled HeavyRain there too.
            ScheduledHeavyRainImpactGuardRuntime.Enable();

            // Authored/default DangerType RoomRain objects never run their vanilla
            // flood/rain-cycle hazard branch while DryCycle owns the region.
            RoomDangerTypeTakeoverRuntime.Enable();

            // Install last on RoomRain. DryCycle-created carriers in DangerType=None
            // rooms use a rain-only update and never enter vanilla RoomRain.Update.
            SyntheticRoomRainTakeoverRuntime.Enable();

            // Install after all rain owners so pickup-hold hydration observes the final
            // scheduled/authored rain state and the same RoomRain shelter mask.
            RainDrinkingRuntime.Enable();

            // WorldClock keeps RainCycle.timer out of RainGameOver, so RoomCamera cannot
            // receive scheduled rain shake through RainCycle.ScreenShake. Bridge the
            // already-scheduled HeavyRain/DeathRain outputs directly into the camera.
            WeatherCameraEffectsRuntime.Enable();

            RainMeterRoundPipRuntime.Enable();
            FogForecastFlowRuntime.Enable();
            RainMeterFastForwardForecastFix.Enable();

            MiscRuntime.Enable();
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: systems enabled.");
        }
        catch (Exception ex)
        {
            MiscRuntime.Disable();
            OpenShelterSleepRuntime.Disable();
            RainDrinkingRuntime.Disable();
            RainMeterFastForwardForecastFix.Disable();
            FogForecastFlowRuntime.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            WeatherCameraEffectsRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            RoomDangerTypeTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
            HeatWaveWeatherRuntime.Disable();
            FogWeatherRuntime.Disable();
            WeatherScheduleRuntime.Disable();
            ShelterCycleResetRuntime.Disable();
            WorldClockRegionContinuityRuntime.Disable();
            DayNightRuntime.Disable();
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
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
        SpinebackLizardDevConsoleSupport.TryRegister();
    }
}
