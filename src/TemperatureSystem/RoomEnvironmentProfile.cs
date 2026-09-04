using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored room-level environment values loaded from TemperatureSets.json.
///
/// RoomHeat remains the base thermal environment. SunlightIntensity and RoomShade
/// describe the solar-radiation branch. Humidity is a signed room baseline in
/// [-1,1], where 0 is neutral, -1 is extremely dry and +1 is extremely humid.
/// </summary>
internal sealed class RoomEnvironmentProfile
{
    internal const float DefaultSunlightIntensity = 0f;
    internal const float DefaultRoomShade = 0f;
    internal const float DefaultHumidity = 0f;

    internal float RoomHeat;
    internal float SunlightIntensity;
    internal float RoomShade;
    internal float Humidity;

    internal RoomEnvironmentProfile(
        float roomHeat = RoomHeatFactor.DefaultHeat,
        float sunlightIntensity = DefaultSunlightIntensity,
        float roomShade = DefaultRoomShade,
        float humidity = DefaultHumidity)
    {
        RoomHeat = RoomHeatFactor.ClampHeat(roomHeat);
        SunlightIntensity = ClampUnit(sunlightIntensity);
        RoomShade = ClampUnit(roomShade);
        Humidity = ClampSigned(humidity);
    }

    internal static float ClampUnit(float value)
    {
        return Mathf.Clamp01(value);
    }

    internal static float ClampSigned(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }
}
