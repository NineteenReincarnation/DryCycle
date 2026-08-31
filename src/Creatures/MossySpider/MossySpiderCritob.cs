using System.Collections.Generic;
using DevInterface;
using Fisobs.Creatures;
using Fisobs.Core;
using Fisobs.Sandbox;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

internal sealed class MossySpiderCritob : Critob
{
    private static readonly Color DevColor = new(0.48f, 0.52f, 0.22f);

    internal MossySpiderCritob() : base(MossySpiderEnums.Type)
    {
        Icon = new SimpleIcon("Kill_BigSpider", DevColor);
        LoadedPerformanceCost = 35f;
        ShelterDanger = ShelterDanger.TooLarge;
    }

    public override int ExpeditionScore() => 12;

    public override Color DevtoolsMapColor(AbstractCreature acrit) => DevColor;

    public override string DevtoolsMapName(AbstractCreature acrit) => "mSp";

    public override IEnumerable<string> WorldFileAliases() =>
    [
        "mossyspider",
        "mossy spider"
    ];

    public override IEnumerable<RoomAttractivenessPanel.Category> DevtoolsRoomAttraction() => [];

    public override CreatureTemplate CreateTemplate()
    {
        CreatureTemplate template = new CreatureFormula(this)
        {
            DefaultRelationship = new(CreatureTemplate.Relationship.Type.Ignores, 0f),
            DamageResistances = new() { Base = 8f },
            StunResistances = new() { Base = 3f },
            HasAI = false
        }.IntoTemplate();

        template.requireAImap = false;
        template.doPreBakedPathing = false;
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
        template.shortcutColor = DevColor;
        template.shortcutSegments = 8;
        template.scaryness = 0.8f;
        template.deliciousness = 0.1f;
        return template;
    }

    public override void EstablishRelationships()
    {
        // Intentionally empty for the first visual/registration pass.
    }

    public override ArtificialIntelligence CreateRealizedAI(AbstractCreature acrit) => null;

    public override Creature CreateRealizedCreature(AbstractCreature acrit) => new MossySpider(acrit, acrit.world);

    public override void LoadResources(RainWorld rainWorld)
    {
        // MossySpider currently uses only built-in Rain World sprites/meshes.
    }
}
