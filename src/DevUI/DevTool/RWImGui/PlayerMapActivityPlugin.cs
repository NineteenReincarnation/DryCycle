namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Legacy plugin identifier retained for source compatibility. Player Map visibility is now marked
/// directly by PlayerMapWorkspaceIntegration at the actual World Workspace integration point, so an
/// additional detour around PlayerMapWorkspaceView.DrawBody is unnecessary.
/// </summary>
public static class PlayerMapActivityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.Activity";
    public const string PluginName = "DryCycle Player Map Activity Gate (retired)";
    public const string PluginVersion = BridgePlugin.PluginVersion;
}

internal static class PlayerMapActivityBridge
{
}
