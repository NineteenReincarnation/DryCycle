using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Explicit owner for every rebuilt Player Map frontend module.
///
/// Helper BepInPlugin types are retained as idempotent compatibility entry points, but BridgePlugin
/// enables this unit directly so a loader that skips auxiliary plugin classes cannot leave the Player
/// Map with only part of its UI (for example workspace without render progress or multi-pipe links).
/// </summary>
internal static class PlayerMapFrontendLifecycle
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;

        PlayerMapWorkspaceIntegration.Enable(logger);
        PlayerMapRenderProgressView.Enable(logger);
        PlayerMapCanvasAuthoring.Enable(logger);
        PlayerMapMultiSelection.Enable(logger);
        PlayerMapGroupLayerControls.Enable(logger);
        PlayerMapLayoutAssist.Enable(logger);
        PlayerMapLiveOverlapPreview.Enable(logger);
        PlayerMapPreflightPanel.Enable(logger);
        PlayerMapMultiPipeConnections.Enable(logger);

        enabled = true;
        logger?.LogInfo("Player Map frontend lifecycle enabled explicitly.");
    }

    internal static void Disable()
    {
        if (!enabled)
        {
            PlayerMapWorkspaceView.ResetRetainedState();
            return;
        }

        PlayerMapMultiPipeConnections.Disable();
        PlayerMapPreflightPanel.Disable();
        PlayerMapLiveOverlapPreview.Disable();
        PlayerMapLayoutAssist.Disable();
        PlayerMapGroupLayerControls.Disable();
        PlayerMapMultiSelection.Disable();
        PlayerMapCanvasAuthoring.Disable();
        PlayerMapRenderProgressView.Disable();
        PlayerMapWorkspaceIntegration.Disable();
        PlayerMapWorkspaceView.ResetRetainedState();
        enabled = false;
    }
}
