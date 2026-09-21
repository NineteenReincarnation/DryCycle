using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Integrates the rebuilt Canon/Player Map editor into the World Workspace through explicit view
/// calls. No DryCycle-owned WorldWorkspaceView method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapWorkspaceIntegrationPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.WorkspaceIntegration";
    public const string PluginName = "DryCycle Player Map Workspace Integration";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => PlayerMapFrontendLifecycle.Enable(Logger),
            PlayerMapFrontendLifecycle.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            PlayerMapFrontendLifecycle.Disable);
}

internal static class PlayerMapWorkspaceIntegration
{
    private static ManualLogSource log;
    private static bool enabled;
    private static bool playerMapActive;

    internal static bool Active => enabled && playerMapActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map integrated into World Workspace through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        playerMapActive = false;
        PlayerMapActivityGate.Reset();
        enabled = false;
        log = null;
    }

    internal static void DrawToolbar(
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        if (playerMapActive && WorldWorkspaceView.WorkspaceModeValue != 0)
        {
            playerMapActive = false;
            PlayerMapActivityGate.Reset();
        }

        ImGui.SameLine(0f, 8f);
        string label = playerMapActive
            ? DevToolUiSettings.T("返回世界地图", "Back to World Map")
            : DevToolUiSettings.T("玩家地图", "Player Map");
        if (DevToolWidgets.ActionButton(
                label,
                "WorldWorkspacePlayerMap",
                playerMapActive ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            playerMapActive = !playerMapActive;
            if (playerMapActive)
                WorldWorkspaceView.WorkspaceModeValue = 0;
            else
                PlayerMapActivityGate.Reset();
        }
    }

    internal static bool DrawBodyIfActive(
        EditorPresentationSnapshot editor,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || !playerMapActive)
            return false;

        PlayerMapActivityGate.MarkVisible();
        PlayerMapWorkspaceView.DrawBody(editor, snapshot);
        return true;
    }
}
