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
    internal static void Enable()
    {
        if (_enabled) return;

        try
        {
            ObjectType = new AbstractPhysicalObject.AbstractObjectType("ScavengerLance", true);
            _definition = new ScavengerLanceDefinition();
            ItemRegistry.Register(_definition);

            // Mark enabled before the HookGen transaction so Disable can unwind a partial
            // installation if any later endpoint fails.
            _enabled = true;
            On.Player.Grabability += Grabability;
            On.Player.CanIPickThisUp += CanIPickThisUp;
            On.Player.HeavyCarry += HeavyCarry;
            On.Player.GetHeldItemDirection += GetHeldItemDirection;
            On.Player.GraphicsModuleUpdated += GraphicsModuleUpdated;
            On.Player.ThrowObject += ThrowObject;
            On.Player.Update += PlayerUpdate;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "ScavengerLanceHooks.Enable",
                error,
                RollbackPartialEnable);
            throw;
        }
    }
    private static void RollbackPartialEnable()
    {
        _enabled = false;

        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/DevConsole.ResetRegistration",
            ScavengerLanceDevConsoleSupport.ResetRegistration);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.Update",
            () => On.Player.Update -= PlayerUpdate);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.ThrowObject",
            () => On.Player.ThrowObject -= ThrowObject);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.GraphicsModuleUpdated",
            () => On.Player.GraphicsModuleUpdated -= GraphicsModuleUpdated);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.GetHeldItemDirection",
            () => On.Player.GetHeldItemDirection -= GetHeldItemDirection);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.HeavyCarry",
            () => On.Player.HeavyCarry -= HeavyCarry);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.CanIPickThisUp",
            () => On.Player.CanIPickThisUp -= CanIPickThisUp);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "ScavengerLanceHooks.Enable/Player.Grabability",
            () => On.Player.Grabability -= Grabability);

        if (_definition != null &&
            global::DryCycle.StartupDiagnostics.RollbackStep(
                "ScavengerLanceHooks.Enable/ItemRegistry.Unregister",
                () => ItemRegistry.Unregister(_definition)))
        {
            _definition = null;
        }

        if (ObjectType != null)
        {
            AbstractPhysicalObject.AbstractObjectType rollbackType = ObjectType;
            if (global::DryCycle.StartupDiagnostics.RollbackStep(
                    "ScavengerLanceHooks.Enable/ObjectType.Unregister",
                    rollbackType.Unregister))
            {
                ObjectType = null;
            }
        }
    }

    internal static void Disable()
    {
        if (!_enabled && _definition == null && ObjectType == null) return;

        _enabled = false;
        ScavengerLanceDevConsoleSupport.ResetRegistration();

        // Removing an uninstalled HookGen delegate is harmless, which lets this method also
        // serve as the rollback path for a partially completed Enable.
        On.Player.Grabability -= Grabability;
        On.Player.CanIPickThisUp -= CanIPickThisUp;
        On.Player.HeavyCarry -= HeavyCarry;
        On.Player.GetHeldItemDirection -= GetHeldItemDirection;
        On.Player.GraphicsModuleUpdated -= GraphicsModuleUpdated;
        On.Player.ThrowObject -= ThrowObject;
        On.Player.Update -= PlayerUpdate;

        ItemRegistry.Unregister(_definition);
        _definition = null;

        if (ObjectType != null)
        {
            ObjectType.Unregister();
            ObjectType = null;
        }
    }
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
