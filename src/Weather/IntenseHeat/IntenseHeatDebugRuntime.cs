using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

internal static class IntenseHeatDebugRuntime
{
    private const int MaxDebugMode = 4;

    private static bool _enabled;
    private static bool _forced;
    private static int _debugMode;

    internal static int DebugMode => _debugMode;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RainWorldGame.Update += RainWorldGame_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainWorldGame.Update -= RainWorldGame_Update;
        _forced = false;
        _debugMode = 0;
        _enabled = false;
    }

    internal static bool TryGetForcedIntensity(Room room, out float intensity)
    {
        intensity = 0f;
        if (!_enabled || !_forced || room?.game == null || !room.game.devToolsActive)
        {
            return false;
        }

        intensity = 1f;
        return true;
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame self)
    {
        orig(self);

        if (!_enabled || self == null || !self.devToolsActive)
        {
            return;
        }

        bool modifiers = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        modifiers &= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (!modifiers)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.I))
        {
            _forced = !_forced;
            Plugin.Logger?.LogInfo(
                $"DryCycle IntenseHeat forced intensity: {(_forced ? "ON" : "OFF")}.");
        }

        if (Input.GetKeyDown(KeyCode.O))
        {
            _debugMode = (_debugMode + 1) % (MaxDebugMode + 1);
            Plugin.Logger?.LogInfo(
                $"DryCycle IntenseHeat debug mode {_debugMode}: {DebugName(_debugMode)}.");
        }
    }

    private static string DebugName(int mode)
    {
        return mode switch
        {
            1 => "SOLAR EXPOSURE",
            2 => "THERMAL FIELD",
            3 => "COLOR / SUN",
            4 => "EDGE HEAT",
            _ => "FINAL"
        };
    }
}
