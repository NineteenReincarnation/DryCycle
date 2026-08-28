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
    public const string Version = "0.1.50";

    internal new static ManualLogSource Logger;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;

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
        SpinebackLizardHooks.Disable();
        SpinebackLizardDevConsoleSupport.ResetRegistration();

        if (_initialized)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
            QuicksandSubmersionCleanup.Disable();
            QuicksandCreatureEscape.Disable();
            QuicksandAIHazard.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandLooseObjectSinkEase.Disable();
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
        SpinebackLizardDevConsoleSupport.ResetRegistration();
        SlugBaseHydrationFeatures.Initialize();
        orig(self);
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
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
            QuicksandAIHazard.Enable();

            // Creature AI avoidance and post-entry escape are separate layers. The
            // escape layer owns creature motion/death once actual immersion begins.
            QuicksandCreatureEscape.Enable();

            QuicksandPlayerStruggleControl.EnableNativeCapture();
            QuicksandPlayerHorizontalStability.Enable();
            QuicksandSinkRateLimiter.Enable();
            QuicksandLooseObjectSinkEase.Enable();
            QuicksandPlayerLocomotionSupport.Enable();
            QuicksandPlayerStruggleControl.Enable();
            QuicksandWeaponSettling.Enable();

            // Carryable items are still deleted by the generic submerged cleanup.
            // Creatures are skipped there and are only removed after their dedicated
            // complete-submersion death confirmation and short post-death delay.
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
            QuicksandCreatureEscape.Disable();
            QuicksandAIHazard.Disable();
            QuicksandWeaponSettling.Disable();
            QuicksandPlayerStruggleControl.Disable();
            QuicksandPlayerLocomotionSupport.Disable();
            QuicksandLooseObjectSinkEase.Disable();
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
        orig(self);
        SpinebackLizardDevConsoleSupport.TryRegister();
    }
}
