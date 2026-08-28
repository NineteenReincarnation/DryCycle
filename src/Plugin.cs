using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Creatures;
using DryCycle.HUD;
using DryCycle.Items.DewPod;
using DryCycle.Items.KingVultureSpear;
using DryCycle.TerrainExt.QuicksandZone;
using DryCycle.Thirst;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
[BepInDependency("slime-cubed.devconsole", BepInDependency.DependencyFlags.SoftDependency)]
internal sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "Anno";
    public const string ModName = "DryCycle";
    public const string Version = "0.0.46";

    internal new static ManualLogSource Logger;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;

        // Creature ExtEnums and StaticWorld hooks must exist before the game's
        // initialization screen constructs creature templates and prebaked pathing.
        SpinebackLizardHooks.Enable();

        // Custom SoundIDs must exist before Rain World's SoundLoader constructs its
        // trigger array from the merged SoundEffects/Sounds.txt data.
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
        SpinebackLizardHooks.Disable();
        SpinebackLizardDevConsoleSupport.ResetRegistration();

        if (_initialized)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
            QuicksandSubmersionCleanup.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandSinkRateLimiter.Disable();
            QuicksandPlayerHorizontalStability.Disable();
            QuicksandZoneHooks.Disable();
            DewPodAudioHooks.Disable();
            DewPodRuntimeTuningHooks.Disable();
            DewPodClassicVisualHooks.Disable();
            DewPodPlantCollisionHooks.Disable();
            DewPodPlantHooks.Disable();
            DewPodHooks.Disable();
            ThirstHooks.Disable();
            KingVultureSpearFeedback.Disable();
            KingVultureSpearPlayerEffects.Disable();
            KingVultureSpearHooks.Disable();
            _initialized = false;
        }
    }

    private static void RainWorld_PreModsInit(On.RainWorld.orig_PreModsInit orig, RainWorld self)
    {
        // Dev Console clears its safe-spawner table during PreModsInit, so allow our
        // optional registration to be rebuilt during the matching PostModsInit.
        SpinebackLizardDevConsoleSupport.ResetRegistration();

        SlugBaseHydrationFeatures.Initialize();
        orig(self);
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        // Retry discovery here in case DryCycle's PreModsInit hook ran before an
        // optional SlugBase assembly became visible. SlugBase JSON scanning occurs
        // later in PostModsInit, so feature registration is still early enough.
        SlugBaseHydrationFeatures.Initialize();
        orig(self);

        if (_initialized)
        {
            return;
        }

        try
        {
            KingVultureSpearHooks.Enable();
            KingVultureSpearPlayerEffects.Enable();
            KingVultureSpearFeedback.Enable();
            ThirstHooks.Enable();
            DewPodHooks.Enable();
            QuicksandZoneHooks.Enable();

            // Native-state capture remains innermost. Horizontal quicksand limiting
            // is installed before the sink controller so its post-update X correction
            // runs before the sink controller performs its final zone/Y pass. This
            // prevents a one-frame high-speed boundary escape from deactivating the
            // sink state before the X limiter can absorb it.
            QuicksandPlayerStruggleControl.EnableNativeCapture();
            QuicksandPlayerHorizontalStability.Enable();
            QuicksandSinkRateLimiter.Enable();
            QuicksandPlayerLocomotionSupport.Enable();
            QuicksandPlayerStruggleControl.Enable();
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
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: systems enabled.");
        }
        catch (Exception ex)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
            QuicksandSubmersionCleanup.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandSinkRateLimiter.Disable();
            QuicksandPlayerHorizontalStability.Disable();
            QuicksandZoneHooks.Disable();
            DewPodAudioHooks.Disable();
            DewPodRuntimeTuningHooks.Disable();
            DewPodClassicVisualHooks.Disable();
            DewPodPlantCollisionHooks.Disable();
            DewPodPlantHooks.Disable();
            DewPodHooks.Disable();
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
        // Soft dependency load order makes Dev Console install its PostModsInit hook
        // first. Calling orig therefore lets it rebuild its built-in safe-spawner
        // table before DryCycle appends SpinebackLizard.
        orig(self);
        SpinebackLizardDevConsoleSupport.TryRegister();
    }
}
