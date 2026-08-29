using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Runtime thermal state for a player. The two body-heat values correspond to the
/// player's two primary body chunks so later local influences (sun, water, etc.) can
/// affect them independently before internal heat transfer equalizes the difference.
/// </summary>
internal sealed class PlayerThermalState
{
    // New players always start thermally neutral.
    internal float BodyHeat0 = 0f;
    internal float BodyHeat1 = 0f;
    internal float InternalHeatFlow = 0f;
}

/// <summary>
/// Minimal dynamic body-temperature trunk.
///
/// 1) Each body heat approaches the authored RoomHeat with a 20-second half-life.
/// 2) A temperature difference between the two body nodes creates an internal heat
///    flow. That heat flow itself responds with a 1.5-second half-life and transfers
///    heat conservatively from the hotter node to the colder node.
///
/// This intentionally contains no sunlight, water, shade, humidity or hydration
/// consequences yet. Those later systems can modify the two BodyHeat values without
/// changing this core room-exchange/internal-transfer model.
/// </summary>
internal static class PlayerThermalModel
{
    internal const float RoomHeatHalfLifeSeconds = 20f;
    internal const float InternalDifferenceSlowModeHalfLifeSeconds = 8f;
    internal const float InternalHeatFlowHalfLifeSeconds = 1.5f;

    // With the 1.5-second heat-flow response above, this conductance gives the slow
    // decay mode of the two-node system an approximately eight-second half-life.
    // A newly created heat flow takes additional time to build from zero, by design.
    internal const float InternalConductancePerSecond = 0.03519888f;

    // Rain World's gameplay simulation runs at 40 ticks per second. Use a fixed
    // simulation step so thermal timing does not depend on render frame rate.
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

        // Runtime-only state intentionally does not persist between player objects.
        // Re-enabling starts every newly observed player from BodyHeat 0 / 0.
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

        // Room exchange pauses when there is no realized room or while travelling
        // through a shortcut. Internal body-to-body transfer continues during that
        // time so an existing temperature difference still relaxes naturally.
        if (self.room != null && !self.inShortcut)
        {
            ApplyRoomExchange(state, RoomHeatFactor.GetRoomHeat(self.room), TickSeconds);
        }

        ApplyInternalTransfer(state, TickSeconds);
    }

    private static void ApplyRoomExchange(
        PlayerThermalState state,
        float roomHeat,
        float deltaTime)
    {
        // alpha = 1 - 2^(-dt / 20)
        // Every 20 seconds, the remaining difference to RoomHeat is halved.
        float blend = HalfLifeBlend(deltaTime, RoomHeatHalfLifeSeconds);

        state.BodyHeat0 += (roomHeat - state.BodyHeat0) * blend;
        state.BodyHeat1 += (roomHeat - state.BodyHeat1) * blend;
    }

    private static void ApplyInternalTransfer(
        PlayerThermalState state,
        float deltaTime)
    {
        // Positive difference means node 0 is hotter, so positive flow transfers
        // heat from BodyHeat0 to BodyHeat1. Negative values automatically reverse it.
        float difference = state.BodyHeat0 - state.BodyHeat1;
        float targetFlow = difference * InternalConductancePerSecond;

        // beta = 1 - 2^(-dt / 1.5)
        // Internal heat flow has its own response inertia instead of snapping to the
        // target immediately when a body-part temperature difference appears.
        float flowBlend = HalfLifeBlend(deltaTime, InternalHeatFlowHalfLifeSeconds);
        state.InternalHeatFlow +=
            (targetFlow - state.InternalHeatFlow) * flowBlend;

        // Transfer is conservative: whatever node 0 loses, node 1 receives, and
        // vice versa. Internal transfer therefore cannot change total body heat.
        float transferredHeat = state.InternalHeatFlow * deltaTime;
        state.BodyHeat0 -= transferredHeat;
        state.BodyHeat1 += transferredHeat;

        // RoomHeat is currently authored in [-1, 1], so keep the initial thermal
        // trunk normalized to the same range.
        state.BodyHeat0 = RoomHeatFactor.ClampHeat(state.BodyHeat0);
        state.BodyHeat1 = RoomHeatFactor.ClampHeat(state.BodyHeat1);
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
