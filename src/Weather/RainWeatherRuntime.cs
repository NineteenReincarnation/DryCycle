using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather;

/// <summary>
/// Bridges scheduled Rain variants into Rain World's native rain simulation.
/// LightRain and HeavyRain reuse the original RoomEffect channels; DeathRain reuses
/// the native GlobalRain.DeathRain state machine but is owned only for the scheduled
/// interval and is cleaned up when that interval ends.
/// </summary>
internal static class RainWeatherRuntime
{
    private sealed class GlobalRainState
    {
        internal bool OwnsDeathRain;
        internal float StartFlood;
        internal float StartFloodSpeed;
        internal bool StartForceSlowFlood;
    }

    private sealed class SyntheticRoomRainMarker
    {
    }

    [ThreadStatic]
    private static RoomSettings _effectOverrideSettings;

    [ThreadStatic]
    private static float _lightRainOverride;

    [ThreadStatic]
    private static float _heavyRainOverride;

    // ScheduledHeavyRainTraversalRuntime temporarily opens this gate only while
    // GlobalRain is establishing the room-authored/native HeavyRain baseline. It is
    // thread-local so nested or unrelated room-effect queries cannot leak the state.
    [ThreadStatic]
    internal static bool SuppressScheduledHeavyOverride;

    private static ConditionalWeakTable<GlobalRain, GlobalRainState> _globalStates = new();
    private static ConditionalWeakTable<RoomRain, SyntheticRoomRainMarker> _syntheticRoomRain = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Loaded += Room_Loaded;
        On.GlobalRain.Update += GlobalRain_Update;
        On.RoomRain.Update += RoomRain_Update;
        On.RoomSettings.GetEffectAmount += RoomSettings_GetEffectAmount;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        On.GlobalRain.Update -= GlobalRain_Update;
        On.RoomRain.Update -= RoomRain_Update;
        On.RoomSettings.GetEffectAmount -= RoomSettings_GetEffectAmount;

        _effectOverrideSettings = null;
        _lightRainOverride = 0f;
        _heavyRainOverride = 0f;
        SuppressScheduledHeavyOverride = false;
        _globalStates = new ConditionalWeakTable<GlobalRain, GlobalRainState>();
        _syntheticRoomRain = new ConditionalWeakTable<RoomRain, SyntheticRoomRainMarker>();
        _enabled = false;
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (self?.world?.region == null ||
            self.game == null ||
            !self.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(self.world) ||
            self.abstractRoom == null ||
            self.abstractRoom.shelter ||
            !RegionSupportsRain(self.world.region.name) ||
            self.roomRain != null)
        {
            return;
        }

        // Region weather needs the same renderer/shelter mask/sounds as authored
        // Rain rooms. Mark only the object DryCycle creates so native RoomRain never
        // gets suppressed by DryCycle's dormant-state rules.
        RoomRain roomRain = new(self.game.globalRain, self)
        {
            dangerType = RoomRain.DangerType.Rain
        };
        self.roomRain = roomRain;
        self.AddObject(roomRain);
        _syntheticRoomRain.Add(roomRain, new SyntheticRoomRainMarker());
    }

    private static void GlobalRain_Update(
        On.GlobalRain.orig_Update orig,
        GlobalRain self)
    {
        World world = self?.game?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            StopOwnedDeathRain(self);
            orig(self);
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

        GlobalRainState state = _globalStates.GetOrCreateValue(self);
        if (death > 0.0001f)
        {
            StartOwnedDeathRain(self, state);
            orig(self);

            // If another system already owned DeathRain, leave it completely alone.
            if (!state.OwnsDeathRain)
            {
                return;
            }

            // Native DeathRain remains responsible for stage selection and all of its
            // nonlinear relationships. The schedule envelope fades the complete native
            // output through the extra lead-in/tail windows defined by the scheduler.
            self.Intensity *= death;
            self.RumbleSound *= death;
            self.ScreenShake *= death;
            self.MicroScreenShake *= death;
            self.bulletRainDensity *= death;
            return;
        }

        StopOwnedDeathRain(self);

        RoomSettings settings = self.game.cameras != null &&
                                self.game.cameras.Length > 0
            ? self.game.cameras[0]?.room?.roomSettings
            : null;

        RoomSettings previousSettings = _effectOverrideSettings;
        float previousLight = _lightRainOverride;
        float previousHeavy = _heavyRainOverride;

        _effectOverrideSettings = settings;
        _lightRainOverride = light;
        _heavyRainOverride = heavy;

        try
        {
            orig(self);
        }
        finally
        {
            _effectOverrideSettings = previousSettings;
            _lightRainOverride = previousLight;
            _heavyRainOverride = previousHeavy;
        }
    }

    private static void RoomRain_Update(
        On.RoomRain.orig_Update orig,
        RoomRain self,
        bool eu)
    {
        Room room = self?.room;
        World world = room?.world;
        bool synthetic = self != null && _syntheticRoomRain.TryGetValue(self, out _);

        // A DryCycle-created RoomRain must never become a hidden source of vanilla
        // rain after this region's DryCycle switch is turned off. Native RoomRain is
        // deliberately not touched here and runs through orig exactly as authored.
        if (synthetic &&
            (world?.game == null ||
             !world.game.IsStorySession ||
             !RegionDayNightOptions.IsEnabled(world)))
        {
            QuiesceSyntheticRoomRain(self);
            return;
        }

        if (world?.game == null ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            orig(self, eu);
            return;
        }

        float rain = Math.Max(
            WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.Weather,
                "LightRain",
                "HeavyRain"),
            WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.DangerType,
                "DeathRain",
                "Rain"));

        if (rain <= 0.0001f)
        {
            if (synthetic)
            {
                QuiesceSyntheticRoomRain(self);
            }
            else
            {
                orig(self, eu);
            }
            return;
        }

        float? previousRainIntensity = room.roomSettings.rInts;
        RoomRain.DangerType previousDanger = self.dangerType;

        // Regional rain should render in rooms with no authored local rain intensity.
        room.roomSettings.rInts = 1f;
        if (synthetic)
        {
            self.dangerType = RoomRain.DangerType.Rain;
        }
        else if (self.dangerType == RoomRain.DangerType.Flood)
        {
            // Kept as a legacy fallback for hook stacks where the direct DangerType
            // takeover is bypassed by another mod. Normal DryCycle-owned DangerType
            // rooms are intercepted earlier by RoomDangerTypeTakeoverRuntime.
            EnsureRainLoopsForPromotedFlood(self);
            self.dangerType = RoomRain.DangerType.FloodAndRain;
        }

        try
        {
            orig(self, eu);
        }
        finally
        {
            room.roomSettings.rInts = previousRainIntensity;
            self.dangerType = previousDanger;
        }
    }

    private static void EnsureRainLoopsForPromotedFlood(RoomRain rain)
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

    private static void QuiesceSyntheticRoomRain(RoomRain rain)
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

        if (rain.normalRainSound != null)
        {
            rain.normalRainSound.Volume = 0f;
            rain.normalRainSound.Update();
        }
        if (rain.heavyRainSound != null)
        {
            rain.heavyRainSound.Volume = 0f;
            rain.heavyRainSound.Update();
        }
        if (rain.deathRainSound != null)
        {
            rain.deathRainSound.Volume = 0f;
            rain.deathRainSound.Update();
        }
        if (rain.rumbleSound != null)
        {
            rain.rumbleSound.Volume = 0f;
            rain.rumbleSound.Update();
        }
        if (rain.floodingSound != null)
        {
            rain.floodingSound.Volume = 0f;
            rain.floodingSound.Update();
        }
        if (rain.distantDeathRainSound != null)
        {
            rain.distantDeathRainSound.Volume = 0f;
            rain.distantDeathRainSound.Update();
        }
        if (rain.SCREENSHAKESOUND != null)
        {
            rain.SCREENSHAKESOUND.Volume = 0f;
            rain.SCREENSHAKESOUND.Update();
        }
    }

    private static float RoomSettings_GetEffectAmount(
        On.RoomSettings.orig_GetEffectAmount orig,
        RoomSettings self,
        RoomSettings.RoomEffect.Type type)
    {
        float authored = orig(self, type);
        if (_effectOverrideSettings != self)
        {
            return authored;
        }

        if (type == RoomSettings.RoomEffect.Type.LightRain)
        {
            return Math.Max(authored, _lightRainOverride);
        }

        if (type == RoomSettings.RoomEffect.Type.HeavyRain)
        {
            return SuppressScheduledHeavyOverride
                ? authored
                : Math.Max(authored, _heavyRainOverride);
        }

        // RoomEffect.BulletRain is deliberately not overridden here. It remains a
        // native room-authored effect and is no longer a DryCycle scheduled weather.
        return authored;
    }

    private static void StartOwnedDeathRain(GlobalRain rain, GlobalRainState state)
    {
        if (rain == null || state == null || state.OwnsDeathRain)
        {
            return;
        }

        if (rain.deathRain != null)
        {
            return;
        }

        state.StartFlood = rain.flood;
        state.StartFloodSpeed = rain.floodSpeed;
        state.StartForceSlowFlood = rain.forceSlowFlood;
        rain.InitDeathRain();
        state.OwnsDeathRain = true;
    }

    private static void StopOwnedDeathRain(GlobalRain rain)
    {
        if (rain == null ||
            !_globalStates.TryGetValue(rain, out GlobalRainState state) ||
            !state.OwnsDeathRain)
        {
            return;
        }

        rain.deathRain = null;
        rain.Intensity = 0f;
        rain.RumbleSound = 0f;
        rain.ScreenShake = 0f;
        rain.MicroScreenShake = 0f;
        rain.bulletRainDensity = 0f;
        rain.ShaderLight = -1f;
        rain.flood = state.StartFlood;
        rain.floodSpeed = state.StartFloodSpeed;
        rain.forceSlowFlood = state.StartForceSlowFlood;
        state.OwnsDeathRain = false;
    }

    private static bool RegionSupportsRain(string regionId)
    {
        return RegionClimateRegistry.RegionCanUseWeather(regionId, "Rain") ||
               RegionClimateRegistry.RegionCanUseWeather(regionId, "LightRain") ||
               RegionClimateRegistry.RegionCanUseWeather(regionId, "HeavyRain") ||
               RegionClimateRegistry.RegionCanUseDanger(regionId, "DeathRain") ||
               RegionClimateRegistry.RegionCanUseDanger(regionId, "Rain");
    }
}
