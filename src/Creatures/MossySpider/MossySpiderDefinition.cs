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
        CreatureTemplate template = new CreatureTemplateBuilder(
            Type,
            "Mossy Spider")
        {
            HasAI = false,
            RequireAIMap = false,
            DoPreBakedPathing = false,
            BaseDamageResistance = 8f,
            BaseStunResistance = 3f
        }.Build();

        template.canAutoAbstractPath = false;
        template.offScreenSpeed = 0f;
        template.bodySize = 12f;
        template.grasps = 0;
        template.visualRadius = 700f;
        template.movementBasedVision = 0f;
        template.dangerousToPlayer = 0f;
        template.communityInfluence = 0f;
        template.waterRelationship = CreatureTemplate.WaterRelationship.AirOnly;
        template.canSwim = false;
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
