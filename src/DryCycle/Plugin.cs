using BepInEx;

namespace DryCycle;

[BepInPlugin(ModId, ModName, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string ModId = "nineteenreincarnation.drycycle";
    public const string ModName = "DryCycle";
    public const string Version = "0.1.0";

    private bool _initialized;

    private void OnEnable()
    {
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        _initialized = false;
    }

    private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Logger.LogInfo($"{ModName} {Version} initialized. Gameplay systems are not enabled yet.");
    }
}
