using BepInEx;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Supplies the rebuilt World Map with the small piece of vanilla MapObject lifecycle it still needs
/// to materialize missing per-room MapTex sources. The recovery runtime is intentionally frame-budgeted
/// and never re-enables MapPage.Update or MapObject.Update.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapImGuiPresentationFallbackPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapSourceRecoveryPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.SourceRecovery";
    public const string PluginName = "DryCycle DevTool World Map Source Recovery";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void Update() =>
        MapRoomGeometryPresentationHub.RecoverMissingSources(DevToolRuntime.ActiveSession);

    private void OnDisable() =>
        MapRoomGeometryPresentationHub.ResetSourceRecovery();
}
