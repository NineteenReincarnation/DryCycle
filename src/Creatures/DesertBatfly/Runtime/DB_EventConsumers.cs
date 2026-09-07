namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// First R1 consumers of DB_EventHub. Cross-domain reactions consume semantic facts here
/// while their mature domain implementations remain unchanged behind explicit APIs.
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

        DesertBatflySocialLife.CancelForPriority(victim, "semantic capture event");

        if (capture.Captor is Lizard predator &&
            DesertBatflyIntimidation.IsSupportedLethalThreat(predator))
        {
            // One capture session produces one fear event. Tongue -> ordinary lizard grasp
            // transfer is suppressed by DB_EventHub, so Intimidation no longer needs to be
            // called independently by every Watcher/Core tongue observer.
            DesertBatflyIntimidation.BroadcastPredatorCapture(
                victim,
                predator,
                capture.Tongue);

            // A tongue capture precedes Fly.Grabbed; preserve the immediate native danger
            // response that the old tongue hooks supplied. Grasp capture already passes
            // through DesertBatfly.Grabbed and therefore does not need this second call.
            if (capture.CaptureKind == DB_CaptureKind.Tongue)
                victim.DesertAI.Threatened(predator, true);
        }
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
