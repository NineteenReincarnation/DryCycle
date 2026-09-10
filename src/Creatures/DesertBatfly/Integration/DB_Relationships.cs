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

        On.StaticWorld.InitStaticWorld += StaticWorld_InitStaticWorld;
        _enabled = true;
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
        EstablishRelationships();
    }

    private static void EstablishRelationships()
    {
        CreatureTemplate.Type type = DB_Definition.CreatureType;
        CreatureTemplate desert = StaticWorld.GetCreatureTemplate(type);
        CreatureTemplate fly = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Fly);

        if (desert == null || fly == null)
        {
            return;
        }

        foreach (CreatureTemplate other in StaticWorld.creatureTemplates)
        {
            if (other == null || other == desert)
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

            if (peach != null)
            {
                peach.relationships[type.Index] = new CreatureTemplate.Relationship(
                    CreatureTemplate.Relationship.Type.Eats,
                    0.32f);
                desert.relationships[peach.type.Index] = new CreatureTemplate.Relationship(
                    CreatureTemplate.Relationship.Type.Afraid,
                    0.90f);
            }
        }

        desert.relationships[CreatureTemplate.Type.Slugcat.Index] = new CreatureTemplate.Relationship(
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
        desert.relationships[type.Index] = new CreatureTemplate.Relationship(
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
        desert.relationships[CreatureTemplate.Type.Fly.Index] = new CreatureTemplate.Relationship(
            CreatureTemplate.Relationship.Type.Ignores,
            0f);
    }
}
