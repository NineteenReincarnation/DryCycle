using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DryCycle.Framework.Creature.Core;

/// <summary>
/// 用于描述一种自定义生物类型的注册信息；一旦完成注册，该描述对象即不可再修改。
/// 本类刻意只保存生物的核心身份信息与对象工厂声明。
/// 它不负责 AI 行为、绘制、战斗、生态、沙盒接入、资源加载、DevTools 接入，
/// 或任何其他属于具体功能层的内容。
///
/// Immutable-after-registration description of one custom creature type.
/// This class deliberately contains only core identity and factory declarations.
/// It does not own AI behavior, graphics, combat, ecology, sandbox integration,
/// resource loading, DevTools integration, or other feature-specific concerns.
/// </summary>
public sealed class CreatureDescriptor
{
    private readonly List<string> _aliases = new();
    private readonly HashSet<string> _aliasSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReadOnlyCollection<string> _readOnlyAliases;
    private bool _isFrozen;

    /// <summary>
    /// 创建一个新的生物描述对象。
    /// <paramref name="type"/> 是 Rain World 使用的生物类型；<paramref name="ownerId"/> 是拥有该注册项的 Mod 或框架标识。
    ///
    /// Creates a new creature descriptor.
    /// <paramref name="type"/> is the Rain World creature type; <paramref name="ownerId"/> identifies the mod or framework that owns the registration.
    /// </summary>
    public CreatureDescriptor(CreatureTemplate.Type type, string ownerId)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));

        if (string.IsNullOrWhiteSpace(type.value))
        {
            throw new ArgumentException("Creature type must have a non-empty value.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("Creature owner ID must not be empty.", nameof(ownerId));
        }

        OwnerId = ownerId.Trim();
        DisplayName = type.value;
        _readOnlyAliases = _aliases.AsReadOnly();
    }

    /// <summary>
    /// 此描述对象所代表的 Rain World 生物类型。
    ///
    /// Rain World's creature type represented by this descriptor.
    /// </summary>
    public CreatureTemplate.Type Type { get; }

    /// <summary>
    /// 注册该生物的 Mod 或框架的稳定标识。
    /// 该值用于诊断信息与注册冲突报告，不作为玩家可见文本使用。
    ///
    /// Stable identifier of the mod or framework owner that registered this creature.
    /// This is intended for diagnostics and collision reporting, not for display text.
    /// </summary>
    public string OwnerId { get; }

    /// <summary>
    /// 面向人类阅读的生物名称。默认使用生物类型的 value。
    ///
    /// Human-readable creature name. Defaults to the creature type value.
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>
    /// 该生物额外接受的名称别名；忽略大小写，并保持声明顺序。
    /// 不需要在这里重复填写标准生物类型名称，Registry 会单独索引标准名称。
    /// 返回的是只读集合，外部代码不能借此修改描述对象。
    ///
    /// Additional case-insensitive names accepted for this creature, in declaration order.
    /// The canonical creature type value is not required here; the registry indexes it
    /// separately. The returned collection cannot mutate the descriptor.
    /// </summary>
    public IReadOnlyList<string> Aliases => _readOnlyAliases;

    /// <summary>
    /// 创建将被写入 StaticWorld 的 CreatureTemplate。
    /// 这是一个完整注册的 CreatureDescriptor 唯一必须提供的工厂。
    ///
    /// Creates the creature template inserted into StaticWorld.
    /// This is the only required factory for a fully registered descriptor.
    /// </summary>
    public Func<CreatureTemplate> TemplateFactory { get; private set; }

    /// <summary>
    /// 可选：替换 Rain World 默认创建 CreatureState 的方式。
    /// 如果没有设置，则保留 Rain World 原本已经创建的 State。
    ///
    /// Optional replacement for Rain World's default CreatureState creation.
    /// When unset, the original state created by Rain World is preserved.
    /// </summary>
    public Func<AbstractCreature, CreatureState> StateFactory { get; private set; }

    /// <summary>
    /// 可选：替换 Rain World 默认创建实体 Creature 的方式。
    /// 如果没有设置，则完整保留 Rain World 原本的 Realize 流程。
    ///
    /// Optional replacement for Rain World's realized Creature creation.
    /// When unset, Rain World's original realization path is preserved.
    /// </summary>
    public Func<AbstractCreature, global::Creature> CreatureFactory { get; private set; }

    /// <summary>
    /// 可选：替换 Rain World 默认创建 AbstractCreatureAI 的方式。
    /// 如果没有设置，则保留 Rain World 原本的 Abstract AI。
    ///
    /// Optional replacement for Rain World's AbstractCreatureAI creation.
    /// When unset, Rain World's original abstract AI is preserved.
    /// </summary>
    public Func<AbstractCreature, AbstractCreatureAI> AbstractAIFactory { get; private set; }

    /// <summary>
    /// 可选：替换 Rain World 默认创建实际 ArtificialIntelligence 的方式。
    /// 如果没有设置，则继续使用 Rain World 原本的 AI 分派流程。
    ///
    /// Optional replacement for Rain World's realized ArtificialIntelligence creation.
    /// When unset, Rain World's original AI dispatch is preserved.
    /// </summary>
    public Func<AbstractCreature, ArtificialIntelligence> RealizedAIFactory { get; private set; }

    /// <summary>
    /// 当该描述对象已经被 CreatureRegistry 接受并注册后为 true。
    /// 冻结后的描述对象不能继续修改。
    ///
    /// True after the descriptor has been accepted by CreatureRegistry.
    /// Frozen descriptors cannot be modified.
    /// </summary>
    public bool IsFrozen => _isFrozen;

    /// <summary>
    /// 设置玩家与开发者可读的生物显示名称。
    ///
    /// Sets the human-readable display name of the creature.
    /// </summary>
    public CreatureDescriptor SetDisplayName(string displayName)
    {
        ThrowIfFrozen();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Creature display name must not be empty.", nameof(displayName));
        }

        DisplayName = displayName.Trim();
        return this;
    }

    /// <summary>
    /// 设置显示名称的简写语法糖；行为与 <see cref="SetDisplayName"/> 完全一致。
    ///
    /// Fluent shorthand for setting the display name; behavior is identical to <see cref="SetDisplayName"/>.
    /// </summary>
    public CreatureDescriptor Name(string displayName) => SetDisplayName(displayName);

    /// <summary>
    /// 添加一个 world.txt 等文本入口可使用的生物别名。
    /// 别名忽略大小写去重，但保留第一次声明时的文本与顺序。
    ///
    /// Adds a creature alias usable by text-based entry points such as world.txt.
    /// Aliases are deduplicated case-insensitively while preserving the first declaration's text and order.
    /// </summary>
    public CreatureDescriptor AddAlias(string alias)
    {
        ThrowIfFrozen();

        string normalized = NormalizeAlias(alias);
        if (_aliasSet.Add(normalized))
        {
            _aliases.Add(normalized);
        }

        return this;
    }

    /// <summary>
    /// 添加单个别名的简写语法糖；行为与 <see cref="AddAlias"/> 完全一致。
    ///
    /// Fluent shorthand for adding one alias; behavior is identical to <see cref="AddAlias"/>.
    /// </summary>
    public CreatureDescriptor Alias(string alias) => AddAlias(alias);

    /// <summary>
    /// 一次添加多个生物别名。
    ///
    /// Adds multiple creature aliases at once.
    /// </summary>
    public CreatureDescriptor AddAliases(IEnumerable<string> aliases)
    {
        ThrowIfFrozen();

        if (aliases == null)
        {
            throw new ArgumentNullException(nameof(aliases));
        }

        foreach (string alias in aliases)
        {
            AddAlias(alias);
        }

        return this;
    }

    /// <summary>
    /// 批量添加别名的简写语法糖，可直接写 <c>.WithAliases("name1", "name2")</c>。
    /// 保持与 <see cref="AddAliases"/> 相同的去重、顺序与冻结规则。
    ///
    /// Fluent shorthand for adding multiple aliases, allowing calls such as <c>.WithAliases("name1", "name2")</c>.
    /// It preserves the same deduplication, ordering, and freeze rules as <see cref="AddAliases"/>.
    /// </summary>
    public CreatureDescriptor WithAliases(params string[] aliases) => AddAliases(aliases);

    /// <summary>
    /// 删除一个已声明的生物别名；比较时忽略大小写。
    ///
    /// Removes a declared creature alias using case-insensitive comparison.
    /// </summary>
    public bool RemoveAlias(string alias)
    {
        ThrowIfFrozen();

        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        string normalized = alias.Trim();
        if (!_aliasSet.Remove(normalized))
        {
            return false;
        }

        for (int i = 0; i < _aliases.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_aliases[i], normalized))
            {
                _aliases.RemoveAt(i);
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// 清空所有额外别名。
    ///
    /// Clears all additional aliases.
    /// </summary>
    public CreatureDescriptor ClearAliases()
    {
        ThrowIfFrozen();
        _aliases.Clear();
        _aliasSet.Clear();
        return this;
    }

    /// <summary>
    /// 设置 CreatureTemplate 工厂。该工厂是完成注册所必需的。
    ///
    /// Sets the CreatureTemplate factory. This factory is required for registration.
    /// </summary>
    public CreatureDescriptor SetTemplateFactory(Func<CreatureTemplate> factory)
    {
        ThrowIfFrozen();
        TemplateFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// 设置 CreatureTemplate 工厂的简写语法糖。
    ///
    /// Fluent shorthand for setting the CreatureTemplate factory.
    /// </summary>
    public CreatureDescriptor Template(Func<CreatureTemplate> factory) => SetTemplateFactory(factory);

    /// <summary>
    /// 设置可选的 CreatureState 工厂。
    ///
    /// Sets the optional CreatureState factory.
    /// </summary>
    public CreatureDescriptor SetStateFactory(Func<AbstractCreature, CreatureState> factory)
    {
        ThrowIfFrozen();
        StateFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// 设置 CreatureState 工厂的简写语法糖。
    ///
    /// Fluent shorthand for setting the CreatureState factory.
    /// </summary>
    public CreatureDescriptor State(Func<AbstractCreature, CreatureState> factory) => SetStateFactory(factory);

    /// <summary>
    /// 清除自定义 CreatureState 工厂，使该部分重新回退到 Rain World 原版流程。
    ///
    /// Clears the custom CreatureState factory so this part falls back to Rain World's original flow.
    /// </summary>
    public CreatureDescriptor ClearStateFactory()
    {
        ThrowIfFrozen();
        StateFactory = null;
        return this;
    }

    /// <summary>
    /// 设置可选的实体 Creature 工厂。
    ///
    /// Sets the optional realized Creature factory.
    /// </summary>
    public CreatureDescriptor SetCreatureFactory(Func<AbstractCreature, global::Creature> factory)
    {
        ThrowIfFrozen();
        CreatureFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// 设置实体 Creature 工厂的简写语法糖。
    /// 使用 Realized 这个名称用于明确区分 AbstractCreature 与房间中实际存在的 Creature。
    ///
    /// Fluent shorthand for setting the realized Creature factory.
    /// The name Realized explicitly distinguishes it from AbstractCreature.
    /// </summary>
    public CreatureDescriptor Realized(Func<AbstractCreature, global::Creature> factory) => SetCreatureFactory(factory);

    /// <summary>
    /// 清除自定义实体 Creature 工厂，使 Realize 重新回退到 Rain World 原版流程。
    ///
    /// Clears the custom realized Creature factory so realization falls back to Rain World's original flow.
    /// </summary>
    public CreatureDescriptor ClearCreatureFactory()
    {
        ThrowIfFrozen();
        CreatureFactory = null;
        return this;
    }

    /// <summary>
    /// 设置可选的 AbstractCreatureAI 工厂。
    ///
    /// Sets the optional AbstractCreatureAI factory.
    /// </summary>
    public CreatureDescriptor SetAbstractAIFactory(Func<AbstractCreature, AbstractCreatureAI> factory)
    {
        ThrowIfFrozen();
        AbstractAIFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// 设置 AbstractCreatureAI 工厂的简写语法糖。
    ///
    /// Fluent shorthand for setting the AbstractCreatureAI factory.
    /// </summary>
    public CreatureDescriptor AbstractAI(Func<AbstractCreature, AbstractCreatureAI> factory) => SetAbstractAIFactory(factory);

    /// <summary>
    /// 清除自定义 AbstractCreatureAI 工厂，使该部分重新回退到 Rain World 原版流程。
    ///
    /// Clears the custom AbstractCreatureAI factory so this part falls back to Rain World's original flow.
    /// </summary>
    public CreatureDescriptor ClearAbstractAIFactory()
    {
        ThrowIfFrozen();
        AbstractAIFactory = null;
        return this;
    }

    /// <summary>
    /// 设置可选的实际 ArtificialIntelligence 工厂。
    ///
    /// Sets the optional realized ArtificialIntelligence factory.
    /// </summary>
    public CreatureDescriptor SetRealizedAIFactory(Func<AbstractCreature, ArtificialIntelligence> factory)
    {
        ThrowIfFrozen();
        RealizedAIFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// 设置实际 ArtificialIntelligence 工厂的简写语法糖。
    ///
    /// Fluent shorthand for setting the realized ArtificialIntelligence factory.
    /// </summary>
    public CreatureDescriptor AI(Func<AbstractCreature, ArtificialIntelligence> factory) => SetRealizedAIFactory(factory);

    /// <summary>
    /// 清除自定义实际 AI 工厂，使 AI 创建重新回退到 Rain World 原版分派流程。
    ///
    /// Clears the custom realized AI factory so AI creation falls back to Rain World's original dispatch.
    /// </summary>
    public CreatureDescriptor ClearRealizedAIFactory()
    {
        ThrowIfFrozen();
        RealizedAIFactory = null;
        return this;
    }

    /// <summary>
    /// 在 Registry 真正接收描述对象之前验证所有注册必需条件。
    ///
    /// Validates all conditions required before the registry accepts this descriptor.
    /// </summary>
    internal void ValidateForRegistration()
    {
        if (Type.Index < 0)
        {
            throw new InvalidOperationException(
                $"Creature type '{Type.value}' owned by '{OwnerId}' is not a registered ExtEnum value.");
        }

        if (TemplateFactory == null)
        {
            throw new InvalidOperationException(
                $"Creature '{Type.value}' owned by '{OwnerId}' has no template factory.");
        }
    }

    /// <summary>
    /// 在成功注册后冻结描述对象，阻止注册信息被继续修改。
    ///
    /// Freezes the descriptor after successful registration, preventing further registration-data changes.
    /// </summary>
    internal void Freeze()
    {
        ValidateForRegistration();
        _isFrozen = true;
    }

    /// <summary>
    /// 如果描述对象已经冻结，则拒绝后续修改。
    ///
    /// Rejects further mutation when the descriptor is already frozen.
    /// </summary>
    private void ThrowIfFrozen()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException(
                $"Creature descriptor '{Type.value}' owned by '{OwnerId}' is already registered and cannot be modified.");
        }
    }

    /// <summary>
    /// 规范化一个别名，并拒绝空白别名。
    ///
    /// Normalizes an alias and rejects empty or whitespace-only aliases.
    /// </summary>
    private static string NormalizeAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Creature alias must not be empty.", nameof(alias));
        }

        return alias.Trim();
    }
}
