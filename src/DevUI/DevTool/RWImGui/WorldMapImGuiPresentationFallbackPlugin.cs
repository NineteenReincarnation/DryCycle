using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Correctness bridge for the retained World Map renderer.
///
/// The current visible presentation remains ImGui-owned. The retained scene exposes a direct camera
/// suppression API, so no DrawCanvas self-hook or reflection is required.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapLegacyVisualGuardPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapImGuiPresentationFallbackPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.ImGuiPresentationFallback";
    public const string PluginName = "DryCycle DevTool World Map ImGui Presentation Fallback";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapImGuiPresentationFallback.Enable(Logger);
    private void LateUpdate() => WorldMapImGuiPresentationFallback.LateUpdate();
    private void OnDisable() => WorldMapImGuiPresentationFallback.Disable();
}

internal static class WorldMapImGuiPresentationFallback
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        SuppressStandaloneCamera();
        logger?.LogInfo("World Map ImGui presentation fallback uses direct camera suppression; no self-detour attached.");
    }

    internal static void Disable() => enabled = false;

    internal static void LateUpdate()
    {
        if (!enabled || !DevToolSessionHub.IsCurrentSessionLive)
            return;

        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map)
            return;

        SuppressStandaloneCamera();
    }

    internal static void SuppressStandaloneCamera()
    {
        if (!enabled) return;
        WorldMapGpuScene.SuppressStandaloneCamera();
    }
}
