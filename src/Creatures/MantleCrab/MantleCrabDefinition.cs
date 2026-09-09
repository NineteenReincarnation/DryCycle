using DryCycle.Creatures.Platforming;
using DryCycle.Registration;

namespace DryCycle.Creatures.MantleCrab;

internal sealed class MantleCrabDefinition : CreatureDefinition
{
    internal MantleCrabDefinition() : base(MantleCrabEnums.Type) { }

    internal override CreatureTemplate CreateTemplate()
    {
        CreatureTemplate template = new CreatureTemplateBuilder(Type, "Mantle Crab")
        {
            HasAI = false, RequireAIMap = false, DoPreBakedPathing = false
        }.Build();
        template.grasps = 0;
        template.bodySize = 6f;
        template.canAutoAbstractPath = false;
        template.roamInRoomChance = template.roamBetweenRoomsChance = 0f;
        template.forbidStandardShortcutEntry = true;
        template.doesNotUseDens = true;
        // No ecology, unlocks, sandbox, AI or combat policy in the appearance prototype.
        return template;
    }

    internal override Creature CreateRealizedCreature(AbstractCreature creature)
    {
        MantleCrab crab = new(creature, creature.world);
        // Room resolves Creature-to-Creature collisions after each object's Update. Register a
        // provider-local finalizer so the generic platform runtime can restore the hard shell
        // before reconciling riders, without taking a MantleCrab dependency itself.
        WalkableDynamicSurfaceRuntime.RegisterPostPhysicsFinalizer(crab, crab.MaintainRigidShell);
        return crab;
    }

    internal override void LoadResources(RainWorld rainWorld)
    {
        DryCycle.Rendering.DryCycleShaderAssets.EnsureCreatureAssets(rainWorld);
        Rendering.MantleCrabMaterialCache.Enable();
    }
}
