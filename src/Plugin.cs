using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
using DryCycle.HUD;
using DryCycle.Thirst;

#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
[BepInDependency("slime-cubed.slugbase", BepInDependency.DependencyFlags.HardDependency)]
internal sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "Anno";
    public const string ModName = "DryCycle";
    public const string Version = "0.0.24";

    internal new static ManualLogSource Logger;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;

        // Register DryCycle's custom SlugBase PlayerFeatures before SlugBase's
        // post-mod-init JSON scan. The hard dependency above guarantees SlugBase
        // is present before this plugin is allowed to load.
        SlugBaseHydrationFeatures.Initialize();

        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    public void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;

        if (_initialized)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            ThirstHooks.Disable();
            _initialized = false;
        }
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        if (_initialized)
        {
            return;
        }

        try
        {
            ThirstHooks.Enable();
            HydrationWeakness.Enable();
            HydrationDivider.Enable();
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: thirst system enabled.");
        }
        catch (Exception ex)
        {
            HydrationDivider.Disable();
            HydrationWeakness.Disable();
            ThirstHooks.Disable();
            Logger.LogError(ex);
            throw;
        }
    }
}
