using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Runtime thermal state for a player. The two body-heat values correspond to the
/// player's two primary body chunks so local sunlight, water and later heat sources
/// can affect them independently before internal heat transfer equalizes them.
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
/// node only loses heat to the room while BodyHeat is above RoomHeat. Sunlight adds
/// heat directly to each body node according to that chunk's EffectiveSunlight.
/// Internal body-to-body transfer remains conservative and smooths local differences.
/// </summary>
internal static class PlayerThermalModel
{
    internal const float MinimumBodyHeat = 0f;
    internal const float MaximumBodyHeat = 1f;

    // Agreed room-cooling model:
    // CoolingRate = 0.0175 * max(0, BodyHeat - RoomHeat)^1.25
    internal const float BaseCoolingCoefficient = 0.0175f;
    internal const float CoolingExponent = 1.25f;

    // Agreed solar heat input at EffectiveSunlight == 1.
    internal const float BaseSolarHeatingRatePerSecond = 0.01f;

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

        // Room exchange and direct solar heating stop while travelling through a
        // shortcut. Stored body heat and internal transfer continue to exist.
        if (self.room != null && !self.inShortcut)
        {
            float effectiveSunlight0 = SolarEnvironment.GetEffectiveSunlight(self, 0);
            float effectiveSunlight1 = SolarEnvironment.GetEffectiveSunlight(self, 1);

            ApplySolarHeating(
                state,
                effectiveSunlight0,
                effectiveSunlight1,
                TickSeconds);

            ApplyRoomCooling(
                state,
                RoomHeatFactor.GetRoomHeat(self.room),
                TickSeconds);
        }

        ApplyInternalTransfer(state, TickSeconds);
        ClampBodyHeat(state);
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

    private static void ApplyRoomCooling(
        PlayerThermalState state,
        float roomHeat,
        float deltaTime)
    {
        state.BodyHeat0 -= CalculateRoomCoolingRate(state.BodyHeat0, roomHeat) * deltaTime;
        state.BodyHeat1 -= CalculateRoomCoolingRate(state.BodyHeat1, roomHeat) * deltaTime;
    }

    internal static float CalculateRoomCoolingRate(float bodyHeat, float roomHeat)
    {
        float difference = Mathf.Max(0f, bodyHeat - roomHeat);
        if (difference <= 0f)
        {
            return 0f;
        }

        return BaseCoolingCoefficient * Mathf.Pow(difference, CoolingExponent);
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

    private static void ClampBodyHeat(PlayerThermalState state)
    {
        state.BodyHeat0 = Mathf.Clamp(state.BodyHeat0, MinimumBodyHeat, MaximumBodyHeat);
        state.BodyHeat1 = Mathf.Clamp(state.BodyHeat1, MinimumBodyHeat, MaximumBodyHeat);
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
