using System;
using System.Collections.Generic;

namespace DryCycle.Debugging.AI;

internal static class AIDebugRegistry
{
    private static readonly List<IAIDebugSource> Sources = new(6);
    private static bool initialized;

    internal static RainWorldGame CurrentGame { get; private set; }

    internal static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        Register(new DesertBatflyDebugSource());
        Register(new MossySpiderDebugSource());
        Register(new SpinebackLizardDebugSource());
        Register(new GenericCreatureDebugSource());
    }

    internal static void BindGame(RainWorldGame game) => CurrentGame = game;

    internal static void Register(IAIDebugSource source)
    {
        if (source == null) return;
        Sources.Add(source);
        Sources.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    internal static IAIDebugSource SourceFor(AbstractCreature creature)
    {
        Initialize();
        for (int i = 0; i < Sources.Count; i++)
            if (Sources[i].CanInspect(creature)) return Sources[i];
        return null;
    }

    internal static AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        if (game != null) CurrentGame = game;
        IAIDebugSource source = SourceFor(creature);
        return source?.Capture(creature, game ?? CurrentGame);
    }

    // Called only while the Observatory is visible. The list is refreshed at a low rate,
    // not once per simulation tick.
    internal static void CollectWorld(RainWorldGame game, List<AbstractCreature> output)
    {
        if (game != null) CurrentGame = game;
        output.Clear();
        if (game?.world?.abstractRooms == null) return;

        for (int r = 0; r < game.world.abstractRooms.Length; r++)
        {
            AbstractRoom room = game.world.abstractRooms[r];
            if (room?.creatures == null) continue;
            for (int i = 0; i < room.creatures.Count; i++)
            {
                AbstractCreature creature = room.creatures[i];
                if (creature == null || creature.slatedForDeletion) continue;
                output.Add(creature);
            }
        }
    }

    internal static AbstractCreature Resolve(RainWorldGame game, DebugEntityKey key)
    {
        if (game != null) CurrentGame = game;
        game ??= CurrentGame;
        if (game?.world?.abstractRooms == null) return null;
        for (int r = 0; r < game.world.abstractRooms.Length; r++)
        {
            AbstractRoom room = game.world.abstractRooms[r];
            if (room?.creatures == null) continue;
            for (int i = 0; i < room.creatures.Count; i++)
            {
                AbstractCreature creature = room.creatures[i];
                if (creature != null && DebugEntityKey.From(creature) == key) return creature;
            }
        }
        return null;
    }

    internal static AIDebugEntityState EntityState(AbstractCreature creature)
    {
        if (creature == null || creature.slatedForDeletion) return AIDebugEntityState.Deleted;
        if (creature.InDen) return AIDebugEntityState.Den;
        if (creature.realizedCreature?.inShortcut == true) return AIDebugEntityState.Shortcut;
        return creature.realizedCreature != null ? AIDebugEntityState.Realized : AIDebugEntityState.Abstract;
    }
}

internal sealed class GenericCreatureDebugSource : IAIDebugSource
{
    public int Priority => int.MinValue;
    public bool CanInspect(AbstractCreature creature) => creature != null;

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        DebugEntityKey key = DebugEntityKey.From(creature);
        Creature realized = creature.realizedCreature;
        ArtificialIntelligence ai = creature.abstractAI?.RealAI;
        string typeName = creature.creatureTemplate?.type?.value ?? creature.GetType().Name;
        var snapshot = new AIDebugSnapshot(key, $"{typeName} #{creature.ID.number}",
            AIDebugRegistry.EntityState(creature), ai?.GetType().Name ?? creature.abstractAI?.GetType().Name ?? "AbstractCreature");

        snapshot.Sections.Add(new AIDebugSection("section.identity")
            .Add("field.entity_id", "AbstractCreature.ID", creature.ID)
            .Add("field.template", "CreatureTemplate.type", typeName)
            .Add("field.room", "AbstractCreature.Room", creature.Room?.name ?? "—")
            .Add("field.coordinate", "AbstractCreature.pos", creature.pos)
            .Add("field.entity_state", "DebugEntityState", AIDebugLocalization.EntityState(snapshot.EntityState))
            .Add("field.in_den", "AbstractWorldEntity.InDen", creature.InDen));

        var state = new AIDebugSection("section.state")
            .Add("field.dead", "Creature.dead", realized?.dead ?? creature.state?.dead ?? false)
            .Add("field.in_shortcut", "Creature.inShortcut", realized?.inShortcut ?? false);
        if (realized != null)
        {
            state.Add("field.conscious", "Creature.Consious", realized.Consious)
                .Add("field.position", "mainBodyChunk.pos", realized.mainBodyChunk?.pos)
                .Add("field.velocity", "mainBodyChunk.vel", realized.mainBodyChunk?.vel);
        }
        snapshot.Sections.Add(state);

        var genericAI = new AIDebugSection("section.generic_ai")
            .Add("field.abstract_ai", "AbstractCreature.abstractAI", creature.abstractAI?.GetType().Name)
            .Add("field.real_ai", "AbstractCreatureAI.RealAI", ai?.GetType().Name)
            .Add("field.destination", "AbstractCreatureAI.destination", creature.abstractAI?.destination)
            .Add("field.pathfinder", "ArtificialIntelligence.pathFinder", ai?.pathFinder?.GetType().Name)
            .Add("field.modules", "ArtificialIntelligence.modules.Count", ai?.modules?.Count ?? 0);
        snapshot.Sections.Add(genericAI);

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.availability", AIDebugDecisionState.Active));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.conscious",
            realized == null || realized.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked,
            realized == null ? "abstract" : realized.Consious.ToString(), "Creature.Consious", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.shortcut",
            realized?.inShortcut == true ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            null, "Creature.inShortcut", 1));

        return snapshot;
    }
}
