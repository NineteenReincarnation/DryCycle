using RWCustom;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Border-exit pathing for MossySpider.
///
/// MossySpider reuses Deer's pre-baked pathing slot, so its realized pather must use
/// the same border-oriented path model rather than StandardPather's short-creature
/// anti-loop history. A slow giant can spend many frames following the same tile
/// connection; StandardPather marks that repeated connection Unwanted and starts
/// choosing an equally legal connection in the opposite direction, which produced the
/// visible left/right rocking in place.
/// </summary>
internal sealed class MossySpiderPather : BorderExitPather
{
    private const int SideExitWalkMarginTiles = 18;

    internal MossySpiderPather(
        ArtificialIntelligence ai,
        World world,
        AbstractCreature creature)
        : base(ai, world, creature)
    {
        walkPastPointOfNoReturn = true;
    }

    public override PathCost HeuristicForCell(PathingCell cell, PathCost costToGoal)
    {
        // DeerPather deliberately follows the baked generation/cost field directly.
        // Do the same so probing different points of the long torso cannot change the
        // heuristic direction from frame to frame.
        return costToGoal;
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

    internal MovementConnection FollowPath(
        WorldCoordinate originPos,
        bool actuallyFollowingThisPath)
    {
        if ((!originPos.TileDefined && !originPos.NodeDefined) || realizedRoom == null)
        {
            return default;
        }

        WorldCoordinate origin = RestrictedOriginPos(originPos);
        PathingCell originCell = PathingCellAtWorldCoordinate(origin);
        if (originCell == null)
        {
            return default;
        }

        if (!originCell.reachable || !originCell.possibleToGetBackFrom)
        {
            OutOfElement(origin);
        }

        MovementConnection chosen = default;
        PathCost chosenTotal = new(0f, PathCost.Legality.Unallowed);
        PathCost.Legality chosenLegality = PathCost.Legality.Unallowed;
        int chosenGeneration = -acceptablePathAge;

        int connectionIndex = 0;
        while (true)
        {
            MovementConnection candidate =
                ConnectionAtCoordinate(outGoing: true, origin, connectionIndex++);

            if (candidate == default)
            {
                break;
            }

            if (candidate.destinationCoord.TileDefined &&
                !Custom.InsideRect(candidate.DestTile, coveredArea))
            {
                continue;
            }

            PathingCell destinationCell = PathingCellAtWorldCoordinate(candidate.destinationCoord);
            if (destinationCell == null)
            {
                continue;
            }

            PathCost connectionCost =
                CheckConnectionCost(originCell, destinationCell, candidate, followingPath: true);

            if (!destinationCell.possibleToGetBackFrom && !walkPastPointOfNoReturn)
            {
                connectionCost.legality = PathCost.Legality.Unallowed;
            }

            PathCost totalCost = destinationCell.costToGoal + connectionCost;
            if (candidate.destinationCoord.TileDefined &&
                destination.TileDefined &&
                candidate.destinationCoord.Tile == destination.Tile)
            {
                totalCost.resistance = 0f;
            }

            // Unlike StandardPather there is intentionally no repeated-connection
            // penalty here. A body this large may need dozens of frames to cross one
            // 20 px tile; following the same connection is progress, not being stuck.
            if (connectionCost.legality < chosenLegality ||
                (connectionCost.legality == chosenLegality &&
                 (destinationCell.generation > chosenGeneration ||
                  (destinationCell.generation == chosenGeneration && totalCost <= chosenTotal))))
            {
                chosen = candidate;
                chosenLegality = connectionCost.legality;
                chosenGeneration = destinationCell.generation;
                chosenTotal = totalCost;
            }
        }

        if (chosenLegality > PathCost.Legality.Unwanted)
        {
            return default;
        }

        if (actuallyFollowingThisPath)
        {
            creatureFollowingGeneration = chosenGeneration;
        }

        if (!actuallyFollowingThisPath ||
            chosen == default ||
            chosen.type != MovementConnection.MovementType.OutsideRoom ||
            chosen.destinationCoord.TileDefined)
        {
            return chosen;
        }

        if (creature.realizedCreature is not MossySpider spider || spider.shortcutDelay > 0)
        {
            return chosen;
        }

        IntVector2 outward = OutwardDirection(chosen);
        if (outward.x == 0 && outward.y == 0)
        {
            return chosen;
        }

        // Keep the whole long body walking beyond the visible room before handing it
        // to SideSpace, just as DeerPather does for Rain Deer.
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

        WorldCoordinate sideDestination = BestSideDestination(chosen.destinationCoord);
        spider.AccessSideSpace(chosen.destinationCoord, sideDestination);
        if (sideDestination.room != creaturePos.room)
        {
            LeavingRoom();
        }

        return default;
    }

    private IntVector2 OutwardDirection(MovementConnection connection)
    {
        if (connection.startCoord.x <= 0)
        {
            return new IntVector2(-1, 0);
        }

        if (connection.startCoord.x >= realizedRoom.TileWidth - 1)
        {
            return new IntVector2(1, 0);
        }

        if (connection.startCoord.y <= 0)
        {
            return new IntVector2(0, -1);
        }

        if (connection.startCoord.y >= realizedRoom.TileHeight - 1)
        {
            return new IntVector2(0, 1);
        }

        return new IntVector2(0, 0);
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
