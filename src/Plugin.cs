using System;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Logging;
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
    public const string Version = "0.2.10";

    internal new static ManualLogSource Logger;
    private static bool _initialized;

    public void OnEnable()
    {
        Logger = base.Logger;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    public void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;

        if (_initialized)
        {
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
            _initialized = true;
            Logger.LogInfo($"{ModName} {Version}: thirst system enabled.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }
    }
}
