using System;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored room environmental heat baseline.
///
/// RoomHeat is not a target that automatically heats the player. The thermal model
/// only uses it as the lower baseline for room cooling: a body node above RoomHeat
/// can dissipate heat toward it, while a body node at or below RoomHeat receives no
/// room-driven temperature change.
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
