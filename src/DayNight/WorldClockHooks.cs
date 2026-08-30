using System;
using System.Runtime.CompilerServices;

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

    private static void RainMeter_Update(On.HUD.RainMeter.orig_Update orig, HUD.RainMeter self)
    {
        Player player = self?.hud?.owner as Player;
        RainCycle rainCycle = player?.abstractCreature?.world?.rainCycle;
        if (rainCycle == null || !ShouldRun(rainCycle) || !TryGetClock(rainCycle.world.game, out WorldClock clock))
        {
            orig(self);
            return;
        }

        // Both halves use the full vanilla RainMeter range. Day consumes it over the
        // full original cycleLength; night consumes the same visual range in half the
        // real time. This keeps the HUD shape unchanged while making night 50% as long.
        int previousTimer = rainCycle.timer;
        rainCycle.timer = clock.VirtualRainTimer(rainCycle.cycleLength);
        try
        {
            orig(self);
        }
        finally
        {
            rainCycle.timer = previousTimer;
        }
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
