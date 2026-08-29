using System.Globalization;
using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Draggable developer overlay for player, room/environment and thermal-model data.
///
/// The panel is only available while Rain World's developer tools are active.
/// Press Ctrl + Shift + T to show/hide it. Drag the title bar to reposition it;
/// the last position is persisted with PlayerPrefs.
/// </summary>
internal static class TemperatureDeveloperHud
{
    private const string PositionXKey = "DryCycle.ThermalDebugPanel.X";
    private const string PositionYKey = "DryCycle.ThermalDebugPanel.Y";

    private const float PanelWidth = 430f;
    private const float PanelHeight = 440f;
    private const float TitleHeight = 34f;
    private const float EdgePadding = 8f;
    private const float LabelScale = 0.62f;

    private static readonly Color PanelBackground = new(0f, 0f, 0f, 0.80f);
    private static readonly Color BorderColor = new(0.78f, 0.78f, 0.78f, 0.90f);
    private static readonly Color HeaderColor = new(1f, 1f, 0.58f, 1f);
    private static readonly Color KeyColor = new(0.76f, 0.76f, 0.76f, 1f);
    private static readonly Color ValueColor = Color.white;

    private static bool _enabled;
    private static bool _visible;
    private static bool _dragging;
    private static bool _positionLoaded;
    private static Vector2 _panelPosition;
    private static Vector2 _dragOffset;

    private static FContainer _root;
    private static FLabel _titleLabel;
    private static FLabel _closeLabel;
    private static FLabel _playerHeader;
    private static FLabel _playerKeys;
    private static FLabel _playerValues;
    private static FLabel _roomHeader;
    private static FLabel _roomKeys;
    private static FLabel _roomValues;
    private static FLabel _thermalHeader;
    private static FLabel _thermalKeys;
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

        // The overlay belongs to developer mode. Turning dev tools off immediately
        // hides it, but keeps the user's saved position for the next activation.
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

        AddHorizontalLine(0f, 1.2f, BorderColor);
        AddHorizontalLine(PanelHeight, 1.2f, BorderColor);
        AddVerticalLine(0f, 1.2f, BorderColor);
        AddVerticalLine(PanelWidth, 1.2f, BorderColor);

        // Title separator + section separators.
        AddHorizontalLine(PanelHeight - TitleHeight, 1f, new Color(0.58f, 0.58f, 0.58f, 0.78f));
        AddHorizontalLine(276f, 1f, new Color(0.45f, 0.45f, 0.45f, 0.68f));
        AddHorizontalLine(142f, 1f, new Color(0.45f, 0.45f, 0.45f, 0.68f));

        _titleLabel = CreateLabel(
            "DryCycle Thermal Debug",
            14f,
            PanelHeight - 10f,
            HeaderColor,
            0.70f,
            FLabelAlignment.Left);

        _closeLabel = CreateLabel(
            "×",
            PanelWidth - 17f,
            PanelHeight - 9f,
            Color.white,
            0.78f,
            FLabelAlignment.Center);
        _closeLabel.anchorX = 0.5f;

        _playerHeader = CreateLabel("PLAYER", 14f, 394f, HeaderColor, 0.64f, FLabelAlignment.Left);
        _playerKeys = CreateLabel(string.Empty, 14f, 374f, KeyColor, LabelScale, FLabelAlignment.Left);
        _playerValues = CreateLabel(string.Empty, 147f, 374f, ValueColor, LabelScale, FLabelAlignment.Left);

        _roomHeader = CreateLabel("ROOM / ENVIRONMENT", 14f, 260f, HeaderColor, 0.64f, FLabelAlignment.Left);
        _roomKeys = CreateLabel(string.Empty, 14f, 240f, KeyColor, LabelScale, FLabelAlignment.Left);
        _roomValues = CreateLabel(string.Empty, 147f, 240f, ValueColor, LabelScale, FLabelAlignment.Left);

        _thermalHeader = CreateLabel("THERMAL MODEL", 14f, 126f, HeaderColor, 0.64f, FLabelAlignment.Left);
        _thermalKeys = CreateLabel(string.Empty, 14f, 106f, KeyColor, LabelScale, FLabelAlignment.Left);
        _thermalValues = CreateLabel(string.Empty, 147f, 106f, ValueColor, LabelScale, FLabelAlignment.Left);

        _playerKeys.text =
            "Player\n" +
            "Slugcat\n" +
            "Position\n" +
            "BodyChunk 0\n" +
            "BodyChunk 1\n" +
            "Velocity\n" +
            "Water";

        _roomKeys.text =
            "Region\n" +
            "Room\n" +
            "RoomHeat\n" +
            "Gravity\n" +
            "Water level\n" +
            "Shade / Sun\n" +
            "Humidity / Wind";

        _thermalKeys.text =
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
            x = x,
            y = y,
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
            y = y,
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
            x = x,
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
            if (Contains(local, PanelWidth - 36f, PanelHeight - TitleHeight, 36f, TitleHeight))
            {
                SetVisible(game, false);
                return;
            }

            if (Contains(local, 0f, PanelHeight - TitleHeight, PanelWidth - 36f, TitleHeight))
            {
                _dragging = true;
                _dragOffset = mouse - _panelPosition;
            }
        }

        if (_dragging && Input.GetMouseButton(0))
        {
            _panelPosition = mouse - _dragOffset;
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
            _roomValues.text = "--\n--\n+0.000\n--\n--\n-- / --\n-- / --";
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
        string regionName = room?.world?.region?.name ?? "--";
        string roomName = room?.abstractRoom?.name ?? "--";
        float gravity = room?.gravity ?? 0f;
        string waterLevel = room?.waterObject != null
            ? room.waterObject.fWaterLevel.ToString("0.0", CultureInfo.InvariantCulture)
            : "--";

        // Shade, direct sun, humidity and wind are deliberately placeholders for now.
        // Keeping the rows visible means future environmental branches can be wired
        // into the panel without changing its basic layout.
        _roomValues.text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}\n{1}\n{2:+0.000;-0.000;0.000}\n{3:0.000}\n{4}\n-- / --\n-- / --",
            regionName,
            roomName,
            roomHeat,
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
            PlayerPrefs.GetFloat(PositionXKey, defaultX),
            PlayerPrefs.GetFloat(PositionYKey, defaultY));

        ClampPanelToScreen();
    }

    private static void ClampPanelToScreen()
    {
        float maxX = Mathf.Max(EdgePadding, Futile.screen.pixelWidth - PanelWidth - EdgePadding);
        float maxY = Mathf.Max(EdgePadding, Futile.screen.pixelHeight - PanelHeight - EdgePadding);

        _panelPosition.x = Mathf.Clamp(_panelPosition.x, EdgePadding, maxX);
        _panelPosition.y = Mathf.Clamp(_panelPosition.y, EdgePadding, maxY);
    }

    private static void ApplyPanelPosition()
    {
        if (_root == null)
        {
            return;
        }

        _root.x = _panelPosition.x;
        _root.y = _panelPosition.y;
    }

    private static void SavePosition()
    {
        if (!_positionLoaded)
        {
            return;
        }

        PlayerPrefs.SetFloat(PositionXKey, _panelPosition.x);
        PlayerPrefs.SetFloat(PositionYKey, _panelPosition.y);
        PlayerPrefs.Save();
    }

    private static void DestroyPanel()
    {
        if (_root != null)
        {
            _root.RemoveFromContainer();
        }

        _root = null;
        _titleLabel = null;
        _closeLabel = null;
        _playerHeader = null;
        _playerKeys = null;
        _playerValues = null;
        _roomHeader = null;
        _roomKeys = null;
        _roomValues = null;
        _thermalHeader = null;
        _thermalKeys = null;
        _thermalValues = null;
    }
}
