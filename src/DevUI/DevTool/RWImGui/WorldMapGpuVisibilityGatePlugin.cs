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
[BepInDependency(WorldMapGpuHotQueryPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuVisibilityGatePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.VisibilityGate";
    public const string PluginName = "DryCycle DevTool GPU World Map Visibility Gate";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        Logger?.LogInfo("GPU World Map visibility gate is running in baseline mode; no self-detour attached.");
    }
}
