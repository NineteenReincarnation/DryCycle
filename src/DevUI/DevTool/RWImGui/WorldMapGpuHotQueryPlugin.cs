using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility plugin marker for retained GPU spatial-query optimization.
///
/// Allocation-free visit stamps, retained visible-room scratch storage and same-frame route-hit
/// caching now live directly inside WorldMapGpuScene. The plugin ID stays in place for dependency
/// stability while deliberately installing no RuntimeDetour hooks.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuHotQueryPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.HotQuery";
    public const string PluginName = "DryCycle DevTool GPU World Map Hot Query";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuHotQuery.Enable(Logger);
    private void OnDisable() => WorldMapGpuHotQuery.Disable();
}

internal static class WorldMapGpuHotQuery
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo(
            "GPU World Map allocation-free spatial scratch is integrated directly into WorldMapGpuScene; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
    }
}
