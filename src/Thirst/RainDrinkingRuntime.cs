using System;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.HUD;
using DryCycle.Weather.Scheduling;
using RWCustom;
using UnityEngine;

namespace DryCycle.Thirst;

/// <summary>
/// Lets story players collect hydration directly from exposed rainfall by holding
/// pickup. Rain source detection accepts DryCycle scheduled LightRain/HeavyRain/
/// DeathRain and authored RoomSettings LightRain/HeavyRain effects. Native room
/// DangerType is not a DryCycle drinking source. Exposure follows RoomRain.rainReach
/// when available so drinking uses the same shelter boundary as rain rendering.
/// </summary>
internal static class RainDrinkingRuntime
{
    private const int HoldFramesRequired = 24;
    private const int PickupGraceFrames = 5;
    private const float RainSourceThreshold = 0.0001f;
    private const float MinimumExposure = 0.5f;

    private sealed class PlayerRainDrinkState
    {
        internal int HoldFrames;
    }

    private static ConditionalWeakTable<Player, PlayerRainDrinkState> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.GrabUpdate += Player_GrabUpdate;
        On.Player.Update += Player_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.Player.Update -= Player_Update;
        _states = new ConditionalWeakTable<Player, PlayerRainDrinkState>();
        _enabled = false;
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (!ShouldReserveHeldPickupForRain(self))
        {
            orig(self, eu);
            return;
        }

        bool pickupHeld = self.input[0].pckp;
        self.input[0].pckp = false;

        try
        {
            orig(self, eu);
        }
        finally
        {
            self.input[0].pckp = pickupHeld;
        }
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        PlayerRainDrinkState rainState = self != null
            ? _states.GetOrCreateValue(self)
            : null;

        if (rainState == null)
        {
            return;
        }

        if (!CanChargeRainDrink(self, out ThirstState thirstState))
        {
            rainState.HoldFrames = 0;
            return;
        }

        rainState.HoldFrames = Math.Min(
            HoldFramesRequired,
            rainState.HoldFrames + 1);

        if (rainState.HoldFrames < HoldFramesRequired)
        {
            return;
        }

        thirstState.IsDrinking = true;
        ThirstMeter.ShowDrinking(self);
        ThirstStore.AddRuntime(self, ThirstConstants.DrinkPerTick);
    }

    private static bool ShouldReserveHeldPickupForRain(Player player)
    {
        if (player == null ||
            player.input == null ||
            player.input.Length == 0 ||
            !player.input[0].pckp ||
            !_states.TryGetValue(player, out PlayerRainDrinkState state) ||
            state.HoldFrames < PickupGraceFrames)
        {
            return false;
        }

        return CanChargeRainDrink(player, out _);
    }

    private static bool CanChargeRainDrink(
        Player player,
        out ThirstState thirstState)
    {
        thirstState = null;

        if (player?.room?.game == null ||
            !player.room.game.IsStorySession ||
            player.playerState == null ||
            player.dead ||
            !player.Consious ||
            player.inShortcut ||
            player.input == null ||
            player.input.Length == 0 ||
            !player.input[0].pckp ||
            player.bodyChunks == null ||
            player.bodyChunks.Length == 0 ||
            player.bodyChunks[0] == null ||
            player.bodyChunks[0].submersion >= 0.5f ||
            player.submerged ||
            IsIncompatibleBodyMode(player) ||
            GetDrinkableRainIntensity(player.room) <= RainSourceThreshold ||
            GetRainExposure(player) < MinimumExposure)
        {
            return false;
        }

        thirstState = ThirstStore.For(player);
        float maxWater = ThirstStore.GetMaxWaterPips(player);
        return thirstState.Water < maxWater - 0.0001f;
    }

    private static bool IsIncompatibleBodyMode(Player player)
    {
        return player.bodyMode == Player.BodyModeIndex.Swimming ||
               player.bodyMode == Player.BodyModeIndex.ZeroG ||
               player.bodyMode == Player.BodyModeIndex.CorridorClimb ||
               player.bodyMode == Player.BodyModeIndex.WallClimb ||
               player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam;
    }

    internal static float GetRainExposure(Player player)
    {
        Room room = player?.room;
        BodyChunk head = player?.bodyChunks != null && player.bodyChunks.Length > 0
            ? player.bodyChunks[0]
            : null;

        if (room == null || head == null || room.TileWidth <= 0 || room.TileHeight <= 0)
        {
            return 0f;
        }

        float sampleOffset = Math.Max(2f, head.rad * 0.6f);
        float sampleY = head.pos.y + Math.Max(2f, head.rad * 0.35f);
        float exposed = 0f;

        for (int i = -1; i <= 1; i++)
        {
            Vector2 sample = new(
                head.pos.x + sampleOffset * i,
                sampleY);
            IntVector2 tile = room.GetTilePosition(sample);
            int x = Custom.IntClamp(tile.x, 0, room.TileWidth - 1);
            int y = Custom.IntClamp(tile.y, 0, room.TileHeight - 1);

            if (ColumnIsOpenToRain(room, x, y))
            {
                exposed += 1f;
            }
        }

        return exposed / 3f;
    }

    private static bool ColumnIsOpenToRain(Room room, int x, int y)
    {
        RoomRain roomRain = room.roomRain;
        if (roomRain?.rainReach != null &&
            x >= 0 &&
            x < roomRain.rainReach.Length)
        {
            return roomRain.rainReach[x] < y;
        }

        for (int scanY = y; scanY < room.TileHeight; scanY++)
        {
            if (room.HasAnySolid(x, scanY))
            {
                return false;
            }
        }

        return true;
    }

    internal static float GetDrinkableRainIntensity(Room room)
    {
        if (room == null)
        {
            return 0f;
        }

        float intensity = 0f;
        RoomSettings settings = room.roomSettings;
        if (settings != null)
        {
            intensity = Math.Max(
                settings.GetEffectAmount(RoomSettings.RoomEffect.Type.LightRain),
                settings.GetEffectAmount(RoomSettings.RoomEffect.Type.HeavyRain));
        }

        World world = room.world;
        if (world != null && WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            float scheduledLight = WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.Weather,
                "LightRain");
            float scheduledHeavy = WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.Weather,
                "HeavyRain");
            float scheduledDeathRain = WeatherScheduleRuntime.GetIntensity(
                world,
                clock,
                WeatherScheduleEventKind.DangerType,
                "DeathRain");

            intensity = Math.Max(
                intensity,
                Math.Max(scheduledDeathRain, Math.Max(scheduledLight, scheduledHeavy)));
        }

        return intensity;
    }
}
