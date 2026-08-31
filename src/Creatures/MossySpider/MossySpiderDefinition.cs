using System.Collections.Generic;
using DryCycle.Registration;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

internal sealed class MossySpiderDefinition : CreatureDefinition
{
    private static readonly Color MossColor = new(0.48f, 0.52f, 0.22f);

    internal MossySpiderDefinition() : base(MossySpiderEnums.Type)
    {
    }

    internal override CreatureTemplate CreateTemplate()
    {
        CreatureTemplateBuilder builder = new(
            Type,
            "Mossy Spider")
        {
            // MossySpider owns its AI behavior while reusing Deer's existing pre-baked
            // AI-map slot so ordinary installed room files remain load-compatible.
            HasAI = true,
            RequireAIMap = true,
            DoPreBakedPathing = false,
            PreBakedPathingAncestorType = CreatureTemplate.Type.Deer,
            BaseDamageResistance = 8f,
            BaseStunResistance = 3f
        };

        // Body-space accessibility: everything except Wall, Climb and Solid is usable.
        builder
            .SetExactTileResistance(AItile.Accessibility.OffScreen, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.Floor, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.CurvedFloor, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.Corridor, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.Climb, 100f, PathCost.Legality.IllegalTile)
            .SetExactTileResistance(AItile.Accessibility.Wall, 100f, PathCost.Legality.IllegalTile)
            .SetExactTileResistance(AItile.Accessibility.Ceiling, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.Air, 1f, PathCost.Legality.Allowed)
            .SetExactTileResistance(AItile.Accessibility.Solid, 100f, PathCost.Legality.SolidTile)
            .SetExactTileResistance(AItile.Accessibility.Sand, 1f, PathCost.Legality.Allowed)

            // The creature migrates through side/off-screen space like a large walker.
            // It does not use ordinary shortcuts, dens or pole/wall-specific movement.
            .AddConnectionResistance(MovementConnection.MovementType.Standard, 1f)
            .AddConnectionResistance(MovementConnection.MovementType.OpenDiagonal, 1f)
            .AddConnectionResistance(MovementConnection.MovementType.OutsideRoom, 1f)
            .AddConnectionResistance(MovementConnection.MovementType.SideHighway, 1f)
            .AddConnectionResistance(MovementConnection.MovementType.OffScreenMovement, 1f)
            .AddConnectionResistance(MovementConnection.MovementType.BetweenRooms, 1f);

        CreatureTemplate template = builder.Build();

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

    internal override Creature CreateRealizedCreature(AbstractCreature abstractCreature)
    {
        return new MossySpider(abstractCreature, abstractCreature.world);
    }

    internal override AbstractCreatureAI CreateAbstractAI(AbstractCreature abstractCreature)
    {
        return new MossySpiderAbstractAI(abstractCreature.world, abstractCreature);
    }

    internal override ArtificialIntelligence CreateRealizedAI(AbstractCreature abstractCreature)
    {
        return new MossySpiderAI(abstractCreature, abstractCreature.world);
    }

    internal override IEnumerable<string> WorldFileAliases()
    {
        yield return "MossySpider";
        yield return "mossyspider";
        yield return "mossy spider";
    }
}
