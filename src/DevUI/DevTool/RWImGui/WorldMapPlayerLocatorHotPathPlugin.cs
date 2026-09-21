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
[BepInDependency(WorldMapPlayerLocatorPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPlayerLocatorHotPathPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPlayerLocator.HotPath";
    public const string PluginName = "DryCycle DevTool World Map Player Locator Hot Path";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => Logger?.LogInfo("World Map player-locator hot path is running in baseline mode; no self-detour attached."),
            null);
}
