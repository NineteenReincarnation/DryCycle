using DryCycle.Framework.Creature.Core;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

internal static class MossySpiderDefinition
{
    private static readonly Color MossColor = new(0.48f, 0.52f, 0.22f);

    /// <summary>
    /// 把 MossySpider 的模板、实体、抽象 AI、实际 AI 和 world.txt 别名登记到新的 CreatureRegistry。
    ///
    /// Registers MossySpider's template, realized creature, abstract AI, realized AI, and world.txt alias through the new CreatureRegistry.
    /// </summary>
    internal static CreatureDescriptor Register()
    {
        CreatureDescriptor descriptor = new CreatureDescriptor(
                MossySpiderEnums.Type,
                DryCycle.Plugin.ModId)
            .Name("Mossy Spider")
            .Alias("mossy spider")
            .Template(CreateTemplate)
            .Realized(CreateRealizedCreature)
            .AbstractAI(CreateAbstractAI)
            .AI(CreateRealizedAI);

        return CreatureRegistry.Register(descriptor);
    }

    private static CreatureTemplate CreateTemplate()
    {
        // MossySpider owns its AI behavior while reusing Deer's existing pre-baked
        // AI-map slot so ordinary installed room files remain load-compatible.
        CreatureTemplate template = new CreatureTemplateBuilder(MossySpiderEnums.Type)
            .Name("Mossy Spider")
            .AI()
            .RequireAIMap()
            .ReusePreBakedPathing(CreatureTemplate.Type.Deer)
            .DamageResistance(8f)
            .StunResistance(3f)

            // Body-space accessibility: everything except Wall, Climb and Solid is usable.
            .ExactTile(AItile.Accessibility.OffScreen, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.Floor, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.CurvedFloor, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.Corridor, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.Climb, 100f, PathCost.Legality.IllegalTile)
            .ExactTile(AItile.Accessibility.Wall, 100f, PathCost.Legality.IllegalTile)
            .ExactTile(AItile.Accessibility.Ceiling, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.Air, 1f, PathCost.Legality.Allowed)
            .ExactTile(AItile.Accessibility.Solid, 100f, PathCost.Legality.SolidTile)
            .ExactTile(AItile.Accessibility.Sand, 1f, PathCost.Legality.Allowed)

            // The creature migrates through side/off-screen space like a large walker.
            // It does not use ordinary shortcuts, dens or pole/wall-specific movement.
            .Connection(MovementConnection.MovementType.Standard, 1f)
            .Connection(MovementConnection.MovementType.OpenDiagonal, 1f)
            .Connection(MovementConnection.MovementType.OutsideRoom, 1f)
            .Connection(MovementConnection.MovementType.SideHighway, 1f)
            .Connection(MovementConnection.MovementType.OffScreenMovement, 1f)
            .Connection(MovementConnection.MovementType.BetweenRooms, 1f)
            .Build();

        // Custom AbstractAI owns migration; keep automatic generic roaming disabled.
        template.canAutoAbstractPath = false;
        template.roamInRoomChance = 0f;
        template.roamBetweenRoomsChance = 0f;
        template.offScreenSpeed = 0.55f;
        template.abstractedLaziness = 60;
        template.doesNotUseDens = true;
        template.hibernateOffScreen = false;
        template.forbidStandardShortcutEntry = true;

        template.bodySize = 12f;
        template.grasps = 0;
        template.visualRadius = 700f;
        template.movementBasedVision = 0f;
        template.dangerousToPlayer = 0f;
        template.communityInfluence = 0f;

        // Water is path-cost neutral. Shallow water stays in walking mode; deep water
        // switches locomotion to dorsal flotation plus leg paddling.
        template.waterRelationship = CreatureTemplate.WaterRelationship.Amphibious;
        template.canSwim = true;
        template.waterPathingResistance = 1f;
        template.canFly = false;

        // Preserve the explicit Wall / Climb exclusion even under the vanilla swimmer
        // fallback that otherwise makes many submerged non-solid tiles traversable.
        template.isTooCloseToTerrain = MossySpiderTileAccessibilityOverride;

        template.meatPoints = 12;
        template.countsAsAKill = 1;
        template.shortcutColor = MossColor;
        template.shortcutSegments = 8;
        template.scaryness = 0.8f;
        template.deliciousness = 0.1f;

        return template;
    }

    private static int MossySpiderTileAccessibilityOverride(
        AImap aiMap,
        RWCustom.IntVector2 position)
    {
        AItile.Accessibility accessibility = aiMap.getAItile(position).acc;
        if (accessibility == AItile.Accessibility.Climb ||
            accessibility == AItile.Accessibility.Wall)
        {
            return 1;
        }

        return 0;
    }

    private static Creature CreateRealizedCreature(AbstractCreature abstractCreature)
    {
        return new MossySpider(abstractCreature, abstractCreature.world);
    }

    private static AbstractCreatureAI CreateAbstractAI(AbstractCreature abstractCreature)
    {
        return new MossySpiderAbstractAI(abstractCreature.world, abstractCreature);
    }

    private static ArtificialIntelligence CreateRealizedAI(AbstractCreature abstractCreature)
    {
        return new MossySpiderAI(abstractCreature, abstractCreature.world);
    }
}
