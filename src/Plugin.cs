using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using DryCycle.HUD;
using DryCycle.Items.DewPod;
using DryCycle.Items.KingVultureSpear;
using DryCycle.Thirst;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
internal sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "Anno";
    public const string ModName = "DryCycle";
    public const string Version = "0.0.34";

    internal new static ManualLogSource Logger;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;
        On.RainWorld.PreModsInit += RainWorld_PreModsInit;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    public void OnDisable()
    {
        On.RainWorld.PreModsInit -= RainWorld_PreModsInit;
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;

        if (_initialized)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            KingVultureSpearCombat.Disable();
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
            DewPodPlantHooks.Enable();
            DewPodPlantCollisionHooks.Enable();
            DewPodClassicVisualHooks.Enable();
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
            DewPodClassicVisualHooks.Disable();
            DewPodPlantCollisionHooks.Disable();
            DewPodPlantHooks.Disable();
            DewPodHooks.Disable();
            ThirstHooks.Disable();
            KingVultureSpearFeedback.Disable();
            KingVultureSpearPlayerEffects.Disable();
            KingVultureSpearHooks.Disable();
            Logger.LogError(ex);
            throw;
        }
    }
}
