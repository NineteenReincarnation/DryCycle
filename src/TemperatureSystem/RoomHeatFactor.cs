using System;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Temperature influence: authored room base heat.
///
/// This is intentionally only one factor. It does not yet change player stats,
/// rendering or hydration; later temperature consumers can combine this value with
/// additional factors without changing the room-data loader.
/// </summary>
internal static class RoomHeatFactor
{
    internal const float MinimumHeat = -1f;
    internal const float MaximumHeat = 1f;
    internal const float DefaultHeat = 0f;

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

        return TemperatureSetsLoader.GetRoomHeat(regionName, roomName);
    }

    internal static float ClampHeat(float value)
    {
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
