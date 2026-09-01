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
/// GlobalRain intensity, so without this guard a RoomRain carrier can still add
/// rainDeath on ground impacts even though ThrowAroundObjects is isolated.
///
/// During DryCycle Scheduled HeavyRain, only a native RoomRain that genuinely existed
/// without DryCycle may preserve its own baseline impact behavior. DryCycle-created
/// synthetic carriers never receive impact rainDeath from Scheduled HeavyRain.
/// Scheduled/foreign DeathRain remains untouched and fully lethal.
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

        // A DeathRain state owned by somebody else is authoritative. Scheduled
        // HeavyRain is already prevented from layering onto it in GlobalRain, so the
        // impact guard must not suppress any of that foreign disaster's native paths.
        if (self.globalRain.deathRain != null &&
            !RainWeatherRuntime.OwnsDeathRain(self.globalRain))
        {
            orig(self, crit, speed);
            return;
        }

        float deathRain = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.DangerType,
            "DeathRain",
            "Rain");
        if (deathRain > Epsilon)
        {
            // DryCycle Scheduled DeathRain intentionally keeps the complete lethal path.
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

        // This carrier did not exist in vanilla. There is therefore no native impact
        // rainDeath behavior to preserve. In particular, do not call vanilla
        // CreatureSmashedInGround with dangerType=Rain merely because the carrier uses
        // that value for rendering; vanilla's Mathf.Lerp(num, 1, .5) would add
        // rainDeath even when the local pressure is zero.
        if (RainWeatherRuntime.IsSyntheticRoomRain(self))
        {
            return;
        }

        // Rooms with an authored/default DangerType are handled by the outer
        // RoomDangerTypeTakeoverRuntime. If hook ordering changes, defer to that/native
        // chain instead of trying to duplicate its authored-danger policy here.
        RoomRain.DangerType authoredDanger = room.roomSettings?.DangerType;
        if (authoredDanger != null && authoredDanger != RoomRain.DangerType.None)
        {
            orig(self, crit, speed);
            return;
        }

        if (!ScheduledHeavyRainTraversalRuntime.TryGetNativeIntensity(
                self.globalRain,
                out float nativeIntensity))
        {
            // Failing closed is important here: current GlobalRain.Intensity can contain
            // Scheduled HeavyRain, so delegating without a baseline could reintroduce
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
            // This is a native DangerType=None WaterCycle carrier (or equivalent
            // foreign RoomRain), so preserve the baseline behavior it already had.
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
