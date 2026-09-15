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
            // The ordinary spear is reserve equipment and normally lives in grasp 1.
            // Preserve it in any grasp while it is still physically carried by this scavenger;
            // once thrown or stolen it is no longer protected and behaves as a normal world item.
            foreach (AbstractPhysicalObject.AbstractObjectStick stick in parent.stuckObjects)
                if (stick is AbstractPhysicalObject.CreatureGripStick grip && grip.A == parent && grip.B == item)
                    return false;
        }
        return base.DoIwantToDropThisItemInDen(item);
    }
}
