namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// First R1 consumers of DB_EventHub. This file deliberately contains only cross-domain
/// lifecycle reactions that were previously duplicated in Rain World hooks. Fear, Threat
/// and Signal semantic migration continues behind their existing domain APIs.
/// </summary>
internal static class DB_EventConsumers
{
    private static bool enabled;

    internal static bool Enabled => enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        DB_EventHub.Capture += OnCapture;
        DB_EventHub.Mortality += OnMortality;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        DB_EventHub.Capture -= OnCapture;
        DB_EventHub.Mortality -= OnMortality;
    }

    private static void OnCapture(DB_CaptureEvent capture)
    {
        DesertBatfly victim = capture.Victim;
        if (victim == null || victim.dead) return;

        // This preserves the old Core Tongue hook's neutral-social cancellation while the
        // actual fear/signal consumers are migrated to DB_EventHub in later R1 commits.
        DesertBatflySocialLife.CancelForPriority(victim, "semantic capture event");
    }

    private static void OnMortality(DB_MortalityEvent mortality)
    {
        DesertBatfly victim = mortality.Victim;
        if (victim == null || !victim.dead) return;

        // These were formerly owned by DesertBatflyHooks.CreatureDie. They now consume the
        // one authoritative mortality fact and therefore share the same killer attribution.
        DesertBatflySocialLife.CancelForPriority(victim, "death");
        DesertBatflySignalRuntime.Forget(victim);
        DesertBatflyColonyRuntime.ReportDeath(victim, mortality.Killer);
        DesertBatflyThreatRuntime.Forget(victim);
        DesertBatflyEnvironmentalBehavior.Forget(victim);
    }
}
