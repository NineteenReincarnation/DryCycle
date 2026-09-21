using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Always-visible Player Map input health summary. Unlike Render progress, this panel evaluates the
/// current editor snapshot before a render job starts, so authors can fix missing bakes, invalid
/// multi-pipe endpoints and unsafe output dimensions without producing a failed render attempt.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapWorkspaceIntegrationPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapPreflightPanelPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.PreflightPanel";
    public const string PluginName = "DryCycle Player Map Preflight Panel";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => PlayerMapPreflightPanel.Enable(Logger),
            PlayerMapPreflightPanel.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            PlayerMapPreflightPanel.Disable);
}

internal static class PlayerMapPreflightPanel
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live preflight inspector enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
        PlayerMapPreflightSnapshot preflight = PlayerMapPreflightDiagnostics.Evaluate(snapshot, world);
        if (!preflight.Available) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("Render 预检", "RENDER PREFLIGHT"));
        string state = preflight.CanRender
            ? DevToolUiSettings.T("可 Render", "Ready to Render")
            : DevToolUiSettings.T("需要处理", "Needs Attention");
        ImGui.TextUnformatted(state);

        DrawMetric("Rooms",
            preflight.ReadyRooms + " ready / " +
            preflight.PendingRooms + " pending / " +
            preflight.MissingRooms + " missing / " +
            preflight.FailedRooms + " failed");
        DrawMetric("Pipes",
            preflight.ExactConnections + " exact / " + preflight.AmbiguousConnections + " unresolved");
        if (preflight.InvalidEndpoints > 0)
            DrawMetric("Invalid endpoints", preflight.InvalidEndpoints.ToString());
        if (preflight.DuplicateEndpointClaims > 0)
            DrawMetric("Endpoint conflicts", preflight.DuplicateEndpointClaims.ToString());
        DrawMetric("Overlaps", preflight.OverlapPairs.ToString());
        if (preflight.Width > 0 && preflight.Height > 0)
            DrawMetric("Output", preflight.Width + " × " + preflight.Height);

        int errorLimit = Math.Min(6, preflight.Errors.Length);
        for (int i = 0; i < errorLimit; i++)
            ImGui.TextWrapped("ERROR · " + preflight.Errors[i]);
        if (preflight.Errors.Length > errorLimit)
            ImGui.TextDisabled("… +" + (preflight.Errors.Length - errorLimit) + " errors");

        int warningLimit = Math.Min(5, preflight.Warnings.Length);
        for (int i = 0; i < warningLimit; i++)
            ImGui.TextWrapped("WARN · " + preflight.Warnings[i]);
        if (preflight.Warnings.Length > warningLimit)
            ImGui.TextDisabled("… +" + (preflight.Warnings.Length - warningLimit) + " warnings");
    }

    private static void DrawMetric(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value ?? string.Empty);
    }

}
