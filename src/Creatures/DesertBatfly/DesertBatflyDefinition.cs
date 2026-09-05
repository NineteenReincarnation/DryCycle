using System.Collections.Generic;
using DryCycle.Registration;
using Watcher;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DesertBatflyDefinition : CreatureDefinition
{
    internal static readonly CreatureTemplate.Type CreatureType = new("DesertBatfly", true);
    internal DesertBatflyDefinition() : base(CreatureType) { }

    internal override CreatureTemplate CreateTemplate()
    {
        var ancestor = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Fly);
        // Fly owns a non-ArtificialIntelligence controller; retain that lifecycle.
        var template = new CreatureTemplate(Type, ancestor, new List<TileTypeResistance>(),
            new List<TileConnectionResistance>(), new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f));
        template.name = "Desert Batfly";
        template.quantified = false;
        template.AI = false;
        template.preBakedPathingAncestor = ancestor;
        template.doPreBakedPathing = false;
        template.bodySize = 0.18f;
        template.grasps = 1;
        template.meatPoints = 0;
        template.baseDamageResistance = 0.3f;
        template.baseStunResistance = 1f;
        template.instantDeathDamageLimit = 0.9f;
        template.quickDeath = true;
        template.shortcutColor = new UnityEngine.Color(0.65f, 0.48f, 0.29f);
        return template;
    }

    internal override Creature CreateRealizedCreature(AbstractCreature creature) => new DesertBatfly(creature, creature.world);
    internal override CreatureState CreateState(AbstractCreature creature) => new DesertBatflyState(creature);

    internal override void EstablishRelationships()
    {
        var desert = StaticWorld.GetCreatureTemplate(Type);
        var fly = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Fly);
        foreach (var other in StaticWorld.creatureTemplates)
        {
            if (other == null || other == desert) continue;
            desert.relationships[other.type.Index] = fly.CreatureRelationship(other).Duplicate();
            other.relationships[Type.Index] = other.CreatureRelationship(fly).Duplicate();
            if (other.TopAncestor().type == CreatureTemplate.Type.Scavenger)
                other.relationships[Type.Index] = new(CreatureTemplate.Relationship.Type.Attacks, DesertBatflyTuning.ScavengerHostility);
        }

        // Peach Lizard is a deliberate ecological predator of Desert Batflies. Its
        // intensity is comparable to Watcher's own Peach->Frog relationship: enough
        // for PreyTracker/Hunt/tongue logic to engage without making a tiny flying
        // prey override every other useful target in the room. The reverse Afraid
        // relationship also plugs directly into DesertBatflyAI's predator detection,
        // so even nasty individuals flee instead of trying to harass their predator.
        if (ModManager.Watcher &&
            WatcherEnums.CreatureTemplateType.PeachLizard != null &&
            WatcherEnums.CreatureTemplateType.PeachLizard.Index >= 0 &&
            WatcherEnums.CreatureTemplateType.PeachLizard.Index < StaticWorld.creatureTemplates.Length)
        {
            CreatureTemplate peach = StaticWorld.GetCreatureTemplate(
                WatcherEnums.CreatureTemplateType.PeachLizard);
            if (peach != null)
            {
                peach.relationships[Type.Index] = new(
                    CreatureTemplate.Relationship.Type.Eats,
                    0.32f);
                desert.relationships[peach.type.Index] = new(
                    CreatureTemplate.Relationship.Type.Afraid,
                    0.90f);
            }
        }

        desert.relationships[CreatureTemplate.Type.Slugcat.Index] = new(CreatureTemplate.Relationship.Type.Ignores, 0f);
        desert.relationships[Type.Index] = new(CreatureTemplate.Relationship.Type.Ignores, 0f);
        desert.relationships[CreatureTemplate.Type.Fly.Index] = new(CreatureTemplate.Relationship.Type.Ignores, 0f);
    }
}
