using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility plugin marker for the retained World Map interaction index.
///
/// Hover lookup now calls WorldMapGpuScene directly from WorldMapView.FindHoveredRoom. Keeping this
/// plugin ID preserves dependency ordering for existing builds while deliberately installing no
/// RuntimeDetour hook on our own view method.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRegionPreloadPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuInteractionIndexPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.InteractionIndex";
    public const string PluginName = "DryCycle DevTool GPU World Map Interaction Index";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable()
    {
        Logger?.LogInfo(
            "GPU World Map spatial hover index is integrated directly into WorldMapView; no detour attached.");
    }
}


internal static class WorldMapGpuInteractionIndex
{
    // Hover acceleration now lives directly in WorldMapView.FindHoveredRoom. These methods retain
    // the old lifecycle contract without installing a RuntimeDetour trampoline.
    internal static void Enable(ManualLogSource logger)
    {
        WorldMapHotState.Invalidate();
    }

    internal static void Disable()
    {
        WorldMapHotState.Invalidate();
    }
}
