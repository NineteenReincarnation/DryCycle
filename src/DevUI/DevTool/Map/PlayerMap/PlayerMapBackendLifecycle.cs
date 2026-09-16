using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Authoritative lifetime for the rebuilt Player Map backend.
///
/// Auxiliary BepInPlugin types remain idempotent compatibility entry points, but correctness no
/// longer depends on the loader discovering every helper type in the DryCycle assembly. MiscRuntime
/// explicitly enables/disables this unit together with the rest of the rebuilt DevTool backend.
/// </summary>
internal static class PlayerMapBackendLifecycle
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;

        // Base state must exist before any hook redirects its command/presentation boundaries.
        PlayerMapWorkspaceRuntime.Enable();
        PlayerMapLegacyRenderGuard.Enable(logger);
        PlayerMapIncrementalRenderHooks.Enable(logger);
        PlayerMapTerrainBakeBridge.Enable(logger);
        PlayerMapDerivedLayoutBridge.Enable(logger);
        PlayerMapConfigBuildPipeline.Enable(logger);
        PlayerMapRenderOutputValidator.Enable(logger);
        PlayerMapGroupCommandRuntime.Enable(logger);
        PlayerMapRenderRevisionGuard.Enable(logger);
        // Install last so it becomes the outer command/synchronize gate: pending bakes are allowed
        // to finish before the incremental renderer freezes its authoritative input snapshot.
        PlayerMapRenderPreparationController.Enable(logger);

        enabled = true;
        logger?.LogInfo("Player Map backend lifecycle enabled explicitly.");
    }

    internal static void Disable()
    {
        if (!enabled)
        {
            ResetTransientState();
            return;
        }

        // Remove outer hooks first, then their inner dependencies.
        PlayerMapRenderPreparationController.Disable();
        PlayerMapRenderRevisionGuard.Disable();
        PlayerMapGroupCommandRuntime.Disable();
        PlayerMapRenderOutputValidator.Disable();
        PlayerMapConfigBuildPipeline.Disable();
        PlayerMapDerivedLayoutBridge.Disable();
        PlayerMapTerrainBakeBridge.Disable();
        PlayerMapIncrementalRenderHooks.Disable();
        PlayerMapLegacyRenderGuard.Disable();

        ResetTransientState();
        PlayerMapWorkspaceRuntime.Disable();
        enabled = false;
    }

    internal static void ResetTransientState()
    {
        PlayerMapRenderPreparationController.Reset();
        PlayerMapRenderScheduler.Reset();
        PlayerMapGroupCommandQueue.Clear();
        PlayerMapTerrainSemanticRevision.Reset();
        PlayerMapPreflightDiagnostics.Reset();
        PlayerMapActivityGate.Reset();
        RoomMapBakeCache.Clear();
    }
}
