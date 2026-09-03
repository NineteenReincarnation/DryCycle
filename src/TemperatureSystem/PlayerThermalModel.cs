using System.Runtime.CompilerServices;
using DryCycle.Weather.HeatWave;
using DryCycle.Weather.IntenseHeat;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Runtime thermal state for a player. The two body-heat values correspond to the
/// player's two primary body chunks so local sunlight, water and heat sources can
/// affect them independently before internal heat transfer equalizes them.
/// </summary>
internal sealed class PlayerThermalState
{
    internal float BodyHeat0 = 0f;
    internal float BodyHeat1 = 0f;
    internal float InternalHeatFlow = 0f;
}

/// <summary>
/// Dynamic two-node body-heat model.
///
/// RoomHeat is an environmental cooling baseline, not a temperature target. A body
/// node only loses heat to the room while BodyHeat is above its locally sampled
/// RoomHeat. Sunlight and active heat weather add heat directly. Humidity and Wetness
/// independently modify room-cooling efficiency. Internal body-to-body transfer remains
/// conservative.
/// </summary>
internal static class PlayerThermalModel
{
    internal const float MinimumBodyHeat = 0f;

    // Nominal normalization point used by existing heat-stress formulas. Runtime
    // BodyHeat is deliberately allowed to exceed this value.
    internal const float MaximumBodyHeat = 1f;

    // Agreed room-cooling model before humidity/wetness correction:
    // CoolingRate = 0.0175 * max(0, BodyHeat - RoomHeat)^1.25
    internal const float BaseCoolingCoefficient = 0.0175f;
    internal const float CoolingExponent = 1.25f;

    // Agreed solar heat input at EffectiveSunlight == 1.
    internal const float BaseSolarHeatingRatePerSecond = 0.01f;

    // Explicit weather-driven BodyHeat gain. Current schedule intensity participates
    // continuously, including fade-in and fade-out.
    internal const float HeatWaveBodyHeatGainPerSecond = 0.02f;
    internal const float IntenseHeatBodyHeatGainPerSecond = 0.05f;

    internal const float InternalDifferenceSlowModeHalfLifeSeconds = 8f;
    internal const float InternalHeatFlowHalfLifeSeconds = 1.5f;

    // With the 1.5-second heat-flow response above, this conductance gives the slow
    // decay mode of the two-node system an approximately eight-second half-life.
    internal const float InternalConductancePerSecond = 0.03519888f;

    private const float SimulationTicksPerSecond = 40f;
    private const float TickSeconds = 1f / SimulationTicksPerSecond;

    private static ConditionalWeakTable<Player, PlayerThermalState> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        _states = new ConditionalWeakTable<Player, PlayerThermalState>();
    }

    internal static PlayerThermalState For(Player player)
    {
        return player == null ? null : _states.GetOrCreateValue(player);
    }

    internal static float GetBodyHeat(Player player, int bodyIndex)
    {
        PlayerThermalState state = For(player);
        if (state == null)
        {
            return 0f;
        }

        return bodyIndex <= 0 ? state.BodyHeat0 : state.BodyHeat1;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!_enabled || self == null)
        {
            return;
        }

        PlayerThermalState state = _states.GetOrCreateValue(self);

        // Room exchange and active environmental heating stop while travelling through
        // a shortcut. Stored body heat and internal transfer continue to exist.
        if (self.room != null && !self.inShortcut)
        {
            float effectiveSunlight0 = SolarEnvironment.GetEffectiveSunlight(self, 0);
            float effectiveSunlight1 = SolarEnvironment.GetEffectiveSunlight(self, 1);
            float roomHeat0 = RoomHeatFactor.GetEffectiveRoomHeat(self, 0);
            float roomHeat1 = RoomHeatFactor.GetEffectiveRoomHeat(self, 1);
            float humidity0 = HumidityEnvironment.GetEffectiveHumidity(self, 0);
            float humidity1 = HumidityEnvironment.GetEffectiveHumidity(self, 1);
            float wetness0 = PlayerWetnessModel.GetWetness(self, 0);
            float wetness1 = PlayerWetnessModel.GetWetness(self, 1);

            ApplySolarHeating(
                state,
                effectiveSunlight0,
                effectiveSunlight1,
                TickSeconds);

            ApplyWeatherHeating(state, self.room, TickSeconds);

            ApplyRoomCooling(
                state,
                roomHeat0,
                roomHeat1,
                humidity0,
                humidity1,
                wetness0,
                wetness1,
                TickSeconds);
        }

        ApplyInternalTransfer(state, TickSeconds);
        ClampMinimumBodyHeat(state);
    }

    private static void ApplySolarHeating(
        PlayerThermalState state,
        float effectiveSunlight0,
        float effectiveSunlight1,
        float deltaTime)
    {
        state.BodyHeat0 +=
            RoomEnvironmentProfile.ClampUnit(effectiveSunlight0) *
            BaseSolarHeatingRatePerSecond *
            deltaTime;

        state.BodyHeat1 +=
            RoomEnvironmentProfile.ClampUnit(effectiveSunlight1) *
            BaseSolarHeatingRatePerSecond *
            deltaTime;
    }

    private static void ApplyWeatherHeating(
        PlayerThermalState state,
        Room room,
        float deltaTime)
    {
        float heatWaveIntensity = HeatWaveWeatherRuntime.TryEvaluate(room, out float h)
            ? Mathf.Clamp01(h)
            : 0f;
        float intenseHeatIntensity = IntenseHeatWeatherRuntime.TryEvaluate(room, out float i)
            ? Mathf.Clamp01(i)
            : 0f;

        float heatingRate =
            heatWaveIntensity * HeatWaveBodyHeatGainPerSecond +
            intenseHeatIntensity * IntenseHeatBodyHeatGainPerSecond;

        if (heatingRate <= 0f)
        {
            return;
        }

        float gainedHeat = heatingRate * deltaTime;
        state.BodyHeat0 += gainedHeat;
        state.BodyHeat1 += gainedHeat;
    }

    private static void ApplyRoomCooling(
        PlayerThermalState state,
        float roomHeat0,
        float roomHeat1,
        float humidity0,
        float humidity1,
        float wetness0,
        float wetness1,
        float deltaTime)
    {
        state.BodyHeat0 -= CalculateRoomCoolingRate(
            state.BodyHeat0,
            roomHeat0,
            humidity0,
            wetness0) * deltaTime;

        state.BodyHeat1 -= CalculateRoomCoolingRate(
            state.BodyHeat1,
            roomHeat1,
            humidity1,
            wetness1) * deltaTime;
    }

    /// <summary>
    /// Neutral-humidity and neutral-wetness room cooling. Kept as the base-rate query
    /// for callers that explicitly want the unmodified environmental rate.
    /// </summary>
    internal static float CalculateRoomCoolingRate(float bodyHeat, float roomHeat)
    {
        float difference = Mathf.Max(0f, bodyHeat - roomHeat);
        if (difference <= 0f)
        {
            return 0f;
        }

        return BaseCoolingCoefficient * Mathf.Pow(difference, CoolingExponent);
    }

    /// <summary>
    /// Room-cooling rate after humidity correction only.
    /// </summary>
    internal static float CalculateRoomCoolingRate(
        float bodyHeat,
        float roomHeat,
        float humidity)
    {
        float baseRate = CalculateRoomCoolingRate(bodyHeat, roomHeat);
        if (baseRate <= 0f)
        {
            return 0f;
        }

        return baseRate * HumidityEnvironment.GetBodyHeatCoolingMultiplier(
            bodyHeat,
            humidity);
    }

    /// <summary>
    /// Final room-cooling rate after independent humidity and wetness multipliers.
    /// Wetness does not create a direct WV-loss branch; it changes BodyHeat cooling,
    /// which can then indirectly change BodyHeat-driven WV loss.
    /// </summary>
    internal static float CalculateRoomCoolingRate(
        float bodyHeat,
        float roomHeat,
        float humidity,
        float wetness)
    {
        float humidityAdjustedRate = CalculateRoomCoolingRate(
            bodyHeat,
            roomHeat,
            humidity);
        if (humidityAdjustedRate <= 0f)
        {
            return 0f;
        }

        return humidityAdjustedRate *
               PlayerWetnessModel.GetBodyHeatCoolingMultiplier(wetness);
    }

    private static void ApplyInternalTransfer(
        PlayerThermalState state,
        float deltaTime)
    {
        // Positive difference means node 0 is hotter, so positive flow transfers
        // heat from BodyHeat0 to BodyHeat1. Negative values automatically reverse it.
        float difference = state.BodyHeat0 - state.BodyHeat1;
        float targetFlow = difference * InternalConductancePerSecond;

        float flowBlend = HalfLifeBlend(deltaTime, InternalHeatFlowHalfLifeSeconds);
        state.InternalHeatFlow +=
            (targetFlow - state.InternalHeatFlow) * flowBlend;

        // Conservative transfer: whatever one node loses the other receives.
        float transferredHeat = state.InternalHeatFlow * deltaTime;
        state.BodyHeat0 -= transferredHeat;
        state.BodyHeat1 += transferredHeat;
    }

    private static void ClampMinimumBodyHeat(PlayerThermalState state)
    {
        // There is intentionally no upper clamp. Heat weather can push BodyHeat above
        // the nominal 1.0 reference level, after which room cooling still acts normally.
        state.BodyHeat0 = Mathf.Max(MinimumBodyHeat, state.BodyHeat0);
        state.BodyHeat1 = Mathf.Max(MinimumBodyHeat, state.BodyHeat1);
    }

    private static float HalfLifeBlend(float deltaTime, float halfLifeSeconds)
    {
        if (deltaTime <= 0f)
        {
            return 0f;
        }

        if (halfLifeSeconds <= 0f)
        {
            return 1f;
        }

        return 1f - Mathf.Pow(0.5f, deltaTime / halfLifeSeconds);
    }
}
