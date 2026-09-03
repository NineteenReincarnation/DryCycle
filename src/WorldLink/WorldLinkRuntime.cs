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
            WorldLinkTraversal.Enable();
            OrientedGateCollision.Enable();
            WorldLinkMapRuntime.Enable();
            _enabled = true;
            Plugin.Logger?.LogInfo("WorldLink: MultiGate system enabled.");
        }
        catch (Exception ex)
        {
            // Enable is transactional: tear down every hook that may have installed
            // before the failing step, including same-region inbound authorization.
            WorldLinkMapRuntime.Disable();
            OrientedGateCollision.Disable();
            WorldLinkTraversal.Disable();
            WorldLinkPlacedObjects.Disable();
            Plugin.Logger?.LogError($"WorldLink: initialization failed and was rolled back: {ex}");
            throw;
        }
    }

    internal static void Disable()
    {
        _enabled = false;
        // Stop accepting new inbound shortcut events before removing room runtimes.
        WorldLinkTraversal.Disable();
        WorldLinkMapRuntime.Disable();
        OrientedGateCollision.Disable();
        WorldLinkPlacedObjects.Disable();
    }
}
