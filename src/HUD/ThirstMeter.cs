using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using Menu;
using UnityEngine;

namespace DryCycle.HUD;

internal sealed class ThirstMeter : global::HUD.HudPart
{
    private enum MeterMode
    {
        Gameplay,
        SleepScreen,
        CharacterSelect
    }

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

    private readonly MeterMode _mode;
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
        MeterMode mode,
        Player player,
        SaveState saveState,
        SleepAndDeathScreen sleepScreen,
        float fixedWater,
        bool animateHibernateCost)
        : base(hud)
    {
        _mode = mode;
        _player = player;
        _saveState = saveState;
        _sleepScreen = sleepScreen;

        switch (_mode)
        {
            case MeterMode.Gameplay:
                _displayWater = ThirstStore.For(_player).Water;
                break;

            case MeterMode.SleepScreen:
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
                break;

            case MeterMode.CharacterSelect:
                _displayWater = Mathf.Clamp(fixedWater, 0f, ThirstConstants.MaxWater);
                break;
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

        ThirstMeter meter = new(
            hud,
            MeterMode.Gameplay,
            player,
            null,
            null,
            0f,
            false);

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

        ThirstMeter meter = new(
            hud,
            MeterMode.SleepScreen,
            null,
            saveState,
            sleepScreen,
            0f,
            animateHibernateCost);

        HudMeters.Add(hud, meter);
        hud.AddPart(meter);
    }

    public static void AttachCharacterSelect(global::HUD.HUD hud, float water)
    {
        if (hud == null || HudMeters.TryGetValue(hud, out _))
        {
            return;
        }

        ThirstMeter meter = new(
            hud,
            MeterMode.CharacterSelect,
            null,
            null,
            null,
            water,
            false);

        HudMeters.Add(hud, meter);
        hud.AddPart(meter);
    }

    public static void SyncCharacterSelect(global::HUD.HUD hud)
    {
        if (hud == null ||
            !HudMeters.TryGetValue(hud, out ThirstMeter meter) ||
            meter._mode != MeterMode.CharacterSelect)
        {
            return;
        }

        // SlugcatPageContinue updates the vanilla food position/fade after
        // HUD.Update. Re-sync here from the page's Update hook so hydration has
        // no one-frame lag while the character pages scroll.
        meter._fade = hud.foodMeter?.fade ?? 0f;
        meter.RefreshLayout();
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

        if (_mode == MeterMode.Gameplay)
        {
            ThirstState state = ThirstStore.For(_player);
            _displayWater = state.Water;
            isDrinking = state.IsDrinking;
        }
        else if (_mode == MeterMode.SleepScreen)
        {
            UpdateSleepConsumption();
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

        if (_mode == MeterMode.SleepScreen || _mode == MeterMode.CharacterSelect)
        {
            // Menu meters follow the vanilla food meter exactly. In particular,
            // the sleep screen intentionally dims its meter during the animation.
            _fade = foodFade;
        }
        else
        {
            bool inShelter = _player?.room?.abstractRoom != null && _player.room.abstractRoom.shelter;
            float targetFade = inShelter || _visibleCounter > 0 || isDrinking ? 1f : foodFade;
            _fade = Mathf.Lerp(_fade, Mathf.Max(foodFade, targetFade), 0.2f);
        }

        for (int i = 0; i < _outer.Length; i++)
        {
            _outer[i].Update();
            _inner[i].Update();
        }

        RefreshLayout();
    }

    private void UpdateSleepConsumption()
    {
        if (_sleepConsumePips > 0 &&
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
        else if (_sleepConsumePips <= 0)
        {
            _displayWater = _sleepTargetWater;
        }
    }

    private void RefreshLayout()
    {
        Vector2 origin = GetOrigin();
        Color color = WaterColor;

        if (_rejectCounter > 0 && (_rejectCounter / 5) % 2 == 0)
        {
            color = Color.red;
        }

        for (int i = 0; i < _outer.Length; i++)
        {
            float x = origin.x + i * 30f + (i >= ThirstConstants.DividerAfterPip ? 15f : 0f);
            Vector2 pos = new(x, origin.y);
            float fill = Mathf.Clamp01(_displayWater - i);

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

        // The divider matches a vanilla food meter survival limit: the three
        // pips to its left are the normal hibernation requirement/cost, while
        // the two pips to its right are extra hydration capacity.
        _separator.x = origin.x + ThirstConstants.DividerAfterPip * 30f - 7.5f;
        _separator.y = origin.y;
        _separator.color = color;
        _separator.alpha = _fade;
    }

    private Vector2 GetOrigin()
    {
        if (hud.foodMeter == null)
        {
            return new Vector2(50f, 55f);
        }

        Vector2 foodOrigin = hud.foodMeter.DrawPos(1f);

        if (_mode == MeterMode.CharacterSelect)
        {
            // Character-select food sits immediately to the right of the karma
            // symbol. Put hydration after the entire vanilla food meter, keeping
            // both resources on the same row.
            return foodOrigin + new Vector2(hud.foodMeter.TotalWidth(1f) + 40f, 0f);
        }

        return foodOrigin + new Vector2(0f, 30f);
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
