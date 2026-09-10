using System.Collections.Generic;
using DryCycle.Framework.Creature.Core;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DB_Definition
{
    internal static readonly CreatureTemplate.Type CreatureType = new("DesertBatfly", true);

    /// <summary>
    /// 把 DesertBatfly 的模板、状态和实际生物创建方式登记到新的 CreatureRegistry。
    /// DesertBatfly 继续复用 Fly 的原版 AI 生命周期，因此这里不登记 AbstractAI 或实际 AI 工厂。
    ///
    /// Registers DesertBatfly's template, state, and realized creature through the new CreatureRegistry.
    /// DesertBatfly keeps Fly's vanilla AI lifecycle, so no abstract or realized AI factory is declared here.
    /// </summary>
    internal static CreatureDescriptor Register()
    {
        CreatureDescriptor descriptor = new CreatureDescriptor(
                CreatureType,
                DryCycle.Plugin.ModId)
            .Name("Desert Batfly")
            .Template(CreateTemplate)
            .State(CreateState)
            .Realized(CreateRealizedCreature);

        return CreatureRegistry.Register(descriptor);
    }

    private static CreatureTemplate CreateTemplate()
    {
        CreatureTemplate ancestor = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Fly);

        // Fly owns a non-ArtificialIntelligence controller; retain that lifecycle.
        CreatureTemplate template = new(
            CreatureType,
            ancestor,
            new List<TileTypeResistance>(),
            new List<TileConnectionResistance>(),
            new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f));

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

    private static Creature CreateRealizedCreature(AbstractCreature creature)
    {
        return new DB_Creature(creature, creature.world);
    }

    private static CreatureState CreateState(AbstractCreature creature)
    {
        return new DB_State(creature);
    }
}
