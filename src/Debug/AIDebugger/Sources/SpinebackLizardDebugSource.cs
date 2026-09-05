using DryCycle.Creatures;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Spineback intentionally inherits Green Lizard AI through compatibility hooks. This
// adapter makes that fact explicit while exposing the live LizardAI state so developers
// can tell a DryCycle hook issue from ordinary vanilla lizard decision-making.
internal sealed class SpinebackLizardDebugSource : IAIDebugSource
{
    public int Priority => 700;

    public bool CanInspect(AbstractCreature creature)
    {
        return creature?.realizedCreature is Lizard lizard &&
               SpinebackLizardEnums.Type != null &&
               lizard.Template?.type == SpinebackLizardEnums.Type;
    }

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        if (!CanInspect(creature) || creature.realizedCreature is not Lizard lizard) return null;
        LizardAI ai = creature.abstractAI?.RealAI as LizardAI;
        string behavior = ai?.behavior?.value ?? ai?.behavior?.ToString() ?? "—";
        string owner = "LizardAI / Green baseline";

        var snapshot = new AIDebugSnapshot(DebugEntityKey.From(creature),
            $"SpinebackLizard #{creature.ID.number}", AIDebugRegistry.EntityState(creature), owner);

        snapshot.Sections.Add(new AIDebugSection("section.identity")
            .Add("field.entity_id", "AbstractCreature.ID", creature.ID)
            .Add("field.template", "CreatureTemplate.type", creature.creatureTemplate?.type?.value)
            .Add("field.room", "AbstractCreature.Room", creature.Room?.name)
            .Add("field.coordinate", "AbstractCreature.pos", creature.pos)
            .Add("field.entity_state", "DebugEntityState", AIDebugLocalization.EntityState(snapshot.EntityState)));

        snapshot.Sections.Add(new AIDebugSection("section.state")
            .Add("field.dead", "Lizard.dead", lizard.dead)
            .Add("field.conscious", "Lizard.Consious", lizard.Consious)
            .Add("field.in_shortcut", "Lizard.inShortcut", lizard.inShortcut)
            .Add("field.position", "Lizard.mainBodyChunk.pos", lizard.mainBodyChunk?.pos)
            .Add("field.velocity", "Lizard.mainBodyChunk.vel", lizard.mainBodyChunk?.vel));

        snapshot.Sections.Add(new AIDebugSection("section.ai")
            .Add("field.abstract_ai", "AbstractCreature.abstractAI", creature.abstractAI?.GetType().Name)
            .Add("field.real_ai", "LizardAI", ai?.GetType().Name)
            .Add("field.behavior", "LizardAI.behavior", behavior)
            .Add("field.destination", "AbstractCreatureAI.destination", creature.abstractAI?.destination)
            .Add("field.pathfinder", "ArtificialIntelligence.pathFinder", ai?.pathFinder?.GetType().Name)
            .Add("field.modules", "ArtificialIntelligence.modules.Count", ai?.modules?.Count ?? 0));

        snapshot.Sections.Add(new AIDebugSection("section.movement")
            .Add("field.position", "Lizard.mainBodyChunk.pos", lizard.mainBodyChunk?.pos)
            .Add("field.velocity", "Lizard.mainBodyChunk.vel", lizard.mainBodyChunk?.vel)
            .Add("field.behavior", "Spineback compatibility", "Green Lizard AI baseline"));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.availability", AIDebugDecisionState.Active,
            null, "Spineback lifecycle"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.conscious",
            lizard.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked,
            lizard.Consious.ToString(), "Lizard.Consious", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.shortcut",
            lizard.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            null, "Lizard.inShortcut", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.motor", AIDebugDecisionState.Active,
            $"Green baseline compatibility → LizardAI.{behavior}", "SpinebackLizardHooks + LizardAI"));

        return snapshot;
    }
}
