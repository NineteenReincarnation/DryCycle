using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// REJECTED FEATURE COMPATIBILITY SEAM
// -----------------------------------
// Sentinel / Bully / Opportunist were rejected after playtesting.  This class
// survives only because a few older DesertBatflyAI call sites still invoke its
// members.  It has no state machine, no scanning, no role selection and no
// movement authority.
//
// IMPORTANT:
// - do not add gameplay state here;
// - do not read the room or scan creatures here;
// - do not write FlyAI.localGoal here;
// - do not add velocity here;
// - do not raise alarms here;
// - do not change combat/harass thresholds here.
//
// The final cleanup step is to remove the old calls from DesertBatflyAI and then
// delete this file.  Until that structural migration is performed, this seam
// must remain a zero-cost no-op.
internal sealed class DesertBatflySocialRoles
{
    internal ExpressedSocialRole Role => ExpressedSocialRole.None;
    internal ExpressedSocialRole Expressed => ExpressedSocialRole.None;
    internal DesertBatflyRoleScores Scores => default;
    internal int Commitment => 0;
    internal int Cooldown => 0;
    internal int EvaluationTicks => 0;
    internal float SentinelAlertConfidence => 0f;
    internal int OpportunityTicks => 0;
    internal bool OpportunistRecoveryActive => false;
    internal bool IsBully => false;
    internal bool WatchesInsteadOfInitiating => false;

    // Neutral values reproduce ordinary, non-role AI behavior at legacy call sites.
    internal float HarassThresholdScale => 1f;
    internal float ObserveDurationScale => 1f;
    internal float ObserveRadius => 150f;
    internal float FakeDiveBonus => 0f;

    internal DesertBatflySocialRoles(DesertBatfly bat) { }

    internal void Reset() { }
    internal void Tick() { }
    internal void CheckSuppression() { }
    internal void Evaluate(DesertBatflyFlockSnapshot flock) { }
    internal void BeginVisibleScan() { }
    internal void ObserveVisible(Creature creature, float distance, bool predator) { }
    internal void EndVisibleScan(DesertBatflyFlockSnapshot flock) { }

    // Historical API retained only so stale tools/tests fail safely instead of
    // manufacturing a watch target. Runtime gameplay must not call it.
    internal static Vector2 WatchGoal(
        Vector2 pos,
        Vector2 threat,
        Vector2 center,
        bool predator,
        bool sentinel) => pos;

    // Critical removal point: role code no longer participates in locomotion.
    internal void BiasOrdinaryFlight(DesertBatflyFlockSnapshot flock) { }
}
