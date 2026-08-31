using System.Collections.Generic;

namespace DryCycle.Registration;

/// <summary>
/// Defines one custom creature type for DryCycle's internal content registry.
/// The definition owns template creation and realization; the registry owns the
/// Rain World hooks required to insert it into the vanilla lifecycle.
/// </summary>
internal abstract class CreatureDefinition
{
    protected CreatureDefinition(CreatureTemplate.Type type)
    {
        Type = type;
    }

    internal CreatureTemplate.Type Type { get; }

    internal abstract CreatureTemplate CreateTemplate();

    internal abstract Creature CreateRealizedCreature(AbstractCreature abstractCreature);

    /// <summary>
    /// Return null to keep the state created by AbstractCreature's vanilla ctor.
    /// </summary>
    internal virtual CreatureState CreateState(AbstractCreature abstractCreature) => null;

    /// <summary>
    /// Return null to keep the vanilla/default abstract AI.
    /// </summary>
    internal virtual AbstractCreatureAI CreateAbstractAI(AbstractCreature abstractCreature) => null;

    /// <summary>
    /// Return null to let Rain World attempt its normal realized-AI dispatch.
    /// </summary>
    internal virtual ArtificialIntelligence CreateRealizedAI(AbstractCreature abstractCreature) => null;

    internal virtual void InitializeAbstractCreature(
        AbstractCreature abstractCreature,
        World world,
        WorldCoordinate position,
        EntityID id)
    {
    }

    internal virtual IEnumerable<string> WorldFileAliases()
    {
        yield return Type.value;
    }

    internal virtual void EstablishRelationships()
    {
    }

    internal virtual void LoadResources(RainWorld rainWorld)
    {
    }
}
