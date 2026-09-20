using BepInEx;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility plugin marker for World Workspace ASCII direction labels.
/// The presentation now lives directly in WorldWorkspaceView; no self-detours are installed.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldWorkspaceAsciiDirectionPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldWorkspaceAsciiDirections";
    public const string PluginName = "DryCycle DevTool World Workspace ASCII Directions";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        Logger?.LogInfo("World Workspace ASCII direction labels are integrated directly into the view; no self-detours attached.");
}
