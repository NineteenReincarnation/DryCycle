using System.Collections.Generic;

namespace DryCycle.Registration;

/// <summary>
/// Small DryCycle-owned replacement for the part of Fisobs' CreatureFormula that
/// we actually need. It deliberately creates fresh resistance lists per build and
/// leaves creature-specific fields available for explicit configuration afterwards.
/// </summary>
internal sealed class CreatureTemplateBuilder
{
    private readonly CreatureTemplate.Type _type;
    private readonly CreatureTemplate _ancestor;
    private readonly List<TileTypeResistance> _tileResistances = new();
    private readonly List<TileConnectionResistance> _connectionResistances = new();

    internal CreatureTemplateBuilder(
        CreatureTemplate.Type type,
        string name,
        CreatureTemplate ancestor = null)
    {
        _type = type;
        _ancestor = ancestor;
        Name = name;
    }

    internal string Name { get; set; }

    internal bool HasAI { get; set; }

    internal bool RequireAIMap { get; set; }

    internal bool DoPreBakedPathing { get; set; }

    internal float BaseDamageResistance { get; set; } = 1f;

    internal float BaseStunResistance { get; set; } = 1f;

    internal float InstantDeathDamageLimit { get; set; } = float.MaxValue;

    internal CreatureTemplate.Relationship DefaultRelationship { get; set; } =
        new(CreatureTemplate.Relationship.Type.Ignores, 0f);

    internal CreatureTemplateBuilder AddTileResistance(
        AItile.Accessibility accessibility,
        float resistance,
        PathCost.Legality legality = PathCost.Legality.Allowed)
    {
        _tileResistances.Add(new TileTypeResistance(accessibility, resistance, legality));
        return this;
    }

    internal CreatureTemplateBuilder AddConnectionResistance(
        MovementConnection.MovementType movementType,
        float resistance,
        PathCost.Legality legality = PathCost.Legality.Allowed)
    {
        _connectionResistances.Add(new TileConnectionResistance(movementType, resistance, legality));
        return this;
    }

    internal CreatureTemplate Build()
    {
        CreatureTemplate template = new(
            _type,
            _ancestor,
            new List<TileTypeResistance>(_tileResistances),
            new List<TileConnectionResistance>(_connectionResistances),
            DefaultRelationship)
        {
            name = Name,
            AI = HasAI,
            requireAImap = RequireAIMap,
            doPreBakedPathing = DoPreBakedPathing,
            baseDamageResistance = BaseDamageResistance,
            baseStunResistance = BaseStunResistance,
            instantDeathDamageLimit = InstantDeathDamageLimit
        };

        return template;
    }
}
