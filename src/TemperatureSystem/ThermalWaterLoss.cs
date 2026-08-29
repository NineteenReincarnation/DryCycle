using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Extra hydration loss produced by the temperature system.
///
/// These values are WV/second and are intentionally additive to the existing
/// SlugBase-compatible base WaterLossRate. Solar exposure and stored body heat are
/// separate loss sources so shading can reduce direct solar evaporation immediately
/// while accumulated BodyHeat can continue to cost water after leaving the sun.
/// </summary>
internal static class ThermalWaterLoss
{
    internal const float MaxSolarWaterLossPerSecond = 0.5f;
    internal const float SolarWaterLossExponent = 1.4f;

    internal const float BodyHeatWaterLossThreshold = 0.25f;
    internal const float BodyHeatWaterLossExponent = 2f;
    internal const float MaxBodyHeatWaterLossPerSecond = 1f;

    internal static float GetSolarExposure(Player player)
    {
        if (player?.room == null || player.inShortcut)
        {
            return 0f;
        }

        float sunlight0 = SolarEnvironment.GetEffectiveSunlight(player, 0);
        float sunlight1 = SolarEnvironment.GetEffectiveSunlight(player, 1);
        return RoomEnvironmentProfile.ClampUnit((sunlight0 + sunlight1) * 0.5f);
    }

    internal static float GetSolarWaterLossRate(Player player)
    {
        float exposure = GetSolarExposure(player);
        if (exposure <= 0f)
        {
            return 0f;
        }

        return MaxSolarWaterLossPerSecond *
               Mathf.Pow(exposure, SolarWaterLossExponent);
    }

    internal static float GetBodyHeatWaterLossRate(Player player)
    {
        if (player == null)
        {
            return 0f;
        }

        float loss0 = CalculateBodyNodeWaterLossRate(
            PlayerThermalModel.GetBodyHeat(player, 0));
        float loss1 = CalculateBodyNodeWaterLossRate(
            PlayerThermalModel.GetBodyHeat(player, 1));

        return (loss0 + loss1) * 0.5f;
    }

    internal static float GetTotalExtraWaterLossRate(Player player)
    {
        return GetSolarWaterLossRate(player) + GetBodyHeatWaterLossRate(player);
    }

    internal static float CalculateBodyNodeWaterLossRate(float bodyHeat)
    {
        if (bodyHeat <= BodyHeatWaterLossThreshold)
        {
            return 0f;
        }

        float range = PlayerThermalModel.MaximumBodyHeat - BodyHeatWaterLossThreshold;
        if (range <= 0f)
        {
            return 0f;
        }

        float normalized = Mathf.Clamp01(
            (bodyHeat - BodyHeatWaterLossThreshold) / range);

        return MaxBodyHeatWaterLossPerSecond *
               Mathf.Pow(normalized, BodyHeatWaterLossExponent);
    }
}
