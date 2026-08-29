using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Authored room-level environment values loaded from TemperatureSets.txt.
///
/// RoomHeat remains the base thermal environment. SunlightIntensity and RoomShade
/// describe only the solar-radiation branch and intentionally do not modify RoomHeat.
/// </summary>
internal sealed class RoomEnvironmentProfile
{
    internal const float DefaultSunlightIntensity = 0f;
    internal const float DefaultRoomShade = 0f;

    internal float RoomHeat;
    internal float SunlightIntensity;
    internal float RoomShade;

    internal RoomEnvironmentProfile(
        float roomHeat = RoomHeatFactor.DefaultHeat,
        float sunlightIntensity = DefaultSunlightIntensity,
        float roomShade = DefaultRoomShade)
    {
        RoomHeat = RoomHeatFactor.ClampHeat(roomHeat);
        SunlightIntensity = ClampUnit(sunlightIntensity);
        RoomShade = ClampUnit(roomShade);
    }

    internal static float ClampUnit(float value)
    {
        return Mathf.Clamp01(value);
    }
}
