using RWCustom;
using UnityEngine;

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

        Creature signalThreat = capture.Captor;
        if (signalThreat != null && signalThreat.room == victim.room)
        {
            bool tongueSignal = capture.CaptureKind == DB_CaptureKind.Tongue;
            DesertBatflySignalRuntime.EmitDistress(
                victim,
                signalThreat,
                tongueSignal ? 0.94f : 0.88f,
                tongueSignal
                    ? "semantic tongue capture emits one DistressCall"
                    : "semantic non-Fly grasp emits one DistressCall");
            Vector2 signalOrigin = signalThreat.mainBodyChunk?.pos ?? capture.Position;
            DesertBatflySignalRuntime.EmitAlarm(
                victim,
                signalThreat,
                signalOrigin,
                Custom.DirVec(victim.mainBodyChunk.pos, signalOrigin),
                tongueSignal ? 0.90f : 0.82f,
                tongueSignal
                    ? "semantic tongue capture emits one AlarmFlutter"
                    : "semantic grasp emits one AlarmFlutter alongside DistressCall");
        }

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
                victim.DesertAI.Threatened(predator, true, false);
        }
    }

    private static void OnMortality(DB_MortalityEvent mortality)
    {
        DesertBatfly victim = mortality.Victim;
        if (victim == null || !victim.dead) return;

        // Intimidation may add a finite CorpseWarning while reacting to this mortality.
        // Remember only the Room weakly so full Reset/Disable can actively destroy any
        // warning that has not naturally expired yet.
        DB_CorpseWarningRuntime.TrackRoom(victim.room);

        // Generic lifecycle consumers share the canonical killer. ThreatRuntime deliberately
        // clears its own realized state only after it has processed this same MortalityEvent,
        // so subscriber order cannot erase counter-kill / kill evidence prematurely.
        DesertBatflySocialLife.CancelForPriority(victim, "death");
        DesertBatflySignalRuntime.Forget(victim);
        DesertBatflyColonyRuntime.ReportDeath(victim, mortality.Killer);
        DesertBatflyEnvironmentalBehavior.Forget(victim);

        Creature killer = mortality.Killer;
        if (killer is Player playerKiller)
        {
            DesertBatflyIntimidation.BroadcastPlayerKill(
                victim,
                playerKiller,
                mortality.Position,
                mortality.ChainWitnesses,
                mortality.ThreatScale,
                mortality.RevengeFailed);
        }
        else if (killer is Lizard lizardKiller &&
                 DesertBatflyIntimidation.IsSupportedLethalThreat(lizardKiller))
        {
            DesertBatflyIntimidation.BroadcastPredatorKill(
                victim,
                lizardKiller,
                mortality.Position,
                mortality.ChainWitnesses,
                mortality.ThreatScale,
                mortality.RevengeFailed);
        }
        else if (victim.room != null)
        {
            // Unsupported/environmental deaths keep the old direct-experience grief rule:
            // chain witnesses or very close line-of-sight observers may learn the bond loss,
            // but no synthetic predator/player fear wave is invented.
            foreach (Fly member in DesertSwarmRoom.For(victim.room).Hive.flies)
            {
                if (member is not DesertBatfly observer || observer == victim) continue;
                if (System.Array.IndexOf(mortality.ChainWitnesses, observer) < 0 &&
                    (Vector2.Distance(observer.mainBodyChunk.pos, mortality.Position) > 180f ||
                     !victim.room.VisualContact(observer.mainBodyChunk.pos, mortality.Position)))
                    continue;

                DesertBatflySocialBond.OnBondPartnerDeath(observer, victim, killer);
            }
        }

        DesertBatflyIntimidation.Forget(victim);
    }
}
