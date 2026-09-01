using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Owns safe rain-only updates for DryCycle-created RoomRain carriers and for the
/// vanilla DangerType=None RoomRain objects created solely by WaterCycleBottom/Top.
/// DryCycle carriers never enter vanilla rain/flood hazard branches.
/// </summary>
internal static class SyntheticRoomRainTakeoverRuntime
{
    private const float Epsilon = 0.0001f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RoomRain.Update += RoomRain_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomRain.Update -= RoomRain_Update;
        _enabled = false;
    }

    private static void RoomRain_Update(
        On.RoomRain.orig_Update orig,
        RoomRain self,
        bool eu)
    {
        bool dryCycleCarrier = RainWeatherRuntime.IsSyntheticRoomRain(self);
        bool waterCycleCarrier = IsNativeWaterCycleCarrier(self);
        if (!dryCycleCarrier && !waterCycleCarrier)
        {
            orig(self, eu);
            return;
        }

        Room room = self?.room;
        World world = room?.world;
        bool validDryCycleContext =
            room?.roomSettings != null &&
            world?.game != null &&
            world.game.IsStorySession &&
            RegionDayNightOptions.IsEnabled(world) &&
            WorldClockHooks.TryGetClock(world, out WorldClock clock);

        if (!validDryCycleContext)
        {
            if (dryCycleCarrier)
            {
                Quiesce(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        WeatherScheduleRuntime.Synchronize(world);

        float light = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "LightRain");
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

        GlobalRain global = self.globalRain;
        bool foreignDeathRain = global?.deathRain != null &&
                                !RainWeatherRuntime.OwnsDeathRain(global);

        // A native WaterCycle carrier already existed before DryCycle. If another
        // system owns DeathRain, return that carrier completely to the native/foreign
        // hook chain rather than approximating the foreign disaster in our rain-only
        // path. DryCycle-created carriers have no native lifecycle, so they still need
        // our safe rain-only renderer for that external GlobalRain state.
        if (foreignDeathRain && waterCycleCarrier)
        {
            orig(self, eu);
            return;
        }

        bool scheduledRain = light > Epsilon || heavy > Epsilon || death > Epsilon;
        if (!scheduledRain && !foreignDeathRain)
        {
            if (dryCycleCarrier)
            {
                Quiesce(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        UpdateRainOnly(
            self,
            eu,
            lethalDeathRain: death > Epsilon || foreignDeathRain,
            preserveNativeCarrier: waterCycleCarrier);
    }

    private static bool IsNativeWaterCycleCarrier(RoomRain rain)
    {
        if (rain == null || RainWeatherRuntime.IsSyntheticRoomRain(rain))
        {
            return false;
        }

        Room room = rain.room;
        return room?.roomSettings != null &&
               room.roomSettings.DangerType == RoomRain.DangerType.None &&
               rain.dangerType == RoomRain.DangerType.None &&
               (rain.waterLevelMin != null || rain.waterLevelMax != null);
    }

    private static void UpdateRainOnly(
        RoomRain rain,
        bool eu,
        bool lethalDeathRain,
        bool preserveNativeCarrier)
    {
        Room room = rain?.room;
        GlobalRain global = rain?.globalRain;
        if (room?.roomSettings == null || room.game == null || global == null)
        {
            return;
        }

        rain.evenUpdate = eu;
        EnsureRainLoops(rain);

        rain.intensity = Mathf.Lerp(rain.intensity, global.Intensity, 0.2f);
        rain.intensity = Mathf.Min(rain.intensity, 1f);
        rain.lastIntensity = rain.intensity;

        if (lethalDeathRain && global.AnyPushAround)
        {
            // Only DeathRain may use native rain-pressure/rainDeath physics on a
            // DryCycle regional carrier. Scheduled HeavyRain never enters this path.
            float? previousRainIntensity = room.roomSettings.rInts;
            RoomRain.DangerType previousDanger = rain.dangerType;
            room.roomSettings.rInts = 1f;
            rain.dangerType = RoomRain.DangerType.Rain;
            try
            {
                rain.ThrowAroundObjects();
            }
            finally
            {
                room.roomSettings.rInts = previousRainIntensity;
                rain.dangerType = previousDanger;
            }
        }

        PreserveWaterAccessibility(rain);

        float bulletGate = lethalDeathRain
            ? 1f
            : preserveNativeCarrier
                ? Mathf.Clamp01(room.roomSettings.RainIntensity)
                : 0f;
        UpdateBulletDrips(rain, bulletGate);
        UpdateRainSounds(rain);
    }

    private static void PreserveWaterAccessibility(RoomRain rain)
    {
        if (rain?.room?.game == null)
        {
            return;
        }

        if (rain.waterLevelMin != null ||
            (rain.waterLevelMax != null && rain.room.game.clock % 10 == 0))
        {
            rain.UpdateWaterAccessibility();
        }
    }

    private static void UpdateBulletDrips(RoomRain rain, float densityGate)
    {
        if (rain?.room == null || rain.globalRain == null || rain.bulletDrips == null)
        {
            return;
        }

        int target = (int)(
            rain.room.TileWidth *
            Mathf.Max(0f, rain.globalRain.bulletRainDensity) *
            Mathf.Clamp01(densityGate));

        if (rain.bulletDrips.Count < target)
        {
            BulletDrip drip = new(rain);
            rain.bulletDrips.Add(drip);
            rain.room.AddObject(drip);
        }
        else if (rain.bulletDrips.Count > target && rain.bulletDrips.Count > 0)
        {
            rain.bulletDrips[0]?.Destroy();
            rain.bulletDrips.RemoveAt(0);
        }
    }

    private static void UpdateRainSounds(RoomRain rain)
    {
        Room room = rain?.room;
        if (room?.game == null || rain.globalRain == null)
        {
            return;
        }

        float rippleFade = room.game.rippleFade;

        if (rain.normalRainSound != null)
        {
            rain.normalRainSound.Volume = rain.intensity > 0f
                ? 0.1f + 0.9f * Mathf.Pow(
                    Mathf.Clamp01(Mathf.Sin(
                        Mathf.InverseLerp(0.001f, 0.7f, rain.intensity) * Mathf.PI)),
                    1.5f)
                : 0f;
            rain.normalRainSound.Volume *= rippleFade;
            rain.normalRainSound.Update();
        }

        if (rain.heavyRainSound != null)
        {
            float deathVolume = rain.deathRainSound?.Volume ?? 0f;
            rain.heavyRainSound.Volume = Mathf.Pow(
                                             Mathf.InverseLerp(0.12f, 0.5f, rain.intensity),
                                             0.85f) *
                                         Mathf.Pow(1f - deathVolume, 0.3f) *
                                         rippleFade;
            rain.heavyRainSound.Update();
        }

        if (rain.deathRainSound != null)
        {
            rain.deathRainSound.Volume = Mathf.Pow(
                                             Mathf.InverseLerp(0.35f, 0.75f, rain.intensity),
                                             0.8f) *
                                         rippleFade;
            rain.deathRainSound.Update();
        }

        if (rain.rumbleSound != null)
        {
            rain.rumbleSound.Volume = rain.globalRain.RumbleSound *
                                      room.roomSettings.RumbleIntensity *
                                      rippleFade;
            rain.rumbleSound.Update();
        }

        MuteLoop(rain.floodingSound);
        MuteLoop(rain.distantDeathRainSound);

        if (rain.SCREENSHAKESOUND != null)
        {
            if (room.game.cameras != null &&
                room.game.cameras.Length > 0 &&
                room.game.cameras[0]?.room == room)
            {
                float rumbleVolume = rain.rumbleSound?.Volume ?? 0f;
                rain.SCREENSHAKESOUND.Volume =
                    room.game.cameras[0].ScreenShake * (1f - rumbleVolume);
            }
            else
            {
                rain.SCREENSHAKESOUND.Volume = 0f;
            }
            rain.SCREENSHAKESOUND.Update();
        }
    }

    private static void EnsureRainLoops(RoomRain rain)
    {
        if (rain == null)
        {
            return;
        }

        if (rain.normalRainSound == null)
        {
            rain.normalRainSound = new DisembodiedDynamicSoundLoop(rain)
            {
                sound = SoundID.Normal_Rain_LOOP,
                VolumeGroup = 3
            };
        }

        if (rain.heavyRainSound == null)
        {
            rain.heavyRainSound = new DisembodiedDynamicSoundLoop(rain)
            {
                sound = SoundID.Heavy_Rain_LOOP,
                VolumeGroup = 3
            };
        }
    }

    private static void Quiesce(RoomRain rain)
    {
        if (rain == null)
        {
            return;
        }

        rain.intensity = 0f;
        rain.lastIntensity = 0f;

        if (rain.bulletDrips != null)
        {
            for (int i = rain.bulletDrips.Count - 1; i >= 0; i--)
            {
                rain.bulletDrips[i]?.Destroy();
            }
            rain.bulletDrips.Clear();
        }

        MuteLoop(rain.normalRainSound);
        MuteLoop(rain.heavyRainSound);
        MuteLoop(rain.deathRainSound);
        MuteLoop(rain.rumbleSound);
        MuteLoop(rain.floodingSound);
        MuteLoop(rain.distantDeathRainSound);
        MuteLoop(rain.SCREENSHAKESOUND);
    }

    private static void MuteLoop(DisembodiedDynamicSoundLoop loop)
    {
        if (loop == null)
        {
            return;
        }

        loop.Volume = 0f;
        loop.Update();
    }
}
