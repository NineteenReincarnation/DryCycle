using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Keeps DryCycle Scheduled HeavyRain out of Rain World's lethal terrain-impact path.
/// DryCycle Scheduled DeathRain is handled explicitly from GlobalRain pressure and does
/// not depend on a room's native DangerType.
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

        // Foreign DeathRain remains completely foreign. DryCycle does not reinterpret
        // or suppress another owner's native RoomRain path.
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
            "DeathRain");
        if (deathRain > Epsilon)
        {
            ApplyScheduledDeathRainImpact(self, crit, speed);
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

        // A DryCycle-created carrier has no native lethal rain behavior to preserve.
        if (RainWeatherRuntime.IsSyntheticRoomRain(self))
        {
            return;
        }

        if (!ScheduledHeavyRainTraversalRuntime.TryGetNativeIntensity(
                self.globalRain,
                out float nativeIntensity))
        {
            // Current GlobalRain.Intensity may contain Scheduled HeavyRain. Without the
            // native baseline, delegating would leak DryCycle HeavyRain into rainDeath.
            return;
        }

        float nativeOutside = PushOutside(nativeIntensity);
        float nativeInside = PushInside(nativeIntensity);
        if (nativeOutside <= Epsilon && nativeInside <= Epsilon)
        {
            return;
        }

        float scheduledIntensity = self.globalRain.Intensity;
        self.globalRain.Intensity = nativeIntensity;
        try
        {
            // Preserve whatever behavior this pre-existing native/foreign RoomRain
            // already had, but only at its pre-DryCycle GlobalRain intensity.
            orig(self, crit, speed);
        }
        finally
        {
            self.globalRain.Intensity = scheduledIntensity;
        }
    }

    private static void ApplyScheduledDeathRainImpact(
        RoomRain rain,
        Creature crit,
        float speed)
    {
        if (rain?.room == null ||
            rain.globalRain == null ||
            rain.rainReach == null ||
            crit?.bodyChunks == null ||
            crit.bodyChunks.Length == 0 ||
            speed < 2.5f)
        {
            return;
        }

        float inside = Mathf.Max(0f, rain.globalRain.InsidePushAround);
        float outside = Mathf.Max(0f, rain.globalRain.OutsidePushAround);
        BodyChunk chunk = crit.bodyChunks[UnityEngine.Random.Range(0, crit.bodyChunks.Length)];
        if (chunk == null)
        {
            return;
        }

        IntVector2 tile = rain.room.GetTilePosition(
            chunk.pos + new Vector2(
                Mathf.Lerp(-chunk.rad, chunk.rad, UnityEngine.Random.value),
                Mathf.Lerp(-chunk.rad, chunk.rad, UnityEngine.Random.value)));
        int x = Custom.IntClamp(tile.x, 0, rain.room.TileWidth - 1);
        float pressure = rain.rainReach[x] < tile.y
            ? Mathf.Max(outside, inside)
            : inside;

        crit.rainDeath += Mathf.InverseLerp(-2.5f, -15f, speed) *
                          Mathf.Lerp(pressure, 1f, 0.5f) *
                          0.65f /
                          crit.bodyChunks.Length;
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
