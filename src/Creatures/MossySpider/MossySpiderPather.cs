using RWCustom;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Standard Rain World path search with two MossySpider-specific additions:
/// Wall/Climb remain invalid, and SideExit movement continues physically beyond the
/// screen edge before handing the creature to the off-screen shortcut system.
/// </summary>
internal sealed class MossySpiderPather : StandardPather
{
    private const int SideExitWalkMarginTiles = 18;

    internal MossySpiderPather(
        ArtificialIntelligence ai,
        World world,
        AbstractCreature creature)
        : base(ai, world, creature)
    {
        walkPastPointOfNoReturn = true;
        savedPastConnections = 32;
        numnerOfTimesConnectionHasToHaveBeenFollowedToBeOffLimits = 5;
        heuristicCostFac = 30f;
        heuristicDestFac = 1f;
    }

    public override PathCost CheckConnectionCost(
        PathingCell start,
        PathingCell goal,
        MovementConnection connection,
        bool followingPath)
    {
        PathCost cost = base.CheckConnectionCost(start, goal, connection, followingPath);
        if (!cost.Allowed ||
            realizedRoom?.aimap == null ||
            !connection.destinationCoord.TileDefined)
        {
            return cost;
        }

        AItile.Accessibility accessibility =
            realizedRoom.aimap.getAItile(connection.destinationCoord).acc;

        if (accessibility == AItile.Accessibility.Wall ||
            accessibility == AItile.Accessibility.Climb)
        {
            return new PathCost(cost.resistance + 1000f, PathCost.Legality.IllegalConnection);
        }

        return cost;
    }

    internal new MovementConnection FollowPath(
        WorldCoordinate originPos,
        bool actuallyFollowingThisPath)
    {
        MovementConnection connection = base.FollowPath(originPos, actuallyFollowingThisPath);

        if (!actuallyFollowingThisPath ||
            connection == default ||
            connection.type != MovementConnection.MovementType.OutsideRoom ||
            connection.destinationCoord.TileDefined)
        {
            return connection;
        }

        if (creature.realizedCreature is not MossySpider spider ||
            realizedRoom == null ||
            spider.shortcutDelay > 0)
        {
            return connection;
        }

        RWCustom.IntVector2 outward = OutwardDirection(connection);
        if (outward.x == 0 && outward.y == 0)
        {
            return connection;
        }

        // Keep walking beyond the visible room boundary so a 300 px body does not
        // disappear while most of it is still on screen.
        if (originPos.TileDefined &&
            Custom.InsideRect(
                originPos.Tile,
                new IntRect(
                    -SideExitWalkMarginTiles,
                    -SideExitWalkMarginTiles,
                    realizedRoom.TileWidth + SideExitWalkMarginTiles,
                    realizedRoom.TileHeight + SideExitWalkMarginTiles)))
        {
            return new MovementConnection(
                MovementConnection.MovementType.Standard,
                originPos,
                new WorldCoordinate(
                    originPos.room,
                    originPos.x + outward.x * 10,
                    originPos.y + outward.y * 10,
                    originPos.abstractNode),
                1);
        }

        WorldCoordinate sideDestination = BestSideDestination(connection.destinationCoord);
        spider.AccessSideSpace(connection.destinationCoord, sideDestination);
        return default;
    }

    private RWCustom.IntVector2 OutwardDirection(MovementConnection connection)
    {
        if (connection.startCoord.x <= 0)
        {
            return new RWCustom.IntVector2(-1, 0);
        }

        if (connection.startCoord.x >= realizedRoom.TileWidth - 1)
        {
            return new RWCustom.IntVector2(1, 0);
        }

        if (connection.startCoord.y <= 0)
        {
            return new RWCustom.IntVector2(0, -1);
        }

        if (connection.startCoord.y >= realizedRoom.TileHeight - 1)
        {
            return new RWCustom.IntVector2(0, 1);
        }

        return new RWCustom.IntVector2(0, 0);
    }

    private WorldCoordinate BestSideDestination(WorldCoordinate fallback)
    {
        WorldCoordinate best = fallback;
        int bestGeneration = int.MinValue;
        PathCost bestCost = new(0f, PathCost.Legality.Unallowed);

        if (world.sideAccessNodes == null)
        {
            return best;
        }

        WorldCoordinate requested = GetDestination;
        for (int i = 0; i < world.sideAccessNodes.Length; i++)
        {
            WorldCoordinate candidate = world.sideAccessNodes[i];
            PathingCell cell = PathingCellAtWorldCoordinate(candidate);
            if (cell == null)
            {
                continue;
            }

            if (candidate.CompareDisregardingTile(requested))
            {
                return candidate;
            }

            if (cell.generation > bestGeneration ||
                (cell.generation == bestGeneration && cell.costToGoal < bestCost))
            {
                bestGeneration = cell.generation;
                bestCost = cell.costToGoal;
                best = candidate;
            }
        }

        return best;
    }
}
