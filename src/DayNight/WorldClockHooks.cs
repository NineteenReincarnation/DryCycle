using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Weather;
using RWCustom;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class WorldClockHooks
{
    // Retained diagnostic switch. Production uses the authored RainCycle length and
    // RegionClimate scheduler; set true only when the old five-pip fixed test is
    // deliberately needed again.
    internal static bool TestScheduleEnabled = false;

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

    public static bool TryGetClock(World world, out WorldClock clock)
    {
        clock = null;
        return world?.game != null &&
               RegionDayNightOptions.IsEnabled(world) &&
               _clocks.TryGetValue(world.game, out clock);
    }

    internal static bool TryEnsureClock(RainCycle rainCycle, out WorldClock clock)
    {
        clock = null;
        if (!ShouldRun(rainCycle))
        {
            return false;
        }

        clock = GetOrCreate(rainCycle);
        return clock != null;
    }

    private static bool ShouldRun(RainCycle rainCycle)
    {
        return rainCycle?.world?.game != null &&
               rainCycle.world.game.IsStorySession &&
               RegionDayNightOptions.IsEnabled(rainCycle.world);
    }

    private static int DayCycleLengthFor(RainCycle rainCycle)
    {
        if (TestScheduleEnabled)
        {
            return TestDayCycleLength;
        }

        return Math.Max(1, rainCycle?.cycleLength ?? TestDayCycleLength);
    }

    private static WorldClock GetOrCreate(RainCycle rainCycle)
    {
        RainWorldGame game = rainCycle.world.game;
        int dayCycleLength = DayCycleLengthFor(rainCycle);

        if (!_clocks.TryGetValue(game, out WorldClock clock))
        {
            clock = new WorldClock(dayCycleLength);

            // A clock can first come into existence after the player has already spent
            // time in a region whose DryCycle switch is off. Import that vanilla phase
            // progress instead of silently restarting at Base/0.
            long initialElapsed = InitialElapsedTicks(rainCycle, dayCycleLength);
            if (initialElapsed > 0)
            {
                clock.AlignToDayElapsedTicks(initialElapsed);
            }

            _clocks.Add(game, clock);
        }

        clock.SetCycleLength(dayCycleLength);
        return clock;
    }

    private static long InitialElapsedTicks(RainCycle target, int targetLength)
    {
        if (target?.world?.game == null)
        {
            return 0;
        }

        RainCycle source = target.world.game.world?.rainCycle;
        if (source != null &&
            !ReferenceEquals(source, target) &&
            source.world != null &&
            !RegionDayNightOptions.IsEnabled(source.world))
        {
            float sourceProgress = Mathf.Clamp01(
                source.timer / (float)Math.Max(1, source.cycleLength));
            return (long)Math.Round(sourceProgress * Math.Max(1, targetLength));
        }

        if (target.timer <= 0)
        {
            return 0;
        }

        float progress = Mathf.Clamp01(
            target.timer / (float)Math.Max(1, target.cycleLength));
        return (long)Math.Round(progress * Math.Max(1, targetLength));
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

        self.timer = safeTimer;
        self.deathRainHasHit = false;
        orig(self);

        int advanced = Math.Max(0, self.timer - safeTimer);
        if (advanced > 0 && HasLiveGameplay(self.world.game))
        {
            clock.Advance(advanced);
        }

        self.timer = safeTimer;
        self.deathRainHasHit = false;
        self.dayNightCounter = clock.LegacyDayNightCounter;
    }

    internal static bool HasLiveGameplay(RainWorldGame game)
    {
        if (game == null || game.Players == null || game.Players.Count == 0)
        {
            return false;
        }

        bool realizedPlayer = false;
        for (int i = 0; i < game.Players.Count; i++)
        {
            if (game.Players[i]?.realizedCreature is Player)
            {
                realizedPlayer = true;
                break;
            }
        }

        if (!realizedPlayer || game.cameras == null || game.cameras.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < game.cameras.Length; i++)
        {
            if (game.cameras[i]?.room != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void RainCycle_RainHit(On.RainCycle.orig_RainHit orig, RainCycle self)
    {
        if (ShouldRun(self))
        {
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

        int previousCycleLength = rainCycle.cycleLength;
        rainCycle.cycleLength = DayCycleLengthFor(rainCycle);
        try
        {
            orig(self, hud, fContainer);
            if (TestScheduleEnabled)
            {
                CreateWeatherPipState(self, fContainer);
            }
        }
        finally
        {
            rainCycle.cycleLength = previousCycleLength;
        }
    }

    private static void RainMeter_Update(
        On.HUD.RainMeter.orig_Update orig,
        global::HUD.RainMeter self)
    {
        Player player = self?.hud?.owner as Player;
        RainCycle rainCycle = player?.abstractCreature?.world?.rainCycle;
        if (rainCycle == null ||
            !ShouldRun(rainCycle) ||
            !TryGetClock(rainCycle.world, out WorldClock clock))
        {
            ReleaseCustomHudOverrides(self);
            orig(self);
            return;
        }

        int previousTimer = rainCycle.timer;
        int previousCycleLength = rainCycle.cycleLength;
        int dayCycleLength = DayCycleLengthFor(rainCycle);
        rainCycle.cycleLength = dayCycleLength;
        rainCycle.timer = clock.VirtualRainTimer(dayCycleLength);

        try
        {
            // Production only needs the vanilla RainMeter state machine to observe the
            // WorldClock timer proxy. Final pip layout/shape/weather rendering belongs
            // exclusively to RainMeterRoundPipRuntime. The legacy fixed test retains
            // its original local visualization behind TestScheduleEnabled.
            orig(self);
            if (TestScheduleEnabled)
            {
                ApplyBidirectionalPips(self, clock);
                ApplyWeatherForecastPips(self, clock);
            }
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

    private static void ApplyBidirectionalPips(
        global::HUD.RainMeter meter,
        WorldClock clock)
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
            int order = clock.IsNight ? i : count - 1 - i;
            float boundary = Mathf.Clamp01(scaledProgress - order);
            boundary = boundary * boundary * (3f - 2f * boundary);
            float hollow = clock.IsNight ? 1f - boundary : boundary;
            ApplyPipShape(circle, hollow, sizeFade);

            float index01 = count > 1 ? (float)i / (count - 1) : 0f;
            float angle = (1f - (float)i / count)
                * 360f
                * Custom.SCurve(Mathf.Pow(hudFade, 1.5f - index01), 0.6f);
            circle.pos = meter.pos
                + Custom.DegToVec(angle)
                * (meter.hud.karmaMeter.Radius + 8.5f + hollow + 4f * meter.tickPulse);
        }
    }

    private static void ApplyWeatherForecastPips(
        global::HUD.RainMeter meter,
        WorldClock clock)
    {
        if (!_weatherPips.TryGetValue(meter, out WeatherPipState state) ||
            state.Fills == null ||
            meter.circles == null)
        {
            return;
        }

        HideWeatherPips(state);
        if (!TestScheduleEnabled || clock.IsNight)
        {
            return;
        }

        int count = meter.circles.Length;
        float progress = Mathf.Clamp01(clock.HalfProgress);
        float scaledProgress = progress * count;
        float hudFade = Mathf.Clamp01(meter.fade);
        float sizeFade = hudFade * hudFade;

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

    private static void CreateWeatherPipState(
        global::HUD.RainMeter meter,
        FContainer container)
    {
        if (!TestScheduleEnabled ||
            meter?.circles == null ||
            container == null ||
            _weatherPips.TryGetValue(meter, out _))
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

    private static void ReleaseCustomHudOverrides(global::HUD.RainMeter meter)
    {
        if (meter == null)
        {
            return;
        }

        if (_weatherPips.TryGetValue(meter, out WeatherPipState state))
        {
            HideWeatherPips(state);
        }

        if (meter.circles == null)
        {
            return;
        }

        for (int i = 0; i < meter.circles.Length; i++)
        {
            if (meter.circles[i] != null)
            {
                meter.circles[i].forceColor = null;
            }
        }
    }

    private static void HideWeatherPips(WeatherPipState state)
    {
        if (state?.Fills == null)
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

    private static void ApplyPipShape(
        global::HUD.HUDCircle circle,
        float hollow,
        float sizeFade)
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

        circle.rad = Mathf.Lerp(2f, 3f, hollow) * sizeFade;
        circle.thickness = Mathf.Lerp(3.5f, 1f, hollow) * sizeFade;
    }

    private static int SafeLegacyTimer(RainCycle rainCycle)
    {
        int length = Math.Max(1, rainCycle.cycleLength);
        int latestSafe = Math.Max(0, length - 3000);
        return Math.Min(length / 2, latestSafe);
    }
}
