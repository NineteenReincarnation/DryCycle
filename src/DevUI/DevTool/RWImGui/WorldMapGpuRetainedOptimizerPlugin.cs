using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility plugin marker for retained World Map room-source optimization.
///
/// WorldMapGpuScene now calls WorldMapLegacyRoomSourceService directly. The plugin ID remains so the
/// existing BepInEx dependency graph is stable, but DryCycle no longer RuntimeDetours its own scene
/// methods to reach this service.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRetainedOptimizerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RetainedOptimizer";
    public const string PluginName = "DryCycle DevTool GPU World Map Retained Optimizer";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => WorldMapGpuRetainedOptimizer.Enable(Logger),
            WorldMapGpuRetainedOptimizer.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            WorldMapGpuRetainedOptimizer.Disable);
}

internal static class WorldMapGpuRetainedOptimizer
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("GPU World Map retained room-source optimization uses direct service calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        WorldMapLegacyRoomSourceService.Reset();
        enabled = false;
    }
}
