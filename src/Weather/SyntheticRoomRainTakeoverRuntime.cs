using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Owns the RoomRain objects created by RainWeatherRuntime for rooms that did not
/// originally have a RoomRain DangerType. Those synthetic objects are rendering
/// carriers for DryCycle regional rain; they must not enter vanilla RoomRain.Update,
/// whose flood/rain-cycle branches assume a natively-authored RoomRain lifecycle.
/// </summary>
internal static class SyntheticRoomRainTakeoverRuntime
{
    private const float Epsilon = 0.0001f;

    private sealed class Marker
    {
    }

    private static ConditionalWeakTable<RoomRain, Marker> _synthetic = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Room.Loaded += Room_Loaded;
        On.RoomRain.Update += RoomRain_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        On.RoomRain.Update -= RoomRain_Update;
        _synthetic = new ConditionalWeakTable<RoomRain, Marker>();
        _enabled = false;
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        RoomRain before = self?.roomRain;
        orig(self);

        if (self == null ||
            before != null ||
            self.roomRain == null ||
            self.roomSettings == null ||
            self.roomSettings.DangerType != RoomRain.DangerType.None ||
            self.roomRain.dangerType != RoomRain.DangerType.Rain ||
            self.abstractRoom == null ||
            self.abstractRoom.shelter ||
            self.world?.game == null ||
            !self.world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(self.world))
        {
            return;
        }

        // RainWeatherRuntime is enabled before this runtime. Calling orig above lets
        // its Room.Loaded hook create the regional carrier first; a transition from
        // no RoomRain to Rain in an authored DangerType=None room identifies that
        // carrier without touching native/authored RoomRain objects.
        _synthetic.Remove(self.roomRain);
        _synthetic.Add(self.roomRain, new Marker());
    }

    private static void RoomRain_Update(
        On.RoomRain.orig_Update orig,
        RoomRain self,
        bool eu)
    {
        if (self == null || !_synthetic.TryGetValue(self, out _))
        {
            orig(self, eu);
            return;
        }

        Room room = self.room;
        World world = room?.world;
        if (room?.roomSettings == null ||
            world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            Quiesce(self);
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

        // A foreign/native DeathRain may own GlobalRain even when DryCycle did not
        // schedule one. Keep rendering/physics from the already-established native
        // GlobalRain outputs, but still avoid vanilla RoomRain.Update on this synthetic
        // carrier.
        bool foreignDeathRain = self.globalRain?.deathRain != null && death <= Epsilon;
        if (light <= Epsilon &&
            heavy <= Epsilon &&
            death <= Epsilon &&
            !foreignDeathRain)
        {
            Quiesce(self);
            return;
        }

        UpdateRainOnly(self, eu, death > Epsilon || foreignDeathRain);
    }

    private static void UpdateRainOnly(
        RoomRain rain,
        bool eu,
        bool lethalDeathRain)
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
            // Only DeathRain is allowed to enter native rain-pressure/rainDeath
            // physics on a DryCycle synthetic carrier. Scheduled HeavyRain never
            // calls this path; its traversal resistance is handled separately by
            // ScheduledHeavyRainTraversalRuntime.
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

        UpdateBulletDrips(rain, lethalDeathRain ? 1f : 0f);
        UpdateRainSounds(rain);
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

        // Synthetic regional rain is governed by the DryCycle schedule, not the
        // vanilla RainCycle countdown/flood branches.
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
