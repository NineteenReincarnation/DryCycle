using System;

namespace DryCycle.WorldLink;

internal static class WorldLinkRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        try
        {
            GateUnlockRequirements.Reload();
            WorldLinkTraversal.ClearSession();
            WorldLinkPlacedObjects.Enable();
            OrientedGateCollision.Enable();
            WorldLinkMapRuntime.Enable();
            _enabled = true;
            Plugin.Logger?.LogInfo("WorldLink: MultiGate system enabled.");
        }
        catch (Exception ex)
        {
            // Enable is transactional: Plugin's outer initialization guard cannot clean
            // a subsystem that failed before MiscRuntime marked itself enabled.
            WorldLinkMapRuntime.Disable();
            OrientedGateCollision.Disable();
            WorldLinkPlacedObjects.Disable();
            WorldLinkTraversal.ClearSession();
            Plugin.Logger?.LogError($"WorldLink: initialization failed and was rolled back: {ex}");
            throw;
        }
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            // Still perform idempotent cleanup in case a future partial initialization
            // path calls Disable defensively.
            WorldLinkMapRuntime.Disable();
            OrientedGateCollision.Disable();
            WorldLinkPlacedObjects.Disable();
            WorldLinkTraversal.ClearSession();
            return;
        }

        _enabled = false;
        WorldLinkMapRuntime.Disable();
        OrientedGateCollision.Disable();
        WorldLinkPlacedObjects.Disable();
        WorldLinkTraversal.ClearSession();
    }
}
