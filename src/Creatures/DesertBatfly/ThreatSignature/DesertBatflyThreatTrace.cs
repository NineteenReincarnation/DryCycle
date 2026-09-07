using DryCycle.Debugging.AI;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflyThreatTrace
{
    internal static void Sample(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null ||
            !AIDebugTrace.IsWatched(bat.abstractCreature) ||
            !DesertBatflyThreatRuntime.TryGetDebugState(
                bat,
                out DesertBatflyThreatDebugState threat))
            return;

        AbstractCreature creature = bat.abstractCreature;
        float confidence = Mathf.Round(threat.Confidence * 20f) / 20f;
        float evidence = Mathf.Round(threat.LastEvidenceStrength * 20f) / 20f;

        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Perception,
            "ThreatTargetPlayerSlot",
            threat.PlayerSlot,
            "per-player Task11 memory selection");
        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.State,
            "ThreatDominantSignature",
            threat.DominantSignature ?? "None",
            "display summary only; AI uses all twelve continuous dimensions");
        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.State,
            "ThreatConfidence",
            confidence,
            "quantized 0.05 memory-confidence bucket");

        string evidenceKey = threat.LastEvidencePlayerSlot < 0
            ? "—"
            : $"P{threat.LastEvidencePlayerSlot}:{threat.LastEvidenceType}:{evidence:0.00}";
        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Perception,
            "ThreatEvidenceAdded",
            evidenceKey,
            string.IsNullOrEmpty(threat.LastWitnessReason)
                ? "no Task11 evidence yet"
                : threat.LastWitnessReason);

        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Perception,
            "ThreatCueChanged",
            CueText(threat.Cue),
            "current observable cue only; never persisted as Threat Signature Memory");

        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Warning,
            "ThreatAcuteEvent",
            AcuteText(threat),
            threat.HazardCenter.HasValue
                ? $"hazard={threat.HazardCenter.Value.x:0.0},{threat.HazardCenter.Value.y:0.0}"
                : "no active local hazard center");

        string adjustment = string.IsNullOrEmpty(threat.AttackGeometryAdjustment)
            ? "—"
            : threat.AttackGeometryAdjustment;
        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Combat,
            "ThreatAttackAdjusted",
            adjustment,
            string.IsNullOrEmpty(threat.ModifierReason)
                ? "no active Task11 tactical modifier"
                : threat.ModifierReason);

        string evade = threat.EvadeTarget.HasValue
            ? $"{threat.EvadeTarget.Value.x:0.0},{threat.EvadeTarget.Value.y:0.0}"
            : "—";
        AIDebugTrace.RecordChange(
            creature,
            AIDebugEventCategory.Combat,
            "ThreatEvadeTarget",
            evade,
            string.IsNullOrEmpty(threat.ModifierReason)
                ? "no Task11 evade"
                : threat.ModifierReason);
    }

    private static string CueText(in DesertBatflyThreatCue cue)
    {
        return $"P{cue.PlayerSlot}" +
               (cue.VisibleSpear ? "+Spear" : "") +
               (cue.VisibleRock ? "+Rock" : "") +
               (cue.VisibleExplosive ? "+Explosive" : "") +
               (cue.VisibleStartle ? "+Startle" : "") +
               (cue.VisibleShock ? "+Shock" : "") +
               (cue.RecentSpearThrow ? "+RecentSpear" : "") +
               (cue.RecentRockThrow ? "+RecentRock" : "") +
               (cue.RecentExplosion ? "+RecentExplosion" : "") +
               (cue.RecentGrabAttempt ? "+RecentGrab" : "") +
               (cue.ProjectileThreat ? "+ProjectileThreat" : "") +
               (cue.PlayerRetreating ? "+Retreating" : "");
    }

    private static string AcuteText(in DesertBatflyThreatDebugState threat)
    {
        return $"E{threat.AcuteExplosionTimer}/" +
               $"S{threat.AcuteStartleTimer}/" +
               $"M{threat.AcuteMassCasualtyTimer}/" +
               $"C{threat.AcuteCaptureTimer}/" +
               $"K{threat.AcuteShockTimer}";
    }
}
