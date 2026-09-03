using System;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored room environmental heat baseline plus explicit heat-weather bonuses.
///
/// The authored TemperatureSets value remains the normal room baseline. HeatWave and
/// IntenseHeat are applied afterwards as additive gameplay bonuses from the current
/// schedule intensity, so fade-in/fade-out participates continuously and weather can
/// intentionally push RoomHeat above the nominal authored maximum of 1.
/// </summary>
internal static class RoomHeatFactor
{
    internal const float MinimumHeat = -1f;
    internal const float MaximumHeat = 1f;
    internal const float DefaultHeat = 0f;

    internal const float HeatWaveRoomHeatBonus = 0.3f;
    internal const float IntenseHeatRoomHeatBonus = 0.7f;

    internal static float GetRoomHeat(Room room)
    {
        if (room?.abstractRoom == null)
        {
            return DefaultHeat;
        }

        string roomName = room.abstractRoom.name;
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return DefaultHeat;
        }

        string regionName = room.world?.region?.name;
        if (string.IsNullOrWhiteSpace(regionName))
        {
            regionName = InferRegionFromRoomName(roomName);
        }

        float authored = TemperatureSetsLoader.GetRoomHeat(regionName, roomName);
        float heatWaveIntensity = HeatWaveWeatherRuntime.TryEvaluate(room, out float h)
            ? Mathf.Clamp01(h)
            : 0f;
        float intenseHeatIntensity = IntenseHeatWeatherRuntime.TryEvaluate(room, out float i)
            ? Mathf.Clamp01(i)
            : 0f;

        // Do not clamp the final weather-adjusted value. The explicit purpose of these
        // additive weather bonuses is to allow RoomHeat to exceed the authored range.
        return authored +
               heatWaveIntensity * HeatWaveRoomHeatBonus +
               intenseHeatIntensity * IntenseHeatRoomHeatBonus;
    }

    internal static float ClampHeat(float value)
    {
        // This remains the authored-data clamp. Weather bonuses are added after it.
        return Mathf.Clamp(value, MinimumHeat, MaximumHeat);
    }

    private static string InferRegionFromRoomName(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return string.Empty;
        }

        int separator = roomName.IndexOf('_');
        if (separator <= 0)
        {
            return string.Empty;
        }

        return roomName.Substring(0, separator);
    }
}
