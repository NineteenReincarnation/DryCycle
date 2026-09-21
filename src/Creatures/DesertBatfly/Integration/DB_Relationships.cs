using System;
using DryCycle.Framework.Creature.Core;
using Watcher;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// 负责把 DesertBatfly 的食物链关系写入 StaticWorld。
/// 这部分属于物种生态接入，不进入通用 Creature Core Registry。
///
/// Installs DesertBatfly's creature relationships into StaticWorld.
/// This is species ecology integration and deliberately stays outside the generic Creature Core Registry.
/// </summary>
internal static class DB_Relationships
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        try
        {
            On.StaticWorld.InitStaticWorld += StaticWorld_InitStaticWorld;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "DB_Relationships.Enable",
                error,
                () =>
                {
                    global::DryCycle.StartupDiagnostics.RollbackStep(
                        "DB_Relationships.Enable/StaticWorld.InitStaticWorld",
                        () => On.StaticWorld.InitStaticWorld -= StaticWorld_InitStaticWorld);
                    _enabled = false;
                });
            throw;
        }
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.StaticWorld.InitStaticWorld -= StaticWorld_InitStaticWorld;
        _enabled = false;
    }

    private static void StaticWorld_InitStaticWorld(On.StaticWorld.orig_InitStaticWorld orig)
    {
        orig();

        if (CreatureRegistry.IsQuarantined(DB_Definition.CreatureType))
        {
            global::DryCycle.StartupDiagnostics.Marker(
                "DB_Relationships/StaticWorld.InitStaticWorld",
                "SKIP-QUARANTINED",
                "DesertBatfly template is quarantined");
            return;
        }

        try
        {
            EstablishRelationships();
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "DB_Relationships/StaticWorld.InitStaticWorld",
                error);
            global::DryCycle.StartupDiagnostics.Marker(
                "DB_Relationships/StaticWorld.InitStaticWorld",
                "ISOLATED",
                "relationship setup failed; StaticWorld startup will continue");
        }
    }

    private static void EstablishRelationships()
    {
        CreatureTemplate.Type type = DB_Definition.CreatureType;
        CreatureTemplate desert = StaticWorld.GetCreatureTemplate(type);
        CreatureTemplate fly = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Fly);

        if (desert == null ||
            fly == null ||
            desert.relationships == null ||
            fly.relationships == null)
        {
            return;
        }

        foreach (CreatureTemplate other in StaticWorld.creatureTemplates)
        {
            if (other == null ||
                other == desert ||
                other.type == null ||
                other.relationships == null ||
                other.type.Index < 0 ||
                other.type.Index >= desert.relationships.Length ||
                type.Index < 0 ||
                type.Index >= other.relationships.Length)
            {
                continue;
            }

            desert.relationships[other.type.Index] = fly.CreatureRelationship(other).Duplicate();
            other.relationships[type.Index] = other.CreatureRelationship(fly).Duplicate();

            if (other.TopAncestor().type == CreatureTemplate.Type.Scavenger)
            {
                other.relationships[type.Index] = new CreatureTemplate.Relationship(
                    CreatureTemplate.Relationship.Type.Attacks,
                    DB_Tuning.ScavengerHostility);
            }
        }

        // Peach Lizard is a deliberate ecological predator of Desert Batflies. Its
        // intensity is comparable to Watcher's own Peach->Frog relationship: enough
        // for PreyTracker/Hunt/tongue logic to engage without making a tiny flying
        // prey override every other useful target in the room. The reverse Afraid
        // relationship also plugs directly into DB_AI's predator detection.
        if (ModManager.Watcher &&
            WatcherEnums.CreatureTemplateType.PeachLizard != null &&
            WatcherEnums.CreatureTemplateType.PeachLizard.Index >= 0 &&
            WatcherEnums.CreatureTemplateType.PeachLizard.Index < StaticWorld.creatureTemplates.Length)
        {
            CreatureTemplate peach = StaticWorld.GetCreatureTemplate(
                WatcherEnums.CreatureTemplateType.PeachLizard);

            if (peach?.relationships != null &&
                type.Index >= 0 &&
                type.Index < peach.relationships.Length &&
                peach.type != null &&
                peach.type.Index >= 0 &&
                peach.type.Index < desert.relationships.Length)
            {
                peach.relationships[type.Index] = new CreatureTemplate.Relationship(
                    CreatureTemplate.Relationship.Type.Eats,
                    0.32f);
                desert.relationships[peach.type.Index] = new CreatureTemplate.Relationship(
                    CreatureTemplate.Relationship.Type.Afraid,
                    0.90f);
            }
        }

        SetRelationshipIfValid(
            desert,
            CreatureTemplate.Type.Slugcat.Index,
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
        SetRelationshipIfValid(
            desert,
            type.Index,
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
        SetRelationshipIfValid(
            desert,
            CreatureTemplate.Type.Fly.Index,
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
    }
    private static void SetRelationshipIfValid(
        CreatureTemplate source,
        int targetIndex,
        CreatureTemplate.Relationship.Type relationshipType,
        float intensity)
    {
        if (source?.relationships == null ||
            targetIndex < 0 ||
            targetIndex >= source.relationships.Length)
        {
            return;
        }

        source.relationships[targetIndex] =
            new CreatureTemplate.Relationship(relationshipType, intensity);
    }

}
