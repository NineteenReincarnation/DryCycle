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
using DryCycle.TemperatureSystem;
using DryCycle.TerrainExt.QuicksandZone;
using DryCycle.Thirst;
using DryCycle.Weather;
using DryCycle.Weather.Climate;
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
    public const string Version = "0.1.106";

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
            RainMeterFastForwardForecastFix.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            RoomDangerTypeTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            ScheduledRainNativeBaselineRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
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

            // Keep the old fixed five-pip diagnostic path in source, but production
            // always uses the authored cycle length and RegionClimate schedules.
            WorldClockHooks.TestScheduleEnabled = false;
            RegionClimateRegistry.Reload();
            DayNightRuntime.Enable();
            WorldClockRegionContinuityRuntime.Enable();
            ShelterCycleResetRuntime.Enable();
            WeatherScheduleRuntime.Enable();
            SandstormWeatherRuntime.Enable();
            RainWeatherRuntime.Enable();

            // Capture the native GlobalRain result after RainWeatherRuntime but before
            // ScheduledHeavyRainTraversalRuntime overlays DryCycle's nonlethal HeavyRain.
            ScheduledRainNativeBaselineRuntime.Enable();

            // Scheduled HeavyRain keeps its room-authored baseline intact while adding
            // only the DryCycle traversal pressure on top.
            ScheduledHeavyRainTraversalRuntime.Enable();

            // Creature.TerrainImpact has a second native rainDeath path through
            // RoomRain.CreatureSmashedInGround. Install this before the DangerType
            // takeover so no-DangerType/synthetic RoomRain is protected as well.
            ScheduledHeavyRainImpactGuardRuntime.Enable();

            // Authored/default DangerType RoomRain objects never run their vanilla
            // flood/rain-cycle hazard branch while DryCycle owns the region.
            RoomDangerTypeTakeoverRuntime.Enable();

            // Install last on RoomRain. RainWeatherRuntime-created carriers in
            // DangerType=None rooms use a DryCycle rain-only update and never enter
            // vanilla RoomRain.Update, avoiding native lifecycle assumptions/NREs.
            SyntheticRoomRainTakeoverRuntime.Enable();

            // RainMeterRoundPipRuntime is the single authoritative DryCycle RainMeter
            // renderer. WeatherForecastHudRuntime remains in source as the old split
            // implementation but is intentionally not enabled to avoid two HUD hooks
            // reading different schedule representations.
            RainMeterRoundPipRuntime.Enable();

            // DevTools S changes the game update rate to 400 FPS. Install this after
            // the authoritative renderer so its final overlay can keep weather colors
            // above the interpolated white HUDCircle during fast-forward inspection.
            RainMeterFastForwardForecastFix.Enable();

            MiscRuntime.Enable();
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: systems enabled.");
        }
        catch (Exception ex)
        {
            MiscRuntime.Disable();
            RainMeterFastForwardForecastFix.Disable();
            RainMeterRoundPipRuntime.Disable();
            WeatherForecastHudRuntime.Disable();
            SyntheticRoomRainTakeoverRuntime.Disable();
            RoomDangerTypeTakeoverRuntime.Disable();
            ScheduledHeavyRainImpactGuardRuntime.Disable();
            ScheduledHeavyRainTraversalRuntime.Disable();
            ScheduledRainNativeBaselineRuntime.Disable();
            RainWeatherRuntime.Disable();
            SandstormWeatherRuntime.Disable();
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
