using System;
using DryCycle.DayNight;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Weather;

/// <summary>
/// Temporary WorldClock-driven sandstorm schedule used while the weather layer is
/// being built. The actual storm renderer, sound and wind simulation remain Watcher's
/// native Sandstorm; DryCycle only supplies the time envelope and changes hazardous
/// exposure so solid terrain can shelter creatures from the lethal storm.
/// </summary>
internal static class SandstormWeatherRuntime
{
    internal const int RainMeterPipTicks = 1200;
    internal const int NormalWeatherPip = 2;
    internal const int HazardWeatherPip = 4;

    private const int HalfPipTicks = RainMeterPipTicks / 2;
    private const int SyntheticCycleLength = 10000;

    internal static readonly Color NormalForecastColor = new(0.90f, 0.76f, 0.42f);
    internal static readonly Color HazardForecastColor = new(0.66f, 0.44f, 0.16f);

    [ThreadStatic]
    private static RoomSettings _effectOverrideSettings;

    [ThreadStatic]
    private static float _surfaceEffectOverride;

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

        internal bool Active => Normal > 0.0001f || Hazard > 0.0001f;
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
        _enabled = false;
    }

    internal static WeatherSample Evaluate(WorldClock clock)
    {
        if (!WorldClockHooks.TestScheduleEnabled || clock == null || clock.IsNight)
        {
            return default;
        }

        float dayTicks = clock.HalfProgress * clock.DayCycleLength;
        float normal = EventEnvelope(
            dayTicks,
            NormalWeatherPip * RainMeterPipTicks,
            HalfPipTicks);
        float hazard = EventEnvelope(
            dayTicks,
            HazardWeatherPip * RainMeterPipTicks,
            HalfPipTicks);
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

        if (!WorldClockHooks.TestScheduleEnabled ||
            !ModManager.Watcher ||
            self?.game == null ||
            !self.game.IsStorySession)
        {
            return;
        }

        // Watcher normally only creates this object for SurfaceSandstorm rooms or
        // DangerType.Sandstorm. The temporary test schedule is global, so keep one
        // dormant Sandstorm object in every realized story room only while testing.
        if (self.sandstorm == null)
        {
            self.sandstorm = new Sandstorm(self);
            self.AddObject(self.sandstorm);
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
        if (!WorldClockHooks.TestScheduleEnabled ||
            !TryGetClock(self, out WorldClock clock))
        {
            orig(self, eu);
            return;
        }

        WeatherSample sample = Evaluate(clock);
        RainCycle rainCycle = self.room.world.rainCycle;
        RoomSettings settings = self.room.roomSettings;
        if (rainCycle == null || settings == null)
        {
            orig(self, eu);
            return;
        }

        // The hazardous storm must still have a terrain exposure mask. Watcher's
        // original DangerType path can skip mask generation because GlobalIntensity
        // is assigned after the SurfaceIntensity mask check.
        if (sample.Active && self.surfaceMask == null)
        {
            self.GenerateSurfaceMask();
        }

        int previousTimer = rainCycle.timer;
        int previousCycleLength = rainCycle.cycleLength;
        float? previousRainIntensity = settings.rInts;
        RoomSettings previousOverrideSettings = _effectOverrideSettings;
        float previousSurfaceOverride = _surfaceEffectOverride;

        // Compress Watcher's native -400..2800 danger progression into the hazard
        // envelope. This preserves its native ScreenShake, GlobalIntensity^2.2 and
        // late lethality relationships while allowing the same curve to play backward
        // during the half-pip fade-out.
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

    private static void Sandstorm_AffectObjects(
        On.Watcher.Sandstorm.orig_AffectObjects orig,
        Sandstorm self,
        float amount)
    {
        if (!TryGetClock(self, out _))
        {
            orig(self, amount);
            return;
        }

        // Watcher's native disaster storm progressively lerps the SurfaceMask toward
        // 1 as GlobalIntensity rises, which eventually makes walls stop protecting the
        // player. DryCycle keeps the local exposure mask authoritative at every storm
        // strength: wind and lethality must reach a body through open terrain.
        // This protection is a real DryCycle behavior, not a temporary test feature,
        // so it intentionally remains active when TestScheduleEnabled is false.
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

                    // Unlike Watcher DangerType.Sandstorm, GlobalIntensity never
                    // bypasses this value. A wall shadow therefore remains a valid
                    // shelter even at maximum disaster intensity.
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
        // The mask is deliberately smoothed by Watcher. Give the deep part of a wind
        // shadow a true zero-damage shelter while retaining a gradual edge transition.
        float t = Mathf.InverseLerp(0.18f, 0.78f, surfaceExposure);
        return t * t * (3f - 2f * t);
    }

    private static bool TryGetClock(Sandstorm storm, out WorldClock clock)
    {
        clock = null;
        return storm?.room?.world?.game != null &&
               storm.room.world.game.IsStorySession &&
               WorldClockHooks.TryGetClock(storm.room.world.game, out clock);
    }
}
