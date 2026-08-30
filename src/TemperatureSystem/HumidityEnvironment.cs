using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Room/local humidity query and Base-WV correction layer.
///
/// Humidity is signed in [-1,1]: -1 = extremely dry, 0 = neutral,
/// +1 = extremely humid. The room value is the baseline. A unified Environment
/// Zone carries an absolute local Humidity value; when a sample is inside one or
/// more zones, the overlapping zone values are averaged. With no local zone, the
/// room baseline is used.
/// </summary>
internal static class HumidityEnvironment
{
    internal static float GetRoomHumidity(Room room)
    {
        GetRoomNames(room, out string regionName, out string roomName);
        return TemperatureSetsLoader.GetHumidity(regionName, roomName);
    }

    internal static float GetEffectiveHumidity(Player player)
    {
        if (player?.room == null)
        {
            return RoomEnvironmentProfile.DefaultHumidity;
        }

        if (player.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return GetEffectiveHumidityAt(
                player.room,
                player.mainBodyChunk?.pos ?? Vector2.zero);
        }

        float h0 = GetEffectiveHumidityAt(player.room, player.bodyChunks[0].pos);
        if (player.bodyChunks.Length == 1)
        {
            return h0;
        }

        float h1 = GetEffectiveHumidityAt(player.room, player.bodyChunks[1].pos);
        return RoomEnvironmentProfile.ClampSigned((h0 + h1) * 0.5f);
    }

    internal static float GetEffectiveHumidity(Player player, int bodyIndex)
    {
        if (player?.room == null)
        {
            return RoomEnvironmentProfile.DefaultHumidity;
        }

        if (player.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return GetEffectiveHumidityAt(
                player.room,
                player.mainBodyChunk?.pos ?? Vector2.zero);
        }

        int index = Mathf.Clamp(bodyIndex, 0, player.bodyChunks.Length - 1);
        return GetEffectiveHumidityAt(player.room, player.bodyChunks[index].pos);
    }

    internal static float GetEffectiveHumidityAt(Room room, Vector2 samplePoint)
    {
        float roomHumidity = GetRoomHumidity(room);
        if (room?.roomSettings?.placedObjects == null)
        {
            return roomHumidity;
        }

        float localSum = 0f;
        int localCount = 0;

        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placed = room.roomSettings.placedObjects[i];
            if (placed == null ||
                !placed.active ||
                !SolarShadeZoneHooks.IsEnvironmentZoneType(placed.type) ||
                placed.data is not SolarShadeZoneData data ||
                data.Vertices.Count < 3)
            {
                continue;
            }

            if (!ContainsWorldPoint(placed, data, samplePoint))
            {
                continue;
            }

            localSum += data.Humidity;
            localCount++;
        }

        if (localCount <= 0)
        {
            return roomHumidity;
        }

        return RoomEnvironmentProfile.ClampSigned(localSum / localCount);
    }

    /// <summary>
    /// Humidity modifies only the Base WV loss branch:
    /// -1 => x1.50, 0 => x1.00, +1 => x0.35.
    /// </summary>
    internal static float GetBaseWaterLossMultiplier(float humidity)
    {
        float h = RoomEnvironmentProfile.ClampSigned(humidity);
        return h < 0f
            ? 1f + (-h * 0.5f)
            : 1f - (h * 0.65f);
    }

    internal static float GetBaseWaterLossMultiplier(Player player)
    {
        return GetBaseWaterLossMultiplier(GetEffectiveHumidity(player));
    }

    private static bool ContainsWorldPoint(
        PlacedObject placed,
        SolarShadeZoneData data,
        Vector2 worldPoint)
    {
        Vector2 localPoint = worldPoint - placed.pos;
        int count = data.Vertices.Count;
        bool inside = false;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 a = data.Vertices[j];
            Vector2 b = data.Vertices[i];

            if (PointOnSegment(localPoint, a, b))
            {
                return true;
            }

            bool crosses = (a.y > localPoint.y) != (b.y > localPoint.y);
            if (!crosses)
            {
                continue;
            }

            float edgeX = (b.x - a.x) * (localPoint.y - a.y) /
                          (b.y - a.y) + a.x;
            if (localPoint.x < edgeX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool PointOnSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.sqrMagnitude;
        if (lengthSquared <= 0.000001f)
        {
            return Vector2.SqrMagnitude(point - a) <= 0.0001f;
        }

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
        Vector2 closest = a + ab * t;
        return Vector2.SqrMagnitude(point - closest) <= 0.01f;
    }

    private static void GetRoomNames(Room room, out string regionName, out string roomName)
    {
        regionName = room?.world?.region?.name;
        roomName = room?.abstractRoom?.name;

        if (!string.IsNullOrWhiteSpace(regionName) || string.IsNullOrWhiteSpace(roomName))
        {
            return;
        }

        int separator = roomName.IndexOf('_');
        regionName = separator > 0 ? roomName.Substring(0, separator) : string.Empty;
    }
}
