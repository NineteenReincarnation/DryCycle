namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Realized MossySpider brain. The ecology is intentionally simple: it has no prey,
/// threat, relationship, rain or aggression modules. Its only behavior is slow roaming
/// toward the migration destination owned by MossySpiderAbstractAI.
/// </summary>
internal sealed class MossySpiderAI : ArtificialIntelligence
{
    internal enum Behavior
    {
        Roaming,
        Waiting
    }

    internal MossySpiderAI(AbstractCreature creature, World world)
        : base(creature, world)
    {
        Pather = new MossySpiderPather(this, world, creature);
        AddModule(Pather);
        CurrentBehavior = Behavior.Waiting;
    }

    internal MossySpiderPather Pather { get; }

    internal Behavior CurrentBehavior { get; private set; }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        SyncMigrationDestination(force: true);
    }

    public override void Update()
    {
        base.Update();

        if (creature.realizedCreature is not MossySpider spider || spider.room == null)
        {
            CurrentBehavior = Behavior.Waiting;
            return;
        }

        if (creature.abstractAI is MossySpiderAbstractAI abstractAI)
        {
            abstractAI.AbstractBehavior(1);

            // The abstract destination is the ecological migration target, while the
            // realized PathFinder owns the actual in-room heat map. PathFinder can reset
            // its destination when a room is realized/newly entered, so keep the two
            // explicitly synchronized instead of assuming one SetDestination call will
            // survive every room transition.
            SyncMigrationDestination(force: false);

            // A creature spawned by DevConsole can begin on a local tile with no useful
            // abstract node. If that leaves the pather targeting its own position, ask
            // the abstract layer for a new side-access target immediately rather than
            // spending the whole session in Waiting.
            if (Pather.GetDestination.CompareDisregardingTile(creature.pos))
            {
                abstractAI.ForceRetarget();
                abstractAI.AbstractBehavior(1);
                SyncMigrationDestination(force: true);
            }
        }

        WorldCoordinate destination = Pather.GetDestination;
        CurrentBehavior = destination.room != creature.pos.room ||
                          destination.TileDefined ||
                          destination.NodeDefined
            ? Behavior.Roaming
            : Behavior.Waiting;
    }

    private void SyncMigrationDestination(bool force)
    {
        if (creature.abstractAI is not MossySpiderAbstractAI abstractAI ||
            !abstractAI.RoamTarget.HasValue)
        {
            return;
        }

        WorldCoordinate target = abstractAI.RoamTarget.Value;
        if (force || !Pather.GetDestination.CompareDisregardingTile(target))
        {
            SetDestination(target);
        }
    }

    public override PathCost TravelPreference(MovementConnection connection, PathCost cost)
    {
        if (!cost.Allowed ||
            creature.realizedCreature?.room == null ||
            !connection.destinationCoord.TileDefined)
        {
            return cost;
        }

        AItile.Accessibility accessibility =
            creature.realizedCreature.room.aimap.getAItile(connection.destinationCoord).acc;

        if (accessibility == AItile.Accessibility.Wall ||
            accessibility == AItile.Accessibility.Climb)
        {
            return new PathCost(cost.resistance + 1000f, PathCost.Legality.IllegalConnection);
        }

        // Land, sand, shallow water and deep water deliberately receive no extra
        // preference cost. Water changes locomotion, not motivation.
        return cost;
    }
}
