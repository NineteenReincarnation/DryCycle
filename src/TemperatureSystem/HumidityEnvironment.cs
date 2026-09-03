using System.Runtime.CompilerServices;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Room/local humidity query plus hydration and thermal correction rules.
///
/// Humidity is signed in [-1,1]: -1 = extremely dry, 0 = neutral,
/// +1 = extremely humid. TemperatureSets supplies the authored room baseline.
/// HeatWave and IntenseHeat may gradually dry the runtime room humidity without
/// modifying that authored value. A unified Environment Zone still carries an
/// absolute local Humidity value; when a sample is inside one or more zones, the
/// overlapping zone values are averaged. With no local zone, the weather-adjusted
/// room humidity is used.
/// </summary>
internal static class HumidityEnvironment
{
    internal const float CoolingHeatStressThreshold = 0.25f;
    internal const float MaximumDryCoolingBonus = 0.25f;
    internal const float MaximumHumidCoolingPenalty = 0.55f;

    // Weather humidity targets at full scheduled intensity.
    internal const float HeatWaveHumidityTarget = -0.3f;
    internal const float IntenseHeatHumidityTarget = -1f;

    // Signed-humidity units changed per real-time second at the normal 40 Hz update.
    internal const float HeatWaveDryingRatePerSecond = 0.04f;
    internal const float IntenseHeatDryingRatePerSecond = 0.08f;
    internal const float HumidityRecoveryRatePerSecond = 0.025f;

    private const float SimulationTicksPerSecond = 40f;
    private const float TickSeconds = 1f / SimulationTicksPerSecond;
    private const float WeatherEpsilon = 0.0001f;

    private sealed class RoomHumidityState
    {
        internal float CurrentHumidity;
        internal bool Initialized;
    }

    private static ConditionalWeakTable<Room, RoomHumidityState> _roomStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Update -= Room_Update;
        _roomStates = new ConditionalWeakTable<Room, RoomHumidityState>();
        _enabled = false;
    }

    internal static float GetRoomHumidity(Room room)
    {
        float authoredHumidity = GetAuthoredRoomHumidity(room);
        if (!_enabled || room == null)
        {
            return authoredHumidity;
        }

        RoomHumidityState state = _roomStates.GetOrCreateValue(room);
        EnsureInitialized(state, authoredHumidity);
        return RoomEnvironmentProfile.ClampSigned(state.CurrentHumidity);
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

    /// <summary>
    /// Humidity changes room-cooling efficiency only when BodyHeat is above the
    /// heat-stress threshold. At BodyHeat == 1, extreme dry air gives x1.25 room
    /// cooling while extreme humidity gives x0.45. At or below BodyHeat 0.25 the
    /// multiplier is exactly x1 so normal low-heat room exchange is unchanged.
    /// </summary>
    internal static float GetBodyHeatCoolingMultiplier(float bodyHeat, float humidity)
    {
        float heatStressRange = PlayerThermalModel.MaximumBodyHeat - CoolingHeatStressThreshold;
        float heatStress = heatStressRange > 0f
            ? Mathf.Clamp01((bodyHeat - CoolingHeatStressThreshold) / heatStressRange)
            : bodyHeat > CoolingHeatStressThreshold ? 1f : 0f;

        if (heatStress <= 0f)
        {
            return 1f;
        }

        float h = RoomEnvironmentProfile.ClampSigned(humidity);
        return h < 0f
            ? 1f + (-h * MaximumDryCoolingBonus * heatStress)
            : 1f - (h * MaximumHumidCoolingPenalty * heatStress);
    }

    internal static float GetBodyHeatCoolingMultiplier(
        Player player,
        int bodyIndex,
        float bodyHeat)
    {
        return GetBodyHeatCoolingMultiplier(
            bodyHeat,
            GetEffectiveHumidity(player, bodyIndex));
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        if (_enabled && self != null)
        {
            UpdateWeatherHumidity(self, TickSeconds);
        }

        orig(self);
    }

    private static void UpdateWeatherHumidity(Room room, float deltaTime)
    {
        float authoredHumidity = GetAuthoredRoomHumidity(room);
        RoomHumidityState state = _roomStates.GetOrCreateValue(room);
        EnsureInitialized(state, authoredHumidity);

        float heatWaveIntensity = Mathf.Clamp01(
            HeatWaveWeatherRuntime.GetAmbientHeatInfluence(room));
        float intenseHeatIntensity = Mathf.Clamp01(
            IntenseHeatWeatherRuntime.GetAmbientHeatInfluence(room));

        float heatWaveFloor = Mathf.Min(authoredHumidity, HeatWaveHumidityTarget);
        float heatWaveTarget = Mathf.Lerp(
            authoredHumidity,
            heatWaveFloor,
            heatWaveIntensity);

        float intenseHeatTarget = Mathf.Lerp(
            authoredHumidity,
            IntenseHeatHumidityTarget,
            intenseHeatIntensity);

        // Weather can only make the authored room climate drier, never wetter.
        float targetHumidity = Mathf.Min(
            authoredHumidity,
            Mathf.Min(heatWaveTarget, intenseHeatTarget));
        targetHumidity = RoomEnvironmentProfile.ClampSigned(targetHumidity);

        float currentHumidity = state.CurrentHumidity;
        if (Mathf.Abs(currentHumidity - targetHumidity) <= WeatherEpsilon)
        {
            state.CurrentHumidity = targetHumidity;
            return;
        }

        float ratePerSecond;
        if (currentHumidity > targetHumidity)
        {
            // Whichever weather currently demands the drier target controls the
            // drying rate. IntenseHeat therefore takes over naturally as its
            // intensity becomes strong enough to undercut HeatWave's target.
            bool intenseHeatControls =
                intenseHeatIntensity > WeatherEpsilon &&
                intenseHeatTarget <= heatWaveTarget + WeatherEpsilon;

            ratePerSecond = intenseHeatControls
                ? IntenseHeatDryingRatePerSecond
                : HeatWaveDryingRatePerSecond;
        }
        else
        {
            // Fade-out and post-weather recovery deliberately use the slower common
            // recovery rate, preserving residual dryness after the heat has passed.
            ratePerSecond = HumidityRecoveryRatePerSecond;
        }

        state.CurrentHumidity = Mathf.MoveTowards(
            currentHumidity,
            targetHumidity,
            Mathf.Max(0f, ratePerSecond * deltaTime));
        state.CurrentHumidity = RoomEnvironmentProfile.ClampSigned(state.CurrentHumidity);
    }

    private static float GetAuthoredRoomHumidity(Room room)
    {
        GetRoomNames(room, out string regionName, out string roomName);
        return RoomEnvironmentProfile.ClampSigned(
            TemperatureSetsLoader.GetHumidity(regionName, roomName));
    }

    private static void EnsureInitialized(RoomHumidityState state, float authoredHumidity)
    {
        if (state.Initialized)
        {
            return;
        }

        state.CurrentHumidity = RoomEnvironmentProfile.ClampSigned(authoredHumidity);
        state.Initialized = true;
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
