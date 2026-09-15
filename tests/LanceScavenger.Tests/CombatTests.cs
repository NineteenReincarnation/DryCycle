using DryCycle.Creatures.LanceScavenger;
using DryCycle.Items.ScavengerLance;
using UnityEngine;
using static LanceScavenger.Tests.Program;

namespace LanceScavenger.Tests;

internal static class CombatTests
{
    internal static LanceSituation Situation(bool hostile = true, float distance = 250f, bool lane = true,
        bool armed = true, bool active = true, bool target = true, bool warning = false, bool stable = true) =>
        new(active, armed, target, hostile, warning, distance, lane, stable);
    internal static LanceCombatState Charge(float distance = 250f)
    {
        var combat = new LanceCombatState();
        for (int i = 0; i < 60 && combat.State != LanceState.Charge; i++) combat.Tick(Situation(distance: distance));
        Check(combat.State == LanceState.Charge, "A clear, grounded hostile encounter can reach Charge");
        return combat;
    }
    internal static void WarningAndRecovery()
    {
        var combat = new LanceCombatState();
        for (int i = 0; i < 120; i++) combat.Tick(Situation(hostile: false));
        Check(combat.State == LanceState.Observe && combat.AttackSerial == 0, "Neutral proximity never charges");
        for (int i = 0; i < 120; i++) combat.Tick(Situation(hostile: false, warning: true));
        Check(combat.State == LanceState.Threaten && combat.AttackSerial == 0, "Warnings cannot escalate without hostility");
        combat = new LanceCombatState();
        int brace = 0;
        while (combat.AttackSerial == 0)
        {
            if (combat.State == LanceState.Brace) brace++;
            combat.Tick(Situation());
            Check(combat.Age < 200, "Bounded charge preparation");
        }
        Check(brace >= LanceCombatState.BraceFrames, "Every full charge has a readable brace interval");
        int serial = combat.AttackSerial;
        for (int i = 0; i < LanceCombatState.MaxChargeFrames; i++) combat.Tick(Situation());
        Check(combat.State == LanceState.Recover, "A miss ends in recovery");
        for (int i = 0; i < 40; i++) combat.Tick(Situation());
        Check(combat.AttackSerial == serial && combat.State == LanceState.Recover, "No repeat charge during recovery");
        combat.Recover(true);
        combat.ResetForRoom();
        for (int i = 0; i < 70; i++) combat.Tick(Situation());
        Check(combat.State == LanceState.Recover, "A room transition cannot shorten wall recovery");
    }
    internal static void WeaknessesAndInterruptions()
    {
        foreach (LanceSituation situation in new[] { Situation(distance: 59f), Situation(lane: false),
            Situation(armed: false), Situation(stable: false), Situation(active: false) })
        {
            var combat = new LanceCombatState();
            for (int i = 0; i < 400; i++) combat.Tick(situation);
            Check(combat.AttackSerial == 0, "Sub-3-tile range, obstruction, disarm, unstable stance or stun cannot start full charge");
        }
        LanceCombatState boundary = Charge(60f);
        Check(boundary.AttackSerial == 1, "Exactly 3 tiles is a valid full-charge distance");
        foreach (LanceSituation interruption in new[] { Situation(armed: false), Situation(target: false), Situation(active: false), Situation(lane: false) })
        {
            LanceCombatState combat = Charge(); combat.Tick(interruption);
            Check(combat.State == LanceState.Recover, "Interrupted charge retains a recovery cost");
        }
        var brace = new LanceCombatState();
        for (int i = 0; i < 30; i++) brace.Tick(Situation());
        Check(brace.State == LanceState.Brace, "Test reaches brace");
        brace.Tick(Situation(lane: false));
        Check(brace.State == LanceState.AcquireChargeLane && brace.AttackSerial == 0, "A friend or wall cancels brace before launch");
    }
    internal static void ImpactAndSweeps()
    {
        LanceImpact full = LanceCombatMath.Impact(19f, 1f, 0.85f, 0.85f, true, 120f, false);
        LanceImpact jab = LanceCombatMath.Impact(8f, 1f, 0.85f, 0.85f, false, 0f, true);
        Check(full.Damage > jab.Damage && full.Impulse > jab.Impulse, "Charge needs speed/run-up and exceeds a jab");
        Check(LanceCombatMath.Impact(0f, 1f, 1f, 1f, true, 200f, true).Damage == 0f, "Stationary tip is harmless");
        Check(LanceCombatMath.Impact(20f, 0f, 1f, 1f, true, 200f, true).Damage == 0f, "Side strike cannot pierce");
        Check(LanceCombatMath.Impact(20f, -1f, 1f, 1f, true, 200f, true).Damage == 0f, "Rear strike cannot pierce");
        Check(LanceCombatMath.Impact(19f, 1f, 1f, 1f, true, 0f, false).Damage < jab.Damage, "Zero run-up cannot deal full damage");
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
