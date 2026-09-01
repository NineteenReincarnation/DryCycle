using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Closes the second native rain-death path used by Creature.TerrainImpact.
///
/// Creature.TerrainImpact calls RoomRain.CreatureSmashedInGround whenever the current
/// GlobalRain reports AnyPushAround. Scheduled HeavyRain deliberately raises the visual
/// GlobalRain intensity, so without this guard a synthetic RoomRain could still add
/// rainDeath on ground impacts even though ThrowAroundObjects had already been isolated.
///
/// During DryCycle Scheduled HeavyRain, only the native/authored GlobalRain baseline is
/// allowed to drive CreatureSmashedInGround. If that baseline had no push at all, the
/// impact callback is suppressed. DeathRain remains untouched and fully lethal.
/// </summary>
internal static class ScheduledHeavyRainImpactGuardRuntime
{
    private const float Epsilon = 0.0001f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RoomRain.CreatureSmashedInGround += RoomRain_CreatureSmashedInGround;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomRain.CreatureSmashedInGround -= RoomRain_CreatureSmashedInGround;
        _enabled = false;
    }

    private static void RoomRain_CreatureSmashedInGround(
        On.RoomRain.orig_CreatureSmashedInGround orig,
        RoomRain self,
        Creature crit,
        float speed)
    {
        Room room = self?.room;
        World world = room?.world;
        if (self?.globalRain == null ||
            world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            orig(self, crit, speed);
            return;
        }

        WeatherScheduleRuntime.Synchronize(world);

        float deathRain = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.DangerType,
            "DeathRain",
            "Rain");
        if (deathRain > Epsilon)
        {
            // Scheduled DeathRain intentionally keeps the complete native lethal path.
            orig(self, crit, speed);
            return;
        }

        float heavyRain = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "HeavyRain");
        if (heavyRain <= Epsilon)
        {
            orig(self, crit, speed);
            return;
        }

        // Rooms with an authored/default DangerType are already handled by the outer
        // RoomDangerTypeTakeoverRuntime. This guard targets the no-DangerType/synthetic
        // RoomRain path that otherwise falls through to vanilla CreatureSmashedInGround.
        RoomRain.DangerType authoredDanger = room.roomSettings?.DangerType;
        if (authoredDanger != null && authoredDanger != RoomRain.DangerType.None)
        {
            orig(self, crit, speed);
            return;
        }

        if (!ScheduledRainNativeBaselineRuntime.TryGetIntensity(
                self.globalRain,
                out float nativeIntensity))
        {
            // Failing closed is important here: current GlobalRain.Intensity contains
            // Scheduled HeavyRain, so delegating without a baseline would reintroduce
            // the exact rainDeath leak this guard exists to remove.
            return;
        }

        float nativeOutside = PushOutside(nativeIntensity);
        float nativeInside = PushInside(nativeIntensity);
        if (nativeOutside <= Epsilon && nativeInside <= Epsilon)
        {
            // Without Scheduled HeavyRain, Creature.TerrainImpact would never have
            // entered this callback because native GlobalRain.AnyPushAround was false.
            return;
        }

        float scheduledIntensity = self.globalRain.Intensity;
        self.globalRain.Intensity = nativeIntensity;
        try
        {
            // Preserve room-authored/foreign rain lethality exactly at its own baseline.
            orig(self, crit, speed);
        }
        finally
        {
            self.globalRain.Intensity = scheduledIntensity;
        }
    }

    private static float PushOutside(float intensity)
    {
        return Mathf.Pow(Mathf.InverseLerp(0.35f, 0.7f, intensity), 0.8f);
    }

    private static float PushInside(float intensity)
    {
        return Mathf.Pow(Mathf.InverseLerp(0.63f, 0.98f, intensity), 3.5f);
    }
}
