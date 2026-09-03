using System;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored/local environmental heat baseline plus explicit heat-weather bonuses.
/// Local Environment Zone RoomHeat is an absolute authored override. HeatWave and
/// IntenseHeat bonuses are applied afterwards so weather remains global and can push
/// both room and local heat above the nominal authored maximum of 1.
/// </summary>
internal static class RoomHeatFactor
{
    internal const float MinimumHeat = -1f;
    internal const float MaximumHeat = 1f;
    internal const float DefaultHeat = 0f;

    internal const float HeatWaveRoomHeatBonus = 0.3f;
    internal const float IntenseHeatRoomHeatBonus = 0.7f;

    internal static float GetAuthoredRoomHeat(Room room)
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

        return ClampHeat(TemperatureSetsLoader.GetRoomHeat(regionName, roomName));
    }

    internal static float GetRoomHeat(Room room)
    {
        return GetAuthoredRoomHeat(room) + GetWeatherRoomHeatBonus(room);
    }

    internal static float GetEffectiveRoomHeat(Player player, int bodyIndex)
    {
        if (player?.room == null)
        {
            return DefaultHeat;
        }

        return GetEffectiveRoomHeatAt(
            player.room,
            GetBodyChunkSamplePoint(player, bodyIndex));
    }

    internal static float GetEffectiveRoomHeatAt(Room room, Vector2 samplePoint)
    {
        if (room == null)
        {
            return DefaultHeat;
        }

        float authored = GetAuthoredRoomHeat(room);
        float localSum = 0f;
        int localCount = 0;

        if (room.roomSettings?.placedObjects != null)
        {
            for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
            {
                PlacedObject placed = room.roomSettings.placedObjects[i];
                if (placed == null ||
                    !placed.active ||
                    !SolarShadeZoneHooks.IsEnvironmentZoneType(placed.type) ||
                    placed.data is not SolarShadeZoneData data ||
                    !data.HasRoomHeat ||
                    data.Vertices.Count < 3)
                {
                    continue;
                }

                if (!ContainsWorldPoint(placed, data, samplePoint))
                {
                    continue;
                }

                localSum += data.RoomHeat;
                localCount++;
            }
        }

        float localOrRoom = localCount > 0
            ? ClampHeat(localSum / localCount)
            : authored;

        // Weather is deliberately added after the local authored value. This keeps
        // H +0.3 and I +0.7 active everywhere, including inside Environment Zones.
        return localOrRoom + GetWeatherRoomHeatBonus(room);
    }

    internal static float ClampHeat(float value)
    {
        return Mathf.Clamp(value, MinimumHeat, MaximumHeat);
    }

    private static float GetWeatherRoomHeatBonus(Room room)
    {
        float heatWaveIntensity = HeatWaveWeatherRuntime.TryEvaluate(room, out float h)
            ? Mathf.Clamp01(h)
            : 0f;
        float intenseHeatIntensity = IntenseHeatWeatherRuntime.TryEvaluate(room, out float i)
            ? Mathf.Clamp01(i)
            : 0f;

        return heatWaveIntensity * HeatWaveRoomHeatBonus +
               intenseHeatIntensity * IntenseHeatRoomHeatBonus;
    }

    private static Vector2 GetBodyChunkSamplePoint(Player player, int bodyIndex)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return player?.mainBodyChunk?.pos ?? Vector2.zero;
        }

        int index = Mathf.Clamp(bodyIndex, 0, player.bodyChunks.Length - 1);
        return player.bodyChunks[index]?.pos ?? player.mainBodyChunk?.pos ?? Vector2.zero;
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
