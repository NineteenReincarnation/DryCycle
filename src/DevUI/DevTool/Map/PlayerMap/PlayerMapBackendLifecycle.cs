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

        // Base state must exist before auxiliary services consume command/presentation boundaries.
        PlayerMapWorkspaceRuntime.Enable();
        PlayerMapMigrationStreamRuntime.Enable(logger);
        PlayerMapPlacementBootstrap.Enable(logger);
        PlayerMapIncrementalRenderHooks.Enable(logger);
        PlayerMapTerrainBakeBridge.Enable(logger);
        PlayerMapDerivedLayoutBridge.Enable(logger);
        PlayerMapConfigBuildPipeline.Enable(logger);
        PlayerMapDisabledConfigFilter.Enable(logger);
        PlayerMapRenderOutputValidator.Enable(logger);
        PlayerMapGroupCommandRuntime.Enable(logger);
        PlayerMapMigrationCommandBridge.Enable(logger);
        PlayerMapLayerMutationFilter.Enable(logger);
        PlayerMapRenderRevisionGuard.Enable(logger);
        // Enable preparation last so its direct scheduler/synchronize gate sees every prerequisite
        // service ready before the incremental renderer freezes its authoritative input snapshot.
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

        // Disable consumers before the services they depend on.
        PlayerMapRenderPreparationController.Disable();
        PlayerMapRenderRevisionGuard.Disable();
        PlayerMapLayerMutationFilter.Disable();
        PlayerMapMigrationCommandBridge.Disable();
        PlayerMapGroupCommandRuntime.Disable();
        PlayerMapRenderOutputValidator.Disable();
        PlayerMapDisabledConfigFilter.Disable();
        PlayerMapConfigBuildPipeline.Disable();
        PlayerMapDerivedLayoutBridge.Disable();
        PlayerMapTerrainBakeBridge.Disable();
        PlayerMapIncrementalRenderHooks.Disable();
        PlayerMapPlacementBootstrap.Disable();
        PlayerMapMigrationStreamRuntime.Disable();

        ResetTransientState();
        PlayerMapWorkspaceRuntime.Disable();
        enabled = false;
    }

    internal static void ResetTransientState()
    {
        PlayerMapRenderPreparationController.Reset();
        PlayerMapRenderScheduler.Reset();
        PlayerMapMigrationCommandQueue.Clear();
        PlayerMapMigrationStreamRuntime.Reset();
        PlayerMapGroupCommandQueue.Clear();
        PlayerMapTerrainSemanticRevision.Reset();
        PlayerMapPreflightDiagnostics.Reset();
        PlayerMapActivityGate.Reset();
        RoomMapBakeCache.Clear();
    }
}
