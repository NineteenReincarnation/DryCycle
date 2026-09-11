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
        // Fly owns a non-ArtificialIntelligence controller and an existing pre-baked pathing slot.
        // Inherit its ordinary template behavior while reusing that baked slot instead of creating a new one.
        CreatureTemplate template = new CreatureTemplateBuilder(CreatureType)
            .Name("Desert Batfly")
            .Ancestor(CreatureTemplate.Type.Fly)
            .AI(false)
            .ReusePreBakedPathing(CreatureTemplate.Type.Fly)
            .DamageResistance(0.3f)
            .StunResistance(1f)
            .InstantDeathLimit(0.9f)
            .Build();

        template.quantified = false;
        template.bodySize = 0.18f;
        template.grasps = 1;
        template.meatPoints = 0;
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
