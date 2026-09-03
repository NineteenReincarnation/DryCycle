using System;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Owns RoomRain.Update only for carriers created explicitly by DryCycle. Native room
/// DangerType objects are left to Rain World and are never used as DryCycle schedule
/// inputs. The synthetic carrier renders LightRain/HeavyRain/DeathRain and implements
/// DeathRain pressure directly from GlobalRain without switching RoomRain.dangerType.
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
        if (!RainWeatherRuntime.IsSyntheticRoomRain(self))
        {
            orig(self, eu);
            return;
        }

        Room room = self?.room;
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
            "DeathRain");

        GlobalRain global = self.globalRain;
        bool foreignDeathRain = global?.deathRain != null &&
                                !RainWeatherRuntime.OwnsDeathRain(global);
        bool scheduledRain = light > Epsilon || heavy > Epsilon || death > Epsilon;
        if (!scheduledRain && !foreignDeathRain)
        {
            Quiesce(self);
            return;
        }

        UpdateRainOnly(
            self,
            eu,
            lethalDeathRain: death > Epsilon || foreignDeathRain);
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
            ApplyDeathRainPressure(rain);
        }

        PreserveWaterAccessibility(rain);
        UpdateBulletDrips(rain, lethalDeathRain ? 1f : 0f);
        UpdateRainSounds(rain);
    }

    private static void ApplyDeathRainPressure(RoomRain rain)
    {
        Room room = rain?.room;
        GlobalRain global = rain?.globalRain;
        if (room?.physicalObjects == null || rain.rainReach == null || global == null)
        {
            return;
        }

        float insidePush = Mathf.Max(0f, global.InsidePushAround);
        float outsidePush = Mathf.Max(0f, global.OutsidePushAround);
        if (insidePush <= Epsilon && outsidePush <= Epsilon)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                PhysicalObject item = objects[objectIndex];
                if (item?.bodyChunks == null)
                {
                    continue;
                }

                if (ModManager.Watcher &&
                    room.game.IsStorySession &&
                    item.abstractPhysicalObject != null &&
                    item.abstractPhysicalObject.rippleLayer != 0)
                {
                    continue;
                }

                for (int chunkIndex = 0; chunkIndex < item.bodyChunks.Length; chunkIndex++)
                {
                    BodyChunk chunk = item.bodyChunks[chunkIndex];
                    if (chunk == null)
                    {
                        continue;
                    }

                    IntVector2 tile = room.GetTilePosition(
                        chunk.pos + new Vector2(
                            Mathf.Lerp(-chunk.rad, chunk.rad, UnityEngine.Random.value),
                            Mathf.Lerp(-chunk.rad, chunk.rad, UnityEngine.Random.value)));
                    int x = Custom.IntClamp(tile.x, 0, room.TileWidth - 1);
                    bool exposed = rain.rainReach[x] < tile.y;
                    float pressure = exposed
                        ? Mathf.Max(outsidePush, insidePush)
                        : insidePush;

                    if (room.water)
                    {
                        pressure *= Mathf.InverseLerp(
                            room.FloatWaterLevel(chunk.pos) - 100f,
                            room.FloatWaterLevel(chunk.pos),
                            chunk.pos.y);
                    }

                    if (pressure <= Epsilon)
                    {
                        continue;
                    }

                    if (chunk.ContactPoint.y < 0)
                    {
                        int sideBias = 0;
                        if (rain.rainReach[Custom.IntClamp(tile.x - 1, 0, room.TileWidth - 1)] >= tile.y &&
                            !room.GetTile(tile + new IntVector2(-1, 0)).Solid)
                        {
                            sideBias--;
                        }
                        if (rain.rainReach[Custom.IntClamp(tile.x + 1, 0, room.TileWidth - 1)] >= tile.y &&
                            !room.GetTile(tile + new IntVector2(1, 0)).Solid)
                        {
                            sideBias++;
                        }

                        chunk.vel += Custom.DegToVec(
                            Mathf.Lerp(-30f, 30f, UnityEngine.Random.value) + sideBias * 16f) *
                            (UnityEngine.Random.value * (exposed ? 9f : 4f) * pressure) /
                            chunk.mass;
                    }
                    else
                    {
                        chunk.vel.y -= Mathf.Pow(UnityEngine.Random.value, 5f) *
                                       16.5f * pressure /
                                       chunk.mass;
                    }

                    if (chunk.owner is Creature creature)
                    {
                        if (Mathf.Pow(UnityEngine.Random.value, 1.2f) *
                            2f * creature.bodyChunks.Length < pressure)
                        {
                            creature.Stun(UnityEngine.Random.Range(
                                1,
                                1 + (int)(9f * pressure)));
                        }

                        if (chunk == creature.mainBodyChunk)
                        {
                            creature.rainDeath += pressure / 20f;
                        }

                        if (pressure > 0.5f &&
                            creature.rainDeath > 1f &&
                            UnityEngine.Random.value < 0.025f)
                        {
                            creature.Die();
                        }
                    }

                    chunk.vel += Custom.DegToVec(
                        Mathf.Lerp(90f, 270f, UnityEngine.Random.value)) *
                        (UnityEngine.Random.value * 5f * insidePush);
                }
            }
        }
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
