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
        }

        WorldCoordinate destination = pathFinder.GetDestination;
        CurrentBehavior = destination.room != creature.pos.room ||
                          destination.TileDefined ||
                          destination.NodeDefined
            ? Behavior.Roaming
            : Behavior.Waiting;
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
