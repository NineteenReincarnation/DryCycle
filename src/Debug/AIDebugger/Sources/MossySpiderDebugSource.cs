using DryCycle.Creatures.MossySpider;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Species adapter for DryCycle's non-predatory roaming spider. It exposes the
// migration target and realized pather without inventing prey/threat modules that the
// creature deliberately does not have.
internal sealed class MossySpiderDebugSource : IAIDebugSource
{
    public int Priority => 800;
    public bool CanInspect(AbstractCreature creature) => creature?.realizedCreature is MossySpider;

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        if (creature?.realizedCreature is not MossySpider spider) return null;
        MossySpiderAI ai = spider.AI;
        MossySpiderAbstractAI abstractAI = creature.abstractAI as MossySpiderAbstractAI;
        string owner = ai != null ? "MossySpiderAI / " + ai.CurrentBehavior : "MossySpiderAbstractAI";

        var snapshot = new AIDebugSnapshot(DebugEntityKey.From(creature),
            $"MossySpider #{creature.ID.number}", AIDebugRegistry.EntityState(creature), owner);

        snapshot.Sections.Add(new AIDebugSection("section.identity")
            .Add("field.entity_id", "AbstractCreature.ID", creature.ID)
            .Add("field.template", "CreatureTemplate.type", creature.creatureTemplate?.type?.value)
            .Add("field.room", "AbstractCreature.Room", creature.Room?.name)
            .Add("field.coordinate", "AbstractCreature.pos", creature.pos)
            .Add("field.entity_state", "DebugEntityState", AIDebugLocalization.EntityState(snapshot.EntityState)));

        snapshot.Sections.Add(new AIDebugSection("section.state")
            .Add("field.dead", "MossySpider.dead", spider.dead)
            .Add("field.conscious", "MossySpider.Consious", spider.Consious)
            .Add("field.in_shortcut", "MossySpider.inShortcut", spider.inShortcut)
            .Add("field.position", "MossySpider.BodyCenter", spider.BodyCenter)
            .Add("field.velocity", "MossySpider.mainBodyChunk.vel", spider.mainBodyChunk?.vel));

        WorldCoordinate? roam = abstractAI?.RoamTarget;
        snapshot.Sections.Add(new AIDebugSection("section.ai")
            .Add("field.abstract_ai", "MossySpiderAbstractAI", abstractAI?.GetType().Name)
            .Add("field.real_ai", "MossySpiderAI", ai?.GetType().Name)
            .Add("field.behavior", "MossySpiderAI.CurrentBehavior", ai?.CurrentBehavior)
            .Add("field.destination", "MossySpiderAbstractAI.RoamTarget", roam.HasValue ? roam.Value : null)
            .Add("field.pathfinder", "MossySpiderAI.Pather", ai?.Pather?.GetType().Name)
            .Add("field.modules", "ArtificialIntelligence.modules.Count", ai?.modules?.Count ?? 0));

        snapshot.Sections.Add(new AIDebugSection("section.movement")
            .Add("field.local_goal", "MossySpiderPather.GetDestination", ai?.Pather?.GetDestination)
            .Add("field.velocity", "MossySpider.MoveDirection", spider.MoveDirection)
            .Add("field.interest", "MossySpider.GaitCycle", spider.GaitCycle)
            .Add("field.panic_ratio", "MossySpider.GroundSupport", spider.GroundSupport)
            .Add("field.thirst", "MossySpider.SwimFactor", spider.SwimFactor));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.availability", AIDebugDecisionState.Active,
            null, "MossySpider lifecycle"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.conscious",
            spider.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked,
            spider.Consious.ToString(), "MossySpider.Consious", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.shortcut",
            spider.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            null, "MossySpider.inShortcut", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.motor",
            ai?.CurrentBehavior == MossySpiderAI.Behavior.Roaming ? AIDebugDecisionState.Active : AIDebugDecisionState.Ready,
            $"MossySpiderAI.{ai?.CurrentBehavior}; destination={AIDebugFormat.Value(ai?.Pather?.GetDestination)}",
            "MossySpiderAI + MossySpiderPather"));

        return snapshot;
    }
}
