using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Extra hydration loss produced by the temperature system.
///
/// SolarWaterLoss and BodyHeatWaterLoss are calculated normally first, then receive
/// their own final heat-weather multiplier. Current weather intensity participates
/// continuously, including fade-in and fade-out.
/// </summary>
internal static class ThermalWaterLoss
{
    internal const float MaxSolarWaterLossPerSecond = 0.5f;
    internal const float SolarWaterLossExponent = 1.4f;

    internal const float BodyHeatWaterLossThreshold = 0.25f;
    internal const float BodyHeatWaterLossExponent = 2f;
    internal const float MaxBodyHeatWaterLossPerSecond = 1f;

    // Final weather multipliers. No numeric multipliers were specified yet, so these
    // remain neutral until explicitly tuned.
    internal const float HeatWaveSolarWaterLossFinalMultiplier = 1f;
    internal const float IntenseHeatSolarWaterLossFinalMultiplier = 1f;
    internal const float HeatWaveBodyHeatWaterLossFinalMultiplier = 1f;
    internal const float IntenseHeatBodyHeatWaterLossFinalMultiplier = 1f;

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

        float rawLoss = MaxSolarWaterLossPerSecond *
                        Mathf.Pow(exposure, SolarWaterLossExponent);

        return rawLoss * GetSolarWaterLossFinalMultiplier(player);
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

        float rawLoss = (loss0 + loss1) * 0.5f;
        return rawLoss * GetBodyHeatWaterLossFinalMultiplier(player);
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

    internal static float GetSolarWaterLossFinalMultiplier(Player player)
    {
        if (player?.room == null)
        {
            return 1f;
        }

        GetHeatWeatherIntensities(player.room, out float h, out float i);
        return Mathf.Lerp(1f, HeatWaveSolarWaterLossFinalMultiplier, h) *
               Mathf.Lerp(1f, IntenseHeatSolarWaterLossFinalMultiplier, i);
    }

    internal static float GetBodyHeatWaterLossFinalMultiplier(Player player)
    {
        if (player?.room == null)
        {
            return 1f;
        }

        GetHeatWeatherIntensities(player.room, out float h, out float i);
        return Mathf.Lerp(1f, HeatWaveBodyHeatWaterLossFinalMultiplier, h) *
               Mathf.Lerp(1f, IntenseHeatBodyHeatWaterLossFinalMultiplier, i);
    }

    private static void GetHeatWeatherIntensities(
        Room room,
        out float heatWaveIntensity,
        out float intenseHeatIntensity)
    {
        heatWaveIntensity = HeatWaveWeatherRuntime.TryEvaluate(room, out float h)
            ? Mathf.Clamp01(h)
            : 0f;
        intenseHeatIntensity = IntenseHeatWeatherRuntime.TryEvaluate(room, out float i)
            ? Mathf.Clamp01(i)
            : 0f;
    }
}
