using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// R4 goal-space navigation uncertainty for authorized DryCycle Fog/DenseFog.
/// It never owns locomotion and never writes velocity/localGoal by itself.
/// </summary>
internal static class DB_FogGoalModifier
{
    private sealed class State
    {
        internal Vector2 Offset;
        internal int OffsetUntil;
        internal int WeatherOrdinal = -1;
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset() => states = new ConditionalWeakTable<DesertBatfly, State>();

    internal static void Forget(DesertBatfly bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static Vector2 ModifyGoal(DesertBatfly bat, DB_BehaviorOwner owner, Vector2 goal)
    {
        if (bat?.room == null || owner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival) ||
            !DB_EnvironmentRuntime.TryGetInfluence(bat, out DB_EnvironmentInfluence influence) ||
            influence.NavigationUncertainty <= 0.05f ||
            influence.Weather is not (
                DB_EnvironmentWeather.Fog or DB_EnvironmentWeather.DenseFog))
            return goal;

        State state = states.GetOrCreateValue(bat);
        int tick = bat.room.game?.clock ?? 0;
        int weatherOrdinal = (int)influence.Weather;
        if (tick >= state.OffsetUntil || state.WeatherOrdinal != weatherOrdinal)
        {
            float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;
            float familiarity = DB_EnvironmentalPolicy.FogNavigationFamiliarityScale(bat, influence.Weather);
            float uncertainty = Mathf.Clamp01(influence.NavigationUncertainty * familiarity);
            float radius = Mathf.Lerp(5f, 62f, uncertainty);
            state.Offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            state.OffsetUntil = tick + 90 + StableBucket(bat.Personality.VisualSeed, 70);
            state.WeatherOrdinal = weatherOrdinal;
        }

        float distance = Vector2.Distance(bat.mainBodyChunk.pos, goal);
        float anticipation = Mathf.Clamp(influence.ObstacleAnticipationScale, 0.30f, 1f);
        float correctionStart = Mathf.Lerp(22f, 65f, anticipation);
        float correctionFull = Mathf.Lerp(175f, 300f, anticipation);
        float fade = Mathf.InverseLerp(correctionStart, correctionFull, distance);
        return goal + state.Offset * fade;
    }

    private static int StableBucket(int seed, int count)
    {
        if (count <= 1) return 0;
        return Mathf.Clamp(Mathf.FloorToInt(Stable01(seed) * count), 0, count - 1);
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
