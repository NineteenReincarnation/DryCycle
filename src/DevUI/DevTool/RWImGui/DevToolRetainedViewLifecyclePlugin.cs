using BepInEx;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns the lifetime of retained RWImGui-only projections.
///
/// DryCycle.dll cannot call these reset methods directly because the RWImGui frontend already
/// references DryCycle.dll. Keeping this edge in the frontend preserves the one-way assembly
/// dependency while still dropping snapshot arrays, search projections and edit caches as soon as
/// the DevTools lifetime really ends.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class DevToolRetainedViewLifecyclePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.RetainedViewLifetime";
    public const string PluginName = "DryCycle DevTool RWImGui Retained View Lifetime";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private bool observedLiveSession;
    private RainWorldGame observedGame;

    private void OnEnable()
    {
        observedLiveSession = false;
        observedGame = null;
    }

    private void LateUpdate()
    {
        if (DevToolSessionHub.IsCurrentSessionLive)
        {
            EditorSession session = DevToolSessionHub.Current;
            observedGame = session?.Owner?.game ?? observedGame;
            observedLiveSession = true;

            // Page lifecycle follows the editor ToolMode even while RWImGui is temporarily hidden or
            // Vanilla UI is primary. The render path performs the same synchronization defensively,
            // so either update order remains correct and repeated calls are O(1) no-ops.
            if (session != null)
                DevToolPageViewRegistry.SynchronizeActive(session.ToolMode);
            return;
        }

        if (!observedLiveSession)
            return;

        // A temporary owner/page mismatch can occur around Alt+Tab and fullscreen transitions.
        // Release only when the RainWorldGame itself gives positive evidence that the editor lifetime
        // ended. This mirrors BridgePlugin's frontend-lifetime rule without creating a core ->
        // frontend dependency.
        if (!IsDefinitelyClosed(observedGame))
            return;

        ReleaseRetainedState();
        observedLiveSession = false;
        observedGame = null;
    }

    private void OnDisable()
    {
        ReleaseRetainedState();
        observedLiveSession = false;
        observedGame = null;
    }

    private static bool IsDefinitelyClosed(RainWorldGame game)
    {
        if (game == null) return true;
        if (!game.processActive || !game.devToolsActive) return true;

        return game.manager?.currentMainLoop != null &&
               !object.ReferenceEquals(game.manager.currentMainLoop, game);
    }

    private static void ReleaseRetainedState()
    {
        DevToolNumericWidgets.Reset();
        DevToolOverlay.ResetRetainedState();
        SceneWorkspaceWindow.ResetRetainedState();
        DevToolPageViewRegistry.ResetAll();
        UniversalDevUiMirrorView.ResetRetainedState();
    }
}
