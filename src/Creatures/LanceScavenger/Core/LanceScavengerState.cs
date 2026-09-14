namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerState : HealthState
{
    private const string GearKey = "DRYCYCLE_LANCE_ISSUED";
    internal LanceScavengerState(AbstractCreature creature) : base(creature) { }
    internal bool GearIssued { get; set; }

    public override string ToString() => base.ToString() + "<cB>" + GearKey + "<cC>" + (GearIssued ? "1" : "0");
    public override void LoadFromString(string[] s)
    {
        base.LoadFromString(s);
        if (unrecognizedSaveStrings.TryGetValue(GearKey, out string value))
            GearIssued = value == "1";
        unrecognizedSaveStrings.Remove(GearKey);
    }
}
