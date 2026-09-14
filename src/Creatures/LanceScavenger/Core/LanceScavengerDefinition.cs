using DryCycle.Framework.Creature.Core;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal static class LanceScavengerDefinition
{
    // CreatureRegistry deliberately retains descriptors/ExtEnums across Disable/Enable.
    internal static readonly CreatureTemplate.Type Type = new("LanceScavenger", true);
    internal static void Register() => CreatureRegistry.Register(new CreatureDescriptor(Type, Plugin.ModId)
        .Name("Lance Scavenger").Alias("lance scavenger")
        .Template(CreateTemplate).State(c => new LanceScavengerState(c))
        .AbstractAI(c => new LanceScavengerAbstractAI(c.world, c))
        .Realized(c => new LanceScavenger(c, c.world))
        .AI(c => new LanceScavengerAI(c, c.world)));

    internal static CreatureTemplate CreateTemplate()
    {
        var template = new CreatureTemplateBuilder(Type).Ancestor(CreatureTemplate.Type.Scavenger)
            .Name("Lance Scavenger").ReusePreBakedPathing(CreatureTemplate.Type.Scavenger).Build();
        template.socialMemory = true;
        template.communityID = CreatureCommunities.CommunityID.Scavengers;
        template.mappedNodeTypes = (bool[])StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Scavenger).mappedNodeTypes.Clone();
        template.shortcutColor = new Color(0.62f, 0.45f, 0.19f);
        // No Elite ancestry, extra health, larger chunks or special jump template.
        return template;
    }
}
