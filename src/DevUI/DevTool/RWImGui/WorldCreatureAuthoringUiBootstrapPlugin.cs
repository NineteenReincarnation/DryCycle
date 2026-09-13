using BepInEx;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns the lifecycle of the creature-authoring panels that are composed directly by
/// WorldWorkspaceView. The panels must not depend on the old per-panel hook plugins being
/// discovered or enabled in a particular order; their render entry already lives in the active
/// World Workspace inspector.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class WorldCreatureAuthoringUiBootstrapPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.CreatureAuthoringUi";
    public const string PluginName = "DryCycle DevTool Creature Authoring UI";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        WorldCreatureSpawnInspector.Enable(Logger);
        WorldLineageInspector.Enable(Logger);
    }

    private void OnDisable()
    {
        WorldLineageInspector.Disable();
        WorldCreatureSpawnInspector.Disable();
    }
}
