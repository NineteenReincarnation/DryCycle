using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class WorldClockHooks
{
    // Temporary accelerated test schedule. Rain World runs gameplay at 40 ticks/sec,
    // and vanilla RainMeter uses one pip per 1200 ticks (30 sec). Therefore a
    // 2400-tick daytime is exactly one minute and produces exactly two HUD pips.
    // Night remains 50% of daytime in WorldClock, so it lasts 30 seconds.
    private const int TestDayCycleLength = 40 * 60;

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
        On.HUD.RainMeter.ctor += RainMeter_ctor;
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
        On.HUD.RainMeter.ctor -= RainMeter_ctor;
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
            // Weather/hazards will be reintroduced later by the dedicated systems.
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
        // cycleLength / 1200. Expose the one-minute virtual daytime only for that
        // construction step, then restore the real RainCycle value immediately.
        int previousCycleLength = rainCycle.cycleLength;
        rainCycle.cycleLength = TestDayCycleLength;
        try
        {
            orig(self, hud, fContainer);
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
        if (rainCycle == null || !ShouldRun(rainCycle) || !TryGetClock(rainCycle.world.game, out WorldClock clock))
        {
            orig(self);
            return;
        }

        // During HUD update expose a coherent one-minute virtual RainCycle so
        // AmountLeft, fRain and the two test pips all describe the same clock. The
        // rest of the game continues seeing the safe compatibility RainCycle.
        int previousTimer = rainCycle.timer;
        int previousCycleLength = rainCycle.cycleLength;
        rainCycle.cycleLength = TestDayCycleLength;
        rainCycle.timer = clock.VirtualRainTimer(TestDayCycleLength);
        try
        {
            orig(self);
            ApplyBidirectionalPips(self, clock);
        }
        finally
        {
            rainCycle.timer = previousTimer;
            rainCycle.cycleLength = previousCycleLength;
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
