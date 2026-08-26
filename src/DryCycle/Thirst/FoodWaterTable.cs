using System;

namespace DryCycle.Thirst;

internal static class FoodWaterTable
{
    public static float ForEdible(IPlayerEdible edible)
    {
        if (edible == null)
        {
            return 0f;
        }

        string typeName = edible.GetType().Name;

        return typeName switch
        {
            "Fly" => 1f,
            "DangleFruit" => 1f,
            "SwollenWaterNut" => 3f,
            "SSOracleSwarmer" => 1f,
            "SLOracleSwarmer" => 1f,
            "EggBugEgg" => 2f,
            "Hazer" => 2f,
            "JellyFish" => 1f,
            "Mushroom" => 0.5f,
            "SlimeMold" => 1f,
            "LillyPuck" => 3f,
            "GlowWeed" => 2f,
            _ => edible is Creature creature ? ForCreature(creature) : 0f
        };
    }

    public static float ForCreature(Creature creature)
    {
        if (creature?.abstractCreature?.creatureTemplate?.type == null)
        {
            return 0f;
        }

        string type = creature.abstractCreature.creatureTemplate.type.ToString();

        // Lizard family: all ordinary variants are 2, Salamander is 4.
        if (IsLizardType(type))
        {
            return type == "Salamander" ? 4f : 2f;
        }

        if (type == "Centipede" && creature.GetType().Name == "Centipede")
        {
            // The base Centipede template covers medium/large individuals.
            // Runtime size is read without a hard dependency on the concrete class.
            object sizeField = creature.GetType().GetField("size")?.GetValue(creature);
            if (sizeField is float size)
            {
                return size >= 0.5f ? 3f : 2f;
            }
            return 2f;
        }

        return type switch
        {
            "Snail" => 2f,
            "CicadaA" => 3f,
            "CicadaB" => 3f,
            "SmallNeedleWorm" => 2f,
            "BigNeedleWorm" => 4f,
            "Scavenger" => 1f,
            "ScavengerElite" => 1f,
            "ScavengerKing" => 1f,
            "LanternMouse" => 1f,
            "JetFish" => 3f,
            "TubeWorm" => 1f,
            "SmallCentipede" => 1f,
            "Centiwing" => 3f,
            "RedCentipede" => 6f,
            "AquaCenti" => 4f,
            "EggBug" => 2f,
            "Yeek" => 2f,
            _ => 0f
        };
    }

    private static bool IsLizardType(string type)
    {
        return type is
            "PinkLizard" or
            "GreenLizard" or
            "BlueLizard" or
            "YellowLizard" or
            "WhiteLizard" or
            "RedLizard" or
            "BlackLizard" or
            "CyanLizard" or
            "Salamander" or
            "SpitLizard" or
            "EelLizard" or
            "ZoopLizard" or
            "TrainLizard";
    }
}
