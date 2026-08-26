using System.Runtime.CompilerServices;
using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.HUD;

internal sealed class ThirstMeter : global::HUD.HudPart
{
    private sealed class MeterLink
    {
        public ThirstMeter Meter;
    }

    private static readonly ConditionalWeakTable<global::HUD.HUD, ThirstMeter> HudMeters = new();
    private static readonly ConditionalWeakTable<Player, MeterLink> PlayerMeters = new();

    private static readonly Color WaterColor = new(0.25f, 0.95f, 1f);

    private readonly Player _player;
    private readonly global::HUD.HUDCircle[] _outer = new global::HUD.HUDCircle[4];
    private readonly global::HUD.HUDCircle[] _inner = new global::HUD.HUDCircle[4];

    private float _fade;
    private float _lastWater;
    private int _visibleCounter;
    private int _rejectCounter;

    private ThirstMeter(global::HUD.HUD hud, Player player)
        : base(hud)
    {
        _player = player;
        _lastWater = ThirstStore.For(player).Water;
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
    }

    public static void Attach(global::HUD.HUD hud, Player player)
    {
        if (hud == null || player == null || HudMeters.TryGetValue(hud, out _))
        {
            return;
        }

        ThirstMeter meter = new(hud, player);
        HudMeters.Add(hud, meter);
        PlayerMeters.GetOrCreateValue(player).Meter = meter;
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

        ThirstState state = ThirstStore.For(_player);
        float water = state.Water;

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
        bool inShelter = _player.room?.abstractRoom != null && _player.room.abstractRoom.shelter;
        float targetFade = inShelter || _visibleCounter > 0 || state.IsDrinking ? 1f : foodFade;
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
            // Match the FoodMeter language: a gap after the required two pips.
            float x = origin.x + i * 30f + (i >= (int)ThirstConstants.HibernateRequirement ? 15f : 0f);
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

        if (PlayerMeters.TryGetValue(_player, out MeterLink link) && ReferenceEquals(link.Meter, this))
        {
            link.Meter = null;
        }

        base.ClearSprites();
    }
}
