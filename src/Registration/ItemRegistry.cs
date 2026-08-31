using System;
using System.Collections.Generic;

namespace DryCycle.Registration;

internal static class ItemRegistry
{
    private static readonly Dictionary<AbstractPhysicalObject.AbstractObjectType, ItemDefinition> Definitions = new();
    private static bool _enabled;

    internal static IEnumerable<ItemDefinition> Registered => Definitions.Values;

    internal static void Register(ItemDefinition definition)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (definition.Type == null || definition.Type.Index < 0)
        {
            throw new InvalidOperationException("Custom item type must be a registered ExtEnum value.");
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
        On.SaveState.AbstractPhysicalObjectFromString += SaveState_AbstractPhysicalObjectFromString;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.SaveState.AbstractPhysicalObjectFromString -= SaveState_AbstractPhysicalObjectFromString;
        _enabled = false;
    }

    private static AbstractPhysicalObject SaveState_AbstractPhysicalObjectFromString(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string serialized)
    {
        if (!ItemSaveData.TryParse(serialized, out AbstractPhysicalObject.AbstractObjectType type, out ItemSaveData saveData) ||
            !Definitions.TryGetValue(type, out ItemDefinition definition))
        {
            return orig(world, serialized);
        }

        try
        {
            AbstractPhysicalObject result = definition.Parse(world, saveData);
            if (result == null)
            {
                throw new InvalidOperationException(
                    $"{definition.GetType().FullName} returned null while parsing {type.value}.");
            }

            result.rippleLayer = saveData.RippleLayer;
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError(
                $"Failed to parse custom item {type.value}: {ex}");
            return null;
        }
    }
}
