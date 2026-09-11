using DryCycle.Creatures.Platforming;
using DryCycle.Framework.Creature.Core;

namespace DryCycle.Creatures.MantleCrab;

internal static class MantleCrabDefinition
{
    /// <summary>
    /// 把 MantleCrab 的模板、状态、实体和临时移动测试 AI 登记到 CreatureRegistry。
    /// 资源加载仍然留在 MantleCrab 自己的代码里，不塞进 Core Registry。
    ///
    /// Registers MantleCrab's template, state, realized creature, and temporary movement-test AI through CreatureRegistry.
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
            .Realized(CreateRealizedCreature)
            .AI(CreateRealizedAI);

        return CreatureRegistry.Register(descriptor);
    }

    private static CreatureTemplate CreateTemplate()
    {
        // 这里只开启 AI 生命周期，让临时测试脑能够驱动现有 Locomotion；不启用 AIMap 或正式寻路。
        // Only enable the AI lifecycle so the temporary test brain can drive Locomotion; no AIMap or production pathing is enabled.
        CreatureTemplate template = new CreatureTemplateBuilder(MantleCrabEnums.Type)
            .Name("Mantle Crab")
            .AI()
            .RequireAIMap(false)
            .Build();

        template.grasps = 0;
        template.bodySize = 6f;
        template.canAutoAbstractPath = false;
        template.roamInRoomChance = 0f;
        template.roamBetweenRoomsChance = 0f;
        template.forbidStandardShortcutEntry = true;
        template.doesNotUseDens = true;

        // 当前仍然不是正式 AI：这里只有用于验证步态和复杂地形通过的自动巡逻。
        // This is still not production AI; the only automated behavior is a patrol used to validate gait and terrain traversal.
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

    private static ArtificialIntelligence CreateRealizedAI(AbstractCreature creature)
    {
        return new MantleCrabTestMovementAI(creature, creature.world);
    }

    internal static void LoadResources(RainWorld rainWorld)
    {
        DryCycle.Rendering.DryCycleShaderAssets.EnsureCreatureAssets(rainWorld);
        Rendering.MantleCrabMaterialCache.Enable();
    }
}
