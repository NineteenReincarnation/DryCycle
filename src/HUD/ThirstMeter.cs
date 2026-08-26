using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

internal sealed class ThirstMeter : global::HUD.HudPart
{
    private sealed class MeterLink
    {
        public MeterLink()
        {
        }

        public ThirstMeter Meter;
    }

    private static readonly ConditionalWeakTable<global::HUD.HUD, ThirstMeter> HudMeters = new();
    private static readonly ConditionalWeakTable<Player, MeterLink> PlayerMeters = new();

    private static readonly Color WaterColor = new(0.25f, 0.95f, 1f);

    private readonly Player _player;
    private readonly SaveState _saveState;
    private readonly SleepAndDeathScreen _sleepScreen;
    private readonly global::HUD.HUDCircle[] _outer = new global::HUD.HUDCircle[ThirstConstants.MaxPips];
    private readonly global::HUD.HUDCircle[] _inner = new global::HUD.HUDCircle[ThirstConstants.MaxPips];
    private readonly FSprite _separator;

    private float _displayWater;
    private float _sleepTargetWater;
    private int _sleepConsumePips;
    private int _sleepConsumeDelay;
    private float _fade;
    private float _lastWater;
    private int _visibleCounter;
    private int _rejectCounter;

    private ThirstMeter(
        global::HUD.HUD hud,
        Player player,
        SaveState saveState,
        SleepAndDeathScreen sleepScreen,
        bool animateHibernateCost)
        : base(hud)
    {
        _player = player;
        _saveState = saveState;
        _sleepScreen = sleepScreen;

        if (_player != null)
        {
            _displayWater = ThirstStore.For(_player).Water;
        }
        else
        {
            _sleepTargetWater = ThirstStore.GetSaved(_saveState);
            _displayWater = _sleepTargetWater;

            if (animateHibernateCost)
            {
                _displayWater = Mathf.Min(
                    ThirstConstants.MaxWater,
                    _sleepTargetWater + ThirstConstants.HibernateCost);
                _sleepConsumePips = (int)ThirstConstants.HibernateCost;
                _sleepConsumeDelay = 65;
            }
        }

        _lastWater = _displayWater;
        _visibleCounter = 200;

        for (int i = 0; i < _outer.Length; i++)
        {
            _outer[i] = new global::HUD.HUDCircle(
                hud,
                global::HUD.HUDCircle.SnapToGraphic.FoodCircleA,
                hud.fContainers[1],
                0)
            {
                forceColor = WaterColor
            };

            _inner[i] = new global::HUD.HUDCircle(
                hud,
                global::HUD.HUDCircle.SnapToGraphic.FoodCircleB,
                hud.fContainers[1],
                0)
            {
                forceColor = WaterColor
            };
        }

        _separator = new FSprite("pixel")
        {
            color = WaterColor,
            anchorX = 0.5f,
            anchorY = 0.5f,
            scaleX = 1.5f,
            scaleY = 34f,
            alpha = 0f
        };
        hud.fContainers[1].AddChild(_separator);
    }

    public static void Attach(global::HUD.HUD hud, Player player)
    {
        if (hud == null || player == null || HudMeters.TryGetValue(hud, out _))
        {
            return;
        }

        ThirstMeter meter = new(hud, player, null, null, false);
        HudMeters.Add(hud, meter);
        PlayerMeters.GetOrCreateValue(player).Meter = meter;
        hud.AddPart(meter);
    }

    public static void Attach(
        global::HUD.HUD hud,
        SaveState saveState,
        SleepAndDeathScreen sleepScreen,
        bool animateHibernateCost)
    {
        if (hud == null || saveState == null || HudMeters.TryGetValue(hud, out _))
        {
            return;
        }

        ThirstMeter meter = new(hud, null, saveState, sleepScreen, animateHibernateCost);
        HudMeters.Add(hud, meter);
        hud.AddPart(meter);
    }

    public static void TryReject(Player player)
    {
        if (player != null &&
            PlayerMeters.TryGetValue(player, out MeterLink link) &&
            link.Meter != null)
        {
            link.Meter._rejectCounter = 55;
            link.Meter._visibleCounter = 200;
        }
    }

    public override void Update()
    {
        base.Update();

        bool isDrinking = false;

        if (_player != null)
        {
            ThirstState state = ThirstStore.For(_player);
            _displayWater = state.Water;
            isDrinking = state.IsDrinking;
        }
        else if (_sleepConsumePips > 0 &&
                 (_sleepScreen == null || _sleepScreen.AllowFoodMeterTick))
        {
            _sleepConsumeDelay--;
            if (_sleepConsumeDelay <= 0)
            {
                _displayWater = Mathf.Max(_sleepTargetWater, _displayWater - 1f);
                _sleepConsumePips--;
                _sleepConsumeDelay = 40;
                hud.PlaySound(SoundID.HUD_Food_Meter_Deplete_Plop_A);
            }
        }
        else if (_player == null && _sleepConsumePips <= 0)
        {
            _displayWater = _sleepTargetWater;
        }

        float water = _displayWater;

        if (Mathf.Abs(water - _lastWater) > 0.0001f)
        {
            _visibleCounter = 200;
            _lastWater = water;
        }

        if (_visibleCounter > 0)
        {
            _visibleCounter--;
        }

        if (_rejectCounter > 0)
        {
            _rejectCounter--;
        }

        float foodFade = hud.foodMeter?.fade ?? 0f;
        bool inShelter = _player?.room?.abstractRoom != null && _player.room.abstractRoom.shelter;
        bool sleepScreenMeter = _player == null && _saveState != null;
        float targetFade = sleepScreenMeter || inShelter || _visibleCounter > 0 || isDrinking ? 1f : foodFade;
        _fade = Mathf.Lerp(_fade, Mathf.Max(foodFade, targetFade), 0.2f);

        Vector2 origin = hud.foodMeter != null
            ? hud.foodMeter.DrawPos(1f) + new Vector2(0f, 30f)
            : new Vector2(50f, 55f);

        Color color = WaterColor;
        if (_rejectCounter > 0 && (_rejectCounter / 5) % 2 == 0)
        {
            color = Color.red;
        }

        for (int i = 0; i < _outer.Length; i++)
        {
            float x = origin.x + i * 30f + (i >= ThirstConstants.DividerAfterPip ? 15f : 0f);
            Vector2 pos = new(x, origin.y);
            float fill = Mathf.Clamp01(water - i);

            _outer[i].Update();
            _inner[i].Update();

            _outer[i].pos = pos;
            _inner[i].pos = pos;
            _outer[i].rad = _outer[i].snapRad;
            _outer[i].thickness = _outer[i].snapThickness;
            _inner[i].rad = _inner[i].snapRad;
            _inner[i].thickness = _inner[i].snapThickness;
            _outer[i].fade = _fade;
            _inner[i].fade = _fade * fill;
            _outer[i].forceColor = color;
            _inner[i].forceColor = color;
        }

        // Five pips are shown as 3 | 2. A successful normal hibernation
        // removes the two pips on the right side of this divider.
        _separator.x = origin.x + ThirstConstants.DividerAfterPip * 30f - 7.5f;
        _separator.y = origin.y;
        _separator.color = color;
        _separator.alpha = _fade;
    }

    public override void Draw(float timeStacker)
    {
        for (int i = 0; i < _outer.Length; i++)
        {
            _outer[i].Draw(timeStacker);
            _inner[i].Draw(timeStacker);
        }
    }

    public override void ClearSprites()
    {
        for (int i = 0; i < _outer.Length; i++)
        {
            _outer[i].ClearSprite();
            _inner[i].ClearSprite();
        }

        _separator.RemoveFromContainer();

        if (_player != null &&
            PlayerMeters.TryGetValue(_player, out MeterLink link) &&
            ReferenceEquals(link.Meter, this))
        {
            link.Meter = null;
        }

        base.ClearSprites();
    }
}
