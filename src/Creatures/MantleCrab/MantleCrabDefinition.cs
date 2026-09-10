using DryCycle.Creatures.Platforming;
using DryCycle.Framework.Creature.Core;
using CreatureTemplateBuilder = DryCycle.Registration.CreatureTemplateBuilder;

namespace DryCycle.Creatures.MantleCrab;

internal static class MantleCrabDefinition
{
    /// <summary>
    /// 把 MantleCrab 的模板、状态和实际生物创建方式登记到新的 CreatureRegistry。
    /// 资源加载仍然留在 MantleCrab 自己的代码里，不塞进 Core Registry。
    ///
    /// Registers MantleCrab's template, state, and realized-creature factories through the new CreatureRegistry.
    /// Resource loading stays owned by MantleCrab instead of the core registry.
    /// </summary>
    internal static CreatureDescriptor Register()
    {
        CreatureDescriptor descriptor = new CreatureDescriptor(
                MantleCrabEnums.Type,
                DryCycle.Plugin.ModId)
            .Name("Mantle Crab")
            .Template(CreateTemplate)
            .State(CreateState)
            .Realized(CreateRealizedCreature);

        return CreatureRegistry.Register(descriptor);
    }

    private static CreatureTemplate CreateTemplate()
    {
        CreatureTemplate template = new CreatureTemplateBuilder(MantleCrabEnums.Type, "Mantle Crab")
        {
            HasAI = false,
            RequireAIMap = false,
            DoPreBakedPathing = false
        }.Build();

        template.grasps = 0;
        template.bodySize = 6f;
        template.canAutoAbstractPath = false;
        template.roamInRoomChance = 0f;
        template.roamBetweenRoomsChance = 0f;
        template.forbidStandardShortcutEntry = true;
        template.doesNotUseDens = true;

        // 当前 MantleCrab 仍然只是外观与物理原型，不在这里加入生态、Sandbox、AI 或战斗策略。
        // MantleCrab is still an appearance/physics prototype; ecology, sandbox, AI, and combat policy stay out of this definition.
        return template;
    }

    private static CreatureState CreateState(AbstractCreature creature)
    {
        return new HealthState(creature);
    }

    private static Creature CreateRealizedCreature(AbstractCreature creature)
    {
        MantleCrab crab = new(creature, creature.world);

        // Room 会在每个物体 Update 后处理 Creature-to-Creature 碰撞；这里登记物种侧的最终修正，
        // 让通用平台运行时能在处理骑乘者之前重新维持硬壳形状，而不反向依赖 MantleCrab。
        // Room resolves Creature-to-Creature collisions after each object's Update. Register a provider-local
        // finalizer so the generic platform runtime can restore the rigid shell before reconciling riders.
        WalkableDynamicSurfaceRuntime.RegisterPostPhysicsFinalizer(crab, crab.MaintainRigidShell);
        return crab;
    }

    internal static void LoadResources(RainWorld rainWorld)
    {
        DryCycle.Rendering.DryCycleShaderAssets.EnsureCreatureAssets(rainWorld);
        Rendering.MantleCrabMaterialCache.Enable();
    }
}
