using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather;

/// <summary>
/// Bridges DryCycle's scheduled rain outputs into RoomCamera without relying on the
/// legacy RainCycle.timer reaching RainGameOver. WorldClock deliberately keeps that
/// timer in safe territory, so scheduled HeavyRain/DeathRain must feed camera shake
/// directly while preserving native/foreign shake already present on the camera.
/// </summary>
internal static class WeatherCameraEffectsRuntime
{
    private const float Epsilon = 0.0001f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RoomCamera.Update += RoomCamera_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomCamera.Update -= RoomCamera_Update;
        _enabled = false;
    }

    private static void RoomCamera_Update(
        On.RoomCamera.orig_Update orig,
        RoomCamera self)
    {
        ApplyScheduledShake(self);
        orig(self);
    }

    private static void ApplyScheduledShake(RoomCamera camera)
    {
        Room room = camera?.room;
        World world = room?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            IsIntactShelter(room) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return;
        }

        float heavy = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "HeavyRain");

        float death = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.DangerType,
            "DeathRain",
            "Rain");

        float scheduledScreenShake = Math.Max(0f, heavy);
        float scheduledMicroShake = 0f;

        GlobalRain globalRain = world.game.globalRain;
        if (death > Epsilon &&
            globalRain != null &&
            RainWeatherRuntime.OwnsDeathRain(globalRain))
        {
            // RainWeatherRuntime already applies the schedule envelope to these exact
            // DryCycle-owned DeathRain outputs. Reading them here preserves the native
            // DeathRain mode progression instead of inventing a second shake curve.
            scheduledScreenShake = Math.Max(
                scheduledScreenShake,
                Math.Max(0f, globalRain.ScreenShake));
            scheduledMicroShake = Math.Max(0f, globalRain.MicroScreenShake);
        }

        if (scheduledScreenShake > Epsilon)
        {
            camera.screenShake = Math.Max(camera.screenShake, scheduledScreenShake);
        }

        if (scheduledMicroShake > Epsilon)
        {
            // RoomCamera.Update normally decays microShake by 0.025 before using it in
            // DangerType=None rooms. Replenishing that amount keeps the scheduled
            // value stable for the current frame while still letting native MMF screen-
            // shake suppression run inside the original camera update.
            camera.microShake = Math.Max(
                camera.microShake,
                scheduledMicroShake + 0.025f);
        }
    }

    private static bool IsIntactShelter(Room room)
    {
        if (room?.abstractRoom == null || !room.abstractRoom.shelter)
        {
            return false;
        }

        int shelterIndex = room.abstractRoom.shelterIndex;
        bool[] broken = room.world?.brokenShelters;
        if (broken == null || shelterIndex < 0 || shelterIndex >= broken.Length)
        {
            return true;
        }

        return !broken[shelterIndex];
    }
}
