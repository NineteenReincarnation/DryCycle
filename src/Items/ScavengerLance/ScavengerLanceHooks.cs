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
        On.Player.ThrowObject += ThrowObject;
        _enabled = true;
    }
    internal static void Disable()
    {
        if (!_enabled) return;
        ScavengerLanceDevConsoleSupport.ResetRegistration();
        On.Player.Grabability -= Grabability;
        On.Player.ThrowObject -= ThrowObject;
        ItemRegistry.Unregister(_definition);
        _definition = null;
        ObjectType.Unregister();
        ObjectType = null;
        _enabled = false;
    }
    private static Player.ObjectGrabability Grabability(On.Player.orig_Grabability orig, Player self, PhysicalObject obj) =>
        obj is ScavengerLance ? Player.ObjectGrabability.TwoHands : orig(self, obj);

    private static void ThrowObject(On.Player.orig_ThrowObject orig, Player self, int grasp, bool eu)
    {
        if (self.grasps[grasp]?.grabbed is not ScavengerLance lance || self.input[0].y < 0)
        { orig(self, grasp, eu); return; }
        lance.RequestThrust(new Vector2(self.ThrowDirection, self.input[0].y * 0.35f).normalized);
    }
}
