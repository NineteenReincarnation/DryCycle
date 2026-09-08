using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Pure local legality/anchor policy for native Fly chain roost spots.
/// It owns no reservation, state transition, movement or Chain/Hang execution.
/// </summary>
internal static class DB_RoostPolicy
{
    internal static bool TryGetSpot(DesertBatfly fly, IntVector2 tile, out Vector2 spot)
    {
        spot = default;
        if (fly?.room == null || fly.AI == null ||
            tile.x <= 0 || tile.x >= fly.room.TileWidth - 1 ||
            tile.y < 4 || tile.y >= fly.room.TileHeight - 1 ||
            !fly.AI.ChainTile(tile))
            return false;

        Room.Tile current = fly.room.GetTile(tile);
        Room.Tile above = fly.room.GetTile(tile.x, tile.y + 1);
        Vector2 middle = fly.room.MiddleOfTile(tile);

        if (current.horizontalBeam)
            spot = new Vector2(middle.x, middle.y - 4f);
        else if (above.verticalBeam && !current.verticalBeam)
            spot = middle + Vector2.up * 10f;
        else
            spot = middle + Vector2.up * 10f;
        return true;
    }
}
