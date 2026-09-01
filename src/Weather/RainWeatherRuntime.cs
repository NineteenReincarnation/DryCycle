using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;

namespace DryCycle.Weather;

/// <summary>
/// Owns DryCycle's GlobalRain bridge and creates regional RoomRain rendering carriers.
/// RoomRain.Update itself is deliberately owned by the dedicated authored-DangerType
/// and synthetic-carrier takeover runtimes; this class never rewrites a room's
/// DangerType or calls vanilla RoomRain.Update for scheduled weather.
/// </summary>
internal static class RainWeatherRuntime
{
    private sealed class GlobalRainState
    {
        internal bool OwnsDeathRain;
        internal GlobalRain.DeathRain OwnedDeathRain;
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
        On.RoomSettings.GetEffectAmount -= RoomSettings_GetEffectAmount;

        _effectOverrideSettings = null;
        _lightRainOverride = 0f;
        _globalStates = new ConditionalWeakTable<GlobalRain, GlobalRainState>();
        _syntheticRoomRain = new ConditionalWeakTable<RoomRain, SyntheticRoomRainMarker>();
        _enabled = false;
    }

    internal static bool IsSyntheticRoomRain(RoomRain rain)
    {
        return rain != null && _syntheticRoomRain.TryGetValue(rain, out _);
    }

    internal static bool OwnsDeathRain(GlobalRain rain)
    {
        return rain != null &&
               _globalStates.TryGetValue(rain, out GlobalRainState state) &&
               state.OwnsDeathRain &&
               state.OwnedDeathRain != null &&
               ReferenceEquals(rain.deathRain, state.OwnedDeathRain);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (self?.world?.region == null ||
            self.game == null ||
            !self.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(self.world) ||
            self.abstractRoom == null ||
            IsIntactShelter(self) ||
            !RegionSupportsRain(self.world.region.name) ||
            self.roomRain != null)
        {
            return;
        }

        // Region weather needs Rain World's shelter mask, splash data and sound-loop
        // fields, but the carrier must never run the native RainCycle/Flood update.
        // Broken shelters deliberately receive a carrier just like vanilla Room.Loaded.
        RoomRain roomRain = new(self.game.globalRain, self)
        {
            dangerType = RoomRain.DangerType.Rain
        };
        self.roomRain = roomRain;
        self.AddObject(roomRain);
        _syntheticRoomRain.Add(roomRain, new SyntheticRoomRainMarker());
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

        float light = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "LightRain");
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

            // Native DeathRain.Update also advances GlobalRain's shared flood state.
            // Scheduled DeathRain owns the rain hazard, not the world's flood lifecycle,
            // so preserve the exact pre-update flood values and restore them after the
            // native state machine has produced this frame's intensity/shake outputs.
            float frameFlood = self.flood;
            float frameFloodSpeed = self.floodSpeed;
            bool frameForceSlowFlood = self.forceSlowFlood;

            orig(self);

            // Only scale/restore when the exact DeathRain object created by DryCycle is
            // still current. If another system replaced it during orig(), ownership and
            // all shared GlobalRain fields immediately belong to that system.
            if (!OwnsDeathRain(self))
            {
                return;
            }

            self.Intensity *= death;
            self.RumbleSound *= death;
            self.ScreenShake *= death;
            self.MicroScreenShake *= death;
            self.bulletRainDensity *= death;

            self.flood = frameFlood;
            self.floodSpeed = frameFloodSpeed;
            self.forceSlowFlood = frameForceSlowFlood;
            return;
        }

        StopOwnedDeathRain(self);

        RoomSettings settings = self.game.cameras != null &&
                                self.game.cameras.Length > 0
            ? self.game.cameras[0]?.room?.roomSettings
            : null;

        RoomSettings previousSettings = _effectOverrideSettings;
        float previousLight = _lightRainOverride;

        _effectOverrideSettings = settings;
        _lightRainOverride = light;

        try
        {
            orig(self);
        }
        finally
        {
            _effectOverrideSettings = previousSettings;
            _lightRainOverride = previousLight;
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

        // Scheduled HeavyRain is layered only after the native GlobalRain pass by
        // ScheduledHeavyRainTraversalRuntime. RoomEffect.HeavyRain and BulletRain
        // therefore remain purely room-authored inputs to native physics.
        return authored;
    }

    private static void StartOwnedDeathRain(GlobalRain rain, GlobalRainState state)
    {
        if (rain == null || state == null)
        {
            return;
        }

        if (state.OwnsDeathRain)
        {
            if (state.OwnedDeathRain != null &&
                ReferenceEquals(rain.deathRain, state.OwnedDeathRain))
            {
                return;
            }

            // Our old instance is no longer the current GlobalRain DeathRain. Detach
            // only that retired object and relinquish ownership; never clear the new
            // current object that replaced it.
            if (state.OwnedDeathRain != null &&
                ReferenceEquals(state.OwnedDeathRain.globalRain, rain))
            {
                state.OwnedDeathRain.globalRain = null;
            }
            state.OwnedDeathRain = null;
            state.OwnsDeathRain = false;
        }

        if (rain.deathRain != null)
        {
            return;
        }

        if (rain.game?.cameras == null ||
            rain.game.cameras.Length == 0 ||
            rain.game.cameras[0]?.room == null)
        {
            return;
        }

        state.StartFlood = rain.flood;
        state.StartFloodSpeed = rain.floodSpeed;
        state.StartForceSlowFlood = rain.forceSlowFlood;
        rain.InitDeathRain();
        state.OwnedDeathRain = rain.deathRain;
        state.OwnsDeathRain = state.OwnedDeathRain != null;
    }

    private static void StopOwnedDeathRain(GlobalRain rain)
    {
        if (rain == null ||
            !_globalStates.TryGetValue(rain, out GlobalRainState state) ||
            !state.OwnsDeathRain)
        {
            return;
        }

        GlobalRain.DeathRain owned = state.OwnedDeathRain;
        bool stillCurrent = owned != null && ReferenceEquals(rain.deathRain, owned);

        if (owned != null && ReferenceEquals(owned.globalRain, rain))
        {
            owned.globalRain = null;
        }

        state.OwnedDeathRain = null;
        state.OwnsDeathRain = false;

        if (!stillCurrent)
        {
            // Another system has already replaced our DeathRain. Do not reset any
            // GlobalRain outputs/flood fields that may now belong to that system.
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
