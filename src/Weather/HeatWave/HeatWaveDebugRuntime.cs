using DryCycle.Rendering;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal readonly struct HeatWaveDebugSnapshot
{
    internal readonly float Intensity;
    internal readonly float SolarIntensity;
    internal readonly float ToneAmount;
    internal readonly float LevelHeatAmount;
    internal readonly bool LevelHeatApplied;
    internal readonly bool SurfaceFieldAvailable;
    internal readonly int Emitters;

    internal HeatWaveDebugSnapshot(
        float intensity,
        float solarIntensity,
        float toneAmount,
        float levelHeatAmount,
        bool levelHeatApplied,
        bool surfaceFieldAvailable,
        int emitters)
    {
        Intensity = intensity;
        SolarIntensity = solarIntensity;
        ToneAmount = toneAmount;
        LevelHeatAmount = levelHeatAmount;
        LevelHeatApplied = levelHeatApplied;
        SurfaceFieldAvailable = surfaceFieldAvailable;
        Emitters = emitters;
    }
}

/// <summary>
/// Developer-only diagnostics for the actual HeatWave presentation layers.
/// Ctrl+Shift+H cycles final/band/air/color/flow/surface/focus views.
/// Ctrl+Shift+J forces HeatWave intensity 1 in the camera room without changing the
/// climate schedule.
/// </summary>
internal static class HeatWaveDebugRuntime
{
    private const int MaxDebugMode = 6;

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
            scaleX = 590f,
            scaleY = 118f,
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

        string mode = DebugModeName(_debugMode);
        string atmosphere = DryCycleShaderAssets.HasHeatWaveAtmosphere ? "YES" : "NO";
        string textures = HeatWaveNoiseField.IsAvailable ? "YES" : "NO";

        if (!HeatWaveWeatherRuntime.TryGetDebugSnapshot(room, out HeatWaveDebugSnapshot snapshot))
        {
            _label.text =
                "HeatWave Debug\n" +
                $"View: {mode}   Forced: {(_forceWeather ? "YES" : "NO")}   Atmosphere: {atmosphere}\n" +
                $"OpticalTextures: {textures}\n" +
                "No HeatWave controller in camera room\n" +
                "Ctrl+Shift+H view   Ctrl+Shift+J force";
            return;
        }

        _label.text =
            "HeatWave Debug\n" +
            $"View: {mode}   Forced: {(_forceWeather ? "YES" : "NO")}   Atmosphere: {atmosphere}\n" +
            $"OpticalTextures: {textures}   SurfaceField: {(snapshot.SurfaceFieldAvailable ? "YES" : "NO")}   " +
            $"LevelHeat: {(snapshot.LevelHeatApplied ? "YES" : "NO")}\n" +
            $"Intensity {snapshot.Intensity:0.00}   Solar {snapshot.SolarIntensity:0.00}   " +
            $"Tone {snapshot.ToneAmount:0.00}   Level {snapshot.LevelHeatAmount:0.00}   " +
            $"HeatColumns {snapshot.Emitters}\n" +
            "Ctrl+Shift+H view   Ctrl+Shift+J force";
    }

    private static string DebugModeName(int mode)
    {
        return mode switch
        {
            1 => "HEAT BANDS",
            2 => "AIR MOTION",
            3 => "HEAT COLOR",
            4 => "FLOW / MIRAGE",
            5 => "SURFACE / GROUND",
            6 => "LENS FOCUS",
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
