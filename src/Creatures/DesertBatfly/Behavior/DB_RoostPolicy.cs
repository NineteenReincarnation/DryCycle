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
/// Immutable local roost fact. Tile is the terrain/candidate tile that owns the anchor;
/// Spot is only the realized hang coordinate derived from that tile. Consumers keep Tile
/// instead of reconstructing legality from a world-space boundary coordinate.
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
/// species extension is the underside of Rain World's one-way Floor terrain. The extension
/// does not modify FlyAI.ChainTile. An Air candidate immediately below a Floor and the Floor
/// tile itself are normalized to one immutable Floor-owned anchor fact.
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

        // Desert Batfly extension. Normal AI asks about its current Air tile, while spatial
        // searches may encounter the Floor tile directly. Normalize both observations to the
        // Floor tile for ownership, but keep the realized hang point on the Floor tile's
        // underside. With 20 px tiles that is Floor.Middle - 10 px, equivalent to the old
        // Air-below candidate's Middle + 10 px.
        IntVector2 floorTile;
        if (current.Terrain == Room.Tile.TerrainType.Floor)
        {
            floorTile = tile;
        }
        else if (current.Terrain == Room.Tile.TerrainType.Air &&
                 above.Terrain == Room.Tile.TerrainType.Floor)
        {
            floorTile = new IntVector2(tile.x, tile.y + 1);
        }
        else
        {
            return false;
        }

        if (!InBounds(room, floorTile) || floorTile.y < 5 || !HasFloorClearance(room, floorTile))
            return false;

        anchor = new DB_RoostAnchor(
            floorTile,
            DB_RoostAnchorKind.FloorUnderside,
            room.MiddleOfTile(floorTile) + Vector2.down * 10f);
        return true;
    }

    internal static bool IsStillValid(DB_Creature fly, in DB_RoostAnchor anchor)
    {
        if (!TryGetAnchor(fly, anchor.Tile, out DB_RoostAnchor current))
            return false;

        return current.Kind == anchor.Kind &&
               current.Tile.x == anchor.Tile.x && current.Tile.y == anchor.Tile.y &&
               (current.Spot - anchor.Spot).sqrMagnitude <= 0.01f;
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

    private static bool HasFloorClearance(Room room, IntVector2 floorTile)
    {
        // Match the native chain rule's five traversable tiles beneath the support surface.
        // Floor itself is a one-way surface rather than a Solid obstruction, so clearance
        // begins at the Air/candidate tile immediately below it.
        for (int y = floorTile.y - 1; y > floorTile.y - 6; y--)
        {
            Room.Tile below = room.GetTile(floorTile.x, y);
            if (below.Terrain == Room.Tile.TerrainType.Solid || below.AnyWater)
                return false;
        }

        return true;
    }
}
