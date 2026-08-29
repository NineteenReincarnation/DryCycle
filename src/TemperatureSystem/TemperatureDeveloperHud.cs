using System.Globalization;
using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Draggable developer overlay for player, room/environment and thermal-model data.
/// The panel is available only while Rain World's developer tools are active.
/// Ctrl + Shift + T toggles it; the title bar can be dragged and its position persists.
/// </summary>
internal static class TemperatureDeveloperHud
{
    private const string PositionXKey = "DryCycle.ThermalDebugPanel.X";
    private const string PositionYKey = "DryCycle.ThermalDebugPanel.Y";

    private const float PanelWidth = 640f;
    private const float PanelHeight = 680f;
    private const float TitleHeight = 44f;
    private const float EdgePadding = 8f;

    // Rain World's normal UI font is bitmap-oriented. Keep it at native scale and
    // keep the whole panel on integer Futile pixels so the glyphs remain crisp.
    private const float TextScale = 1f;
    private const float HeaderScale = 1f;
    private const float TitleScale = 1f;

    private const float KeyColumnX = 18f;
    private const float ValueColumnX = 250f;

    private const float PlayerSectionTop = 636f;
    private const float PlayerHeaderY = 614f;
    private const float PlayerRowsY = 586f;

    private const float RoomSectionTop = 446f;
    private const float RoomHeaderY = 424f;
    private const float RoomRowsY = 396f;

    private const float ThermalSectionTop = 216f;
    private const float ThermalHeaderY = 194f;
    private const float ThermalRowsY = 166f;

    private static readonly Color PanelBackground = new(0f, 0f, 0f, 0.84f);
    private static readonly Color BorderColor = new(0.78f, 0.78f, 0.78f, 0.90f);
    private static readonly Color HeaderColor = new(1f, 1f, 0.58f, 1f);
    private static readonly Color KeyColor = new(0.78f, 0.78f, 0.78f, 1f);
    private static readonly Color ValueColor = Color.white;
    private static readonly Color SeparatorColor = new(0.45f, 0.45f, 0.45f, 0.68f);

    private static bool _enabled;
    private static bool _visible;
    private static bool _dragging;
    private static bool _positionLoaded;
    private static Vector2 _panelPosition;
    private static Vector2 _dragOffset;

    private static FContainer _root;
    private static FLabel _playerValues;
    private static FLabel _roomValues;
    private static FLabel _thermalValues;

    internal static bool DeveloperMode => _visible;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        _visible = false;
        _dragging = false;
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
        SavePosition();
        DestroyPanel();
        _visible = false;
        _dragging = false;
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

        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (game.devToolsActive && control && shift && Input.GetKeyDown(KeyCode.T))
        {
            SetVisible(game, !_visible);
        }

        if (!game.devToolsActive)
        {
            SetVisible(game, false);
            return;
        }

        if (!_visible)
        {
            return;
        }

        EnsurePanel(game);
        HandleMouse(game);
        UpdatePanelData(game);
    }

    private static void RainWorldGame_ShutDownProcess(
        On.RainWorldGame.orig_ShutDownProcess orig,
        RainWorldGame self)
    {
        SavePosition();
        DestroyPanel();
        _visible = false;
        _dragging = false;
        orig(self);
    }

    private static void SetVisible(RainWorldGame game, bool visible)
    {
        if (_visible == visible && (_root == null || _root.isVisible == visible))
        {
            return;
        }

        _visible = visible;

        if (visible)
        {
            EnsurePanel(game);
            if (_root != null)
            {
                _root.isVisible = true;
            }
        }
        else
        {
            _dragging = false;
            if (_root != null)
            {
                _root.isVisible = false;
            }
        }

        global::DryCycle.Plugin.Logger?.LogInfo(
            $"Thermal debug panel: {(visible ? "ON" : "OFF")}");
    }

    private static void EnsurePanel(RainWorldGame game)
    {
        if (_root != null)
        {
            ClampPanelToScreen();
            ApplyPanelPosition();
            return;
        }

        if (Futile.stage == null)
        {
            return;
        }

        LoadPosition(game);

        _root = new FContainer();
        Futile.stage.AddChild(_root);

        FSprite background = new("pixel")
        {
            anchorX = 0f,
            anchorY = 0f,
            scaleX = PanelWidth,
            scaleY = PanelHeight,
            color = Color.black,
            alpha = PanelBackground.a
        };
        _root.AddChild(background);

        AddHorizontalLine(0f, 1f, BorderColor);
        AddHorizontalLine(PanelHeight, 1f, BorderColor);
        AddVerticalLine(0f, 1f, BorderColor);
        AddVerticalLine(PanelWidth, 1f, BorderColor);

        AddHorizontalLine(PlayerSectionTop, 1f, SeparatorColor);
        AddHorizontalLine(RoomSectionTop, 1f, SeparatorColor);
        AddHorizontalLine(ThermalSectionTop, 1f, SeparatorColor);

        CreateLabel(
            "DryCycle Thermal Debug",
            KeyColumnX,
            PanelHeight - 12f,
            HeaderColor,
            TitleScale,
            FLabelAlignment.Left);

        FLabel closeLabel = CreateLabel(
            "X",
            PanelWidth - 23f,
            PanelHeight - 12f,
            Color.white,
            TitleScale,
            FLabelAlignment.Center);
        closeLabel.anchorX = 0.5f;

        CreateLabel("PLAYER", KeyColumnX, PlayerHeaderY, HeaderColor, HeaderScale, FLabelAlignment.Left);
        FLabel playerKeys = CreateLabel(
            string.Empty,
            KeyColumnX,
            PlayerRowsY,
            KeyColor,
            TextScale,
            FLabelAlignment.Left);
        _playerValues = CreateLabel(
            string.Empty,
            ValueColumnX,
            PlayerRowsY,
            ValueColor,
            TextScale,
            FLabelAlignment.Left);

        CreateLabel("ROOM / ENVIRONMENT", KeyColumnX, RoomHeaderY, HeaderColor, HeaderScale, FLabelAlignment.Left);
        FLabel roomKeys = CreateLabel(
            string.Empty,
            KeyColumnX,
            RoomRowsY,
            KeyColor,
            TextScale,
            FLabelAlignment.Left);
        _roomValues = CreateLabel(
            string.Empty,
            ValueColumnX,
            RoomRowsY,
            ValueColor,
            TextScale,
            FLabelAlignment.Left);

        CreateLabel("THERMAL MODEL", KeyColumnX, ThermalHeaderY, HeaderColor, HeaderScale, FLabelAlignment.Left);
        FLabel thermalKeys = CreateLabel(
            string.Empty,
            KeyColumnX,
            ThermalRowsY,
            KeyColor,
            TextScale,
            FLabelAlignment.Left);
        _thermalValues = CreateLabel(
            string.Empty,
            ValueColumnX,
            ThermalRowsY,
            ValueColor,
            TextScale,
            FLabelAlignment.Left);

        playerKeys.text =
            "Player\n" +
            "Slugcat\n" +
            "Position\n" +
            "BodyChunk 0\n" +
            "BodyChunk 1\n" +
            "Velocity\n" +
            "Water";

        roomKeys.text =
            "Region\n" +
            "Room\n" +
            "RoomHeat\n" +
            "SunlightIntensity\n" +
            "RoomShade\n" +
            "LocalShade\n" +
            "EffectiveSunlight\n" +
            "Gravity\n" +
            "Water level\n" +
            "Humidity / Wind";

        thermalKeys.text =
            "BodyHeat 0\n" +
            "BodyHeat 1\n" +
            "Difference\n" +
            "Target flow\n" +
            "Internal flow\n" +
            "Room half-life\n" +
            "Flow HL / Conduct.";

        ApplyPanelPosition();
        _root.isVisible = _visible;
    }

    private static FLabel CreateLabel(
        string text,
        float x,
        float y,
        Color color,
        float scale,
        FLabelAlignment alignment)
    {
        FLabel label = new(Custom.GetFont(), text)
        {
            x = Mathf.Round(x),
            y = Mathf.Round(y),
            anchorX = 0f,
            anchorY = 1f,
            alignment = alignment,
            color = color,
            alpha = 1f,
            scale = scale
        };

        _root.AddChild(label);
        return label;
    }

    private static void AddHorizontalLine(float y, float thickness, Color color)
    {
        FSprite line = new("pixel")
        {
            anchorX = 0f,
            anchorY = 0.5f,
            x = 0f,
            y = Mathf.Round(y),
            scaleX = PanelWidth,
            scaleY = thickness,
            color = color,
            alpha = color.a
        };
        _root.AddChild(line);
    }

    private static void AddVerticalLine(float x, float thickness, Color color)
    {
        FSprite line = new("pixel")
        {
            anchorX = 0.5f,
            anchorY = 0f,
            x = Mathf.Round(x),
            y = 0f,
            scaleX = thickness,
            scaleY = PanelHeight,
            color = color,
            alpha = color.a
        };
        _root.AddChild(line);
    }

    private static void HandleMouse(RainWorldGame game)
    {
        if (_root == null)
        {
            return;
        }

        Vector2 mouse = new(Futile.mousePosition.x, Futile.mousePosition.y);
        Vector2 local = mouse - _panelPosition;

        if (Input.GetMouseButtonDown(0))
        {
            if (Contains(local, PanelWidth - 48f, PanelHeight - TitleHeight, 48f, TitleHeight))
            {
                SetVisible(game, false);
                return;
            }

            if (Contains(local, 0f, PanelHeight - TitleHeight, PanelWidth - 48f, TitleHeight))
            {
                _dragging = true;
                _dragOffset = mouse - _panelPosition;
            }
        }

        if (_dragging && Input.GetMouseButton(0))
        {
            _panelPosition = new Vector2(
                Mathf.Round(mouse.x - _dragOffset.x),
                Mathf.Round(mouse.y - _dragOffset.y));
            ClampPanelToScreen();
            ApplyPanelPosition();
        }

        if (_dragging && Input.GetMouseButtonUp(0))
        {
            _dragging = false;
            SavePosition();
        }
    }

    private static bool Contains(Vector2 point, float x, float y, float width, float height)
    {
        return point.x >= x &&
               point.y >= y &&
               point.x <= x + width &&
               point.y <= y + height;
    }

    private static void UpdatePanelData(RainWorldGame game)
    {
        if (_root == null ||
            _playerValues == null ||
            _roomValues == null ||
            _thermalValues == null)
        {
            return;
        }

        Player player = ResolveDisplayedPlayer(game);
        if (player == null)
        {
            _playerValues.text = "--\n--\n--\n--\n--\n--\n--";
            _roomValues.text = "--\n--\n+0.000\n0.000\n0.000\n0.000\n0.000\n--\n--\n-- / --";
            _thermalValues.text = "0.000\n0.000\n0.000\n0.00000/s\n0.00000/s\n20.0 s\n1.5 s / 0.03520/s";
            return;
        }

        Vector2 chunk0Pos = GetChunkPosition(player, 0);
        Vector2 chunk1Pos = GetChunkPosition(player, 1);
        Vector2 bodyPosition = (chunk0Pos + chunk1Pos) * 0.5f;
        Vector2 velocity = GetAverageVelocity(player);
        float chunk0Submersion = GetChunkSubmersion(player, 0);
        float chunk1Submersion = GetChunkSubmersion(player, 1);
        int playerNumber = player.playerState?.playerNumber ?? 0;
        string slugcat = player.SlugCatClass?.value ?? "--";

        ThirstState thirst = ThirstStore.For(player);
        float maxWater = ThirstStore.GetMaxWaterPips(player);

        _playerValues.text = string.Format(
            CultureInfo.InvariantCulture,
            "P{0}\n{1}\n{2:0.0}, {3:0.0}\n{4:0.0}, {5:0.0}  sub {6:0.00}\n{7:0.0}, {8:0.0}  sub {9:0.00}\n{10:+0.00;-0.00;0.00}, {11:+0.00;-0.00;0.00}\n{12:0.00} / {13:0.00} pips",
            playerNumber,
            slugcat,
            bodyPosition.x,
            bodyPosition.y,
            chunk0Pos.x,
            chunk0Pos.y,
            chunk0Submersion,
            chunk1Pos.x,
            chunk1Pos.y,
            chunk1Submersion,
            velocity.x,
            velocity.y,
            thirst.Water,
            maxWater);

        Room room = player.room;
        float roomHeat = RoomHeatFactor.GetRoomHeat(room);
        float sunlightIntensity = SolarEnvironment.GetSunlightIntensity(room);
        float roomShade = SolarEnvironment.GetRoomShade(room);
        float localShade = SolarEnvironment.GetLocalShade(player);
        float effectiveSunlight = SolarEnvironment.CalculateEffectiveSunlight(
            sunlightIntensity,
            roomShade,
            localShade);

        string regionName = room?.world?.region?.name ?? "--";
        string roomName = room?.abstractRoom?.name ?? "--";
        float gravity = room?.gravity ?? 0f;
        string waterLevel = room?.waterObject != null
            ? room.waterObject.fWaterLevel.ToString("0.0", CultureInfo.InvariantCulture)
            : "--";

        _roomValues.text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}\n{1}\n{2:+0.000;-0.000;0.000}\n{3:0.000}\n{4:0.000}\n{5:0.000}\n{6:0.000}\n{7:0.000}\n{8}\n-- / --",
            regionName,
            roomName,
            roomHeat,
            sunlightIntensity,
            roomShade,
            localShade,
            effectiveSunlight,
            gravity,
            waterLevel);

        PlayerThermalState thermal = PlayerThermalModel.For(player);
        float bodyHeat0 = thermal?.BodyHeat0 ?? 0f;
        float bodyHeat1 = thermal?.BodyHeat1 ?? 0f;
        float difference = bodyHeat0 - bodyHeat1;
        float targetFlow = difference * PlayerThermalModel.InternalConductancePerSecond;
        float internalFlow = thermal?.InternalHeatFlow ?? 0f;

        _thermalValues.text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:+0.000;-0.000;0.000}\n{1:+0.000;-0.000;0.000}\n{2:+0.000;-0.000;0.000}\n{3:+0.00000;-0.00000;0.00000}/s\n{4:+0.00000;-0.00000;0.00000}/s\n{5:0.0} s\n{6:0.0} s / {7:0.00000}/s",
            bodyHeat0,
            bodyHeat1,
            difference,
            targetFlow,
            internalFlow,
            PlayerThermalModel.RoomHeatHalfLifeSeconds,
            PlayerThermalModel.InternalHeatFlowHalfLifeSeconds,
            PlayerThermalModel.InternalConductancePerSecond);
    }

    private static Player ResolveDisplayedPlayer(RainWorldGame game)
    {
        if (game?.cameras != null && game.cameras.Length > 0)
        {
            Player cameraPlayer = game.cameras[0]?.followAbstractCreature?.realizedCreature as Player;
            if (cameraPlayer != null && !cameraPlayer.isNPC)
            {
                return cameraPlayer;
            }
        }

        if (game?.Players == null)
        {
            return null;
        }

        for (int i = 0; i < game.Players.Count; i++)
        {
            Player player = game.Players[i]?.realizedCreature as Player;
            if (player != null && !player.isNPC)
            {
                return player;
            }
        }

        return null;
    }

    private static Vector2 GetChunkPosition(Player player, int index)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return Vector2.zero;
        }

        index = Mathf.Clamp(index, 0, player.bodyChunks.Length - 1);
        return player.bodyChunks[index].pos;
    }

    private static float GetChunkSubmersion(Player player, int index)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return 0f;
        }

        index = Mathf.Clamp(index, 0, player.bodyChunks.Length - 1);
        return player.bodyChunks[index].submersion;
    }

    private static Vector2 GetAverageVelocity(Player player)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return Vector2.zero;
        }

        Vector2 velocity = Vector2.zero;
        int count = Mathf.Min(2, player.bodyChunks.Length);
        for (int i = 0; i < count; i++)
        {
            velocity += player.bodyChunks[i].vel;
        }

        return velocity / Mathf.Max(1, count);
    }

    private static void LoadPosition(RainWorldGame game)
    {
        if (_positionLoaded)
        {
            ClampPanelToScreen();
            return;
        }

        _positionLoaded = true;
        float defaultX = Mathf.Max(EdgePadding, Futile.screen.pixelWidth - PanelWidth - 24f);
        float defaultY = Mathf.Max(EdgePadding, Futile.screen.pixelHeight - PanelHeight - 52f);

        _panelPosition = new Vector2(
            Mathf.Round(PlayerPrefs.GetFloat(PositionXKey, defaultX)),
            Mathf.Round(PlayerPrefs.GetFloat(PositionYKey, defaultY)));

        ClampPanelToScreen();
    }

    private static void ClampPanelToScreen()
    {
        float maxX = Mathf.Max(EdgePadding, Futile.screen.pixelWidth - PanelWidth - EdgePadding);
        float maxY = Mathf.Max(EdgePadding, Futile.screen.pixelHeight - PanelHeight - EdgePadding);

        _panelPosition.x = Mathf.Round(Mathf.Clamp(_panelPosition.x, EdgePadding, maxX));
        _panelPosition.y = Mathf.Round(Mathf.Clamp(_panelPosition.y, EdgePadding, maxY));
    }

    private static void ApplyPanelPosition()
    {
        if (_root == null)
        {
            return;
        }

        _root.x = Mathf.Round(_panelPosition.x);
        _root.y = Mathf.Round(_panelPosition.y);
    }

    private static void SavePosition()
    {
        if (!_positionLoaded)
        {
            return;
        }

        PlayerPrefs.SetFloat(PositionXKey, Mathf.Round(_panelPosition.x));
        PlayerPrefs.SetFloat(PositionYKey, Mathf.Round(_panelPosition.y));
        PlayerPrefs.Save();
    }

    private static void DestroyPanel()
    {
        if (_root != null)
        {
            _root.RemoveFromContainer();
        }

        _root = null;
        _playerValues = null;
        _roomValues = null;
        _thermalValues = null;
    }
}
