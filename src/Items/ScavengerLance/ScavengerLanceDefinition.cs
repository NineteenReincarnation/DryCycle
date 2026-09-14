using DryCycle.Registration;

namespace DryCycle.Items.ScavengerLance;

internal sealed class ScavengerLanceDefinition : ItemDefinition
{
    internal ScavengerLanceDefinition() : base(ScavengerLanceHooks.ObjectType) { }
    internal override AbstractPhysicalObject Parse(World world, ItemSaveData saveData) =>
        AbstractScavengerLance.Parse(world, saveData);
}
