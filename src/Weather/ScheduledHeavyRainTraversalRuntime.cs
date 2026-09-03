using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// Separates DryCycle's scheduled HeavyRain from authored RoomEffect.HeavyRain.
/// Authored/native RoomRain keeps its own baseline behavior; Scheduled HeavyRain adds
/// visuals, sound, shake and nonlethal traversal pressure without changing room
/// DangerType or feeding its extra intensity into native rainDeath physics.
/// </summary>
internal static class ScheduledHeavyRainTraversalRuntime
{
    private const float ActivationThreshold = 0.25f;
    private const float FullHorizontalVelocityRetention = 0.965f;
    private const float FullAirDownwardAcceleration = 0.22f;
    private const float FullClimbVelocityRetention = 0.88f;
    private const float FullScheduledHeavyRainIntensity = 1.2f;

    private sealed class GlobalState
    {
        internal float NativeIntensity;
        internal float ScheduledHeavy;
    }

    private sealed class RoomRainState
    {
        internal bool InUpdate;
        internal float? AuthoredRainIntensity;
    }

    private static ConditionalWeakTable<GlobalRain, GlobalState> _globalStates = new();
    private static ConditionalWeakTable<RoomRain, RoomRainState> _roomStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.GlobalRain.Update += GlobalRain_Update;
        On.RoomRain.Update += RoomRain_Update;
        On.RoomRain.ThrowAroundObjects += RoomRain_ThrowAroundObjects;
        On.Player.Update += Player_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.GlobalRain.Update -= GlobalRain_Update;
        On.RoomRain.Update -= RoomRain_Update;
        On.RoomRain.ThrowAroundObjects -= RoomRain_ThrowAroundObjects;
        On.Player.Update -= Player_Update;

        _globalStates = new ConditionalWeakTable<GlobalRain, GlobalState>();
        _roomStates = new ConditionalWeakTable<RoomRain, RoomRainState>();
        _enabled = false;
    }

    /// <summary>
    /// Returns GlobalRain.Intensity exactly as it existed after native/room-authored
    /// processing and before DryCycle Scheduled HeavyRain was layered on top.
    /// </summary>
    internal static bool TryGetNativeIntensity(GlobalRain rain, out float intensity)
    {
        intensity = 0f;
        if (rain == null || !_globalStates.TryGetValue(rain, out GlobalState state))
        {
            return false;
        }

        intensity = state.NativeIntensity;
        return true;
    }

    private static void GlobalRain_Update(
        On.GlobalRain.orig_Update orig,
        GlobalRain self)
    {
        orig(self);

        if (self == null)
        {
            return;
        }

        GlobalState state = _globalStates.GetOrCreateValue(self);
        state.NativeIntensity = self.Intensity;
        state.ScheduledHeavy = 0f;

        World world = self.game?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return;
        }

        float heavy = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "HeavyRain");
        if (heavy <= 0.0001f)
        {
            return;
        }

        // Any active DeathRain state is authoritative over Scheduled HeavyRain.
        if (self.deathRain != null)
        {
            return;
        }

        heavy = ApplyWatcherPassiveRainReduction(heavy, out float reducedToLight);
        state.ScheduledHeavy = heavy;

        if (heavy > 0.0001f)
        {
            float scheduledIntensity = FullScheduledHeavyRainIntensity * heavy;
            self.Intensity = Math.Max(self.Intensity, scheduledIntensity);
            self.RumbleSound = Math.Max(self.RumbleSound, heavy * 0.2f);
            self.ScreenShake = Math.Max(self.ScreenShake, heavy);
        }
        else if (reducedToLight > 0.0001f)
        {
            self.Intensity = Math.Max(self.Intensity, reducedToLight * 0.24f);
        }
    }

    private static void RoomRain_Update(
        On.RoomRain.orig_Update orig,
        RoomRain self,
        bool eu)
    {
        if (self == null)
        {
            orig(self, eu);
            return;
        }

        // Synthetic DryCycle carriers are intercepted by SyntheticRoomRainTakeoverRuntime.
        // This wrapper exists only to preserve the pre-DryCycle intensity of any other
        // RoomRain object that reaches vanilla Update while Scheduled HeavyRain is active.
        RoomRainState state = _roomStates.GetOrCreateValue(self);
        state.AuthoredRainIntensity = self.room?.roomSettings?.rInts;
        state.InUpdate = true;

        try
        {
            orig(self, eu);
        }
        finally
        {
            state.InUpdate = false;
        }
    }

    private static void RoomRain_ThrowAroundObjects(
        On.RoomRain.orig_ThrowAroundObjects orig,
        RoomRain self)
    {
        if (self?.globalRain == null ||
            !_globalStates.TryGetValue(self.globalRain, out GlobalState globalState) ||
            globalState.ScheduledHeavy <= 0.0001f ||
            !_roomStates.TryGetValue(self, out RoomRainState roomState) ||
            !roomState.InUpdate)
        {
            orig(self);
            return;
        }

        // Preserve only the state that DryCycle itself changes: GlobalRain intensity and
        // the authored HeavyRain RoomEffect amount. No RoomRain.DangerType is inspected,
        // cached or rewritten here.
        float scheduledIntensity = self.globalRain.Intensity;
        float? currentRainIntensity = self.room?.roomSettings?.rInts;

        self.globalRain.Intensity = globalState.NativeIntensity;
        if (self.room?.roomSettings != null)
        {
            self.room.roomSettings.rInts = roomState.AuthoredRainIntensity;
        }

        try
        {
            orig(self);
        }
        finally
        {
            self.globalRain.Intensity = scheduledIntensity;
            if (self.room?.roomSettings != null)
            {
                self.room.roomSettings.rInts = currentRainIntensity;
            }
        }
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (!TryGetScheduledTraversalPressure(self, out float pressure) ||
            pressure <= 0.0001f)
        {
            return;
        }

        ApplyHorizontalResistance(self, pressure);

        if (IsClimbing(self))
        {
            ApplyClimbResistance(self, pressure);
            return;
        }

        if (IsAirborne(self))
        {
            ApplyAirDownPressure(self, pressure);
        }
    }

    private static bool TryGetScheduledTraversalPressure(
        Player player,
        out float pressure)
    {
        pressure = 0f;
        Room room = player?.room;
        World world = room?.world;
        if (player == null ||
            room == null ||
            world?.game == null ||
            player.dead ||
            !player.Consious ||
            player.submerged ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return false;
        }

        WeatherScheduleRuntime.Synchronize(world);
        float scheduledHeavy = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "HeavyRain");
        if (scheduledHeavy <= ActivationThreshold)
        {
            return false;
        }

        RoomRain rain = room.roomRain;
        if (rain?.rainReach == null || rain.rainReach.Length == 0)
        {
            return false;
        }

        float exposedFraction = 0f;
        int validChunks = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null || chunk.submersion >= 0.5f)
            {
                continue;
            }

            validChunks++;
            IntVector2 tile = room.GetTilePosition(chunk.pos);
            int x = Custom.IntClamp(tile.x, 0, room.TileWidth - 1);
            if (rain.rainReach[x] < tile.y)
            {
                exposedFraction += 1f;
            }
        }

        if (validChunks == 0)
        {
            return false;
        }

        exposedFraction /= validChunks;
        if (exposedFraction <= 0f)
        {
            return false;
        }

        float normalized = Mathf.InverseLerp(
            ActivationThreshold,
            1f,
            Mathf.Clamp01(scheduledHeavy));
        normalized = normalized * normalized * (3f - 2f * normalized);
        pressure = normalized * exposedFraction;
        return pressure > 0.0001f;
    }

    private static void ApplyHorizontalResistance(Player player, float pressure)
    {
        float retention = Mathf.Lerp(
            1f,
            FullHorizontalVelocityRetention,
            Mathf.Clamp01(pressure));

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.vel.x *= retention;
            }
        }
    }

    private static void ApplyAirDownPressure(Player player, float pressure)
    {
        float down = FullAirDownwardAcceleration * Mathf.Clamp01(pressure);
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            if (chunk.vel.y > 0f)
            {
                chunk.vel.y -= down;
            }
            else if (chunk.vel.y > -1.5f)
            {
                chunk.vel.y -= down * 0.25f;
            }
        }
    }

    private static void ApplyClimbResistance(Player player, float pressure)
    {
        float retention = Mathf.Lerp(
            1f,
            FullClimbVelocityRetention,
            Mathf.Clamp01(pressure));

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            chunk.vel.x *= retention;
            chunk.vel.y *= retention;
        }
    }

    private static bool IsAirborne(Player player)
    {
        if (player.bodyMode == Player.BodyModeIndex.Swimming ||
            player.bodyMode == Player.BodyModeIndex.ZeroG ||
            player.bodyMode == Player.BodyModeIndex.CorridorClimb ||
            player.bodyMode == Player.BodyModeIndex.WallClimb ||
            player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam)
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null && chunk.ContactPoint.y < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsClimbing(Player player)
    {
        return player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam ||
               player.bodyMode == Player.BodyModeIndex.WallClimb ||
               player.bodyMode == Player.BodyModeIndex.CorridorClimb ||
               player.animation == Player.AnimationIndex.ClimbOnBeam ||
               player.animation == Player.AnimationIndex.HangFromBeam ||
               player.animation == Player.AnimationIndex.HangUnderVerticalBeam ||
               player.animation == Player.AnimationIndex.VineGrab ||
               player.animation == Player.AnimationIndex.AntlerClimb;
    }

    private static float ApplyWatcherPassiveRainReduction(
        float heavy,
        out float reducedToLight)
    {
        reducedToLight = 0f;
        heavy = Mathf.Clamp01(heavy);
        if (!ModManager.Watcher)
        {
            return heavy;
        }

        float reduction = global::Watcher.Watcher.cfgReducePassiveRainIntensity.Value;
        if (reduction < 0.5f)
        {
            return heavy *
                   (1f - Mathf.Lerp(0f, 0.99f, reduction * 2f));
        }

        reducedToLight = heavy *
                         Mathf.Lerp(1f, 0f, (reduction - 0.5f) * 2f);
        return 0f;
    }
}
