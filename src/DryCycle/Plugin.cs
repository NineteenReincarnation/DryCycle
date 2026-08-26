using BepInEx;
using DryCycle.Thirst;

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "Anno";
    public const string ModName = "DryCycle";
    public const string Version = "0.2.2";

    private bool _initialized;

    private void OnEnable()
    {
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;

        if (_initialized)
        {
            ThirstHooks.Disable();
            _initialized = false;
        }
    }

    private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ThirstHooks.Enable();
        Logger.LogInfo($"{ModName} {Version}: thirst system enabled.");
    }
}
