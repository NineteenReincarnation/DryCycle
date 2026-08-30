using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Dynamic surface-wetness state for the player's two primary body chunks.
/// Wetness is signed in [-1,1]: -1 = extremely dry, 0 = neutral, +1 = fully wet.
/// </summary>
internal sealed class PlayerWetnessState
{
    internal float Wetness0;
    internal float Wetness1;
    internal bool Initialized;
}

/// <summary>
/// Per-body-chunk surface-wetness model.
///
/// Water rapidly pushes the contacted chunk toward +1. Outside water, local humidity
/// pulls Wetness toward EffectiveHumidity while local sunlight independently dries the
/// exposed surface. The two body chunks are deliberately independent so a player
/// floating at the water surface can keep the lower chunk saturated while the upper
/// chunk dries in air.
/// </summary>
internal static class PlayerWetnessModel
{
    internal const float MinimumWetness = -1f;
    internal const float MaximumWetness = 1f;

    // Agreed environmental wetness tuning.
    internal const float BaseHumidityWetnessRatePerSecond = 0.04f;
    internal const float SolarWetnessStrengthMultiplier = 1.75f;
    internal const float HumidityGapCompression = 1.5f;
    internal const float HumidityAbsorptionMultiplier = 0.55f;
    internal const float CrossSignMultiplier = 1.8f;

    // Wetness modifies the existing room-cooling branch rather than adding a new
    // direct WV-loss source. Fully wet = x1.50 cooling, fully dry = x0.90 cooling.
    internal const float MaximumWetCoolingBonus = 0.50f;
    internal const float MaximumDryCoolingPenalty = 0.10f;

    // Partial water contact should wet a body chunk much faster than ambient humidity.
    // A fully submerged chunk is saturated immediately.
    internal const float PartialWaterWetRatePerSecond = 3f;
    internal const float FullSubmersionThreshold = 0.999f;

    private const float SimulationTicksPerSecond = 40f;
    private const float TickSeconds = 1f / SimulationTicksPerSecond;

    private static ConditionalWeakTable<Player, PlayerWetnessState> _states = new();
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
        _states = new ConditionalWeakTable<Player, PlayerWetnessState>();
    }

    internal static PlayerWetnessState For(Player player)
    {
        if (player == null)
        {
            return null;
        }

        PlayerWetnessState state = _states.GetOrCreateValue(player);
        EnsureInitialized(player, state);
        return state;
    }

    internal static float GetWetness(Player player, int bodyIndex)
    {
        PlayerWetnessState state = For(player);
        if (state == null)
        {
            return 0f;
        }

        return bodyIndex <= 0 ? state.Wetness0 : state.Wetness1;
    }

    /// <summary>
    /// Simple signed wetness correction for room cooling:
    /// -1 => x0.90, 0 => x1.00, +1 => x1.50.
    /// </summary>
    internal static float GetBodyHeatCoolingMultiplier(float wetness)
    {
        float w = RoomEnvironmentProfile.ClampSigned(wetness);
        return w >= 0f
            ? 1f + (w * MaximumWetCoolingBonus)
            : 1f + (w * MaximumDryCoolingPenalty);
    }

    internal static float GetBodyHeatCoolingMultiplier(Player player, int bodyIndex)
    {
        return GetBodyHeatCoolingMultiplier(GetWetness(player, bodyIndex));
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!_enabled || self == null)
        {
            return;
        }

        PlayerWetnessState state = _states.GetOrCreateValue(self);
        EnsureInitialized(self, state);

        // Keep the stored state while travelling through shortcuts. Wetness resumes
        // environmental exchange when the player is realized in a room again.
        if (self.room == null || self.inShortcut)
        {
            return;
        }

        state.Wetness0 = UpdateBodyChunkWetness(
            self,
            0,
            state.Wetness0,
            TickSeconds);

        state.Wetness1 = UpdateBodyChunkWetness(
            self,
            1,
            state.Wetness1,
            TickSeconds);
    }

    private static void EnsureInitialized(Player player, PlayerWetnessState state)
    {
        if (state == null || state.Initialized)
        {
            return;
        }

        // Spawn at the local ambient baseline instead of introducing an artificial
        // wetness transient every time a player is realized.
        state.Wetness0 = player?.room != null
            ? HumidityEnvironment.GetEffectiveHumidity(player, 0)
            : 0f;
        state.Wetness1 = player?.room != null
            ? HumidityEnvironment.GetEffectiveHumidity(player, 1)
            : state.Wetness0;

        if (GetChunkSubmersion(player, 0) >= FullSubmersionThreshold)
        {
            state.Wetness0 = MaximumWetness;
        }

        if (GetChunkSubmersion(player, 1) >= FullSubmersionThreshold)
        {
            state.Wetness1 = MaximumWetness;
        }

        state.Wetness0 = RoomEnvironmentProfile.ClampSigned(state.Wetness0);
        state.Wetness1 = RoomEnvironmentProfile.ClampSigned(state.Wetness1);
        state.Initialized = true;
    }

    private static float UpdateBodyChunkWetness(
        Player player,
        int bodyIndex,
        float wetness,
        float deltaTime)
    {
        float submersion = GetChunkSubmersion(player, bodyIndex);
        if (submersion >= FullSubmersionThreshold)
        {
            return MaximumWetness;
        }

        wetness = RoomEnvironmentProfile.ClampSigned(wetness);

        // Partial contact wets rapidly. Environmental drying only acts on the part
        // of the chunk that is exposed to air, so direct sunlight cannot dry the
        // submerged fraction at full strength.
        if (submersion > 0f)
        {
            wetness = Mathf.MoveTowards(
                wetness,
                MaximumWetness,
                PartialWaterWetRatePerSecond * submersion * deltaTime);
        }

        float exposedFraction = 1f - submersion;
        if (exposedFraction <= 0f)
        {
            return RoomEnvironmentProfile.ClampSigned(wetness);
        }

        float humidity = HumidityEnvironment.GetEffectiveHumidity(player, bodyIndex);
        float sunlight = SolarEnvironment.GetEffectiveSunlight(player, bodyIndex);

        wetness += CalculateHumidityWetnessRate(wetness, humidity) *
                   exposedFraction *
                   deltaTime;

        wetness -= CalculateSolarWetnessLossRate(wetness, sunlight) *
                   exposedFraction *
                   deltaTime;

        return RoomEnvironmentProfile.ClampSigned(wetness);
    }

    /// <summary>
    /// Signed rate that moves Wetness toward Humidity.
    ///
    /// Gap / (1 + 1.5*Gap) makes large differences increasingly inefficient while
    /// still allowing a wet chunk to dry noticeably just after leaving water. Moving
    /// upward toward a more humid environment is slower than drying toward a lower
    /// humidity. Crossing from positive Wetness to negative Humidity, or the reverse,
    /// receives the agreed acceleration.
    /// </summary>
    internal static float CalculateHumidityWetnessRate(float wetness, float humidity)
    {
        float w = RoomEnvironmentProfile.ClampSigned(wetness);
        float h = RoomEnvironmentProfile.ClampSigned(humidity);
        float difference = h - w;
        float gap = Mathf.Abs(difference);
        if (gap <= 0.000001f)
        {
            return 0f;
        }

        float drive = gap / (1f + HumidityGapCompression * gap);
        float directionMultiplier = w < h
            ? HumidityAbsorptionMultiplier
            : 1f;
        float crossMultiplier = w * h < 0f
            ? CrossSignMultiplier
            : 1f;

        return Mathf.Sign(difference) *
               BaseHumidityWetnessRatePerSecond *
               drive *
               directionMultiplier *
               crossMultiplier;
    }

    /// <summary>
    /// Sunlight always dries the surface. Its base strength is 1.75 times the
    /// humidity base strength, and the effect fades as Wetness approaches -1.
    /// </summary>
    internal static float CalculateSolarWetnessLossRate(float wetness, float effectiveSunlight)
    {
        float w = RoomEnvironmentProfile.ClampSigned(wetness);
        float sunlight = RoomEnvironmentProfile.ClampUnit(effectiveSunlight);
        float surfaceMoistureFactor = Mathf.Clamp01((w + 1f) * 0.5f);

        return BaseHumidityWetnessRatePerSecond *
               SolarWetnessStrengthMultiplier *
               sunlight *
               surfaceMoistureFactor;
    }

    private static float GetChunkSubmersion(Player player, int bodyIndex)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return 0f;
        }

        int index = Mathf.Clamp(bodyIndex, 0, player.bodyChunks.Length - 1);
        return Mathf.Clamp01(player.bodyChunks[index].submersion);
    }
}
