using BepInEx;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Owns the rebuilt Player Map backend lifetime independently of the optional RWImGui frontend.
/// This keeps SaveMapConfig interception, room-bake cache lifetime and command semantics active as
/// one backend unit while allowing the presentation layer to be replaced without changing data flow.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(global::DryCycle.Plugin.ModId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapRuntimePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.Runtime";
    public const string PluginName = "DryCycle DevTool Player Map Runtime";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapWorkspaceRuntime.Enable();
    private void OnDisable() => PlayerMapWorkspaceRuntime.Disable();
}
