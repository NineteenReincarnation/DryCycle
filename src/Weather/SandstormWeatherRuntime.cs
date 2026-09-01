using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Weather;

/// <summary>
/// WorldClock-driven Watcher sandstorm bridge. DryCycle owns scheduled surface and
/// danger storms in enabled regions while room-authored SurfaceSandstorm remains a
/// native environmental effect. Default Watcher DangerType.Sandstorm progression is
/// neutralized while DryCycle owns the region.
/// </summary>
internal static class SandstormWeatherRuntime
{
    internal const int RainMeterPipTicks = 1200;
    internal const int NormalWeatherPip = 2;
    internal const int HazardWeatherPip = 4;

    private const int HalfPipTicks = RainMeterPipTicks / 2;
    private const int SyntheticCycleLength = 10000;
    private const float Epsilon = 0.0001f;

    internal static readonly Color NormalForecastColor = new(0.90f, 0.76f, 0.42f);
    internal static readonly Color HazardForecastColor = new(0.66f, 0.44f, 0.16f);

    private sealed class SyntheticSandstormMarker
    {
    }

    [ThreadStatic]
    private static RoomSettings _effectOverrideSettings;

    [ThreadStatic]
    private static float _surfaceEffectOverride;

    private static ConditionalWeakTable<Sandstorm, SyntheticSandstormMarker> _syntheticSandstorms = new();
    private static bool _enabled;

    internal readonly struct WeatherSample
    {
        internal readonly float Normal;
        internal readonly float Hazard;

        internal WeatherSample(float normal, float hazard)
        {
            Normal = Mathf.Clamp01(normal);
            Hazard = Mathf.Clamp01(hazard);
        }

        internal bool Active => Normal > Epsilon || Hazard > Epsilon;
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Loaded += Room_Loaded;
        On.RoomSettings.GetEffectAmount += RoomSettings_GetEffectAmount;
        On.Watcher.Sandstorm.Update += Sandstorm_Update;
        On.Watcher.Sandstorm.AffectObjects += Sandstorm_AffectObjects;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        On.RoomSettings.GetEffectAmount -= RoomSettings_GetEffectAmount;
        On.Watcher.Sandstorm.Update -= Sandstorm_Update;
        On.Watcher.Sandstorm.AffectObjects -= Sandstorm_AffectObjects;
        _effectOverrideSettings = null;
        _surfaceEffectOverride = 0f;
        _syntheticSandstorms = new ConditionalWeakTable<Sandstorm, SyntheticSandstormMarker>();
        _enabled = false;
    }

    internal static WeatherSample Evaluate(World world, WorldClock clock)
    {
        if (clock == null)
        {
            return default;
        }

        if (WorldClockHooks.TestScheduleEnabled)
        {
            if (clock.IsNight)
            {
                return default;
            }

            float dayTicks = clock.HalfProgress * clock.DayCycleLength;
            float normalTest = EventEnvelope(
                dayTicks,
                NormalWeatherPip * RainMeterPipTicks,
                HalfPipTicks);
            float hazardTest = EventEnvelope(
                dayTicks,
                HazardWeatherPip * RainMeterPipTicks,
                HalfPipTicks);
            return new WeatherSample(normalTest, hazardTest);
        }

        float normal = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "SandStorm",
            "Sandstorm");
        float hazard = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.DangerType,
            "SandStorm",
            "Sandstorm",
            "DeathSandStorm");
        return new WeatherSample(normal, hazard);
    }

    internal static bool TryGetForecastColor(int chronologicalPip, out Color color)
    {
        if (!WorldClockHooks.TestScheduleEnabled)
        {
            color = Color.white;
            return false;
        }

        if (chronologicalPip == NormalWeatherPip)
        {
            color = NormalForecastColor;
            return true;
        }

        if (chronologicalPip == HazardWeatherPip)
        {
            color = HazardForecastColor;
            return true;
        }

        color = Color.white;
        return false;
    }

    private static float EventEnvelope(float dayTicks, float targetTicks, float halfWidthTicks)
    {
        if (dayTicks <= targetTicks)
        {
            return Mathf.InverseLerp(targetTicks - halfWidthTicks, targetTicks, dayTicks);
        }

        return Mathf.InverseLerp(targetTicks + halfWidthTicks, targetTicks, dayTicks);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (!ModManager.Watcher ||
            self?.game == null ||
            self.world?.region == null ||
            !self.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(self.world))
        {
            return;
        }

        string regionId = self.world.region.name;
        bool scheduledRegion =
            RegionClimateRegistry.RegionCanUseWeather(regionId, "SandStorm") ||
            RegionClimateRegistry.RegionCanUseWeather(regionId, "Sandstorm") ||
            RegionClimateRegistry.RegionCanUseDanger(regionId, "SandStorm") ||
            RegionClimateRegistry.RegionCanUseDanger(regionId, "Sandstorm") ||
            RegionClimateRegistry.RegionCanUseDanger(regionId, "DeathSandStorm");

        if (!WorldClockHooks.TestScheduleEnabled && !scheduledRegion)
        {
            return;
        }

        if (self.sandstorm == null)
        {
            self.sandstorm = new Sandstorm(self);
            self.AddObject(self.sandstorm);
            _syntheticSandstorms.Add(self.sandstorm, new SyntheticSandstormMarker());
        }
    }

    private static float RoomSettings_GetEffectAmount(
        On.RoomSettings.orig_GetEffectAmount orig,
        RoomSettings self,
        RoomSettings.RoomEffect.Type type)
    {
        float authored = orig(self, type);
        if (_effectOverrideSettings == self &&
            type == WatcherEnums.RoomEffectType.SurfaceSandstorm)
        {
            return Mathf.Max(authored, _surfaceEffectOverride);
        }

        return authored;
    }

    private static void Sandstorm_Update(
        On.Watcher.Sandstorm.orig_Update orig,
        Sandstorm self,
        bool eu)
    {
        bool synthetic = self != null && _syntheticSandstorms.TryGetValue(self, out _);
        World world = self?.room?.world;

        if (synthetic &&
            (world?.game == null ||
             !world.game.IsStorySession ||
             !RegionDayNightOptions.IsEnabled(world)))
        {
            QuiesceSandstorm(self);
            return;
        }

        bool dryCycleRegion = world?.game != null &&
                              world.game.IsStorySession &&
                              RegionDayNightOptions.IsEnabled(world);

        if (!TryGetClock(self, out WorldClock clock))
        {
            // Synthetic/default-danger storms must never fall into Watcher's native
            // lifecycle during world/camera transition frames. Pure authored surface
            // storms still return to native behavior when DryCycle is not able to own
            // the frame.
            if (synthetic || (dryCycleRegion && IsDefaultDangerSandstorm(self)))
            {
                QuiesceSandstorm(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        // Match the rain runtime's ownership rule: a DeathRain state started by another
        // system is authoritative. Suppress only DryCycle's scheduled/synthetic storm;
        // a room-authored SurfaceSandstorm is allowed to keep its native behavior.
        if (HasForeignDeathRain(world))
        {
            if (synthetic || IsDefaultDangerSandstorm(self))
            {
                QuiesceSandstorm(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        WeatherSample sample = Evaluate(self.room.world, clock);
        if (!sample.Active)
        {
            if (synthetic)
            {
                QuiesceSandstorm(self);
            }
            else if (IsDefaultDangerSandstorm(self))
            {
                if (HasAuthoredSurfaceSandstorm(self))
                {
                    RunDefaultDangerSurfaceOnly(orig, self, eu);
                }
                else
                {
                    // Watcher's Update keeps windLoop.Volume at 1 even at the neutral
                    // pre-buildup time. With no authored surface effect, bypass it
                    // entirely so an intercepted default DangerType is truly dormant.
                    QuiesceSandstorm(self);
                }
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        RainCycle rainCycle = self.room.world.rainCycle;
        RoomSettings settings = self.room.roomSettings;
        if (rainCycle == null || settings == null)
        {
            if (synthetic || IsDefaultDangerSandstorm(self))
            {
                QuiesceSandstorm(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        if (self.surfaceMask == null)
        {
            self.GenerateSurfaceMask();
        }

        int previousTimer = rainCycle.timer;
        int previousCycleLength = rainCycle.cycleLength;
        float? previousRainIntensity = settings.rInts;
        RoomSettings previousOverrideSettings = _effectOverrideSettings;
        float previousSurfaceOverride = _surfaceEffectOverride;

        int syntheticDangerTime = Mathf.RoundToInt(Mathf.Lerp(
            Sandstorm.buildupStartTime,
            Sandstorm.lethalMaxTime,
            sample.Hazard));

        _effectOverrideSettings = settings;
        _surfaceEffectOverride = sample.Normal;
        settings.rInts = 1f;
        rainCycle.cycleLength = SyntheticCycleLength;
        rainCycle.timer = SyntheticCycleLength + syntheticDangerTime;

        try
        {
            orig(self, eu);
        }
        finally
        {
            rainCycle.timer = previousTimer;
            rainCycle.cycleLength = previousCycleLength;
            settings.rInts = previousRainIntensity;
            _effectOverrideSettings = previousOverrideSettings;
            _surfaceEffectOverride = previousSurfaceOverride;
        }
    }

    private static bool IsDefaultDangerSandstorm(Sandstorm storm)
    {
        return ModManager.Watcher &&
               storm?.room?.roomSettings != null &&
               storm.room.roomSettings.DangerType == WatcherEnums.WatcherDangerType.Sandstorm;
    }

    private static bool HasAuthoredSurfaceSandstorm(Sandstorm storm)
    {
        return storm?.room?.roomSettings != null &&
               storm.room.roomSettings.GetEffectAmount(
                   WatcherEnums.RoomEffectType.SurfaceSandstorm) > Epsilon;
    }

    private static bool HasForeignDeathRain(World world)
    {
        GlobalRain rain = world?.game?.globalRain;
        return rain?.deathRain != null && !RainWeatherRuntime.OwnsDeathRain(rain);
    }

    private static void RunDefaultDangerSurfaceOnly(
        On.Watcher.Sandstorm.orig_Update orig,
        Sandstorm storm,
        bool eu)
    {
        RainCycle rainCycle = storm?.room?.world?.rainCycle;
        if (rainCycle == null)
        {
            QuiesceSandstorm(storm);
            return;
        }

        int previousTimer = rainCycle.timer;
        try
        {
            rainCycle.timer = rainCycle.cycleLength + Sandstorm.buildupStartTime;
            orig(storm, eu);
        }
        finally
        {
            rainCycle.timer = previousTimer;
        }
    }

    private static void QuiesceSandstorm(Sandstorm storm)
    {
        if (storm == null)
        {
            return;
        }

        storm.SurfaceIntensity = 0f;
        storm.GlobalIntensity = 0f;
        storm.ScreenShake = 0f;
        storm.lethality = 0f;
        storm.pushIntensity = 0f;
        storm.targetPushIntensity = 0f;
        storm.windScroll = Vector2.zero;
        storm.lastWindScroll = Vector2.zero;
        storm.windVel = Sandstorm.minWindSpeed * storm.globalWindDir;
        storm.targetWindVel = storm.windVel;

        if (storm.windLoop != null)
        {
            storm.windLoop.Volume = 0f;
            storm.windLoop.Update();
        }
        if (storm.sandLoop != null)
        {
            storm.sandLoop.Volume = 0f;
            storm.sandLoop.Update();
        }
        if (storm.rumbleLoop != null)
        {
            storm.rumbleLoop.Volume = 0f;
            storm.rumbleLoop.Update();
        }
        if (storm.screenShakeLoop != null)
        {
            storm.screenShakeLoop.Volume = 0f;
            storm.screenShakeLoop.Update();
        }
    }

    private static void Sandstorm_AffectObjects(
        On.Watcher.Sandstorm.orig_AffectObjects orig,
        Sandstorm self,
        float amount)
    {
        bool synthetic = self != null && _syntheticSandstorms.TryGetValue(self, out _);
        World world = self?.room?.world;

        if (synthetic &&
            (world?.game == null ||
             !world.game.IsStorySession ||
             !RegionDayNightOptions.IsEnabled(world)))
        {
            return;
        }

        if (!TryGetClock(self, out WorldClock clock))
        {
            if (!synthetic)
            {
                orig(self, amount);
            }
            return;
        }

        if (HasForeignDeathRain(world))
        {
            if (!synthetic && !IsDefaultDangerSandstorm(self))
            {
                orig(self, amount);
            }
            return;
        }

        WeatherSample sample = Evaluate(self.room.world, clock);
        if (sample.Hazard <= Epsilon)
        {
            orig(self, amount);
            return;
        }

        if (self.room?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < self.room.physicalObjects.Length; layer++)
        {
            var objects = self.room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
            {
                PhysicalObject item = objects[objectIndex];
                if (item == null ||
                    item.abstractPhysicalObject == null ||
                    item.abstractPhysicalObject.rippleLayer != 0 ||
                    item.SandstormImmune ||
                    item.bodyChunks == null)
                {
                    continue;
                }

                float windAffectiveness = Mathf.Sqrt(Mathf.Max(0f, item.windAffectiveness));
                float creatureExposureTotal = 0f;
                int creatureExposureSamples = 0;

                for (int chunkIndex = 0; chunkIndex < item.bodyChunks.Length; chunkIndex++)
                {
                    BodyChunk bodyChunk = item.bodyChunks[chunkIndex];
                    if (bodyChunk == null)
                    {
                        continue;
                    }

                    float exposure = ExposureAt(self, bodyChunk.pos);
                    creatureExposureTotal += LethalExposure(exposure);
                    creatureExposureSamples++;

                    float push = amount * 3f * windAffectiveness;
                    if (self.room.GetTile(bodyChunk.pos).wallbehind)
                    {
                        push *= 0.75f;
                    }
                    if (bodyChunk.ContactPoint.y < 0)
                    {
                        push *= 0.75f;
                    }

                    push *= exposure;

                    if (bodyChunk.ContactPoint.y < 0 &&
                        item.bodyChunks.Length == 1 &&
                        bodyChunk.rad < 7f &&
                        amount < 0.5f)
                    {
                        push = 0f;
                    }

                    bodyChunk.vel += push * Custom.RotateAroundOrigo(
                        self.globalWindDir,
                        UnityEngine.Random.Range(-10f, 10f));
                }

                if (item is not Creature creature || self.lethality <= 0f)
                {
                    continue;
                }

                float localExposure = creatureExposureSamples > 0
                    ? creatureExposureTotal / creatureExposureSamples
                    : 1f;
                float localLethality = self.lethality * Mathf.Clamp01(localExposure);
                if (localLethality <= 0.001f)
                {
                    continue;
                }

                if (Mathf.Pow(UnityEngine.Random.value, 1.2f) * 2f < localLethality)
                {
                    creature.Stun(UnityEngine.Random.Range(
                        1,
                        1 + (int)(9f * localLethality)));
                }

                creature.rainDeath += localLethality / 20f *
                                      UnityEngine.Random.Range(0.5f, 1f);
                if (creature.rainDeath > 1f && UnityEngine.Random.value < 0.025f)
                {
                    creature.Die();
                }
            }
        }
    }

    private static float ExposureAt(Sandstorm storm, Vector2 worldPosition)
    {
        if (storm?.room == null ||
            storm.surfaceMaskValues == null ||
            storm.surfaceMaskValues.Length != storm.room.TileWidth * storm.room.TileHeight)
        {
            return 1f;
        }

        IntVector2 tile = storm.room.GetTilePosition(worldPosition);
        int x = Mathf.Clamp(tile.x, 0, storm.room.TileWidth - 1);
        int y = Mathf.Clamp(tile.y, 0, storm.room.TileHeight - 1);
        return Mathf.Clamp01(storm.surfaceMaskValues[x + y * storm.room.TileWidth]);
    }

    private static float LethalExposure(float surfaceExposure)
    {
        float t = Mathf.InverseLerp(0.18f, 0.78f, surfaceExposure);
        return t * t * (3f - 2f * t);
    }

    private static bool TryGetClock(Sandstorm storm, out WorldClock clock)
    {
        clock = null;
        World world = storm?.room?.world;
        RainWorldGame game = world?.game;
        return game != null &&
               game.IsStorySession &&
               RegionDayNightOptions.IsEnabled(world) &&
               game.cameras != null &&
               game.cameras.Length > 0 &&
               game.cameras[0]?.room != null &&
               WorldClockHooks.TryGetClock(world, out clock);
    }
}
