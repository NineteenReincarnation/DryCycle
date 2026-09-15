using DryCycle.Items.ScavengerLance;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAbstractAI : ScavengerAbstractAI
{
    internal LanceScavengerAbstractAI(World world, AbstractCreature parent) : base(world, parent) { }

    public override bool DoIwantToDropThisItemInDen(AbstractPhysicalObject item)
    {
        if (item is AbstractScavengerLance) return false;
        if (item is AbstractSpear spear && !spear.explosive && !spear.electric && !spear.needle)
        {
            // The normal spear currently occupying grasp 0 is the tactical sidearm.
            // Preserve only that carried spear; thrown/stolen spears are ordinary world items.
            foreach (AbstractPhysicalObject.AbstractObjectStick stick in parent.stuckObjects)
                if (stick is AbstractPhysicalObject.CreatureGripStick grip && grip.A == parent && grip.B == item && grip.grasp == 0)
                    return false;
        }
        return base.DoIwantToDropThisItemInDen(item);
    }
}
