using System;
using System.Collections.Generic;

namespace DryCycle.Registration;

/// <summary>
/// Small DryCycle-owned helper for building custom CreatureTemplate instances.
/// It creates fresh resistance lists per build and leaves creature-specific fields
/// available for explicit configuration afterwards.
/// </summary>
internal sealed class CreatureTemplateBuilder
{
    private readonly CreatureTemplate.Type _type;
    private readonly CreatureTemplate _ancestor;
    private readonly List<TileTypeResistance> _tileResistances = new();
    private readonly List<TileConnectionResistance> _connectionResistances = new();
    private readonly Dictionary<AItile.Accessibility, PathCost> _exactTileResistances = new();

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

    /// <summary>
    /// Reuses an existing vanilla pre-baked AI-map slot without making this custom
    /// creature add a new entry to StaticWorld.preBakedPathingCreatures. This is the
    /// safe choice for normal installed room files, whose serialized AI heat maps were
    /// baked against the vanilla pre-baked creature count.
    /// </summary>
    internal CreatureTemplate.Type PreBakedPathingAncestorType { get; set; }

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

    /// <summary>
    /// Applies a tile preference after CreatureTemplate's constructor has performed
    /// its vanilla max-accessibility normalization. This is required for custom
    /// creatures whose legal accessibility set is intentionally non-monotonic, such
    /// as MossySpider allowing Air while explicitly rejecting Climb and Wall.
    /// </summary>
    internal CreatureTemplateBuilder SetExactTileResistance(
        AItile.Accessibility accessibility,
        float resistance,
        PathCost.Legality legality)
    {
        _exactTileResistances[accessibility] = new PathCost(resistance, legality);
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
        if (DoPreBakedPathing && PreBakedPathingAncestorType != null)
        {
            throw new InvalidOperationException(
                "A CreatureTemplate cannot both own a pre-baked pathing slot and inherit one.");
        }

        CreatureTemplate preBakedPathingAncestor = null;
        if (PreBakedPathingAncestorType != null)
        {
            preBakedPathingAncestor = StaticWorld.GetCreatureTemplate(PreBakedPathingAncestorType);
            if (preBakedPathingAncestor == null || !preBakedPathingAncestor.doPreBakedPathing)
            {
                throw new InvalidOperationException(
                    $"Pre-baked pathing ancestor {PreBakedPathingAncestorType.value} is unavailable or does not own a pre-baked pathing slot.");
            }
        }

        CreatureTemplate template = new(
            _type,
            _ancestor,
            new List<TileTypeResistance>(_tileResistances),
            new List<TileConnectionResistance>(_connectionResistances),
            DefaultRelationship)
        {
            name = Name,
            AI = HasAI,
            requireAImap = RequireAIMap || preBakedPathingAncestor != null,
            doPreBakedPathing = DoPreBakedPathing,
            preBakedPathingAncestor = preBakedPathingAncestor,
            baseDamageResistance = BaseDamageResistance,
            baseStunResistance = BaseStunResistance,
            instantDeathDamageLimit = InstantDeathDamageLimit
        };

        ApplyExactTileResistances(template);
        return template;
    }

    private void ApplyExactTileResistances(CreatureTemplate template)
    {
        if (_exactTileResistances.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<AItile.Accessibility, PathCost> pair in _exactTileResistances)
        {
            template.pathingPreferencesTiles[(int)pair.Key] = pair.Value;
        }

        // CreatureTemplate normally derives this before its accessibility hierarchy
        // normalization. Recalculate it from the final exact table so later runtime
        // accessibility checks see the custom creature's real accessibility envelope.
        int maxAccessibleTerrain = 0;
        for (int i = 0; i < template.pathingPreferencesTiles.Length; i++)
        {
            if (i == (int)AItile.Accessibility.Sand)
            {
                continue;
            }

            if (template.pathingPreferencesTiles[i].legality == PathCost.Legality.Allowed)
            {
                maxAccessibleTerrain = i;
            }
        }

        template.maxAccessibleTerrain = maxAccessibleTerrain;
    }
}
