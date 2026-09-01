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

    // Kept only as an internal compatibility gate for ScheduledHeavyRainTraversalRuntime.
    // Scheduled HeavyRain is no longer injected through RoomEffect.HeavyRain at all;
    // the traversal runtime owns its GlobalRain contribution directly.
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
        SuppressScheduledHeavyOverride = false;
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
               state.OwnsDeathRain;
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
            // Unknown shelter state should fail safe as sheltered rather than creating
            // a regional rain carrier in a normal hibernation room.
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

        WeatherScheduleRuntime.Synchronize(world);

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
            orig(self);

            // If another system already owned DeathRain, leave that state completely
            // untouched rather than scaling a foreign disaster with DryCycle's clock.
            if (!state.OwnsDeathRain)
            {
                return;
            }

            // Native DeathRain keeps its nonlinear internal state machine. The
            // universal DryCycle event envelope scales the complete native output.
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

        // Scheduled HeavyRain is intentionally absent here. It is layered by
        // ScheduledHeavyRainTraversalRuntime after the native/authored GlobalRain
        // baseline has been calculated, which keeps its nonlethal contribution out of
        // RoomEffect.HeavyRain and native ThrowAroundObjects.
        // RoomEffect.BulletRain also remains purely room-authored.
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

        // DeathRain.NextDeathRainMode reads camera[0] in several native story paths.
        // WorldClock normally guarantees live gameplay here, but fail closed during
        // room/camera transition frames instead of constructing an invalid native state.
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

        // Mirror the important ownership cleanup performed by GlobalRain.ResetRain
        // without resetting RainCycle.timer/HUD or other systems that belong to the
        // DryCycle clock. Detaching the back-reference prevents a retired native
        // DeathRain state from retaining/operating on GlobalRain after the event.
        if (rain.deathRain != null)
        {
            rain.deathRain.globalRain = null;
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
