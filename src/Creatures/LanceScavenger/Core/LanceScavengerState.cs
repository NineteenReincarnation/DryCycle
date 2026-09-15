using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerState : HealthState
{
    private const string GearKey = "DRYCYCLE_LANCE_ISSUED";
    private const string SidearmKey = "DRYCYCLE_LANCE_SIDEARM_ISSUED";
    private const float BraveryBias = 0.40f;

    internal LanceScavengerState(AbstractCreature creature) : base(creature)
    {
        // Use vanilla scavenger personality instead of adding a custom fear scalar.
        // ScavengerAI already uses bravery for threat utility, scared decay, discomfort and
        // Afraid/Attacks relationship decisions. Biasing the seeded value toward 1 keeps each
        // individual's personality variation while making LanceScavengers noticeably less fearful.
        AbstractCreature.Personality personality = creature.personality;
        personality.bravery = Mathf.Lerp(personality.bravery, 1f, BraveryBias);
        creature.personality = personality;
    }

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
