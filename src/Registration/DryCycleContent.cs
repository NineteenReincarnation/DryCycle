using DryCycle.Creatures.Platforming;

namespace DryCycle.Registration;

/// <summary>
/// DryCycle 旧内容入口现在只保留物品注册与动态可行走表面运行时。
/// 生物注册已经迁移到 DryCycle.Framework.Creature.Core.CreatureRegistry。
///
/// DryCycle's legacy content entry point now retains only item registration and the dynamic walkable-surface runtime.
/// Creature registration has moved to DryCycle.Framework.Creature.Core.CreatureRegistry.
/// </summary>
internal static class DryCycleContent
{
    private static bool _enabled;
    private static bool _resourcesLoaded;

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

        ItemRegistry.Enable();
        WalkableDynamicSurfaceRuntime.Enable();
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        WalkableDynamicSurfaceRuntime.Disable();
        ItemRegistry.Disable();
        _resourcesLoaded = false;
        _enabled = false;
    }

    internal static void LoadResources(RainWorld rainWorld)
    {
        if (_resourcesLoaded)
        {
            return;
        }

        foreach (ItemDefinition definition in ItemRegistry.Registered)
        {
            definition.LoadResources(rainWorld);
        }

        _resourcesLoaded = true;
    }
}
