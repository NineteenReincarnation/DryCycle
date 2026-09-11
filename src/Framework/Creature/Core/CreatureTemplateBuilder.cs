using System;
using System.Collections.Generic;

namespace DryCycle.Framework.Creature.Core;

/// <summary>
/// 用更少的重复代码安全构造 Rain World 的 CreatureTemplate。
/// Builder 只封装模板创建中容易重复或容易配置错误的部分；普通 CreatureTemplate 字段仍可在 Build 后直接修改。
///
/// Safely constructs Rain World's CreatureTemplate with less repetitive setup.
/// The builder only wraps template concerns that are repetitive or error-prone; ordinary CreatureTemplate fields remain directly editable after Build.
/// </summary>
public sealed class CreatureTemplateBuilder
{
    private readonly CreatureTemplate.Type _type;
    private readonly Dictionary<AItile.Accessibility, PathCost> _tileResistances = new();
    private readonly Dictionary<AItile.Accessibility, PathCost> _exactTileResistances = new();
    private readonly Dictionary<MovementConnection.MovementType, PathCost> _connectionResistances = new();

    private string _name;
    private CreatureTemplate.Type _ancestorType;
    private CreatureTemplate.Relationship _defaultRelationship =
        new(CreatureTemplate.Relationship.Type.Ignores, 0f);

    private bool? _hasAI;
    private bool? _requireAIMap;
    private bool _ownsPreBakedPathing;
    private CreatureTemplate.Type _preBakedPathingAncestorType;
    private float? _baseDamageResistance;
    private float? _baseStunResistance;
    private float? _instantDeathDamageLimit;

    /// <summary>
    /// 为指定生物 Type 创建模板装配器。默认显示名称使用 Type.value。
    ///
    /// Creates a template builder for the specified creature type. The display name defaults to Type.value.
    /// </summary>
    public CreatureTemplateBuilder(CreatureTemplate.Type type)
    {
        if (type == null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (string.IsNullOrWhiteSpace(type.value))
        {
            throw new ArgumentException("CreatureTemplate.Type must have a non-empty value.", nameof(type));
        }

        _type = type;
        _name = type.value.Trim();
    }

    /// <summary>
    /// 设置模板显示名称。
    ///
    /// Sets the template display name.
    /// </summary>
    public CreatureTemplateBuilder Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Creature template name cannot be empty.", nameof(name));
        }

        _name = name.Trim();
        return this;
    }

    /// <summary>
    /// 指定要继承的已有生物模板。没有显式覆盖的模板字段继续使用 ancestor 的继承值。
    ///
    /// Selects an existing creature template as the ancestor. Template fields not explicitly overridden keep the inherited values.
    /// </summary>
    public CreatureTemplateBuilder Ancestor(CreatureTemplate.Type type)
    {
        ValidateReferencedType(type, nameof(type));

        if (SameType(_type, type))
        {
            throw new ArgumentException(
                $"Creature template '{_type.value}' cannot use itself as its ancestor.",
                nameof(type));
        }

        _ancestorType = type;
        return this;
    }

    /// <summary>
    /// 设置 CreatureTemplate 构造函数要求的默认生物关系。
    /// 这只是模板的默认关系底值，不负责建立具体物种之间的生态关系表。
    ///
    /// Sets the default relationship required by the CreatureTemplate constructor.
    /// This is only the template fallback relationship and does not establish species-to-species ecology relationships.
    /// </summary>
    public CreatureTemplateBuilder DefaultRelationship(
        CreatureTemplate.Relationship.Type type,
        float intensity)
    {
        if (type == null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        ValidateFinite(intensity, nameof(intensity));
        _defaultRelationship = new CreatureTemplate.Relationship(type, intensity);
        return this;
    }

    /// <summary>
    /// 显式设置模板是否使用 ArtificialIntelligence 系统。
    /// 如果不调用本方法，Build 不覆盖 CreatureTemplate 从 ancestor 继承的 AI 值。
    ///
    /// Explicitly sets whether the template uses the ArtificialIntelligence system.
    /// When omitted, Build does not overwrite the AI value inherited from the ancestor.
    /// </summary>
    public CreatureTemplateBuilder AI(bool enabled = true)
    {
        _hasAI = enabled;
        return this;
    }

    /// <summary>
    /// 显式设置该模板是否需要房间 AI Map。
    /// 如果不调用本方法，Build 不覆盖 CreatureTemplate 从 ancestor 继承的 requireAImap 值。
    ///
    /// Explicitly sets whether the template requires room AI maps.
    /// When omitted, Build does not overwrite the requireAImap value inherited from the ancestor.
    /// </summary>
    public CreatureTemplateBuilder RequireAIMap(bool required = true)
    {
        _requireAIMap = required;
        return this;
    }

    /// <summary>
    /// 声明这个模板自己拥有一个 pre-baked pathing 槽位。
    /// 不能和 ReusePreBakedPathing 同时使用。
    ///
    /// Declares that this template owns its own pre-baked pathing slot.
    /// Cannot be combined with ReusePreBakedPathing.
    /// </summary>
    public CreatureTemplateBuilder OwnPreBakedPathing()
    {
        if (_preBakedPathingAncestorType != null)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot own a pre-baked pathing slot and reuse another creature's slot at the same time.");
        }

        _ownsPreBakedPathing = true;
        return this;
    }

    /// <summary>
    /// 复用另一个已有模板的 pre-baked pathing 槽位，不为当前自定义生物新增房间烘焙槽位。
    /// Build 会确认目标模板存在并确实拥有 pre-baked pathing，同时自动要求 AI Map。
    ///
    /// Reuses another existing template's pre-baked pathing slot without adding a new baked room slot for the custom creature.
    /// Build verifies that the target exists and owns pre-baked pathing, and automatically requires an AI map.
    /// </summary>
    public CreatureTemplateBuilder ReusePreBakedPathing(CreatureTemplate.Type type)
    {
        ValidateReferencedType(type, nameof(type));

        if (_ownsPreBakedPathing)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot reuse another creature's pre-baked pathing slot after OwnPreBakedPathing was selected.");
        }

        _preBakedPathingAncestorType = type;
        return this;
    }

    /// <summary>
    /// 设置一种 AI Tile 的寻路代价。重复设置同一种 Accessibility 时，后一次配置替换前一次配置。
    ///
    /// Sets the path cost for one AI tile accessibility. Setting the same accessibility again replaces the previous value.
    /// </summary>
    public CreatureTemplateBuilder Tile(
        AItile.Accessibility accessibility,
        float resistance,
        PathCost.Legality legality = PathCost.Legality.Allowed)
    {
        ValidateResistance(resistance, nameof(resistance));
        _tileResistances[accessibility] = new PathCost(resistance, legality);
        return this;
    }

    /// <summary>
    /// 在 CreatureTemplate 原版构造和可达性归一化完成后，强制写入一种 Tile 的最终 PathCost。
    /// 用于需要非连续可达性规则的生物，例如允许 Air 但禁止 Climb、Wall 的情况。
    ///
    /// Forces the final PathCost for one tile after CreatureTemplate's vanilla construction and accessibility normalization.
    /// Use this for non-monotonic accessibility rules, such as allowing Air while rejecting Climb and Wall.
    /// </summary>
    public CreatureTemplateBuilder ExactTile(
        AItile.Accessibility accessibility,
        float resistance,
        PathCost.Legality legality)
    {
        ValidateResistance(resistance, nameof(resistance));
        _exactTileResistances[accessibility] = new PathCost(resistance, legality);
        return this;
    }

    /// <summary>
    /// 设置一种 MovementConnection 的寻路代价。重复设置同一种 MovementType 时，后一次配置替换前一次配置。
    ///
    /// Sets the path cost for one movement connection type. Setting the same movement type again replaces the previous value.
    /// </summary>
    public CreatureTemplateBuilder Connection(
        MovementConnection.MovementType movementType,
        float resistance,
        PathCost.Legality legality = PathCost.Legality.Allowed)
    {
        ValidateResistance(resistance, nameof(resistance));
        _connectionResistances[movementType] = new PathCost(resistance, legality);
        return this;
    }

    /// <summary>
    /// 显式设置基础伤害抗性。没有调用时保留 CreatureTemplate 默认值或 ancestor 继承值。
    ///
    /// Explicitly sets base damage resistance. When omitted, the CreatureTemplate default or ancestor-inherited value is preserved.
    /// </summary>
    public CreatureTemplateBuilder DamageResistance(float value)
    {
        ValidateResistance(value, nameof(value));
        _baseDamageResistance = value;
        return this;
    }

    /// <summary>
    /// 显式设置基础眩晕抗性。没有调用时保留 CreatureTemplate 默认值或 ancestor 继承值。
    ///
    /// Explicitly sets base stun resistance. When omitted, the CreatureTemplate default or ancestor-inherited value is preserved.
    /// </summary>
    public CreatureTemplateBuilder StunResistance(float value)
    {
        ValidateResistance(value, nameof(value));
        _baseStunResistance = value;
        return this;
    }

    /// <summary>
    /// 显式设置单次伤害的即死阈值。没有调用时保留 CreatureTemplate 默认值或 ancestor 继承值。
    ///
    /// Explicitly sets the instant-death damage threshold. When omitted, the CreatureTemplate default or ancestor-inherited value is preserved.
    /// </summary>
    public CreatureTemplateBuilder InstantDeathLimit(float value)
    {
        ValidateResistance(value, nameof(value));
        _instantDeathDamageLimit = value;
        return this;
    }

    /// <summary>
    /// 验证当前配置并构造一份新的 CreatureTemplate。
    /// 每次 Build 都创建独立的 resistance 列表，不会让两个模板共享可变集合。
    ///
    /// Validates the current configuration and creates a fresh CreatureTemplate.
    /// Every Build creates independent resistance lists so templates never share mutable setup collections.
    /// </summary>
    public CreatureTemplate Build()
    {
        ValidateBuildState();

        CreatureTemplate ancestor = ResolveExistingTemplate(_ancestorType, "ancestor");
        CreatureTemplate preBakedPathingAncestor =
            ResolveExistingTemplate(_preBakedPathingAncestorType, "pre-baked pathing ancestor");

        if (preBakedPathingAncestor != null && !preBakedPathingAncestor.doPreBakedPathing)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot reuse pre-baked pathing from '{_preBakedPathingAncestorType.value}' because that template does not own a pre-baked pathing slot.");
        }

        CreatureTemplate template = new(
            _type,
            ancestor,
            CreateTileResistanceList(),
            CreateConnectionResistanceList(),
            _defaultRelationship)
        {
            name = _name
        };

        if (_hasAI.HasValue)
        {
            template.AI = _hasAI.Value;
        }

        if (_requireAIMap.HasValue)
        {
            template.requireAImap = _requireAIMap.Value;
        }

        if (_baseDamageResistance.HasValue)
        {
            template.baseDamageResistance = _baseDamageResistance.Value;
        }

        if (_baseStunResistance.HasValue)
        {
            template.baseStunResistance = _baseStunResistance.Value;
        }

        if (_instantDeathDamageLimit.HasValue)
        {
            template.instantDeathDamageLimit = _instantDeathDamageLimit.Value;
        }

        if (_ownsPreBakedPathing)
        {
            template.doPreBakedPathing = true;
            template.preBakedPathingAncestor = null;
        }
        else if (preBakedPathingAncestor != null)
        {
            // 复用已有槽位时必须覆盖 ancestor 可能继承来的 doPreBakedPathing=true，
            // 否则这个自定义 Type 会被误认为还要拥有自己的房间烘焙槽位。
            // Reusing an existing slot must override doPreBakedPathing=true possibly inherited from the ancestor,
            // otherwise the custom type would incorrectly claim its own room-bake slot.
            template.doPreBakedPathing = false;
            template.preBakedPathingAncestor = preBakedPathingAncestor;
            template.requireAImap = true;
        }

        ApplyExactTileResistances(template);
        return template;
    }

    private void ValidateBuildState()
    {
        if (_type.Index < 0)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot be built because its CreatureTemplate.Type is not registered in ExtEnum.");
        }

        if (string.IsNullOrWhiteSpace(_name))
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot be built with an empty name.");
        }

        if (_ownsPreBakedPathing && _preBakedPathingAncestorType != null)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot both own and reuse a pre-baked pathing slot.");
        }
    }

    private List<TileTypeResistance> CreateTileResistanceList()
    {
        List<TileTypeResistance> result = new(_tileResistances.Count);
        foreach (KeyValuePair<AItile.Accessibility, PathCost> pair in _tileResistances)
        {
            result.Add(new TileTypeResistance(pair.Key, pair.Value.resistance, pair.Value.legality));
        }

        return result;
    }

    private List<TileConnectionResistance> CreateConnectionResistanceList()
    {
        List<TileConnectionResistance> result = new(_connectionResistances.Count);
        foreach (KeyValuePair<MovementConnection.MovementType, PathCost> pair in _connectionResistances)
        {
            result.Add(new TileConnectionResistance(pair.Key, pair.Value.resistance, pair.Value.legality));
        }

        return result;
    }

    private void ApplyExactTileResistances(CreatureTemplate template)
    {
        if (_exactTileResistances.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<AItile.Accessibility, PathCost> pair in _exactTileResistances)
        {
            int index = (int)pair.Key;
            if (index < 0 || index >= template.pathingPreferencesTiles.Length)
            {
                throw new InvalidOperationException(
                    $"Creature template '{_type.value}' cannot apply exact tile accessibility '{pair.Key}' because index {index} is outside the pathing table.");
            }

            template.pathingPreferencesTiles[index] = pair.Value;
        }

        // CreatureTemplate 会在构造过程中先根据普通 Tile resistance 计算并归一化最大可达地形。
        // ExactTile 修改的是最终表，因此这里必须根据最终结果重新计算 maxAccessibleTerrain。
        // CreatureTemplate calculates and normalizes max accessibility from ordinary tile resistances during construction.
        // ExactTile changes the final table, so maxAccessibleTerrain must be recalculated from that final result.
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

    private CreatureTemplate ResolveExistingTemplate(CreatureTemplate.Type type, string role)
    {
        if (type == null)
        {
            return null;
        }

        if (StaticWorld.creatureTemplates == null)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot resolve its {role} '{type.value}' before StaticWorld.creatureTemplates has been initialized.");
        }

        if (type.Index < 0 || type.Index >= StaticWorld.creatureTemplates.Length)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot resolve its {role} '{type.value}' because type index {type.Index} is outside the StaticWorld template array length {StaticWorld.creatureTemplates.Length}.");
        }

        CreatureTemplate template = StaticWorld.creatureTemplates[type.Index];
        if (template == null)
        {
            throw new InvalidOperationException(
                $"Creature template '{_type.value}' cannot resolve its {role} '{type.value}' because that template has not been initialized.");
        }

        return template;
    }

    private static void ValidateReferencedType(CreatureTemplate.Type type, string parameterName)
    {
        if (type == null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(type.value))
        {
            throw new ArgumentException("Referenced CreatureTemplate.Type must have a non-empty value.", parameterName);
        }
    }

    private static bool SameType(CreatureTemplate.Type left, CreatureTemplate.Type right)
    {
        return left != null &&
               right != null &&
               left.Index == right.Index &&
               StringComparer.Ordinal.Equals(left.value, right.value);
    }

    private static void ValidateResistance(float value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Resistance values cannot be negative.");
        }
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be a finite number.");
        }
    }
}
