using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_RoostAnchorKind
{
    SolidCeiling,
    HorizontalBeam,
    VerticalBeam,
    FloorUnderside
}

/// <summary>
/// Immutable local roost fact. Tile is the air-space anchor tile used by Fly chain logic;
/// Spot is only the realized hang coordinate derived from that tile.
/// </summary>
internal readonly struct DB_RoostAnchor
{
    internal readonly IntVector2 Tile;
    internal readonly DB_RoostAnchorKind Kind;
    internal readonly Vector2 Spot;

    internal DB_RoostAnchor(IntVector2 tile, DB_RoostAnchorKind kind, Vector2 spot)
    {
        Tile = tile;
        Kind = kind;
        Spot = spot;
    }
}

/// <summary>
/// Canonical Desert Batfly local roost legality and anchor geometry policy.
/// Native FlyAI.ChainTile remains authoritative for native Solid/Beam anchors; the only
/// species extension is the underside of Rain World's one-way Floor terrain. Consumers keep
/// the returned anchor tile instead of reconstructing legality from the world-space Spot.
/// </summary>
internal static class DB_RoostPolicy
{
    internal static bool TryGetAnchor(DB_Creature fly, IntVector2 tile, out DB_RoostAnchor anchor)
    {
        anchor = default;
        if (fly?.room == null || fly.AI == null || !InBounds(fly.room, tile))
            return false;

        Room room = fly.room;
        Room.Tile current = room.GetTile(tile);
        Room.Tile above = room.GetTile(tile.x, tile.y + 1);
        Vector2 middle = room.MiddleOfTile(tile);

        // Preserve Rain World's native ChainTile semantics for all native surfaces.
        if (fly.AI.ChainTile(tile))
        {
            if (current.horizontalBeam)
            {
                anchor = new DB_RoostAnchor(
                    tile,
                    DB_RoostAnchorKind.HorizontalBeam,
                    new Vector2(middle.x, middle.y - 4f));
                return true;
            }

            if (above.verticalBeam && !current.verticalBeam)
            {
                anchor = new DB_RoostAnchor(
                    tile,
                    DB_RoostAnchorKind.VerticalBeam,
                    middle + Vector2.up * 10f);
                return true;
            }

            anchor = new DB_RoostAnchor(
                tile,
                DB_RoostAnchorKind.SolidCeiling,
                middle + Vector2.up * 10f);
            return true;
        }

        // Desert Batfly extension: hang from the underside of a one-way Floor tile without
        // changing FlyAI.ChainTile itself. Keep vanilla's five-tile solid/water clearance.
        if (current.Terrain != Room.Tile.TerrainType.Air ||
            above.Terrain != Room.Tile.TerrainType.Floor ||
            !HasVanillaClearance(room, tile))
            return false;

        anchor = new DB_RoostAnchor(
            tile,
            DB_RoostAnchorKind.FloorUnderside,
            middle + Vector2.up * 10f);
        return true;
    }

    internal static bool IsStillValid(DB_Creature fly, in DB_RoostAnchor anchor)
    {
        if (!TryGetAnchor(fly, anchor.Tile, out DB_RoostAnchor current))
            return false;

        return current.Kind == anchor.Kind;
    }

    // Compatibility/read-only projection for callers that only need the realized coordinate.
    // Legality and geometry still originate exclusively from TryGetAnchor.
    internal static bool TryGetSpot(DB_Creature fly, IntVector2 tile, out Vector2 spot)
    {
        if (TryGetAnchor(fly, tile, out DB_RoostAnchor anchor))
        {
            spot = anchor.Spot;
            return true;
        }

        spot = default;
        return false;
    }

    private static bool InBounds(Room room, IntVector2 tile)
        => room != null &&
           tile.x > 0 && tile.x < room.TileWidth - 1 &&
           tile.y >= 4 && tile.y < room.TileHeight - 1;

    private static bool HasVanillaClearance(Room room, IntVector2 tile)
    {
        for (int y = tile.y; y > tile.y - 5; y--)
        {
            Room.Tile below = room.GetTile(tile.x, y);
            if (below.Terrain == Room.Tile.TerrainType.Solid || below.AnyWater)
                return false;
        }

        return true;
    }
}
