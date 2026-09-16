using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility shell for the former retained-GPU connection router.
///
/// World Map connections are now routed and rendered by the native WorldMapView connection layer.
/// The retained GPU scene owns room raster/highlights only, so intercepting
/// WorldConnectionRouter.BuildRoutes is no longer necessary. The plugin ID is intentionally kept so
/// existing BepInEx dependency graphs remain stable while the obsolete detour implementation is gone.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRetainedOptimizerPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuIncrementalRouterPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.IncrementalRouter";
    public const string PluginName = "DryCycle DevTool GPU World Map Incremental Router";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuIncrementalRouter.Enable(Logger);
    private void OnDisable() => WorldMapGpuIncrementalRouter.Disable();
}

internal static class WorldMapGpuIncrementalRouter
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogDebug("GPU connection router detour retired; native World Map routing is authoritative.");
    }

    internal static void Update()
    {
        // Kept for source compatibility with older callers. There is no per-frame router work now.
    }

    internal static void Disable()
    {
        enabled = false;
    }
}
