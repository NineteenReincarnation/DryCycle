using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_EnvironmentExposureSample
{
    internal readonly float Exposure;
    internal readonly float RoofShielding;
    internal readonly float SideShielding;
    internal readonly float Enclosure;
    internal readonly float Shade;
    internal readonly float RainExposure;
    internal readonly float VisibilityConfidence;

    internal DB_EnvironmentExposureSample(
        float exposure,
        float roofShielding,
        float sideShielding,
        float enclosure,
        float shade,
        float rainExposure,
        float visibilityConfidence)
    {
        Exposure = Mathf.Clamp01(exposure);
        RoofShielding = Mathf.Clamp01(roofShielding);
        SideShielding = Mathf.Clamp01(sideShielding);
        Enclosure = Mathf.Clamp01(enclosure);
        Shade = Mathf.Clamp01(shade);
        RainExposure = Mathf.Clamp01(rainExposure);
        VisibilityConfidence = Mathf.Clamp01(visibilityConfidence);
    }
}

internal sealed class DB_ShelterAnchor
{
    internal readonly int Id;
    internal readonly IntVector2 Tile;
    internal readonly Vector2 Position;
    internal readonly DB_EnvironmentExposureSample Exposure;
    internal readonly bool RoostCompatible;
    internal readonly bool NearHive;
    internal float Crowding;

    internal DB_ShelterAnchor(
        int id,
        IntVector2 tile,
        Vector2 position,
        DB_EnvironmentExposureSample exposure,
        bool roostCompatible,
        bool nearHive)
    {
        Id = id;
        Tile = tile;
        Position = position;
        Exposure = exposure;
        RoostCompatible = roostCompatible;
        NearHive = nearHive;
    }
}
