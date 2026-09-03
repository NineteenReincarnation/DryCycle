using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// One deterministic, room-anchored gust signal shared by Foehn rendering, particles,
/// audio and gameplay physics. The goal is not CFD: it is a readable sequence of broad
/// fast wind bodies with narrow leading fronts so the whole room can react to the same
/// visible event instead of every subsystem inventing unrelated noise.
/// </summary>
internal static class FoehnGustField
{
    private const float PrimarySpacingPx = 1080f;
    private const float SecondarySpacingPx = 640f;

    internal static float BuildRoomSeed(Room room)
    {
        unchecked
        {
            uint hash = 2166136261u;
            string name = room?.abstractRoom?.name ?? room?.world?.region?.name ?? "Foehn";
            for (int i = 0; i < name.Length; i++)
            {
                hash ^= char.ToUpperInvariant(name[i]);
                hash *= 16777619u;
            }

            hash ^= (uint)(room?.TileWidth ?? 0);
            hash *= 16777619u;
            hash ^= (uint)(room?.TileHeight ?? 0);
            hash *= 16777619u;
            return (hash & 0x00FFFFFFu) / 16777215f;
        }
    }

    internal static FoehnGustSample Sample(
        Vector2 worldPosition,
        float visualTime,
        float intensity,
        Vector2 windDirection,
        float roomSeed)
    {
        float drive = Mathf.Clamp01(intensity);
        Vector2 forward = SafeNormalize(windDirection);
        Vector2 cross = new(-forward.y, forward.x);

        float along = Vector2.Dot(worldPosition, forward);
        float across = Vector2.Dot(worldPosition, cross);
        float speed = Mathf.Lerp(178f, 286f, Mathf.Pow(drive, 0.72f));

        // Cross-wind warping keeps the front from becoming a perfectly straight UI
        // stripe while retaining a coherent direction that can be tracked by eye.
        float warp =
            Mathf.Sin(across / 244f + roomSeed * Mathf.PI * 2f) * 54f +
            Mathf.Sin(across / 91f - visualTime * 0.29f + roomSeed * 13.7f) * 18f;

        float primaryPhase = Mathf.Repeat(
            along - visualTime * speed + warp + roomSeed * PrimarySpacingPx * 3.17f,
            PrimarySpacingPx);
        float primaryDistance = Mathf.Min(primaryPhase, PrimarySpacingPx - primaryPhase);
        float primaryFront = 1f - SmoothStep(30f, 108f, primaryDistance);
        float primaryBody = 1f - SmoothStep(92f, 340f, primaryDistance);

        float secondaryWarp =
            Mathf.Sin(across / 137f + visualTime * 0.18f + roomSeed * 21.1f) * 27f;
        float secondaryPhase = Mathf.Repeat(
            along - visualTime * speed * 1.09f + secondaryWarp +
            roomSeed * SecondarySpacingPx * 7.41f,
            SecondarySpacingPx);
        float secondaryDistance = Mathf.Min(
            secondaryPhase,
            SecondarySpacingPx - secondaryPhase);
        float secondaryFront = 1f - SmoothStep(24f, 76f, secondaryDistance);
        float secondaryBody = 1f - SmoothStep(72f, 208f, secondaryDistance);

        // A persistent low background keeps Foehn recognizably windy between fronts,
        // while most of the extra force/density remains concentrated in moving gusts.
        float body = Mathf.Clamp01(
            0.18f + primaryBody * 0.68f + secondaryBody * 0.23f);
        float front = Mathf.Clamp01(Mathf.Max(primaryFront, secondaryFront * 0.62f));

        float turbulenceWave = Mathf.Abs(Mathf.Sin(
            along / 177f - across / 109f - visualTime * 2.06f + roomSeed * 9.3f));
        float turbulence = Mathf.Clamp01(
            turbulenceWave * 0.22f +
            secondaryBody * 0.16f +
            front * 0.68f);

        return new FoehnGustSample(body, front, turbulence);
    }

    private static Vector2 SafeNormalize(Vector2 value)
    {
        return value.sqrMagnitude > 0.0001f
            ? value.normalized
            : new Vector2(1f, -0.16f).normalized;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0)
        {
            return value >= edge1 ? 1f : 0f;
        }

        float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}

internal readonly struct FoehnGustSample
{
    internal readonly float Body;
    internal readonly float Front;
    internal readonly float Turbulence;

    internal FoehnGustSample(float body, float front, float turbulence)
    {
        Body = Mathf.Clamp01(body);
        Front = Mathf.Clamp01(front);
        Turbulence = Mathf.Clamp01(turbulence);
    }
}

/// <summary>
/// Resolved local wind used by gameplay physics. It intentionally exposes the same
/// terrain and gust terms that drive visuals so shelter, wakes and nozzles are felt as
/// well as seen.
/// </summary>
internal readonly struct FoehnWindSample
{
    internal readonly Vector2 Direction;
    internal readonly float Intensity;
    internal readonly float Gust;
    internal readonly float Front;
    internal readonly float Turbulence;
    internal readonly float Exposure;
    internal readonly float Wake;
    internal readonly float Nozzle;
    internal readonly float Edge;

    internal FoehnWindSample(
        Vector2 direction,
        float intensity,
        FoehnGustSample gust,
        FoehnTerrainSample terrain)
    {
        Direction = direction.sqrMagnitude > 0.0001f
            ? direction.normalized
            : new Vector2(1f, -0.16f).normalized;
        Intensity = Mathf.Clamp01(intensity);
        Gust = gust.Body;
        Front = gust.Front;
        Turbulence = gust.Turbulence;
        Exposure = terrain.Exposure;
        Wake = terrain.Wake;
        Nozzle = terrain.Nozzle;
        Edge = terrain.Edge;
    }
}
