using System;
using DryCycle.WatcherExts.PeachLizard;
using UnityEngine;
using Watcher;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Makes QuicksandZone behave as an ordinary TerrainCurve for Watcher DrillCrabs.
///
/// The important part is not post-correcting the crab after it has already sunk. During
/// DrillCrab.Update the zone participates in TerrainManager exactly like normal curved
/// terrain, and SharedPhysics.ExactTerrainRayTracePos is augmented with the same
/// TerrainManager surface. DrillCrab.Leg can therefore discover the curve during its
/// native Scanning -> Seeking -> Supporting sequence instead of losing support first.
///
/// This class is also the existing Watcher-terrain compatibility entry point used by
/// Plugin.cs, so the sibling Peach Lizard adapter is enabled/disabled here as well.
/// Its implementation remains isolated under WatcherExts/PeachLizard.
/// </summary>
internal static class QuicksandDrillCrabCompatibility
{
    private const float RaySampleSpacing = 4f;
    private const int MaxRaySamples = 192;
    private const int RayRefineIterations = 9;

    [ThreadStatic]
    private static int _terrainQueryDepth;

    private static bool _enabled;

    internal static bool TreatQuicksandAsSolidTerrain => _terrainQueryDepth > 0;

    internal static bool IsDrillCrab(Creature creature)
    {
        return creature is DrillCrab;
    }

    internal static void EnsureEnabled()
    {
        // Keep Peach compatibility independent from DrillCrab's enabled guard so a
        // future partial reload can safely re-establish both Watcher adapters.
        PeachLizardQuicksandRuntime.Enable();

        if (_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update += DrillCrab_Update;
        On.SharedPhysics.ExactTerrainRayTracePos += SharedPhysics_ExactTerrainRayTracePos;
        _enabled = true;
    }

    internal static void Disable()
    {
        PeachLizardQuicksandRuntime.Disable();

        if (!_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update -= DrillCrab_Update;
        On.SharedPhysics.ExactTerrainRayTracePos -= SharedPhysics_ExactTerrainRayTracePos;
        _terrainQueryDepth = 0;
        _enabled = false;
    }

    private static void DrillCrab_Update(
        On.Watcher.DrillCrab.orig_Update orig,
        DrillCrab self,
        bool eu)
    {
        _terrainQueryDepth++;
        try
        {
            orig(self, eu);
        }
        finally
        {
            _terrainQueryDepth = Math.Max(0, _terrainQueryDepth - 1);
        }
    }

    private static Vector2? SharedPhysics_ExactTerrainRayTracePos(
        On.SharedPhysics.orig_ExactTerrainRayTracePos orig,
        Room room,
        Vector2 a,
        Vector2 b)
    {
        Vector2? tileHit = orig(room, a, b);

        if (!TreatQuicksandAsSolidTerrain ||
            room?.terrain == null ||
            room.terrain.terrainList == null ||
            room.terrain.terrainList.Count == 0 ||
            !TryRaycastTerrainManager(room, a, b, out Vector2 curveHit))
        {
            return tileHit;
        }

        if (!tileHit.HasValue)
        {
            return curveHit;
        }

        float curveDistance = Vector2.SqrMagnitude(curveHit - a);
        float tileDistance = Vector2.SqrMagnitude(tileHit.Value - a);
        return curveDistance <= tileDistance ? curveHit : tileHit;
    }

    /// <summary>
    /// Finds the first outside-to-inside crossing against TerrainManager.ITerrain.
    /// This is deliberately generic: while a DrillCrab is updating, QuicksandZone's
    /// ITerrain implementation already delegates to TerrainCurve, so this code does not
    /// know anything about quicksand geometry or manually move any leg/body part.
    /// </summary>
    private static bool TryRaycastTerrainManager(
        Room room,
        Vector2 a,
        Vector2 b,
        out Vector2 hit)
    {
        hit = Vector2.zero;

        Vector2 ray = b - a;
        float length = ray.magnitude;
        if (length < 0.001f)
        {
            return false;
        }

        // A ray beginning inside curved terrain should not manufacture an entry hit at
        // its origin. DrillCrab foot scans begin in open space, which is the case we need.
        if (room.terrain.Contains(a))
        {
            return false;
        }

        int samples = Mathf.Clamp(
            Mathf.CeilToInt(length / RaySampleSpacing),
            1,
            MaxRaySamples);

        float previousT = 0f;
        bool previousInside = false;

        for (int i = 1; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector2 point = Vector2.Lerp(a, b, t);
            bool inside = room.terrain.Contains(point);

            if (!previousInside && inside)
            {
                float low = previousT;
                float high = t;

                for (int j = 0; j < RayRefineIterations; j++)
                {
                    float mid = (low + high) * 0.5f;
                    if (room.terrain.Contains(Vector2.Lerp(a, b, mid)))
                    {
                        high = mid;
                    }
                    else
                    {
                        low = mid;
                    }
                }

                Vector2 insidePoint = Vector2.Lerp(a, b, high);
                Vector2 normal;
                hit = room.terrain.SnapToTerrain(
                    insidePoint,
                    0f,
                    out normal,
                    a);
                return true;
            }

            previousInside = inside;
            previousT = t;
        }

        return false;
    }
}
