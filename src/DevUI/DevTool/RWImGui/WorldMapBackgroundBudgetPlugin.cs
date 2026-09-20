using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Spreads non-essential World Map preview discovery over time.
///
/// Geometry and shortcut presentation hubs call this budget directly. The plugin ID remains for
/// dependency ordering, but no DryCycle-owned method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPerformancePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapBackgroundBudgetPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapBackgroundBudget";
    public const string PluginName = "DryCycle DevTool World Map Background Budget";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapBackgroundBudget.Enable(Logger);
    private void OnDisable() => WorldMapBackgroundBudget.Disable();
}

internal static class WorldMapBackgroundBudget
{
    private const float DetailedBackgroundZoom = 0.42f;
    private const int GeometrySweepIntervalFrames = 4;
    private const int ShortcutSweepIntervalFrames = 4;

    private static bool enabled;
    private static int lastGeometrySweepFrame = -1000;
    private static int lastShortcutSweepFrame = -1000;
    private static string geometryRegion = string.Empty;
    private static string shortcutRegion = string.Empty;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        ResetState();
        WorldMapFrontendBridge.RegisterGeometryBackgroundBudget(ShouldProcessGeometry);
        logger?.LogInfo("World Map background preview budget enabled through direct presentation calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        WorldMapFrontendBridge.UnregisterGeometryBackgroundBudget(ShouldProcessGeometry);
        enabled = false;
        ResetState();
        WorldMapHotState.Invalidate();
    }

    internal static bool ShouldProcessGeometry(global::World world)
    {
        if (!enabled) return true;

        string region = world?.name ?? string.Empty;
        if (!string.Equals(region, geometryRegion, StringComparison.OrdinalIgnoreCase))
        {
            geometryRegion = region;
            lastGeometrySweepFrame = Time.frameCount;
            return false;
        }

        if (WorldMapHotState.Zoom < DetailedBackgroundZoom)
            return false;

        if (Time.frameCount - lastGeometrySweepFrame < GeometrySweepIntervalFrames)
            return false;

        lastGeometrySweepFrame = Time.frameCount;
        return true;
    }

    internal static bool ShouldProcessShortcuts()
    {
        if (!enabled) return true;

        string region = DevToolRuntime.ActiveSession?.World?.name ?? string.Empty;
        if (!string.Equals(region, shortcutRegion, StringComparison.OrdinalIgnoreCase))
        {
            shortcutRegion = region;
            lastShortcutSweepFrame = Time.frameCount;
            return false;
        }

        if (Time.frameCount - lastShortcutSweepFrame < ShortcutSweepIntervalFrames)
            return false;

        lastShortcutSweepFrame = Time.frameCount;
        return true;
    }

    private static void ResetState()
    {
        lastGeometrySweepFrame = -1000;
        lastShortcutSweepFrame = -1000;
        geometryRegion = string.Empty;
        shortcutRegion = string.Empty;
    }
}
