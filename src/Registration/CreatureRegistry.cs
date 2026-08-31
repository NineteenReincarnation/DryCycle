using System;
using System.Collections.Generic;

namespace DryCycle.Registration;

/// <summary>
/// DryCycle's creature registry. This is intentionally narrower than Fisobs: it
/// handles the core lifecycle needed by our creatures and keeps unrelated sandbox,
/// expedition and icon behavior out of the registration layer.
/// </summary>
internal static class CreatureRegistry
{
    private static readonly Dictionary<CreatureTemplate.Type, CreatureDefinition> Definitions = new();
    private static bool _enabled;

    internal static IEnumerable<CreatureDefinition> Registered => Definitions.Values;

    internal static void Register(CreatureDefinition definition)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (definition.Type == null || definition.Type.Index < 0)
        {
            throw new InvalidOperationException("Custom creature type must be a registered ExtEnum value.");
        }

        Definitions[definition.Type] = definition;
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.StaticWorld.InitCustomTemplates += StaticWorld_InitCustomTemplates;
        On.StaticWorld.InitStaticWorld += StaticWorld_InitStaticWorld;
        On.AbstractCreature.ctor += AbstractCreature_ctor;
        On.AbstractCreature.Realize += AbstractCreature_Realize;
        On.AbstractCreature.InitiateAI += AbstractCreature_InitiateAI;
        On.WorldLoader.CreatureTypeFromString += WorldLoader_CreatureTypeFromString;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.StaticWorld.InitCustomTemplates -= StaticWorld_InitCustomTemplates;
        On.StaticWorld.InitStaticWorld -= StaticWorld_InitStaticWorld;
        On.AbstractCreature.ctor -= AbstractCreature_ctor;
        On.AbstractCreature.Realize -= AbstractCreature_Realize;
        On.AbstractCreature.InitiateAI -= AbstractCreature_InitiateAI;
        On.WorldLoader.CreatureTypeFromString -= WorldLoader_CreatureTypeFromString;
        _enabled = false;
    }

    private static void StaticWorld_InitCustomTemplates(On.StaticWorld.orig_InitCustomTemplates orig)
    {
        orig();

        foreach (CreatureDefinition definition in Definitions.Values)
        {
            CreatureTemplate template = definition.CreateTemplate();
            if (template == null)
            {
                throw new InvalidOperationException(
                    $"{definition.GetType().FullName} returned a null CreatureTemplate.");
            }

            if (template.type != definition.Type || template.type.Index < 0)
            {
                throw new InvalidOperationException(
                    $"{definition.GetType().FullName} returned a CreatureTemplate with the wrong type.");
            }

            if (template.type.Index >= StaticWorld.creatureTemplates.Length)
            {
                throw new InvalidOperationException(
                    $"Creature type {template.type.value} was registered too late for StaticWorld initialization.");
            }

            StaticWorld.creatureTemplates[template.type.Index] = template;
        }
    }

    private static void StaticWorld_InitStaticWorld(On.StaticWorld.orig_InitStaticWorld orig)
    {
        orig();

        foreach (CreatureDefinition definition in Definitions.Values)
        {
            definition.EstablishRelationships();
        }
    }

    private static void AbstractCreature_ctor(
        On.AbstractCreature.orig_ctor orig,
        AbstractCreature self,
        World world,
        CreatureTemplate template,
        Creature realizedCreature,
        WorldCoordinate position,
        EntityID id)
    {
        orig(self, world, template, realizedCreature, position, id);

        if (!Definitions.TryGetValue(template.type, out CreatureDefinition definition))
        {
            return;
        }

        CreatureState customState = definition.CreateState(self);
        if (customState != null)
        {
            self.state = customState;
        }

        AbstractCreatureAI customAbstractAI = definition.CreateAbstractAI(self);
        if (customAbstractAI != null)
        {
            self.abstractAI = customAbstractAI;
        }

        definition.InitializeAbstractCreature(self, world, position, id);
    }

    private static void AbstractCreature_Realize(
        On.AbstractCreature.orig_Realize orig,
        AbstractCreature self)
    {
        if (self.Room != null &&
            self.realizedCreature == null &&
            Definitions.TryGetValue(self.creatureTemplate.type, out CreatureDefinition definition))
        {
            Creature creature = definition.CreateRealizedCreature(self);
            if (creature == null)
            {
                throw new InvalidOperationException(
                    $"{definition.GetType().FullName} returned a null realized creature.");
            }

            self.realizedObject = creature;

            if (self.creatureTemplate.AI && self.abstractAI != null)
            {
                self.InitiateAI();
            }

            // Vanilla AbstractCreature.Realize returns immediately once a custom
            // realized object already exists, so preserve its stuck-object pass here.
            for (int i = 0; i < self.stuckObjects.Count; i++)
            {
                if (self.stuckObjects[i].A.realizedObject == null)
                {
                    self.stuckObjects[i].A.Realize();
                }

                if (self.stuckObjects[i].B.realizedObject == null)
                {
                    self.stuckObjects[i].B.Realize();
                }
            }
        }

        orig(self);
    }

    private static void AbstractCreature_InitiateAI(
        On.AbstractCreature.orig_InitiateAI orig,
        AbstractCreature self)
    {
        if (!Definitions.TryGetValue(self.creatureTemplate.type, out CreatureDefinition definition))
        {
            orig(self);
            return;
        }

        ArtificialIntelligence customAI = definition.CreateRealizedAI(self);
        if (customAI != null)
        {
            if (self.abstractAI == null)
            {
                throw new InvalidOperationException(
                    $"{definition.Type.value} created realized AI without an AbstractCreatureAI.");
            }

            self.abstractAI.RealAI = customAI;
            return;
        }

        // This allows a custom creature that deliberately inherits a vanilla
        // ancestor to reuse that ancestor's normal AI dispatch.
        orig(self);
    }

    private static CreatureTemplate.Type WorldLoader_CreatureTypeFromString(
        On.WorldLoader.orig_CreatureTypeFromString orig,
        string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;

        foreach (CreatureDefinition definition in Definitions.Values)
        {
            IEnumerable<string> aliases = definition.WorldFileAliases();
            if (aliases == null)
            {
                continue;
            }

            foreach (string alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) &&
                    normalized == alias.Trim().ToLowerInvariant())
                {
                    return definition.Type;
                }
            }
        }

        return orig(value);
    }
}
