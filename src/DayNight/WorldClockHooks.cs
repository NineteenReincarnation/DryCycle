using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class WorldClockHooks
{
    private static ConditionalWeakTable<RainWorldGame, WorldClock> _clocks = new();
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
        On.HUD.RainMeter.Update += RainMeter_Update;
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
        On.HUD.RainMeter.Update -= RainMeter_Update;
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
            _ => new WorldClock(Math.Max(1, rainCycle.cycleLength)));
        clock.SetCycleLength(rainCycle.cycleLength);
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
            // Weather/hazards will be reintroduced later by the dedicated systems.
            return;
        }

        orig(self);
    }

    private static void RainMeter_Update(On.HUD.RainMeter.orig_Update orig, global::HUD.RainMeter self)
    {
        Player player = self?.hud?.owner as Player;
        RainCycle rainCycle = player?.abstractCreature?.world?.rainCycle;
        if (rainCycle == null || !ShouldRun(rainCycle) || !TryGetClock(rainCycle.world.game, out WorldClock clock))
        {
            orig(self);
            return;
        }

        // Let vanilla update visibility, placement, half-time feedback and its normal
        // animation state using a virtual timer. Immediately afterwards, replace only
        // the pip shape/state: elapsed daytime pips remain as hollow circles instead
        // of shrinking away, and the night phase fills them back in in reverse order.
        int previousTimer = rainCycle.timer;
        rainCycle.timer = clock.VirtualRainTimer(rainCycle.cycleLength);
        try
        {
            orig(self);
            ApplyBidirectionalPips(self, clock);
        }
        finally
        {
            rainCycle.timer = previousTimer;
        }
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

            // Day follows vanilla's depletion direction. Night deliberately walks the
            // opposite way around the Karma ring so the meter visibly reverses.
            int order = clock.IsNight ? i : count - 1 - i;
            float boundary = Mathf.Clamp01(scaledProgress - order);
            boundary = boundary * boundary * (3f - 2f * boundary);

            // hollow=0 -> solid pip, hollow=1 -> empty ring.
            float hollow = clock.IsNight ? 1f - boundary : boundary;
            ApplyPipShape(circle, hollow, sizeFade);

            // Rebuild the vanilla radial placement using our own hollow factor. This
            // keeps hollow pips on the ring after vanilla would normally shrink them
            // to zero and also retains the stock reveal/tick-pulse motion.
            float index01 = count > 1 ? (float)i / (count - 1) : 0f;
            float angle = (1f - (float)i / count)
                * 360f
                * Custom.SCurve(Mathf.Pow(hudFade, 1.5f - index01), 0.6f);
            circle.pos = meter.pos
                + Custom.DegToVec(angle)
                * (meter.hud.karmaMeter.Radius + 8.5f + hollow + 4f * meter.tickPulse);
        }
    }

    private static void ApplyPipShape(global::HUD.HUDCircle circle, float hollow, float sizeFade)
    {
        hollow = Mathf.Clamp01(hollow);

        // These are the same two visual states used by vanilla RainMeter:
        // Circle4 is its filled dot, smallEmptyCircle is its empty ring. During the
        // boundary transition we use the vector-circle shader path to morph smoothly
        // between them rather than popping from one atlas graphic to the other.
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
