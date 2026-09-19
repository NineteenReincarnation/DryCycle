using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility marker for a retired self-detour optimization.
///
/// The baseline implementation remains authoritative. This plugin ID is retained so existing
/// dependency ordering stays stable while startup no longer JITs a RuntimeDetour trampoline into
/// DryCycle-owned methods. The optimization can be reintroduced directly at the source call site.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRegionPreloadPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuStableCacheGatePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.StableCacheGate";
    public const string PluginName = "DryCycle DevTool GPU World Map Stable Cache Gate";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        Logger?.LogInfo("GPU World Map stable-cache gate is running in baseline mode; no self-detour attached.");
    }
}


internal static class WorldMapGpuStableCacheGate
{
    // Compatibility lifecycle for callers that used to control the detour-backed optimization.
    // Baseline WorldMapGpuCache remains authoritative while the self-detour implementation is retired.
    internal static void Enable(ManualLogSource logger)
    {
    }

    internal static void Disable()
    {
    }

    internal static void ReleaseRetainedKey()
    {
    }
}
