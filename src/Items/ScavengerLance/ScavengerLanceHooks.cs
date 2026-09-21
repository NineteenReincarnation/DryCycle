using System;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.Registration;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal static class ScavengerLanceHooks
{
    internal static AbstractPhysicalObject.AbstractObjectType ObjectType { get; private set; }
    private static ScavengerLanceDefinition _definition;
    private static bool _enabled;
    private static bool _grababilityHook;
    private static bool _canPickUpHook;
    private static bool _heavyCarryHook;
    private static bool _heldDirectionHook;
    private static bool _graphicsUpdatedHook;
    private static bool _throwObjectHook;
    private static bool _playerUpdateHook;
    internal static void Enable()
    {
        if (_enabled && AllHooksInstalled() && _definition != null && ObjectType?.Index >= 0)
        {
            return;
        }

        _enabled = false;
        try
        {
            EnsureRegistration();

            InstallHook(ref _grababilityHook, () => On.Player.Grabability += Grabability);
            InstallHook(ref _canPickUpHook, () => On.Player.CanIPickThisUp += CanIPickThisUp);
            InstallHook(ref _heavyCarryHook, () => On.Player.HeavyCarry += HeavyCarry);
            InstallHook(ref _heldDirectionHook, () => On.Player.GetHeldItemDirection += GetHeldItemDirection);
            InstallHook(ref _graphicsUpdatedHook, () => On.Player.GraphicsModuleUpdated += GraphicsModuleUpdated);
            InstallHook(ref _throwObjectHook, () => On.Player.ThrowObject += ThrowObject);
            InstallHook(ref _playerUpdateHook, () => On.Player.Update += PlayerUpdate);

            _enabled = true;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "ScavengerLanceHooks.Enable",
                error,
                () => CleanupInstalledState("enable rollback"));
            throw;
        }
    }

    internal static void Disable()
    {
        if (!_enabled &&
            !AnyHookInstalled() &&
            _definition == null &&
            ObjectType == null)
        {
            return;
        }

        CleanupInstalledState("disable");
    }

    private static void EnsureRegistration()
    {
        if (ObjectType == null || ObjectType.Index < 0)
        {
            ObjectType = new AbstractPhysicalObject.AbstractObjectType("ScavengerLance", true);
            _definition = null;
        }

        if (_definition == null || !ReferenceEquals(_definition.Type, ObjectType))
        {
            _definition = new ScavengerLanceDefinition();
        }

        // Register is safe to repeat for the same definition/type and repairs a registry entry
        // that may have been removed during a previous partial cleanup.
        ItemRegistry.Register(_definition);
    }

    private static void InstallHook(ref bool installed, Action install)
    {
        if (installed)
        {
            return;
        }

        install();
        installed = true;
    }

    private static void CleanupInstalledState(string phase)
    {
        _enabled = false;

        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks/" + phase + "/DevConsole.ResetRegistration",
            ScavengerLanceDevConsoleSupport.ResetRegistration);

        RemoveHook(
            ref _playerUpdateHook,
            phase + "/Player.Update",
            () => On.Player.Update -= PlayerUpdate);
        RemoveHook(
            ref _throwObjectHook,
            phase + "/Player.ThrowObject",
            () => On.Player.ThrowObject -= ThrowObject);
        RemoveHook(
            ref _graphicsUpdatedHook,
            phase + "/Player.GraphicsModuleUpdated",
            () => On.Player.GraphicsModuleUpdated -= GraphicsModuleUpdated);
        RemoveHook(
            ref _heldDirectionHook,
            phase + "/Player.GetHeldItemDirection",
            () => On.Player.GetHeldItemDirection -= GetHeldItemDirection);
        RemoveHook(
            ref _heavyCarryHook,
            phase + "/Player.HeavyCarry",
            () => On.Player.HeavyCarry -= HeavyCarry);
        RemoveHook(
            ref _canPickUpHook,
            phase + "/Player.CanIPickThisUp",
            () => On.Player.CanIPickThisUp -= CanIPickThisUp);
        RemoveHook(
            ref _grababilityHook,
            phase + "/Player.Grabability",
            () => On.Player.Grabability -= Grabability);

        if (_definition != null)
        {
            ScavengerLanceDefinition definition = _definition;
            if (global::DryCycle.StartupDiagnostics.RollbackStep(
                    "ScavengerLanceHooks/" + phase + "/ItemRegistry.Unregister",
                    () => ItemRegistry.Unregister(definition)))
            {
                _definition = null;
            }
        }

        // Keep the ExtEnum registered if ItemRegistry cleanup itself failed. Removing the type
        // while a definition still points at it would manufacture a worse half-state.
        if (_definition == null && ObjectType != null)
        {
            AbstractPhysicalObject.AbstractObjectType objectType = ObjectType;
            if (global::DryCycle.StartupDiagnostics.RollbackStep(
                    "ScavengerLanceHooks/" + phase + "/ObjectType.Unregister",
                    objectType.Unregister))
            {
                ObjectType = null;
            }
        }

        if (AnyHookInstalled() || _definition != null || ObjectType != null)
        {
            global::DryCycle.StartupDiagnostics.Marker(
                "ScavengerLanceHooks/" + phase,
                "ROLLBACK-INCOMPLETE",
                "residual registration state retained for safe retry; next Enable will reuse it instead of duplicating it");
        }
    }

    private static void RemoveHook(ref bool installed, string source, Action remove)
    {
        if (!installed)
        {
            return;
        }

        if (global::DryCycle.StartupDiagnostics.RollbackStep(
                "ScavengerLanceHooks/" + source,
                remove))
        {
            installed = false;
        }
    }

    private static bool AllHooksInstalled() =>
        _grababilityHook &&
        _canPickUpHook &&
        _heavyCarryHook &&
        _heldDirectionHook &&
        _graphicsUpdatedHook &&
        _throwObjectHook &&
        _playerUpdateHook;

    private static bool AnyHookInstalled() =>
        _grababilityHook ||
        _canPickUpHook ||
        _heavyCarryHook ||
        _heldDirectionHook ||
        _graphicsUpdatedHook ||
        _throwObjectHook ||
        _playerUpdateHook;

    private static Player.ObjectGrabability Grabability(On.Player.orig_Grabability orig, Player self, PhysicalObject obj) =>
        obj is ScavengerLance ? Player.ObjectGrabability.BigOneHand : orig(self, obj);

    private static bool CanIPickThisUp(On.Player.orig_CanIPickThisUp orig, Player self, PhysicalObject obj)
    {
        if (obj is ScavengerLance lance)
        {
            for (int i = 0; i < lance.grabbedBy.Count; i++)
            {
                if (lance.grabbedBy[i]?.grabber is LanceScavenger)
                    return false;
            }
        }
        return orig(self, obj);
    }

    // A carried lance must never pull the player's body towards its extending tip.
    private static bool HeavyCarry(On.Player.orig_HeavyCarry orig, Player self, PhysicalObject obj) =>
        obj is not ScavengerLance && orig(self, obj);

    private static Vector2 GetHeldItemDirection(On.Player.orig_GetHeldItemDirection orig, Player self, int hand)
    {
        Vector2 direction = orig(self, hand);
        return self.grasps[hand]?.grabbed is ScavengerLance lance ?
            lance.PlayerCarryDirection(self, direction) : direction;
    }

    private static void GraphicsModuleUpdated(On.Player.orig_GraphicsModuleUpdated orig, Player self, bool actuallyViewed, bool eu)
    {
        orig(self, actuallyViewed, eu);
        for (int hand = 0; hand < self.grasps.Length; hand++)
            if (self.grasps[hand]?.grabbed is ScavengerLance lance)
                lance.SynchronizePlayerPose(self, hand, eu);
    }

    private static void ThrowObject(On.Player.orig_ThrowObject orig, Player self, int grasp, bool eu)
    {
        if (self.grasps[grasp]?.grabbed is ScavengerLance lance &&
            ScavengerLancePlayerController.BeginThrowInput(self, grasp, lance))
        {
            // Throw is the lance attack input. The normal pickup/drop controls still release the
            // item, but pressing Throw never ejects the identity weapon from the player's hand.
            return;
        }
        orig(self, grasp, eu);
    }

    private static void PlayerUpdate(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);
        ScavengerLancePlayerController.Update(self, eu);
    }
}
