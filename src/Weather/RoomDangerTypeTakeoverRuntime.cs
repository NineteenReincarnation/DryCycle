using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Captures GlobalRain after RainWeatherRuntime/native processing but before
/// ScheduledHeavyRainTraversalRuntime overlays DryCycle's nonlethal HeavyRain.
/// </summary>
internal static class ScheduledRainNativeBaselineRuntime
{
    private sealed class State
    {
        internal float Intensity;
    }

    private static ConditionalWeakTable<GlobalRain, State> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.GlobalRain.Update += GlobalRain_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.GlobalRain.Update -= GlobalRain_Update;
        _states = new ConditionalWeakTable<GlobalRain, State>();
        _enabled = false;
    }

    internal static bool TryGetIntensity(GlobalRain rain, out float intensity)
    {
        intensity = 0f;
        if (rain == null || !_states.TryGetValue(rain, out State state))
        {
            return false;
        }

        intensity = state.Intensity;
        return true;
    }

    private static void GlobalRain_Update(
        On.GlobalRain.orig_Update orig,
        GlobalRain self)
    {
        orig(self);
        if (self != null)
        {
            _states.GetOrCreateValue(self).Intensity = self.Intensity;
        }
    }
}

/// <summary>
/// Owns RoomRain objects whose room has a native/authored DangerType while DryCycle is
/// enabled. The default Rain/Flood/FloodAndRain/Aerie lifecycle is not allowed to run
/// behind DryCycle's scheduler. Room-authored rain effects are preserved according to
/// the room's original DangerType, while scheduled rain uses a rain-only update path.
/// </summary>
internal static class RoomDangerTypeTakeoverRuntime
{
    private const float Epsilon = 0.0001f;

    private readonly struct WeatherSample
    {
        internal readonly float LightRain;
        internal readonly float HeavyRain;
        internal readonly float DeathRain;

        internal WeatherSample(float lightRain, float heavyRain, float deathRain)
        {
            LightRain = lightRain;
            HeavyRain = heavyRain;
            DeathRain = deathRain;
        }

        internal bool ScheduledRainActive =>
            LightRain > Epsilon ||
            HeavyRain > Epsilon ||
            DeathRain > Epsilon;
    }

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RoomRain.Update += RoomRain_Update;
        On.RoomRain.CreatureSmashedInGround += RoomRain_CreatureSmashedInGround;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomRain.Update -= RoomRain_Update;
        On.RoomRain.CreatureSmashedInGround -= RoomRain_CreatureSmashedInGround;
        _enabled = false;
    }

    private static void RoomRain_Update(
        On.RoomRain.orig_Update orig,
        RoomRain self,
        bool eu)
    {
        if (!TryGetTakeover(self, out _, out WeatherSample sample))
        {
            orig(self, eu);
            return;
        }

        bool authoredRain = AuthoredRainCanRender(self);
        if (sample.ScheduledRainActive || authoredRain)
        {
            UpdateRainOnly(self, eu, sample, authoredRain);
        }
        else
        {
            QuiesceRain(self);
        }
    }

    private static void RoomRain_CreatureSmashedInGround(
        On.RoomRain.orig_CreatureSmashedInGround orig,
        RoomRain self,
        Creature crit,
        float speed)
    {
        if (!TryGetTakeover(self, out _, out WeatherSample sample))
        {
            orig(self, crit, speed);
            return;
        }

        if (crit == null || speed < 2.5f)
        {
            return;
        }

        if (sample.DeathRain > Epsilon)
        {
            float inside = PushInside(self.globalRain.Intensity);
            float outside = PushOutside(self.globalRain.Intensity);
            ApplySmashedRainDeath(self, crit, speed, inside, outside);
            return;
        }

        // Scheduled HeavyRain is nonlethal. Preserve only the room-authored HeavyRain
        // impact contribution using the pre-scheduled native GlobalRain baseline.
        if (!HasAuthoredHeavyRain(self) ||
            !ScheduledRainNativeBaselineRuntime.TryGetIntensity(
                self.globalRain,
                out float baselineIntensity))
        {
            return;
        }

        NativePressureForDanger(
            self,
            baselineIntensity,
            out float nativeInside,
            out float nativeOutside);
        ApplySmashedRainDeath(self, crit, speed, nativeInside, nativeOutside);
    }

    private static bool TryGetTakeover(
        RoomRain rain,
        out WorldClock clock,
        out WeatherSample sample)
    {
        clock = null;
        sample = default;

        Room room = rain?.room;
        World world = room?.world;
        if (room?.roomSettings == null ||
            world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out clock))
        {
            return false;
        }

        RoomRain.DangerType authoredDanger = room.roomSettings.DangerType;
        if (authoredDanger == null || authoredDanger == RoomRain.DangerType.None)
        {
            return false;
        }

        WeatherScheduleRuntime.Synchronize(world);
        sample = new WeatherSample(
            WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.Weather,
                "LightRain"),
            WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.Weather,
                "HeavyRain"),
            WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.DangerType,
                "DeathRain",
                "Rain"));

        // Do not steal a native/foreign DeathRain state that DryCycle did not start.
        if (rain.globalRain?.deathRain != null &&
            !RainWeatherRuntime.OwnsDeathRain(rain.globalRain) &&
            sample.DeathRain <= Epsilon)
        {
            return false;
        }

        return true;
    }

    private static void UpdateRainOnly(
        RoomRain rain,
        bool eu,
        WeatherSample sample,
        bool authoredRain)
    {
        Room room = rain?.room;
        GlobalRain global = rain?.globalRain;
        if (room?.roomSettings == null || room.game == null || global == null)
        {
            return;
        }

        rain.evenUpdate = eu;
        EnsureRainLoops(rain);

        float visualRainCap = sample.ScheduledRainActive
            ? 1f
            : Mathf.Clamp01(room.roomSettings.RainIntensity);

        rain.intensity = Mathf.Lerp(rain.intensity, global.Intensity, 0.2f);
        rain.intensity = Mathf.Min(rain.intensity, visualRainCap);
        rain.lastIntensity = rain.intensity;

        ApplyRainPhysics(rain, sample, authoredRain);
        PreserveWaterAccessibility(rain);

        // Scheduled Light/HeavyRain must not amplify a room-authored BulletRain effect.
        // Only scheduled DeathRain owns regional BulletDrips at full cap.
        float bulletCap = sample.DeathRain > Epsilon
            ? 1f
            : Mathf.Clamp01(room.roomSettings.RainIntensity);
        UpdateBulletDrips(rain, bulletCap);
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

    private static void ApplyRainPhysics(
        RoomRain rain,
        WeatherSample sample,
        bool authoredRain)
    {
        if (rain?.globalRain == null || rain.room?.roomSettings == null)
        {
            return;
        }

        if (sample.DeathRain > Epsilon)
        {
            float inside = PushInside(rain.globalRain.Intensity);
            float outside = PushOutside(rain.globalRain.Intensity);
            ThrowAroundObjectsWithPressure(
                rain,
                inside,
                outside,
                allowRainDeath: true,
                forceRegionalRain: true);
            return;
        }

        if (!authoredRain || !HasAuthoredHeavyRain(rain))
        {
            return;
        }

        float nativeIntensity = rain.globalRain.Intensity;
        if (sample.HeavyRain > Epsilon &&
            ScheduledRainNativeBaselineRuntime.TryGetIntensity(
                rain.globalRain,
                out float baselineIntensity))
        {
            nativeIntensity = baselineIntensity;
        }

        NativePressureForDanger(
            rain,
            nativeIntensity,
            out float nativeInside,
            out float nativeOutside);

        ThrowAroundObjectsWithPressure(
            rain,
            nativeInside,
            nativeOutside,
            allowRainDeath: true,
            forceRegionalRain: false);
    }

    private static void NativePressureForDanger(
        RoomRain rain,
        float intensity,
        out float inside,
        out float outside)
    {
        inside = 0f;
        outside = 0f;
        if (rain?.room?.roomSettings == null)
        {
            return;
        }

        float rainIntensity = Mathf.Clamp01(rain.room.roomSettings.RainIntensity);
        if (rain.dangerType == RoomRain.DangerType.AerieBlizzard)
        {
            inside = PushInside(intensity);
            outside = PushOutside(intensity);
            return;
        }

        if (rain.dangerType == RoomRain.DangerType.Rain)
        {
            inside = PushInside(intensity) * rainIntensity;
            outside = PushOutside(intensity) * rainIntensity;
            return;
        }

        if (rain.dangerType == RoomRain.DangerType.FloodAndRain)
        {
            outside = PushOutside(intensity) * rainIntensity;
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

    private static void ThrowAroundObjectsWithPressure(
        RoomRain rain,
        float insidePush,
        float outsidePush,
        bool allowRainDeath,
        bool forceRegionalRain)
    {
        Room room = rain?.room;
        if (room?.physicalObjects == null || rain.rainReach == null)
        {
            return;
        }

        float authoredRainIntensity = room.roomSettings?.RainIntensity ?? 0f;
        if (!forceRegionalRain &&
            ((rain.dangerType != RoomRain.DangerType.AerieBlizzard &&
              ((ModManager.MMF && authoredRainIntensity < 0.02f) ||
               (ModManager.MSC &&
                room.game.IsStorySession &&
                room.world.region != null &&
                room.world.region.name == "OE" &&
                authoredRainIntensity <= 0.2f))) ||
             authoredRainIntensity == 0f))
        {
            return;
        }

        if (insidePush <= 0f && outsidePush <= 0f)
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
                    BodyChunk bodyChunk = item.bodyChunks[chunkIndex];
                    if (bodyChunk == null)
                    {
                        continue;
                    }

                    IntVector2 tilePosition = room.GetTilePosition(
                        bodyChunk.pos + new Vector2(
                            Mathf.Lerp(-bodyChunk.rad, bodyChunk.rad, UnityEngine.Random.value),
                            Mathf.Lerp(-bodyChunk.rad, bodyChunk.rad, UnityEngine.Random.value)));

                    float pressure = insidePush;
                    bool exposed = false;
                    int x = Custom.IntClamp(tilePosition.x, 0, room.TileWidth - 1);
                    if (rain.rainReach[x] < tilePosition.y)
                    {
                        exposed = true;
                        pressure = Mathf.Max(outsidePush, insidePush);
                    }

                    if (room.water)
                    {
                        pressure *= Mathf.InverseLerp(
                            room.FloatWaterLevel(bodyChunk.pos) - 100f,
                            room.FloatWaterLevel(bodyChunk.pos),
                            bodyChunk.pos.y);
                    }

                    if (pressure <= 0f)
                    {
                        continue;
                    }

                    if (bodyChunk.ContactPoint.y < 0)
                    {
                        int sideBias = 0;
                        if (rain.rainReach[Custom.IntClamp(tilePosition.x - 1, 0, room.TileWidth - 1)] >= tilePosition.y &&
                            !room.GetTile(tilePosition + new IntVector2(-1, 0)).Solid)
                        {
                            sideBias--;
                        }
                        if (rain.rainReach[Custom.IntClamp(tilePosition.x + 1, 0, room.TileWidth - 1)] >= tilePosition.y &&
                            !room.GetTile(tilePosition + new IntVector2(1, 0)).Solid)
                        {
                            sideBias++;
                        }

                        bodyChunk.vel += Custom.DegToVec(
                            Mathf.Lerp(-30f, 30f, UnityEngine.Random.value) + sideBias * 16f) *
                            (UnityEngine.Random.value * (exposed ? 9f : 4f) * pressure) /
                            bodyChunk.mass;
                    }
                    else
                    {
                        bodyChunk.vel.y -= Mathf.Pow(UnityEngine.Random.value, 5f) *
                                           16.5f * pressure /
                                           bodyChunk.mass;
                    }

                    if (bodyChunk.owner is Creature creature)
                    {
                        if (Mathf.Pow(UnityEngine.Random.value, 1.2f) *
                            2f * creature.bodyChunks.Length < pressure)
                        {
                            creature.Stun(UnityEngine.Random.Range(
                                1,
                                1 + (int)(9f * pressure)));
                        }

                        if (allowRainDeath && bodyChunk == creature.mainBodyChunk)
                        {
                            creature.rainDeath += pressure / 20f;
                        }

                        if (allowRainDeath &&
                            pressure > 0.5f &&
                            creature.rainDeath > 1f &&
                            UnityEngine.Random.value < 0.025f)
                        {
                            creature.Die();
                        }
                    }

                    bodyChunk.vel += Custom.DegToVec(
                        Mathf.Lerp(90f, 270f, UnityEngine.Random.value)) *
                        (UnityEngine.Random.value * 5f * insidePush);
                }
            }
        }
    }

    private static void ApplySmashedRainDeath(
        RoomRain rain,
        Creature crit,
        float speed,
        float insidePush,
        float outsidePush)
    {
        if (rain?.room == null ||
            rain.rainReach == null ||
            crit?.bodyChunks == null ||
            crit.bodyChunks.Length == 0 ||
            (insidePush <= 0f && outsidePush <= 0f))
        {
            return;
        }

        BodyChunk bodyChunk = crit.bodyChunks[
            UnityEngine.Random.Range(0, crit.bodyChunks.Length)];
        IntVector2 tilePosition = rain.room.GetTilePosition(
            bodyChunk.pos + new Vector2(
                Mathf.Lerp(-bodyChunk.rad, bodyChunk.rad, UnityEngine.Random.value),
                Mathf.Lerp(-bodyChunk.rad, bodyChunk.rad, UnityEngine.Random.value)));

        float pressure = insidePush;
        int x = Custom.IntClamp(tilePosition.x, 0, rain.room.TileWidth - 1);
        if (rain.rainReach[x] < tilePosition.y)
        {
            pressure = Mathf.Max(outsidePush, insidePush);
        }

        crit.rainDeath += Mathf.InverseLerp(-2.5f, -15f, speed) *
                          Mathf.Lerp(pressure, 1f, 0.5f) *
                          0.65f /
                          bodyChunk.owner.bodyChunks.Length;
    }

    private static void UpdateBulletDrips(RoomRain rain, float rainCap)
    {
        if (rain?.room == null || rain.globalRain == null || rain.bulletDrips == null)
        {
            return;
        }

        int target = (int)(
            rain.room.TileWidth *
            Mathf.Max(0f, rain.globalRain.bulletRainDensity) *
            Mathf.Clamp01(rainCap));

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

        MuteLoop(rain.distantDeathRainSound);
        MuteLoop(rain.floodingSound);

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

    private static bool AuthoredRainCanRender(RoomRain rain)
    {
        if (rain?.room?.roomSettings == null || !HasAnyAuthoredRainEffect(rain))
        {
            return false;
        }

        return rain.dangerType == RoomRain.DangerType.Rain ||
               rain.dangerType == RoomRain.DangerType.FloodAndRain ||
               rain.dangerType == RoomRain.DangerType.AerieBlizzard;
    }

    private static bool HasAnyAuthoredRainEffect(RoomRain rain)
    {
        RoomSettings settings = rain?.room?.roomSettings;
        return settings != null &&
               (settings.GetEffectAmount(RoomSettings.RoomEffect.Type.LightRain) > Epsilon ||
                settings.GetEffectAmount(RoomSettings.RoomEffect.Type.HeavyRain) > Epsilon ||
                settings.GetEffectAmount(RoomSettings.RoomEffect.Type.BulletRain) > Epsilon);
    }

    private static bool HasAuthoredHeavyRain(RoomRain rain)
    {
        RoomSettings settings = rain?.room?.roomSettings;
        return settings != null &&
               settings.GetEffectAmount(RoomSettings.RoomEffect.Type.HeavyRain) > Epsilon;
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

    private static void QuiesceRain(RoomRain rain)
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
