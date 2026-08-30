using System;
using Watcher;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Watcher DrillCrabs locomote by querying Room.terrain while their legs update.
/// For them a QuicksandZone must be indistinguishable from an ordinary TerrainCurve:
/// the feet can acquire support on quicksand and DryCycle's creature quicksand
/// systems must not redirect, sink or kill the crab.
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
