using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DryCycle.Framework.Creature.Core;

/// <summary>
/// Immutable-after-registration description of one custom creature type.
///
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
    /// Rain World's creature type represented by this descriptor.
    /// </summary>
    public CreatureTemplate.Type Type { get; }

    /// <summary>
    /// Stable identifier of the mod or framework owner that registered this creature.
    /// This is intended for diagnostics and collision reporting, not for display text.
    /// </summary>
    public string OwnerId { get; }

    /// <summary>
    /// Human-readable creature name. Defaults to the creature type value.
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>
    /// Additional case-insensitive names accepted for this creature, in declaration order.
    /// The canonical creature type value is not required here; the registry indexes it
    /// separately. The returned collection cannot mutate the descriptor.
    /// </summary>
    public IReadOnlyList<string> Aliases => _readOnlyAliases;

    /// <summary>
    /// Creates the creature template inserted into StaticWorld.
    /// This is the only required factory for a fully registered descriptor.
    /// </summary>
    public Func<CreatureTemplate> TemplateFactory { get; private set; }

    /// <summary>
    /// Optional replacement for Rain World's default CreatureState creation.
    /// When unset, the original state created by Rain World is preserved.
    /// </summary>
    public Func<AbstractCreature, CreatureState> StateFactory { get; private set; }

    /// <summary>
    /// Optional replacement for Rain World's realized Creature creation.
    /// When unset, Rain World's original realization path is preserved.
    /// </summary>
    public Func<AbstractCreature, global::Creature> CreatureFactory { get; private set; }

    /// <summary>
    /// Optional replacement for Rain World's AbstractCreatureAI creation.
    /// When unset, Rain World's original abstract AI is preserved.
    /// </summary>
    public Func<AbstractCreature, AbstractCreatureAI> AbstractAIFactory { get; private set; }

    /// <summary>
    /// Optional replacement for Rain World's realized ArtificialIntelligence creation.
    /// When unset, Rain World's original AI dispatch is preserved.
    /// </summary>
    public Func<AbstractCreature, ArtificialIntelligence> RealizedAIFactory { get; private set; }

    /// <summary>
    /// True after the descriptor has been accepted by CreatureRegistry.
    /// Frozen descriptors cannot be modified.
    /// </summary>
    public bool IsFrozen => _isFrozen;

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

    public CreatureDescriptor ClearAliases()
    {
        ThrowIfFrozen();
        _aliases.Clear();
        _aliasSet.Clear();
        return this;
    }

    public CreatureDescriptor SetTemplateFactory(Func<CreatureTemplate> factory)
    {
        ThrowIfFrozen();
        TemplateFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public CreatureDescriptor SetStateFactory(Func<AbstractCreature, CreatureState> factory)
    {
        ThrowIfFrozen();
        StateFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public CreatureDescriptor ClearStateFactory()
    {
        ThrowIfFrozen();
        StateFactory = null;
        return this;
    }

    public CreatureDescriptor SetCreatureFactory(Func<AbstractCreature, global::Creature> factory)
    {
        ThrowIfFrozen();
        CreatureFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public CreatureDescriptor ClearCreatureFactory()
    {
        ThrowIfFrozen();
        CreatureFactory = null;
        return this;
    }

    public CreatureDescriptor SetAbstractAIFactory(Func<AbstractCreature, AbstractCreatureAI> factory)
    {
        ThrowIfFrozen();
        AbstractAIFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public CreatureDescriptor ClearAbstractAIFactory()
    {
        ThrowIfFrozen();
        AbstractAIFactory = null;
        return this;
    }

    public CreatureDescriptor SetRealizedAIFactory(Func<AbstractCreature, ArtificialIntelligence> factory)
    {
        ThrowIfFrozen();
        RealizedAIFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public CreatureDescriptor ClearRealizedAIFactory()
    {
        ThrowIfFrozen();
        RealizedAIFactory = null;
        return this;
    }

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

    internal void Freeze()
    {
        ValidateForRegistration();
        _isFrozen = true;
    }

    private void ThrowIfFrozen()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException(
                $"Creature descriptor '{Type.value}' owned by '{OwnerId}' is already registered and cannot be modified.");
        }
    }

    private static string NormalizeAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Creature alias must not be empty.", nameof(alias));
        }

        return alias.Trim();
    }
}
