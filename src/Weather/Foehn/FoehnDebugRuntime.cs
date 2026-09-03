using DryCycle.Rendering;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

internal readonly struct FoehnDebugSnapshot
{
    internal readonly float Intensity;
    internal readonly Vector2 WindDirection;
    internal readonly bool TerrainFieldAvailable;

    internal FoehnDebugSnapshot(
        float intensity,
        Vector2 windDirection,
        bool terrainFieldAvailable)
    {
        Intensity = intensity;
        WindDirection = windDirection;
        TerrainFieldAvailable = terrainFieldAvailable;
    }
}

/// <summary>
/// Developer diagnostics for Foehn. Ctrl+Shift+K forces full Foehn in the camera
/// room; Ctrl+Shift+L cycles final/flow/terrain/dust-pressure debug views.
/// </summary>
internal static class FoehnDebugRuntime
{
    private const int MaxDebugMode = 3;

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

        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.RainWorldGame.ShutDownProcess -= RainWorldGame_ShutDownProcess;
        _enabled = false;
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

        if (control && shift && Input.GetKeyDown(KeyCode.K))
        {
            _forceWeather = !_forceWeather;
        }

        if (control && shift && Input.GetKeyDown(KeyCode.L))
        {
            _debugMode = (_debugMode + 1) % (MaxDebugMode + 1);
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
            y = Futile.screen.pixelHeight - 142f,
            scaleX = 610f,
            scaleY = 100f,
            color = Color.black,
            alpha = 0.78f
        };
        _root.AddChild(background);

        _label = new FLabel(Custom.GetFont(), string.Empty)
        {
            anchorX = 0f,
            anchorY = 1f,
            x = 22f,
            y = Futile.screen.pixelHeight - 152f,
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

        string atmosphere = DryCycleShaderAssets.HasFoehnAtmosphere ? "YES" : "NO";
        string textures = FoehnWindField.IsAvailable ? "YES" : "NO";
        string dust = FoehnDustField.IsAvailable ? "YES" : "NO";

        if (!FoehnWeatherRuntime.TryGetDebugSnapshot(room, out FoehnDebugSnapshot snapshot))
        {
            _label.text =
                "Foehn Debug\n" +
                $"View: {DebugModeName(_debugMode)}   Forced: {(_forceWeather ? "YES" : "NO")}   Atmosphere: {atmosphere}\n" +
                $"WindTextures: {textures}   DustField: {dust}\n" +
                "No Foehn controller in camera room\n" +
                "Ctrl+Shift+K force   Ctrl+Shift+L view";
            return;
        }

        _label.text =
            "Foehn Debug\n" +
            $"View: {DebugModeName(_debugMode)}   Forced: {(_forceWeather ? "YES" : "NO")}   Atmosphere: {atmosphere}\n" +
            $"WindTextures: {textures}   DustField: {dust}   TerrainField: {(snapshot.TerrainFieldAvailable ? "YES" : "NO")}\n" +
            $"Intensity {snapshot.Intensity:0.00}   Wind ({snapshot.WindDirection.x:0.00}, {snapshot.WindDirection.y:0.00})\n" +
            "Ctrl+Shift+K force   Ctrl+Shift+L view";
    }

    private static string DebugModeName(int mode)
    {
        return mode switch
        {
            1 => "FLOW / GUST",
            2 => "TERRAIN FIELD",
            3 => "DUST / PRESSURE",
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
