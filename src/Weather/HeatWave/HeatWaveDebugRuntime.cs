using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveDebugSnapshot
{
    internal readonly float Intensity;
    internal readonly float SolarIntensity;
    internal readonly float WhiteHeat;
    internal readonly float Instability;
    internal readonly float Stillness;
    internal readonly float Burst;
    internal readonly float BurstKick;
    internal readonly bool SimulationAvailable;
    internal readonly string BurstPhase;
    internal readonly int Emitters;

    internal HeatWaveDebugSnapshot(
        float intensity,
        float solarIntensity,
        float whiteHeat,
        float instability,
        float stillness,
        float burst,
        float burstKick,
        bool simulationAvailable,
        string burstPhase,
        int emitters)
    {
        Intensity = intensity;
        SolarIntensity = solarIntensity;
        WhiteHeat = whiteHeat;
        Instability = instability;
        Stillness = stillness;
        Burst = burst;
        BurstKick = burstKick;
        SimulationAvailable = simulationAvailable;
        BurstPhase = burstPhase ?? "--";
        Emitters = emitters;
    }
}

/// <summary>
/// Developer-only HeatWave diagnostics.
/// Ctrl+Shift+H cycles optical debug views.
/// Ctrl+Shift+J forces HeatWave intensity 1 in the camera room without changing the
/// climate schedule.
/// Ctrl+Shift+B forces a Thermal Burst through the real state machine.
/// </summary>
internal static class HeatWaveDebugRuntime
{
    private const int MaxDebugMode = 5;

    private static bool _enabled;
    private static bool _forceWeather;
    private static int _debugMode;
    private static FContainer _root;
    private static FLabel _label;

    internal static int DebugMode => _debugMode;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainWorldGame.Update += RainWorldGame_Update;
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.RainWorldGame.ShutDownProcess -= RainWorldGame_ShutDownProcess;
        _forceWeather = false;
        _debugMode = 0;
        DestroyUi();
    }

    internal static bool TryGetForcedIntensity(Room room, out float intensity)
    {
        intensity = 0f;
        if (!_enabled ||
            !_forceWeather ||
            room?.game == null ||
            !room.game.devToolsActive)
        {
            return false;
        }

        intensity = 1f;
        return true;
    }

    private static void RainWorldGame_Update(
        On.RainWorldGame.orig_Update orig,
        RainWorldGame game)
    {
        orig(game);

        if (!_enabled || game == null)
        {
            return;
        }

        if (!game.devToolsActive)
        {
            _forceWeather = false;
            _debugMode = 0;
            SetUiVisible(false);
            return;
        }

        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (control && shift && Input.GetKeyDown(KeyCode.H))
        {
            _debugMode = (_debugMode + 1) % (MaxDebugMode + 1);
        }

        if (control && shift && Input.GetKeyDown(KeyCode.J))
        {
            _forceWeather = !_forceWeather;
        }

        Room cameraRoom = game.cameras != null && game.cameras.Length > 0
            ? game.cameras[0]?.room
            : null;
        if (control && shift && Input.GetKeyDown(KeyCode.B))
        {
            HeatWaveWeatherRuntime.DebugForceBurst(cameraRoom);
        }

        bool visible = _forceWeather || _debugMode > 0;
        SetUiVisible(visible);
        if (visible)
        {
            UpdateUi(cameraRoom);
        }
    }

    private static void RainWorldGame_ShutDownProcess(
        On.RainWorldGame.orig_ShutDownProcess orig,
        RainWorldGame self)
    {
        DestroyUi();
        _forceWeather = false;
        _debugMode = 0;
        orig(self);
    }

    private static void SetUiVisible(bool visible)
    {
        if (!visible)
        {
            if (_root != null)
            {
                _root.isVisible = false;
            }
            return;
        }

        EnsureUi();
        if (_root != null)
        {
            _root.isVisible = true;
            _root.MoveToFront();
        }
    }

    private static void EnsureUi()
    {
        if (_root != null || Futile.stage == null)
        {
            return;
        }

        _root = new FContainer();
        Futile.stage.AddChild(_root);

        FSprite background = new("pixel")
        {
            anchorX = 0f,
            anchorY = 1f,
            x = 12f,
            y = Futile.screen.pixelHeight - 12f,
            scaleX = 440f,
            scaleY = 126f,
            color = Color.black,
            alpha = 0.78f
        };
        _root.AddChild(background);

        _label = new FLabel(Custom.GetFont(), string.Empty)
        {
            anchorX = 0f,
            anchorY = 1f,
            x = 22f,
            y = Futile.screen.pixelHeight - 22f,
            alignment = FLabelAlignment.Left,
            color = Color.white,
            scale = 0.86f
        };
        _root.AddChild(_label);
    }

    private static void UpdateUi(Room room)
    {
        EnsureUi();
        if (_root == null || _label == null)
        {
            return;
        }

        _root.y = 0f;
        string mode = DebugModeName(_debugMode);
        if (!HeatWaveWeatherRuntime.TryGetDebugSnapshot(room, out HeatWaveDebugSnapshot snapshot))
        {
            _label.text =
                "HeatWave Debug\n" +
                $"View: {mode}   Forced: {(_forceWeather ? "YES" : "NO")}\n" +
                "No HeatWave controller in camera room\n" +
                "Ctrl+Shift+H view  J force  B burst";
            return;
        }

        _label.text =
            "HeatWave Debug\n" +
            $"View: {mode}   Forced: {(_forceWeather ? "YES" : "NO")}   GPU: {(snapshot.SimulationAvailable ? "YES" : "NO")}\n" +
            $"I {snapshot.Intensity:0.00}  Solar {snapshot.SolarIntensity:0.00}  White {snapshot.WhiteHeat:0.00}  Emitters {snapshot.Emitters}\n" +
            $"Burst {snapshot.BurstPhase}  Inst {snapshot.Instability:0.00}  Still {snapshot.Stillness:0.00}  B {snapshot.Burst:0.00}/{snapshot.BurstKick:0.00}\n" +
            "Ctrl+Shift+H view  J force  B burst";
    }

    private static string DebugModeName(int mode)
    {
        return mode switch
        {
            1 => "THERMAL",
            2 => "VELOCITY",
            3 => "OPTICAL",
            4 => "TERRAIN/SUN",
            5 => "BOUNDARY",
            _ => "FINAL"
        };
    }

    private static void DestroyUi()
    {
        if (_root != null)
        {
            _root.RemoveFromContainer();
            _root = null;
            _label = null;
        }
    }
}
