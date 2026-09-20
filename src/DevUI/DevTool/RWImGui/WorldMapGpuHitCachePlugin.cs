using BepInEx;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility plugin marker for the duplicate route-hit cache.
///
/// The cache now lives directly inside WorldMapGpuScene.TryHitConnection. Keeping this BepInEx plugin
/// ID avoids breaking dependency ordering for existing builds while intentionally attaching no
/// detours here.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuHotQueryPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuHitCachePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.HitCache";
    public const string PluginName = "DryCycle DevTool GPU World Map Hit Cache";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        Logger?.LogInfo(
            "GPU World Map duplicate route hit-test cache is integrated directly into WorldMapGpuScene; no detour attached.");
    }
}
