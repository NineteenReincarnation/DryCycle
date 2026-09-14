using DryCycle.Items.ScavengerLance;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAbstractAI : ScavengerAbstractAI
{
    internal LanceScavengerAbstractAI(World world, AbstractCreature parent) : base(world, parent) { }
    public override bool DoIwantToDropThisItemInDen(AbstractPhysicalObject item) =>
        item is not AbstractScavengerLance && base.DoIwantToDropThisItemInDen(item);
}
