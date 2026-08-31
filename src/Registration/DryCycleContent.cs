namespace DryCycle.Registration;

/// <summary>
/// Single registration entry point for DryCycle-owned creatures and items.
/// Future content should register through this class rather than taking a dependency
/// on a third-party content registry.
/// </summary>
internal static class DryCycleContent
{
    private static bool _enabled;
    private static bool _resourcesLoaded;

    internal static void Register(CreatureDefinition definition)
    {
        CreatureRegistry.Register(definition);
    }

    internal static void Register(ItemDefinition definition)
    {
        ItemRegistry.Register(definition);
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        CreatureRegistry.Enable();
        ItemRegistry.Enable();
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        ItemRegistry.Disable();
        CreatureRegistry.Disable();
        _resourcesLoaded = false;
        _enabled = false;
    }

    internal static void LoadResources(RainWorld rainWorld)
    {
        if (_resourcesLoaded)
        {
            return;
        }

        foreach (CreatureDefinition definition in CreatureRegistry.Registered)
        {
            definition.LoadResources(rainWorld);
        }

        foreach (ItemDefinition definition in ItemRegistry.Registered)
        {
            definition.LoadResources(rainWorld);
        }

        _resourcesLoaded = true;
    }
}
