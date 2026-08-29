using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Solar-environment query layer. This deliberately stops at EffectiveSunlight;
/// it does not yet add heat to BodyHeat or alter hydration loss.
/// </summary>
internal static class SolarEnvironment
{
    internal static float GetSunlightIntensity(Room room)
    {
        GetRoomNames(room, out string regionName, out string roomName);
        return TemperatureSetsLoader.GetSunlightIntensity(regionName, roomName);
    }

    internal static float GetRoomShade(Room room)
    {
        GetRoomNames(room, out string regionName, out string roomName);
        return TemperatureSetsLoader.GetRoomShade(regionName, roomName);
    }

    internal static float GetLocalShade(Player player)
    {
        if (player?.room?.roomSettings?.placedObjects == null)
        {
            return 0f;
        }

        Vector2 samplePoint = GetPlayerBodyCenter(player);
        float remainingTransmission = 1f;

        for (int i = 0; i < player.room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placed = player.room.roomSettings.placedObjects[i];
            if (placed == null ||
                !placed.active ||
                placed.type != SolarShadeZoneHooks.PlacedType ||
                placed.data is not SolarShadeZoneData data ||
                data.Vertices.Count < 3 ||
                data.Shade <= 0f)
            {
                continue;
            }

            if (!ContainsWorldPoint(placed, data, samplePoint))
            {
                continue;
            }

            // Each shade layer removes a fraction of the sunlight that remains after
            // the previous layers. This keeps overlapping zones bounded and avoids
            // simple-addition blowups.
            remainingTransmission *= 1f - RoomEnvironmentProfile.ClampUnit(data.Shade);

            if (remainingTransmission <= 0.00001f)
            {
                return 1f;
            }
        }

        return RoomEnvironmentProfile.ClampUnit(1f - remainingTransmission);
    }

    internal static float GetEffectiveSunlight(Player player)
    {
        if (player?.room == null)
        {
            return 0f;
        }

        return CalculateEffectiveSunlight(
            GetSunlightIntensity(player.room),
            GetRoomShade(player.room),
            GetLocalShade(player));
    }

    internal static float CalculateEffectiveSunlight(
        float sunlightIntensity,
        float roomShade,
        float localShade)
    {
        float sunlight = RoomEnvironmentProfile.ClampUnit(sunlightIntensity);
        float roomTransmission = 1f - RoomEnvironmentProfile.ClampUnit(roomShade);
        float localTransmission = 1f - RoomEnvironmentProfile.ClampUnit(localShade);

        return RoomEnvironmentProfile.ClampUnit(
            sunlight * roomTransmission * localTransmission);
    }

    private static Vector2 GetPlayerBodyCenter(Player player)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return player?.mainBodyChunk?.pos ?? Vector2.zero;
        }

        if (player.bodyChunks.Length == 1)
        {
            return player.bodyChunks[0].pos;
        }

        return (player.bodyChunks[0].pos + player.bodyChunks[1].pos) * 0.5f;
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
