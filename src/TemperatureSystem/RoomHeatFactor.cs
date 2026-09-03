using System;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored room environmental heat baseline.
///
/// RoomHeat is not a target that automatically heats the player. The thermal model
/// only uses it as the lower baseline for room cooling: a body node above RoomHeat can
/// dissipate heat toward it, while a body node at or below RoomHeat receives no
/// room-driven temperature change.
///
/// Scheduled heat weather may raise this baseline from deterministic schedule intensity
/// only. Visual shader state is deliberately excluded so rendering behavior can never
/// change gameplay temperature.
/// </summary>
internal static class RoomHeatFactor
{
    internal const float MinimumHeat = -1f;
    internal const float MaximumHeat = 1f;
    internal const float DefaultHeat = 0f;
    internal const float MaximumHeatWaveAmbientBaseline = 0.86f;
    internal const float MaximumIntenseHeatAmbientBaseline = 0.97f;

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
        float heatWave = CalculateHeatWaveBaseline(room);
        float intenseHeat = CalculateIntenseHeatBaseline(room);
        return ClampHeat(Mathf.Max(authored, Mathf.Max(heatWave, intenseHeat)));
    }

    internal static float ClampHeat(float value)
    {
        return Mathf.Clamp(value, MinimumHeat, MaximumHeat);
    }

    private static float CalculateHeatWaveBaseline(Room room)
    {
        float intensity = HeatWaveWeatherRuntime.GetAmbientHeatInfluence(room);
        if (intensity <= 0f)
        {
            return DefaultHeat;
        }

        float t = Mathf.Clamp01(intensity);
        t = t * t * (3f - 2f * t);
        return MaximumHeatWaveAmbientBaseline * t;
    }

    private static float CalculateIntenseHeatBaseline(Room room)
    {
        float intensity = IntenseHeatWeatherRuntime.GetAmbientHeatInfluence(room);
        if (intensity <= 0f)
        {
            return DefaultHeat;
        }

        float t = Mathf.Clamp01(intensity);
        t = t * t * (3f - 2f * t);
        return MaximumIntenseHeatAmbientBaseline * t;
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
