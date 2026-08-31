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
            // Layer 1 / module 1: MossySpider owns an independent AIMap pathing type.
            // Realized behavior modules and MovementConnections are intentionally
            // still left for their dedicated design passes.
            HasAI = true,
            RequireAIMap = true,
            DoPreBakedPathing = true,
            BaseDamageResistance = 8f,
            BaseStunResistance = 3f
        };

        // Layer 1 / module 2: exact AI tile accessibility.
        // MossySpider is a Deer-like giant walker: its body may occupy open Air,
        // Corridor and Ceiling-classified space while its legs find support below.
        // It does not use climbable poles/beams or vertical Wall paths.
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
            .SetExactTileResistance(AItile.Accessibility.Sand, 1f, PathCost.Legality.Allowed);

        CreatureTemplate template = builder.Build();

        // Abstract movement and actual MovementConnection rules are deliberately
        // not enabled yet; those belong to later modules.
        template.canAutoAbstractPath = false;
        template.offScreenSpeed = 0f;
        template.bodySize = 12f;
        template.grasps = 0;
        template.visualRadius = 700f;
        template.movementBasedVision = 0f;
        template.dangerousToPlayer = 0f;
        template.communityInfluence = 0f;

        // Water has the same AI path cost as land. Locomotion will later decide the
        // physical mode continuously: shallow water = legs walk on the bottom;
        // deep water = moss-covered dorsal body floats at the surface and legs paddle.
        template.waterRelationship = CreatureTemplate.WaterRelationship.Amphibious;
        template.canSwim = true;
        template.waterPathingResistance = 1f;
        template.canFly = false;

        template.meatPoints = 12;
        template.countsAsAKill = 1;
        template.shortcutColor = MossColor;
        template.shortcutSegments = 8;
        template.scaryness = 0.8f;
        template.deliciousness = 0.1f;

        return template;
    }

    internal override Creature CreateRealizedCreature(AbstractCreature abstractCreature)
    {
        return new MossySpider(abstractCreature, abstractCreature.world);
    }

    internal override IEnumerable<string> WorldFileAliases()
    {
        yield return "MossySpider";
        yield return "mossyspider";
        yield return "mossy spider";
    }
}
