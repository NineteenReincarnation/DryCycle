using BepInEx;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility marker for a retired self-detour optimization.
///
/// The baseline implementation remains authoritative. This plugin ID is retained so existing
/// dependency ordering stays stable while startup no longer JITs a RuntimeDetour trampoline into
/// DryCycle-owned methods. The optimization can be reintroduced directly at the source call site.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuPathScratchPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.PathScratch";
    public const string PluginName = "DryCycle DevTool GPU World Map Path Scratch";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        Logger?.LogInfo("GPU World Map path-scratch optimization is running in baseline mode; no self-detour attached.");
    }
}
