namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerState : HealthState
{
    private const string GearKey = "DRYCYCLE_LANCE_ISSUED";
    private const string SidearmKey = "DRYCYCLE_LANCE_SIDEARM_ISSUED";

    internal LanceScavengerState(AbstractCreature creature) : base(creature) { }
    internal bool GearIssued { get; set; }
    internal bool SidearmIssued { get; set; }

    public override string ToString() => base.ToString() +
        "<cB>" + GearKey + "<cC>" + (GearIssued ? "1" : "0") +
        "<cB>" + SidearmKey + "<cC>" + (SidearmIssued ? "1" : "0");

    public override void LoadFromString(string[] s)
    {
        base.LoadFromString(s);
        if (unrecognizedSaveStrings.TryGetValue(GearKey, out string gear))
            GearIssued = gear == "1";
        if (unrecognizedSaveStrings.TryGetValue(SidearmKey, out string sidearm))
            SidearmIssued = sidearm == "1";
        unrecognizedSaveStrings.Remove(GearKey);
        unrecognizedSaveStrings.Remove(SidearmKey);
    }
}
