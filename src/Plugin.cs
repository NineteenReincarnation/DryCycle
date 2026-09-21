using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Creatures;
using DryCycle.Creatures.MossySpider;
using DryCycle.Creatures.DesertBatfly;
using DryCycle.Creatures.MantleCrab;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.DayNight;
using DryCycle.Debugging.AI;
using DryCycle.DevUI.DevTool.World;
using DryCycle.HUD;
using DryCycle.Items.DewPod;
using DryCycle.Items.KarmaSpear;
using DryCycle.Items.KingVultureSpear;
using DryCycle.Items.RopeSpear;
using DryCycle.Items.ScavengerLance;
using DryCycle.Misc;
using DryCycle.PlayerAbility.SlugCatKarmicArmor;
using DryCycle.Registration;
using DryCycle.Rendering;
using DryCycle.ShelterExts;
using DryCycle.TemperatureSystem;
using DryCycle.TerrainExt.QuicksandZone;
using DryCycle.Thirst;
using DryCycle.Token;
using DryCycle.Weather;
using DryCycle.Weather.Climate;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using DryCycle.Weather.Scheduling;
using DryCycle.WorldLink.InternalGate;
using CreatureCoreRegistry = DryCycle.Framework.Creature.Core.CreatureRegistry;

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
    public const string Version = "0.2.122";

    internal new static ManualLogSource Logger;
    private static bool _contentRegistered;
    private static bool _initialized;
    internal static bool BootstrapFailed { get; private set; }

    public void OnEnable()
    {
        Logger = base.Logger;
        BootstrapFailed = false;
        StartupDiagnostics.Begin(Logger);
        StartupDiagnostics.Marker("Plugin.OnEnable", "ENTER");

        try
        {
            StartupDiagnostics.Step("Plugin.OnEnable/IteratorLogBridge.Enable", () => Iterators.IteratorLogBridge.Enable(Logger));
            bool iteratorHooksInstalled = StartupDiagnostics.Step(
                "Plugin.OnEnable/IteratorHooks.Install",
                Iterators.IteratorHooks.Install);
            if (!iteratorHooksInstalled)
            {
                throw new InvalidOperationException(
                    "IteratorHooks.Install returned false. The iterator runtime was not installed.");
            }
            StartupDiagnostics.Step("Plugin.OnEnable/PwnIteratorExample.Enable", Iterators.PwnIteratorExample.Enable);

            // CreatureTemplate.Type / descriptor registration is process-lifetime state. Install
            // the core bridge before creating any custom type so a later registration failure can
            // never leave parseable ExtEnums without the StaticWorld/realization hooks that own them.
            StartupDiagnostics.Step("Plugin.OnEnable/CreatureCoreRegistry.Enable", CreatureCoreRegistry.Enable);

            if (!_contentRegistered)
            {
                EnsureCreatureDefinitionRegistered(
                    "MossySpider",
                    "Plugin.OnEnable/MossySpiderDefinition.Register",
                    MossySpiderDefinition.Register);
                EnsureCreatureDefinitionRegistered(
                    "MantleCrab",
                    "Plugin.OnEnable/MantleCrabDefinition.Register",
                    MantleCrabDefinition.Register);
                EnsureCreatureDefinitionRegistered(
                    "DesertBatfly",
                    "Plugin.OnEnable/DesertBatflyDefinition.Register",
                    DB_Definition.Register);
                EnsureCreatureDefinitionRegistered(
                    "LanceScavenger",
                    "Plugin.OnEnable/LanceScavengerDefinition.Register",
                    () =>
                    {
                        LanceScavengerDefinition.Register();
                        return CreatureCoreRegistry.Get("LanceScavenger");
                    });
                _contentRegistered = true;
            }

            StartupDiagnostics.Step("Plugin.OnEnable/DryCycleShaderAssets.Enable", DryCycleShaderAssets.Enable);

            // Direct-number editing is a DevTools input facility, not a gameplay system.
            // Install it as soon as the plugin is enabled so palette/day-night numeric fields
            // are available even if a later OnModsInit subsystem fails before MiscRuntime.
            // MiscRuntime.Enable keeps the same idempotent call for normal initialization.
            StartupDiagnostics.Step("Plugin.OnEnable/PaletteDirectInputRuntime.Enable", PaletteDirectInputRuntime.Enable);

            // Explicit endpoint routing is gameplay support for topology authored by the rebuilt WE.
            // It must be active even when DevTools are closed so repeated room links and one-way links
            // resolve correctly during normal play.
            StartupDiagnostics.Step("Plugin.OnEnable/WorldTopologyRuntime.Enable", WorldTopologyRuntime.Enable);

            StartupDiagnostics.Step("Plugin.OnEnable/DryCycleContent.Enable", DryCycleContent.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/ScavengerLanceHooks.Enable", ScavengerLanceHooks.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/LanceScavengerHooks.Enable", LanceScavengerHooks.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/DB_Relationships.Enable", DB_Relationships.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/DB_RainWorldHooks.Enable", DB_RainWorldHooks.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/SpinebackLizardHooks.Enable", SpinebackLizardHooks.Enable);
            StartupDiagnostics.Step("Plugin.OnEnable/DewPodAudioHooks.InitializeSoundIds", DewPodAudioHooks.InitializeSoundIds);

            StartupDiagnostics.Step("Plugin.OnEnable/Hook RainWorld.PreModsInit", () => On.RainWorld.PreModsInit += RainWorld_PreModsInit);
            StartupDiagnostics.Step("Plugin.OnEnable/Hook RainWorld.OnModsInit", () => On.RainWorld.OnModsInit += RainWorld_OnModsInit);
            StartupDiagnostics.Step("Plugin.OnEnable/Hook RainWorld.PostModsInit", () => On.RainWorld.PostModsInit += RainWorld_PostModsInit);
        }
        catch (Exception error)
        {
            BootstrapFailed = true;
            StartupDiagnostics.Failure("Plugin.OnEnable", error);
            Logger?.LogError(
                "DryCycle bootstrap failed during OnEnable. Partial hooks are being rolled back so Rain World can continue loading.");
            RollbackBootstrap();
        }
    }

    public void OnDisable()
    {
        BootstrapFailed = true;
        StartupDiagnostics.Marker("Plugin.OnDisable", "ENTER");

        SafeBootstrapCleanup("OnDisable/PwnIteratorExample.Unregister", Iterators.PwnIteratorExample.Unregister);
        SafeBootstrapCleanup("OnDisable/IteratorHooks.Uninstall", Iterators.IteratorHooks.Uninstall);
        SafeBootstrapCleanup("OnDisable/IteratorLogBridge.Disable", Iterators.IteratorLogBridge.Disable);
        SafeBootstrapCleanup("OnDisable/AIDebuggerRuntime.Uninstall", AIDebuggerRuntime.Uninstall);

        SafeBootstrapCleanup(
            "OnDisable/RainWorld.PreModsInit hook",
            () => On.RainWorld.PreModsInit -= RainWorld_PreModsInit);
        SafeBootstrapCleanup(
            "OnDisable/RainWorld.OnModsInit hook",
            () => On.RainWorld.OnModsInit -= RainWorld_OnModsInit);
        SafeBootstrapCleanup(
            "OnDisable/RainWorld.PostModsInit hook",
            () => On.RainWorld.PostModsInit -= RainWorld_PostModsInit);

        SafeBootstrapCleanup("OnDisable/DB_Relationships.Disable", DB_Relationships.Disable);
        SafeBootstrapCleanup("OnDisable/DB_RainWorldHooks.Disable", DB_RainWorldHooks.Disable);
        SafeBootstrapCleanup("OnDisable/LanceScavengerHooks.Disable", LanceScavengerHooks.Disable);
        SafeBootstrapCleanup("OnDisable/ScavengerLanceHooks.Disable", ScavengerLanceHooks.Disable);
        SafeBootstrapCleanup("OnDisable/LanceScavengerAssets.Unload", LanceScavengerAssets.Unload);
        SafeBootstrapCleanup("OnDisable/CreatureCoreRegistry lifetime", PreserveCreatureCoreRegistryIfRegistered);
        SafeBootstrapCleanup("OnDisable/DryCycleContent.Disable", DryCycleContent.Disable);
        SafeBootstrapCleanup("OnDisable/CreatureDevConsoleSupport.ResetRegistration", CreatureDevConsoleSupport.ResetRegistration);
        SafeBootstrapCleanup("OnDisable/RopeSpearDevConsoleSupport.ResetRegistration", RopeSpearDevConsoleSupport.ResetRegistration);
        SafeBootstrapCleanup("OnDisable/KarmaSpearDevConsoleSupport.ResetRegistration", KarmaSpearDevConsoleSupport.ResetRegistration);
        SafeBootstrapCleanup("OnDisable/SpinebackLizardHooks.Disable", SpinebackLizardHooks.Disable);
        SafeBootstrapCleanup("OnDisable/SpinebackLizardDevConsoleSupport.ResetRegistration", SpinebackLizardDevConsoleSupport.ResetRegistration);

        // These are installed from OnEnable and therefore must be removed even when full
        // RainWorld.OnModsInit initialization never completed.
        SafeBootstrapCleanup("OnDisable/PaletteDirectInputRuntime.Disable", PaletteDirectInputRuntime.Disable);
        SafeBootstrapCleanup("OnDisable/WorldTopologyRuntime.Disable", WorldTopologyRuntime.Disable);

        if (_initialized)
            RollbackRuntimeInitialization();

        SafeBootstrapCleanup("OnDisable/DryCycleShaderAssets.Disable", DryCycleShaderAssets.Disable);
        StartupDiagnostics.Marker("Plugin.OnDisable", "EXIT");
    }

    private static void RollbackBootstrap()
    {
        SafeBootstrapCleanup(
            "RainWorld.PostModsInit hook",
            () => On.RainWorld.PostModsInit -= RainWorld_PostModsInit);
        SafeBootstrapCleanup(
            "RainWorld.OnModsInit hook",
            () => On.RainWorld.OnModsInit -= RainWorld_OnModsInit);
        SafeBootstrapCleanup(
            "RainWorld.PreModsInit hook",
            () => On.RainWorld.PreModsInit -= RainWorld_PreModsInit);
        SafeBootstrapCleanup("Spineback lizard hooks", SpinebackLizardHooks.Disable);
        SafeBootstrapCleanup("Desert Batfly RainWorld hooks", DB_RainWorldHooks.Disable);
        SafeBootstrapCleanup("Desert Batfly relationships", DB_Relationships.Disable);
        SafeBootstrapCleanup("Lance Scavenger hooks", LanceScavengerHooks.Disable);
        SafeBootstrapCleanup("Scavenger Lance hooks", ScavengerLanceHooks.Disable);
        SafeBootstrapCleanup("DryCycle content runtime", DryCycleContent.Disable);
        SafeBootstrapCleanup("Creature Core registry lifetime", PreserveCreatureCoreRegistryIfRegistered);
        SafeBootstrapCleanup("world topology runtime", WorldTopologyRuntime.Disable);
        SafeBootstrapCleanup("palette direct input", PaletteDirectInputRuntime.Disable);
        SafeBootstrapCleanup("shader assets", DryCycleShaderAssets.Disable);
        SafeBootstrapCleanup("PWN iterator example", Iterators.PwnIteratorExample.Unregister);
        SafeBootstrapCleanup("iterator hooks", Iterators.IteratorHooks.Uninstall);
        SafeBootstrapCleanup("iterator log bridge", Iterators.IteratorLogBridge.Disable);
    }

    private static void EnsureCreatureDefinitionRegistered(
        string typeName,
        string startupSource,
        Func<CreatureDescriptor> register)
    {
        if (CreatureCoreRegistry.TryGet(typeName, out _))
        {
            StartupDiagnostics.Marker(startupSource, "SKIP-ALREADY-REGISTERED");
            return;
        }

        StartupDiagnostics.Step(startupSource, register);
    }

    private static void PreserveCreatureCoreRegistryIfRegistered()
    {
        if (CreatureCoreRegistry.Registered.Count == 0)
        {
            CreatureCoreRegistry.Disable();
            return;
        }

        // Descriptor/ExtEnum registration is intentionally process-lifetime. Removing the core
        // hooks while those IDs remain registered creates a harder half-state than leaving the
        // registry active: world.txt can resolve the custom Type but StaticWorld has no template
        // owner. Keep/recover the core bridge until process exit.
        if (!CreatureCoreRegistry.IsEnabled)
            CreatureCoreRegistry.Enable();

        StartupDiagnostics.Marker(
            "CreatureCoreRegistry",
            "PRESERVED",
            "registered descriptors=" + CreatureCoreRegistry.Registered.Count);
    }

    private static void SafeBootstrapCleanup(string name, Action action)
    {
        if (action == null)
        {
            return;
        }

        if (!StartupDiagnostics.RollbackStep("Rollback/" + name, action))
        {
            Logger?.LogWarning(
                "DryCycle rollback step failed for '" + name +
                "'. See the preceding [ROLLBACK-FAIL] startup entry for the full exception.");
        }
    }

    private static void RainWorld_PreModsInit(On.RainWorld.orig_PreModsInit orig, RainWorld self)
    {
        StartupDiagnostics.Marker("RainWorld.PreModsInit", "ENTER");
        StartupDiagnostics.Step("RainWorld.PreModsInit/BeforePreModsInit subscribers", () => DryCycleLifecycleEvents.RaiseBeforePreModsInit(self));
        StartupDiagnostics.Optional("RainWorld.PreModsInit/ScavengerLanceDevConsoleSupport.ResetRegistration", ScavengerLanceDevConsoleSupport.ResetRegistration);
        StartupDiagnostics.Optional("RainWorld.PreModsInit/CreatureDevConsoleSupport.ResetRegistration", CreatureDevConsoleSupport.ResetRegistration);
        StartupDiagnostics.Optional("RainWorld.PreModsInit/RopeSpearDevConsoleSupport.ResetRegistration", RopeSpearDevConsoleSupport.ResetRegistration);
        StartupDiagnostics.Optional("RainWorld.PreModsInit/KarmaSpearDevConsoleSupport.ResetRegistration", KarmaSpearDevConsoleSupport.ResetRegistration);
        StartupDiagnostics.Optional("RainWorld.PreModsInit/SpinebackLizardDevConsoleSupport.ResetRegistration", SpinebackLizardDevConsoleSupport.ResetRegistration);
        TryInitializeSlugBaseHydrationFeatures();
        StartupDiagnostics.Step("RainWorld.PreModsInit/orig", () => orig(self));
        StartupDiagnostics.Step("RainWorld.PreModsInit/AfterPreModsInit subscribers", () => DryCycleLifecycleEvents.RaiseAfterPreModsInit(self));
        StartupDiagnostics.Marker("RainWorld.PreModsInit", "EXIT");
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        StartupDiagnostics.Marker("RainWorld.OnModsInit", "ENTER");
        StartupDiagnostics.Step("RainWorld.OnModsInit/BeforeModsInit subscribers", () => DryCycleLifecycleEvents.RaiseBeforeModsInit(self));
        TryInitializeSlugBaseHydrationFeatures();
        StartupDiagnostics.Step("RainWorld.OnModsInit/orig", () => orig(self));

        if (_initialized)
        {
            StartupDiagnostics.Optional("RainWorld.OnModsInit/AIDebuggerRuntime.Install(rebind)", () => AIDebuggerRuntime.Install(self, Logger));
            StartupDiagnostics.Step("RainWorld.OnModsInit/AfterModsInit subscribers", () => DryCycleLifecycleEvents.RaiseAfterModsInit(self));
            StartupDiagnostics.Marker("RainWorld.OnModsInit", "EXIT-ALREADY-INITIALIZED");
            return;
        }

        try
        {
            StartupDiagnostics.Step("RainWorld.OnModsInit/DryCycleShaderAssets.EnsureLoaded", () => DryCycleShaderAssets.EnsureLoaded(self));
            StartupDiagnostics.Step("RainWorld.OnModsInit/LanceScavengerAssets.EnsureLoaded", LanceScavengerAssets.EnsureLoaded);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RegionDayNightOptions.Register", RegionDayNightOptions.Register);

            StartupDiagnostics.Step("RainWorld.OnModsInit/DryCycleContent.LoadResources", () => DryCycleContent.LoadResources(self));
            StartupDiagnostics.Step("RainWorld.OnModsInit/MantleCrabDefinition.LoadResources", () => MantleCrabDefinition.LoadResources(self));
            StartupDiagnostics.Step("RainWorld.OnModsInit/DB_Sandbox.Enable", DB_Sandbox.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DB_WarpCompatibility.Enable", DB_WarpCompatibility.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/KingVultureSpearHooks.Enable", KingVultureSpearHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearHooks.Enable", RopeSpearHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DryCycleTokenRuntime.Enable", DryCycleTokenRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearSandboxRuntime.Enable", RopeSpearSandboxRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearDiagonalClimbRuntime.Enable", RopeSpearDiagonalClimbRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearMountVinePoseRuntime.Enable", RopeSpearMountVinePoseRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearWallStickRuntime.Enable", RopeSpearWallStickRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RopeSpearAimController.Enable", RopeSpearAimController.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/KingVultureSpearPlayerEffects.Enable", KingVultureSpearPlayerEffects.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/KingVultureSpearFeedback.Enable", KingVultureSpearFeedback.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/ThirstHooks.Enable", ThirstHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DevFoodWaterRefillRuntime.Enable", DevFoodWaterRefillRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/KarmaSpearHooks.Enable", KarmaSpearHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/SlugCatKarmicArmorRuntime.Enable", SlugCatKarmicArmorRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/TemperatureSystemRuntime.Enable", TemperatureSystemRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodHooks.Enable", DewPodHooks.Enable);

            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandZoneHooks.Enable", QuicksandZoneHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandDrillCrabCompatibility.EnsureEnabled", QuicksandDrillCrabCompatibility.EnsureEnabled);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandAIHazard.Enable", QuicksandAIHazard.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandCreatureEscape.Enable", QuicksandCreatureEscape.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandPlayerStruggleControl.EnableNativeCapture", QuicksandPlayerStruggleControl.EnableNativeCapture);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandPlayerHorizontalStability.Enable", QuicksandPlayerHorizontalStability.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandSinkRateLimiter.Enable", QuicksandSinkRateLimiter.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandLooseObjectSinkEase.Enable", QuicksandLooseObjectSinkEase.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandPlayerLocomotionSupport.Enable", QuicksandPlayerLocomotionSupport.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandPlayerStruggleControl.Enable", QuicksandPlayerStruggleControl.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandPlayerShoreConstraint.Enable", QuicksandPlayerShoreConstraint.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandWeaponSettling.Enable", QuicksandWeaponSettling.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/QuicksandSubmersionCleanup.Enable", QuicksandSubmersionCleanup.Enable);

            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodPlantHooks.Enable", DewPodPlantHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodPlantCollisionHooks.Enable", DewPodPlantCollisionHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodClassicVisualHooks.Enable", DewPodClassicVisualHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodRuntimeTuningHooks.Enable", DewPodRuntimeTuningHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DewPodAudioHooks.Enable", DewPodAudioHooks.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/KingVultureSpearCombat.Enable", KingVultureSpearCombat.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/HydrationWeakness.Enable", HydrationWeakness.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DehydrationVisualRuntime.Enable", DehydrationVisualRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/HydrationDivider.Enable", HydrationDivider.Enable);

            WorldClockHooks.TestScheduleEnabled = false;
            StartupDiagnostics.Step("RainWorld.OnModsInit/WeatherTypeRegistry.ResetWarnings", WeatherTypeRegistry.ResetWarnings);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RegionClimateRegistry.Reload", RegionClimateRegistry.Reload);
            StartupDiagnostics.Step("RainWorld.OnModsInit/DayNightRuntime.Enable", DayNightRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/WorldClockRegionContinuityRuntime.Enable", WorldClockRegionContinuityRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/ShelterCycleResetRuntime.Enable", ShelterCycleResetRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/OpenShelterSleepRuntime.Enable", OpenShelterSleepRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/WeatherScheduleRuntime.Enable", WeatherScheduleRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/FogWeatherRuntime.Enable", FogWeatherRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/HeatWaveWeatherRuntime.Enable", HeatWaveWeatherRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/IntenseHeatWeatherRuntime.Enable", IntenseHeatWeatherRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/SandstormWeatherRuntime.Enable", SandstormWeatherRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RainWeatherRuntime.Enable", RainWeatherRuntime.Enable);

            StartupDiagnostics.Step("RainWorld.OnModsInit/ScheduledHeavyRainTraversalRuntime.Enable", ScheduledHeavyRainTraversalRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/ScheduledHeavyRainImpactGuardRuntime.Enable", ScheduledHeavyRainImpactGuardRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/SyntheticRoomRainTakeoverRuntime.Enable", SyntheticRoomRainTakeoverRuntime.Enable);

            StartupDiagnostics.Step("RainWorld.OnModsInit/RainDrinkingRuntime.Enable", RainDrinkingRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/WeatherCameraEffectsRuntime.Enable", WeatherCameraEffectsRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RainMeterRoundPipRuntime.Enable", RainMeterRoundPipRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/FogForecastFlowRuntime.Enable", FogForecastFlowRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/RainMeterFastForwardForecastFix.Enable", RainMeterFastForwardForecastFix.Enable);

            StartupDiagnostics.Step("RainWorld.OnModsInit/InternalGateRuntime.Enable", InternalGateRuntime.Enable);
            StartupDiagnostics.Step("RainWorld.OnModsInit/MiscRuntime.Enable", MiscRuntime.Enable);
            _initialized = true;
            StartupDiagnostics.Optional("RainWorld.OnModsInit/AIDebuggerRuntime.Install", () => AIDebuggerRuntime.Install(self, Logger));
            Logger.LogInfo($"{ModName} {Version}: systems enabled.");
            StartupDiagnostics.Step("RainWorld.OnModsInit/AfterModsInit subscribers", () => DryCycleLifecycleEvents.RaiseAfterModsInit(self));
            StartupDiagnostics.Marker("RainWorld.OnModsInit", "EXIT");
        }
        catch (Exception ex)
        {
            // Log the primary failure before any rollback work. If cleanup itself encounters a
            // secondary problem, the original startup source and full stack are already durable.
            StartupDiagnostics.Failure("RainWorld.OnModsInit/PostModTransaction", ex);
            Logger.LogError("DryCycle post-mod initialization failed; the failing runtime transaction was rolled back so Rain World can continue loading.");
            RollbackRuntimeInitialization();
            StartupDiagnostics.Step("RainWorld.OnModsInit/AfterModsInit subscribers after rollback", () => DryCycleLifecycleEvents.RaiseAfterModsInit(self));
            StartupDiagnostics.Marker("RainWorld.OnModsInit", "EXIT-ROLLED-BACK");
            return;
        }
    }

    private static void TryInitializeSlugBaseHydrationFeatures()
    {
        StartupDiagnostics.Optional(
            "SlugBaseHydrationFeatures.Initialize",
            SlugBaseHydrationFeatures.Initialize);
    }

    private static void RollbackRuntimeInitialization()
    {
        SafeBootstrapCleanup("AIDebuggerRuntime.Uninstall", AIDebuggerRuntime.Uninstall);
        SafeBootstrapCleanup("DB_WarpCompatibility.Disable", DB_WarpCompatibility.Disable);
        SafeBootstrapCleanup("DB_Sandbox.Disable", DB_Sandbox.Disable);
        SafeBootstrapCleanup("InternalGateRuntime.Disable", InternalGateRuntime.Disable);
        SafeBootstrapCleanup("MiscRuntime.Disable", MiscRuntime.Disable);
        SafeBootstrapCleanup("OpenShelterSleepRuntime.Disable", OpenShelterSleepRuntime.Disable);
        SafeBootstrapCleanup("RainDrinkingRuntime.Disable", RainDrinkingRuntime.Disable);
        SafeBootstrapCleanup("RainMeterFastForwardForecastFix.Disable", RainMeterFastForwardForecastFix.Disable);
        SafeBootstrapCleanup("FogForecastFlowRuntime.Disable", FogForecastFlowRuntime.Disable);
        SafeBootstrapCleanup("RainMeterRoundPipRuntime.Disable", RainMeterRoundPipRuntime.Disable);
        SafeBootstrapCleanup("WeatherForecastHudRuntime.Disable", WeatherForecastHudRuntime.Disable);
        SafeBootstrapCleanup("WeatherCameraEffectsRuntime.Disable", WeatherCameraEffectsRuntime.Disable);
        SafeBootstrapCleanup("SyntheticRoomRainTakeoverRuntime.Disable", SyntheticRoomRainTakeoverRuntime.Disable);
        SafeBootstrapCleanup("ScheduledHeavyRainImpactGuardRuntime.Disable", ScheduledHeavyRainImpactGuardRuntime.Disable);
        SafeBootstrapCleanup("ScheduledHeavyRainTraversalRuntime.Disable", ScheduledHeavyRainTraversalRuntime.Disable);
        SafeBootstrapCleanup("RainWeatherRuntime.Disable", RainWeatherRuntime.Disable);
        SafeBootstrapCleanup("SandstormWeatherRuntime.Disable", SandstormWeatherRuntime.Disable);
        SafeBootstrapCleanup("IntenseHeatWeatherRuntime.Disable", IntenseHeatWeatherRuntime.Disable);
        SafeBootstrapCleanup("HeatWaveWeatherRuntime.Disable", HeatWaveWeatherRuntime.Disable);
        SafeBootstrapCleanup("FogWeatherRuntime.Disable", FogWeatherRuntime.Disable);
        SafeBootstrapCleanup("WeatherScheduleRuntime.Disable", WeatherScheduleRuntime.Disable);
        SafeBootstrapCleanup("ShelterCycleResetRuntime.Disable", ShelterCycleResetRuntime.Disable);
        SafeBootstrapCleanup("WorldClockRegionContinuityRuntime.Disable", WorldClockRegionContinuityRuntime.Disable);
        SafeBootstrapCleanup("DayNightRuntime.Disable", DayNightRuntime.Disable);
        SafeBootstrapCleanup("HydrationDivider.Disable", HydrationDivider.Disable);
        SafeBootstrapCleanup("DehydrationVisualRuntime.Disable", DehydrationVisualRuntime.Disable);
        SafeBootstrapCleanup("HydrationWeakness.Disable", HydrationWeakness.Disable);
        SafeBootstrapCleanup("KingVultureSpearCombat.Disable", KingVultureSpearCombat.Disable);
        SafeBootstrapCleanup("RopeSpearWallStickRuntime.Disable", RopeSpearWallStickRuntime.Disable);
        SafeBootstrapCleanup("RopeSpearMountVinePoseRuntime.Disable", RopeSpearMountVinePoseRuntime.Disable);
        SafeBootstrapCleanup("RopeSpearDiagonalClimbRuntime.Disable", RopeSpearDiagonalClimbRuntime.Disable);
        SafeBootstrapCleanup("RopeSpearAimController.Disable", RopeSpearAimController.Disable);
        SafeBootstrapCleanup("RopeSpearSandboxRuntime.Disable", RopeSpearSandboxRuntime.Disable);
        SafeBootstrapCleanup("DryCycleTokenRuntime.Disable", DryCycleTokenRuntime.Disable);
        SafeBootstrapCleanup("RopeSpearHooks.Disable", RopeSpearHooks.Disable);
        SafeBootstrapCleanup("QuicksandSubmersionCleanup.Disable", QuicksandSubmersionCleanup.Disable);
        SafeBootstrapCleanup("QuicksandCreatureEscape.Disable", QuicksandCreatureEscape.Disable);
        SafeBootstrapCleanup("QuicksandAIHazard.Disable", QuicksandAIHazard.Disable);
        SafeBootstrapCleanup("QuicksandWeaponSettling.Disable", QuicksandWeaponSettling.Disable);
        SafeBootstrapCleanup("QuicksandPlayerShoreConstraint.Disable", QuicksandPlayerShoreConstraint.Disable);
        SafeBootstrapCleanup("QuicksandPlayerStruggleControl.Disable", QuicksandPlayerStruggleControl.Disable);
        SafeBootstrapCleanup("QuicksandPlayerLocomotionSupport.Disable", QuicksandPlayerLocomotionSupport.Disable);
        SafeBootstrapCleanup("QuicksandLooseObjectSinkEase.Disable", QuicksandLooseObjectSinkEase.Disable);
        SafeBootstrapCleanup("QuicksandSinkRateLimiter.Disable", QuicksandSinkRateLimiter.Disable);
        SafeBootstrapCleanup("QuicksandPlayerHorizontalStability.Disable", QuicksandPlayerHorizontalStability.Disable);
        SafeBootstrapCleanup("QuicksandDrillCrabCompatibility.Disable", QuicksandDrillCrabCompatibility.Disable);
        SafeBootstrapCleanup("QuicksandZoneHooks.Disable", QuicksandZoneHooks.Disable);
        SafeBootstrapCleanup("DewPodAudioHooks.Disable", DewPodAudioHooks.Disable);
        SafeBootstrapCleanup("DewPodRuntimeTuningHooks.Disable", DewPodRuntimeTuningHooks.Disable);
        SafeBootstrapCleanup("DewPodClassicVisualHooks.Disable", DewPodClassicVisualHooks.Disable);
        SafeBootstrapCleanup("DewPodPlantCollisionHooks.Disable", DewPodPlantCollisionHooks.Disable);
        SafeBootstrapCleanup("DewPodPlantHooks.Disable", DewPodPlantHooks.Disable);
        SafeBootstrapCleanup("DewPodHooks.Disable", DewPodHooks.Disable);
        SafeBootstrapCleanup("TemperatureSystemRuntime.Disable", TemperatureSystemRuntime.Disable);
        SafeBootstrapCleanup("SlugCatKarmicArmorRuntime.Disable", SlugCatKarmicArmorRuntime.Disable);
        SafeBootstrapCleanup("KarmaSpearHooks.Disable", KarmaSpearHooks.Disable);
        SafeBootstrapCleanup("DevFoodWaterRefillRuntime.Disable", DevFoodWaterRefillRuntime.Disable);
        SafeBootstrapCleanup("ThirstHooks.Disable", ThirstHooks.Disable);
        SafeBootstrapCleanup("KingVultureSpearFeedback.Disable", KingVultureSpearFeedback.Disable);
        SafeBootstrapCleanup("KingVultureSpearPlayerEffects.Disable", KingVultureSpearPlayerEffects.Disable);
        SafeBootstrapCleanup("KingVultureSpearHooks.Disable", KingVultureSpearHooks.Disable);
        SafeBootstrapCleanup("SpinebackLizardHooks.Disable", SpinebackLizardHooks.Disable);
        _initialized = false;
    }

    private static void RainWorld_PostModsInit(
        On.RainWorld.orig_PostModsInit orig,
        RainWorld self)
    {
        StartupDiagnostics.Marker("RainWorld.PostModsInit", "ENTER");
        StartupDiagnostics.Step("RainWorld.PostModsInit/orig", () => orig(self));
        StartupDiagnostics.Optional("RainWorld.PostModsInit/CreatureDevConsoleSupport.TryRegisterAll", () => CreatureDevConsoleSupport.TryRegisterAll());
        StartupDiagnostics.Optional("RainWorld.PostModsInit/ScavengerLanceDevConsoleSupport.TryRegister", () => ScavengerLanceDevConsoleSupport.TryRegister());
        StartupDiagnostics.Optional("RainWorld.PostModsInit/RopeSpearDevConsoleSupport.TryRegister", () => RopeSpearDevConsoleSupport.TryRegister());
        StartupDiagnostics.Optional("RainWorld.PostModsInit/KarmaSpearDevConsoleSupport.TryRegister", () => KarmaSpearDevConsoleSupport.TryRegister());
        StartupDiagnostics.Optional("RainWorld.PostModsInit/SpinebackLizardDevConsoleSupport.TryRegister", () => SpinebackLizardDevConsoleSupport.TryRegister());
        StartupDiagnostics.Marker("RainWorld.PostModsInit", "EXIT");
    }
}
