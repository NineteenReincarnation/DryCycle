using RWCustom;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Extends vanilla FlyAI chain anchors for Desert Batflies only so they may use the
/// underside of Rain World's one-way Floor tiles as a hanging surface.
/// Vanilla ChainTile remains authoritative first; the additional rule is deliberately
/// narrow and retains vanilla's five-tile solid/water clearance requirement.
/// </summary>
internal static class DB_PlatformRoostRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.FlyAI.ChainTile += FlyAI_ChainTile;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.FlyAI.ChainTile -= FlyAI_ChainTile;
        _enabled = false;
    }

    private static bool FlyAI_ChainTile(
        On.FlyAI.orig_ChainTile orig,
        FlyAI self,
        IntVector2 testTile)
    {
        if (orig(self, testTile))
        {
            return true;
        }

        if (self?.fly is not DesertBatfly || self.room == null)
        {
            return false;
        }

        Room room = self.room;
        if (testTile.x <= 0 || testTile.x >= room.TileWidth - 1 ||
            testTile.y < 4 || testTile.y >= room.TileHeight - 1)
        {
            return false;
        }

        Room.Tile current = room.GetTile(testTile);
        if (current.Terrain != Room.Tile.TerrainType.Air)
        {
            return false;
        }

        for (int i = 0; i < 5; i++)
        {
            Room.Tile below = room.GetTile(testTile + new IntVector2(0, -i));
            if (below.Terrain == Room.Tile.TerrainType.Solid || below.AnyWater)
            {
                return false;
            }
        }

        Room.Tile above = room.GetTile(testTile + new IntVector2(0, 1));
        return above.Terrain == Room.Tile.TerrainType.Floor;
    }
}
