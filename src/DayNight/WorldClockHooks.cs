using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Weather;
using RWCustom;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class WorldClockHooks
{
    // Temporary accelerated test schedule. Rain World runs gameplay at 40 ticks/sec
    // and vanilla RainMeter uses one pip per 1200 ticks (30 sec). A 6000-tick day is
    // therefore 2.5 minutes and gives exactly five daytime pips. Night remains 50%
    // of daytime, so the current test night is 75 seconds.
    internal const int TestDayCycleLength = 40 * 150;

    private sealed class WeatherPipState
    {
        internal global::HUD.RainMeter Meter;
        internal FSprite[] Fills;
    }

    private static ConditionalWeakTable<RainWorldGame, WorldClock> _clocks = new();
    private static ConditionalWeakTable<global::HUD.RainMeter, WeatherPipState> _weatherPips = new();
    private static readonly List<WeatherPipState> LiveWeatherPips = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RainCycle.ctor += RainCycle_ctor;
        On.RainCycle.Update += RainCycle_Update;
        On.RainCycle.RainHit += RainCycle_RainHit;
        On.HUD.RainMeter.ctor += RainMeter_ctor;
        On.HUD.RainMeter.Update += RainMeter_Update;
        On.HUD.RainMeter.ClearSprites += RainMeter_ClearSprites;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainCycle.ctor -= RainCycle_ctor;
        On.RainCycle.Update -= RainCycle_Update;
        On.RainCycle.RainHit -= RainCycle_RainHit;
        On.HUD.RainMeter.ctor -= RainMeter_ctor;
        On.HUD.RainMeter.Update -= RainMeter_Update;
        On.HUD.RainMeter.ClearSprites -= RainMeter_ClearSprites;

        for (int i = LiveWeatherPips.Count - 1; i >= 0; i--)
        {
            ClearWeatherPipState(LiveWeatherPips[i]);
        }
        LiveWeatherPips.Clear();

        _weatherPips = new ConditionalWeakTable<global::HUD.RainMeter, WeatherPipState>();
        _clocks = new ConditionalWeakTable<RainWorldGame, WorldClock>();
        _enabled = false;
    }

    public static bool TryGetClock(RainWorldGame game, out WorldClock clock)
    {
        clock = null;
        return game != null && _clocks.TryGetValue(game, out clock);
    }

    private static bool ShouldRun(RainCycle rainCycle)
    {
        return rainCycle?.world?.game != null && rainCycle.world.game.IsStorySession;
    }

    private static WorldClock GetOrCreate(RainCycle rainCycle)
    {
        RainWorldGame game = rainCycle.world.game;
        WorldClock clock = _clocks.GetValue(
            game,
            _ => new WorldClock(TestDayCycleLength));

        // Keep the accelerated test duration stable even though vanilla RainCycle
        // retains its authored cycleLength as a compatibility facade.
        clock.SetCycleLength(TestDayCycleLength);
        return clock;
    }

    private static void RainCycle_ctor(
        On.RainCycle.orig_ctor orig,
        RainCycle self,
        World world,
        float minutes)
    {
        orig(self, world, minutes);

        if (!ShouldRun(self))
        {
            return;
        }

        WorldClock clock = GetOrCreate(self);
        self.dayNightCounter = clock.LegacyDayNightCounter;
        self.deathRainHasHit = false;
        self.timer = SafeLegacyTimer(self);
    }

    private static void RainCycle_Update(On.RainCycle.orig_Update orig, RainCycle self)
    {
        if (!ShouldRun(self))
        {
            orig(self);
            return;
        }

        WorldClock clock = GetOrCreate(self);
        int safeTimer = SafeLegacyTimer(self);

        // Vanilla RainCycle remains alive as a compatibility facade, but it no
        // longer carries world time. Keeping it away from TimeUntilRain == 0 also
        // prevents vanilla rain approach shake/darkening/AI panic from leaking into
        // the new day/night clock.
        self.timer = safeTimer;
        self.deathRainHasHit = false;

        orig(self);

        int advanced = Math.Max(0, self.timer - safeTimer);
        clock.Advance(advanced);

        self.timer = safeTimer;
        self.deathRainHasHit = false;
        self.dayNightCounter = clock.LegacyDayNightCounter;
    }

    private static void RainCycle_RainHit(On.RainCycle.orig_RainHit orig, RainCycle self)
    {
        if (ShouldRun(self))
        {
            // End-of-cycle death rain belongs to the old one-shot cycle model.
            // Weather/hazards are injected separately by DryCycle's weather layer.
            return;
        }

        orig(self);
    }

    private static void RainMeter_ctor(
        On.HUD.RainMeter.orig_ctor orig,
        global::HUD.RainMeter self,
        global::HUD.HUD hud,
        FContainer fContainer)
    {
        Player player = hud?.owner as Player;
        RainCycle rainCycle = player?.abstractCreature?.world?.rainCycle;
        if (rainCycle == null || !ShouldRun(rainCycle))
        {
            orig(self, hud, fContainer);
            return;
        }

        // Vanilla chooses the number of RainMeter pips in its constructor from
        // cycleLength / 1200. Expose the 2.5-minute virtual daytime only for that
        // construction step, then restore the real RainCycle value immediately.
        int previousCycleLength = rainCycle.cycleLength;
        rainCycle.cycleLength = TestDayCycleLength;
        try
        {
            orig(self, hud, fContainer);
            CreateWeatherPipState(self, fContainer);
        }
        finally
        {
            rainCycle.cycleLength = previousCycleLength;
        }
    }

    private static void RainMeter_Update(On.HUD.RainMeter.orig_Update orig, global::HUD.RainMeter self)
    {
        Player player = self?.hud?.owner as Player;
        RainCycle rainCycle = player?.abstractCreature?.world?.rainCycle;
        if (rainCycle == null ||
            !ShouldRun(rainCycle) ||
            !TryGetClock(rainCycle.world.game, out WorldClock clock))
        {
            orig(self);
            return;
        }

        // During HUD update expose a coherent five-pip virtual RainCycle so AmountLeft,
        // fRain and the test meter all describe the same WorldClock daytime. The rest
        // of the game continues seeing the safe compatibility RainCycle.
        int previousTimer = rainCycle.timer;
        int previousCycleLength = rainCycle.cycleLength;
        rainCycle.cycleLength = TestDayCycleLength;
        rainCycle.timer = clock.VirtualRainTimer(TestDayCycleLength);
        try
        {
            orig(self);
            ApplyBidirectionalPips(self, clock);
            ApplyWeatherForecastPips(self, clock);
        }
        finally
        {
            rainCycle.timer = previousTimer;
            rainCycle.cycleLength = previousCycleLength;
        }
    }

    private static void RainMeter_ClearSprites(
        On.HUD.RainMeter.orig_ClearSprites orig,
        global::HUD.RainMeter self)
    {
        if (self != null && _weatherPips.TryGetValue(self, out WeatherPipState state))
        {
            ClearWeatherPipState(state);
            LiveWeatherPips.Remove(state);
            _weatherPips.Remove(self);
        }

        orig(self);
    }

    private static void ApplyBidirectionalPips(global::HUD.RainMeter meter, WorldClock clock)
    {
        global::HUD.HUDCircle[] circles = meter.circles;
        if (circles == null || circles.Length == 0)
        {
            return;
        }

        int count = circles.Length;
        float progress = Mathf.Clamp01(clock.HalfProgress);
        float scaledProgress = progress * count;
        float hudFade = Mathf.Clamp01(meter.fade);
        float sizeFade = hudFade * hudFade;

        for (int i = 0; i < count; i++)
        {
            global::HUD.HUDCircle circle = circles[i];
            if (circle == null)
            {
                continue;
            }

            circle.forceColor = null;

            // Day depletes clockwise into hollow rings. Night deliberately traverses
            // the opposite direction and fills the hollow rings back into solid pips.
            int order = clock.IsNight ? i : count - 1 - i;
            float boundary = Mathf.Clamp01(scaledProgress - order);
            boundary = boundary * boundary * (3f - 2f * boundary);

            // hollow=0 -> solid pip, hollow=1 -> empty ring.
            float hollow = clock.IsNight ? 1f - boundary : boundary;
            ApplyPipShape(circle, hollow, sizeFade);

            // Rebuild vanilla radial placement using our hollow factor. This keeps
            // elapsed pips on the ring instead of allowing vanilla to shrink them out.
            float index01 = count > 1 ? (float)i / (count - 1) : 0f;
            float angle = (1f - (float)i / count)
                * 360f
                * Custom.SCurve(Mathf.Pow(hudFade, 1.5f - index01), 0.6f);
            circle.pos = meter.pos
                + Custom.DegToVec(angle)
                * (meter.hud.karmaMeter.Radius + 8.5f + hollow + 4f * meter.tickPulse);
        }
    }

    private static void ApplyWeatherForecastPips(global::HUD.RainMeter meter, WorldClock clock)
    {
        if (!_weatherPips.TryGetValue(meter, out WeatherPipState state) ||
            state.Fills == null ||
            meter.circles == null)
        {
            return;
        }

        for (int i = 0; i < state.Fills.Length; i++)
        {
            if (state.Fills[i] != null)
            {
                state.Fills[i].isVisible = false;
            }
        }

        if (clock.IsNight)
        {
            return;
        }

        int count = meter.circles.Length;
        float progress = Mathf.Clamp01(clock.HalfProgress);
        float scaledProgress = progress * count;
        float hudFade = Mathf.Clamp01(meter.fade);
        float sizeFade = hudFade * hudFade;

        // Chronological pip 1 is the first 30 seconds of daytime and corresponds to
        // the last RainMeter array element because vanilla lays the pips around the
        // karma ring in reverse timer order.
        for (int chronologicalPip = 1; chronologicalPip <= count; chronologicalPip++)
        {
            if (!SandstormWeatherRuntime.TryGetForecastColor(chronologicalPip, out Color fillColor))
            {
                continue;
            }

            int index = count - chronologicalPip;
            if (index < 0 || index >= count)
            {
                continue;
            }

            global::HUD.HUDCircle circle = meter.circles[index];
            FSprite fill = state.Fills[index];
            if (circle == null || fill == null)
            {
                continue;
            }

            int order = count - 1 - index;
            float hollow = Mathf.Clamp01(scaledProgress - order);
            hollow = hollow * hollow * (3f - 2f * hollow);
            float solid = 1f - hollow;

            // Forecast pips are drawn as a colored solid center with an independent
            // white outline. Once their daytime interval has elapsed only the white
            // hollow ring remains, matching the normal day depletion language.
            circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle;
            circle.snapRad = 3f;
            circle.snapThickness = 1f;
            circle.rad = 3f * sizeFade;
            circle.thickness = 1f * sizeFade;
            circle.forceColor = Color.white;

            fill.SetPosition(circle.pos);
            fill.scale = sizeFade;
            fill.color = fillColor;
            fill.alpha = solid * hudFade;
            fill.isVisible = fill.alpha > 0.002f;
            fill.MoveBehindOtherNode(circle.sprite);
        }
    }

    private static void CreateWeatherPipState(global::HUD.RainMeter meter, FContainer container)
    {
        if (meter?.circles == null || container == null || _weatherPips.TryGetValue(meter, out _))
        {
            return;
        }

        WeatherPipState state = new()
        {
            Meter = meter,
            Fills = new FSprite[meter.circles.Length]
        };

        for (int chronologicalPip = 1; chronologicalPip <= meter.circles.Length; chronologicalPip++)
        {
            if (!SandstormWeatherRuntime.TryGetForecastColor(chronologicalPip, out _))
            {
                continue;
            }

            int index = meter.circles.Length - chronologicalPip;
            if (index < 0 || index >= state.Fills.Length)
            {
                continue;
            }

            FSprite fill = new("Circle4")
            {
                isVisible = false,
                shader = meter.hud.rainWorld.Shaders["Basic"]
            };
            state.Fills[index] = fill;
            container.AddChild(fill);
            if (meter.circles[index]?.sprite != null)
            {
                fill.MoveBehindOtherNode(meter.circles[index].sprite);
            }
        }

        _weatherPips.Add(meter, state);
        LiveWeatherPips.Add(state);
    }

    private static void ClearWeatherPipState(WeatherPipState state)
    {
        if (state?.Fills == null)
        {
            return;
        }

        for (int i = 0; i < state.Fills.Length; i++)
        {
            state.Fills[i]?.RemoveFromContainer();
            state.Fills[i] = null;
        }
    }

    private static void ApplyPipShape(global::HUD.HUDCircle circle, float hollow, float sizeFade)
    {
        hollow = Mathf.Clamp01(hollow);

        if (hollow <= 0.001f)
        {
            circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.Circle4;
            circle.snapRad = 2f;
            circle.snapThickness = -1f;
            circle.rad = 2f * sizeFade;
            circle.thickness = -1f;
            return;
        }

        circle.snapGraphic = global::HUD.HUDCircle.SnapToGraphic.smallEmptyCircle;
        circle.snapRad = 3f;
        circle.snapThickness = 1f;

        if (hollow >= 0.999f)
        {
            circle.rad = 3f * sizeFade;
            circle.thickness = 1f * sizeFade;
            return;
        }

        float targetRad = Mathf.Lerp(2f, 3f, hollow);
        float targetThickness = Mathf.Lerp(3.5f, 1f, hollow);
        circle.rad = targetRad * sizeFade;
        circle.thickness = targetThickness * sizeFade;
    }

    private static int SafeLegacyTimer(RainCycle rainCycle)
    {
        int length = Math.Max(1, rainCycle.cycleLength);

        // Keep at least 3000 ticks between the compatibility timer and vanilla rain
        // onset. For very short custom cycles, use the midpoint instead.
        int latestSafe = Math.Max(0, length - 3000);
        return Math.Min(length / 2, latestSafe);
    }
}
