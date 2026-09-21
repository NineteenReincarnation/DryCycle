using System;
using BepInEx;
using BepInEx.Logging;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Scoped scrollbar chrome invoked directly by the pane that owns scrolling.
/// No DryCycle-owned draw method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class ScopedScrollChromePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.ScopedScrollChrome";
    public const string PluginName = "DryCycle DevTool Scoped Scroll Chrome";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => ScopedScrollChrome.Enable(Logger),
            ScopedScrollChrome.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            ScopedScrollChrome.Disable);
}

internal static class ScopedScrollChrome
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("DevTool scoped scrollbar animation enabled through direct pane calls; no self-detours attached.");
    }

    internal static void Disable() => enabled = false;

    internal static void Draw(string key, bool pruneAfter = false)
    {
        if (!enabled) return;
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        DevToolScrollChrome.DrawCurrentRegion(key, ImGui.GetIO(), scale);
        if (pruneAfter)
            DevToolScrollChrome.PruneInactiveStates();
    }
}
