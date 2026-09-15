using DryCycle.Creatures.LanceScavenger;
using DryCycle.Items.ScavengerLance;
using UnityEngine;
using static LanceScavenger.Tests.Program;

namespace LanceScavenger.Tests;

internal static class CombatTests
{
    internal static LanceSituation Situation(ScavengerAI.ViolenceType violence = null, bool afraid = false,
        float distance = 250f, bool lane = true, bool armed = true, bool sidearm = false, bool active = true,
        bool target = true, bool backstepComplete = true) =>
        new(active, armed, sidearm, target, violence ?? ScavengerAI.ViolenceType.Lethal, afraid, distance, lane, backstepComplete);

    internal static LanceCombatState Charge(float distance = 250f, bool afraid = false, bool sidearm = false)
    {
        var combat = new LanceCombatState();
        for (int i = 0; i < LanceCombatState.BraceFrames + 8 && combat.State != LanceState.Charge; i++)
            combat.Tick(Situation(distance: distance, afraid: afraid, sidearm: sidearm));
        Check(combat.State == LanceState.Charge, "A clear vanilla-Lethal encounter can backstep, brace and reach Charge");
        return combat;
    }

    internal static void WarningAndRecovery()
    {
        var combat = new LanceCombatState();
        for (int i = 0; i < 120; i++)
            combat.Tick(Situation(violence: ScavengerAI.ViolenceType.None));
        Check(combat.State == LanceState.Observe && combat.AttackSerial == 0,
            "Vanilla None proximity never charges");

        for (int i = 0; i < 120; i++)
            combat.Tick(Situation(violence: ScavengerAI.ViolenceType.Warning));
        Check(combat.State == LanceState.Threaten && combat.AttackSerial == 0,
            "Vanilla Warning remains a threat display");

        for (int i = 0; i < 120; i++)
            combat.Tick(Situation(violence: ScavengerAI.ViolenceType.NonLethal));
        Check(combat.State == LanceState.Threaten && combat.AttackSerial == 0,
            "Vanilla NonLethal is not upgraded into a lethal lance charge");

        combat = new LanceCombatState();
        combat.Tick(Situation(backstepComplete: false));
        Check(combat.State == LanceState.Backstep, "A valid charge opportunity enters the pre-brace backstep first");
        for (int i = 0; i < 8; i++) combat.Tick(Situation(backstepComplete: false));
        Check(combat.State == LanceState.Backstep && combat.AttackSerial == 0,
            "Brace timing cannot begin while the physical backstep is still moving");
        combat.Tick(Situation(backstepComplete: true));
        Check(combat.State == LanceState.Brace && combat.Age == 0,
            "Completing the backstep starts a fresh brace timer");

        int brace = 0;
        while (combat.AttackSerial == 0)
        {
            if (combat.State == LanceState.Brace) brace++;
            combat.Tick(Situation());
            Check(combat.Age < 200, "Bounded charge preparation");
        }
        Check(brace >= LanceCombatState.BraceFrames,
            "Every full charge has the configured 0.95-second brace interval after the backstep");

        int serial = combat.AttackSerial;
        for (int i = 0; i < LanceCombatState.MaxChargeFrames; i++) combat.Tick(Situation());
        Check(combat.State == LanceState.Recover, "A miss without a sidearm ends in recovery");
        for (int i = 0; i < 40; i++) combat.Tick(Situation());
        Check(combat.AttackSerial == serial && combat.State == LanceState.Recover,
            "No repeat charge during recovery");

        var combo = Charge(sidearm: true);
        for (int i = 0; i < LanceCombatState.MaxChargeFrames; i++) combo.Tick(Situation(sidearm: true));
        Check(combo.State == LanceState.FollowUpThrow,
            "A predicted charge carrying a sidearm reserves a landing follow-up");
        for (int i = 0; i < LanceCombatState.FollowUpThrowFrames; i++) combo.Tick(Situation(sidearm: true));
        Check(!combo.FollowUpReady,
            "Follow-up spear cannot fire merely because the charge timer ended while still airborne");
        combo.MarkLanding();
        for (int i = 0; i < LanceCombatState.FollowUpThrowFrames; i++) combo.Tick(Situation(sidearm: true));
        Check(combo.FollowUpReady, "Follow-up spear becomes ready 8 frames after actual landing");
        combo.CompleteFollowUp();
        Check(combo.State == LanceState.Recover, "A completed follow-up pays normal recovery");

        combat.Recover(true);
        combat.ResetForRoom();
        for (int i = 0; i < 70; i++) combat.Tick(Situation());
        Check(combat.State == LanceState.Recover,
            "A room transition cannot shorten wall recovery");
    }

    internal static void WeaknessesAndInterruptions()
    {
        foreach (LanceSituation situation in new[] { Situation(distance: 59f), Situation(lane: false),
            Situation(armed: false), Situation(active: false) })
        {
            var combat = new LanceCombatState();
            for (int i = 0; i < 400; i++) combat.Tick(situation);
            Check(combat.AttackSerial == 0,
                "Sub-3-tile range, obstruction, disarm or stun cannot start full charge");
        }

        LanceCombatState boundary = Charge(60f);
        Check(boundary.AttackSerial == 1, "Exactly 3 tiles is a valid full-charge distance");

        foreach (LanceSituation interruption in new[] { Situation(armed: false), Situation(target: false),
            Situation(active: false), Situation(lane: false),
            Situation(violence: ScavengerAI.ViolenceType.Warning) })
        {
            LanceCombatState interrupted = Charge();
            interrupted.Tick(interruption);
            Check(interrupted.State == LanceState.Recover,
                "Loss of commitment during a charge retains a recovery cost");
        }

        var backstep = new LanceCombatState();
        backstep.Tick(Situation(backstepComplete: false));
        backstep.Tick(Situation(lane: false, backstepComplete: true));
        Check(backstep.State == LanceState.AcquireChargeLane && backstep.AttackSerial == 0,
            "Lane is revalidated after the backstep before brace begins");

        var brace = new LanceCombatState();
        for (int i = 0; i < 20; i++) brace.Tick(Situation());
        Check(brace.State == LanceState.Brace, "Test reaches brace after the backstep");
        brace.Tick(Situation(lane: false));
        Check(brace.State == LanceState.AcquireChargeLane && brace.AttackSerial == 0,
            "An aggressive scavenger reacquires a lane when a friend or wall blocks launch");

        var afraid = new LanceCombatState();
        for (int i = 0; i < 180; i++) afraid.Tick(Situation(afraid: true, lane: false));
        Check(afraid.State == LanceState.Threaten && afraid.AttackSerial == 0,
            "Vanilla Afraid keeps flee ownership instead of forcing an attack-position search");

        LanceCombatState counterCharge = Charge(250f, afraid: true);
        Check(counterCharge.AttackSerial == 1,
            "A lethal Afraid scavenger may counter-charge after its backstep when a safe lane exists");
    }

    internal static void ImpactAndSweeps()
    {
        LanceImpact full = LanceCombatMath.Impact(19f, 1f, 0.85f, 0.85f, true, 120f, false);
        LanceImpact standard = LanceCombatMath.Impact(8f, 1f, 0.85f, 0.85f, false, 0f, true);
        LanceImpact player = LanceCombatMath.Impact(8f, 1f, 0.85f, 0.85f, false, 0f, true,
            LanceCombatMath.PlayerThrustMaxDamage);
        LanceImpact scavengerClose = LanceCombatMath.Impact(8f, 1f, 0.85f, 0.85f, false, 0f, true,
            LanceCombatMath.LanceScavengerCloseThrustMaxDamage);
        Check(Mathf.Abs(full.Damage - LanceCombatMath.ChargeMaxDamage) < 0.001f,
            "Maximum charge damage is 2.75x the previous cap");
        Check(Mathf.Abs(scavengerClose.Damage - full.Damage * 0.20f) < 0.001f,
            "Lance scavenger close thrust caps at 20 percent of maximum charge damage");
        Check(Mathf.Abs(player.Damage - LanceCombatMath.PlayerThrustMaxDamage) < 0.001f,
            "Player thrust caps at 1.35 damage");
        Check(standard.Damage <= LanceCombatMath.StandardThrustMaxDamage + 0.001f,
            "Default non-player thrust/throw damage keeps its standard cap");
        Check(full.Damage > player.Damage && player.Damage > scavengerClose.Damage && scavengerClose.Damage > standard.Damage,
            "Charge, player thrust, scavenger close thrust and standard thrust remain distinct damage tiers");
        Check(LanceCombatMath.Impact(0f, 1f, 1f, 1f, true, 200f, true).Damage == 0f, "Stationary tip is harmless");
        Check(LanceCombatMath.Impact(20f, 0f, 1f, 1f, true, 200f, true).Damage == 0f, "Side strike cannot pierce");
        Check(LanceCombatMath.Impact(20f, -1f, 1f, 1f, true, 200f, true).Damage == 0f, "Rear strike cannot pierce");
        Check(LanceCombatMath.Impact(19f, 1f, 1f, 1f, true, 0f, false).Damage < standard.Damage, "Zero run-up cannot deal full damage");
        float light = LanceCombatMath.Impact(19f, 1f, 0.85f, 0.2f, true, 120f, false).RetainedSpeed;
        float heavy = LanceCombatMath.Impact(19f, 1f, 0.85f, 8f, true, 120f, false).RetainedSpeed;
        Check(light > 0.8f && heavy < 0.2f, "Large targets produce materially greater recoil");
        Check(LanceCombatMath.SweepTip(Vector2.zero, new Vector2(40,0), new Vector2(20,0), new Vector2(20,0), 2f, out float fraction) && fraction < 0.5f,
            "Fast tip does not tunnel through a small target");
        Check(LanceCombatMath.SweepTip(Vector2.zero, new Vector2(40,0), new Vector2(20,20), new Vector2(20,-20), 2f, out _), "Relative-motion sweep catches crossing target");
        Check(!LanceCombatMath.SweepTip(Vector2.zero, new Vector2(40,0), new Vector2(20,10), new Vector2(20,10), 2f, out _), "Near miss stays a miss");
        Check(!LanceCombatMath.SweepTip(Vector2.zero, Vector2.zero, new Vector2(10,0), new Vector2(10,0), 2f, out _), "Degenerate sweep stays finite");
    }
}
