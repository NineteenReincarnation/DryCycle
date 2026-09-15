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
        ObjectType = new AbstractPhysicalObject.AbstractObjectType("ScavengerLance", true);
        _definition = new ScavengerLanceDefinition();
        ItemRegistry.Register(_definition);
        On.Player.Grabability += Grabability;
        On.Player.CanIPickThisUp += CanIPickThisUp;
        On.Player.HeavyCarry += HeavyCarry;
        On.Player.GetHeldItemDirection += GetHeldItemDirection;
        On.Player.GraphicsModuleUpdated += GraphicsModuleUpdated;
        On.Player.ThrowObject += ThrowObject;
        _enabled = true;
    }
    internal static void Disable()
    {
        if (!_enabled) return;
        ScavengerLanceDevConsoleSupport.ResetRegistration();
        On.Player.Grabability -= Grabability;
        On.Player.CanIPickThisUp -= CanIPickThisUp;
        On.Player.HeavyCarry -= HeavyCarry;
        On.Player.GetHeldItemDirection -= GetHeldItemDirection;
        On.Player.GraphicsModuleUpdated -= GraphicsModuleUpdated;
        On.Player.ThrowObject -= ThrowObject;
        ItemRegistry.Unregister(_definition);
        _definition = null;
        ObjectType.Unregister();
        ObjectType = null;
        _enabled = false;
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
        foreach (Creature.Grasp grasp in self.grasps)
            if (grasp?.grabbed is ScavengerLance lance) lance.SynchronizePlayerThrust(eu);
    }

    private static void ThrowObject(On.Player.orig_ThrowObject orig, Player self, int grasp, bool eu)
    {
        if (self.grasps[grasp]?.grabbed is not ScavengerLance lance || self.input[0].y < 0)
        { orig(self, grasp, eu); return; }
        lance.RequestThrust(new Vector2(self.ThrowDirection, self.input[0].y * 0.35f).normalized,
            LanceCombatMath.PlayerThrustMaxDamage);
    }
}
