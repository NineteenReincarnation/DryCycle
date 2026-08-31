using System;
using Watcher;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Makes QuicksandZone behave as its ordinary inherited TerrainCurve while a Watcher
/// DrillCrab is updating.
///
/// No post-update body, foot, target or IK correction is performed here. During the
/// native DrillCrab.Update call, QuicksandZone's TerrainManager.ITerrain implementation
/// delegates to TerrainCurve. BodyChunk.CheckVerticalCollision therefore sees the curve
/// through Room.terrain.TrySnapToTerrain, and DrillCrab.Leg sees the same curve through
/// TerrainManager.Contains exactly as it does any normal curved terrain.
/// </summary>
internal static class QuicksandDrillCrabCompatibility
{
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
        if (_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update += DrillCrab_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update -= DrillCrab_Update;
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
}
