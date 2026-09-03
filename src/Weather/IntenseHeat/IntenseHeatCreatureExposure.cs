using System.Runtime.CompilerServices;
using DryCycle.TemperatureSystem;
using UnityEngine;

namespace DryCycle.Weather.IntenseHeat;

/// <summary>
/// Persistent short-term solar exposure state for creatures during IntenseHeat.
/// Exposure is gameplay/world state, not inferred from shader output.
///
/// This component only owns the creature exposure state. Player BodyHeat changes are
/// handled centrally by PlayerThermalModel so IntenseHeat has a single thermal rule.
/// </summary>
internal static class IntenseHeatCreatureExposure
{
    private const float TickSeconds = 1f / 40f;
    private const float ExposureGainPerSecond = 0.070f;
    private const float ShadeRecoveryPerSecond = 0.018f;
    private const float DeepShadeRecoveryPerSecond = 0.032f;

    private sealed class CreatureState
    {
        internal float Exposure;
    }

    private static ConditionalWeakTable<Creature, CreatureState> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _states = new ConditionalWeakTable<Creature, CreatureState>();
        _enabled = false;
    }

    internal static void UpdateRoom(Room room, float hazardIntensity)
    {
        if (!_enabled || room?.physicalObjects == null)
        {
            return;
        }

        float intensity = Mathf.Clamp01(hazardIntensity);

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not Creature creature || creature.room != room)
                {
                    continue;
                }

                UpdateCreature(creature, intensity);
            }
        }
    }

    internal static float GetExposure(Creature creature)
    {
        return creature != null && _states.TryGetValue(creature, out CreatureState state)
            ? Mathf.Clamp01(state.Exposure)
            : 0f;
    }

    private static void UpdateCreature(Creature creature, float intensity)
    {
        CreatureState state = _states.GetOrCreateValue(creature);
        Vector2 center = GetCreatureCenter(creature);
        float directSun = intensity > 0.0001f
            ? IntenseHeatSolarField.SampleExposure(creature.room, center) * intensity
            : 0f;

        if (directSun > 0.08f)
        {
            float gain = ExposureGainPerSecond * Mathf.Lerp(0.32f, 1f, directSun);
            state.Exposure = Mathf.Clamp01(state.Exposure + gain * TickSeconds);
        }
        else
        {
            float roomShade = creature.room != null
                ? Mathf.Clamp01(SolarEnvironment.GetRoomShade(creature.room))
                : 1f;
            float recovery = roomShade > 0.65f
                ? DeepShadeRecoveryPerSecond
                : ShadeRecoveryPerSecond;
            state.Exposure = Mathf.Clamp01(state.Exposure - recovery * TickSeconds);
        }
    }

    private static Vector2 GetCreatureCenter(Creature creature)
    {
        if (creature?.bodyChunks == null || creature.bodyChunks.Length == 0)
        {
            return creature?.mainBodyChunk?.pos ?? Vector2.zero;
        }

        Vector2 total = Vector2.zero;
        int count = 0;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            if (creature.bodyChunks[i] == null)
            {
                continue;
            }

            total += creature.bodyChunks[i].pos;
            count++;
        }

        return count > 0 ? total / count : creature.mainBodyChunk?.pos ?? Vector2.zero;
    }
}
