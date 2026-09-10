using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_PerceptionSource
{
    None,
    DirectCreature,
    DirectPlayer,
    HeldItem,
    Projectile,
    Signal,
    Predicted
}

internal enum DB_PerceptionModality
{
    None,
    Visual,
    Acoustic,
    Reported,
    Predicted
}

internal readonly struct DB_PerceptionTrack
{
    internal readonly Creature Target;
    internal readonly Vector2 ObservedPosition;
    internal readonly Vector2 EstimatedPosition;
    internal readonly Vector2 ObservedVelocity;
    internal readonly float Confidence;
    internal readonly float Salience;
    internal readonly float ThreatUrgency;
    internal readonly float AttentionScore;
    internal readonly int LastObservedTick;
    internal readonly int AgeTicks;
    internal readonly DB_PerceptionSource Source;
    internal readonly DB_PerceptionModality Modality;
    internal readonly bool DirectObservation;

    internal DB_PerceptionTrack(
        Creature target,
        Vector2 observedPosition,
        Vector2 estimatedPosition,
        Vector2 observedVelocity,
        float confidence,
        float salience,
        float threatUrgency,
        float attentionScore,
        int lastObservedTick,
        int ageTicks,
        DB_PerceptionSource source,
        DB_PerceptionModality modality,
        bool directObservation)
    {
        Target = target;
        ObservedPosition = observedPosition;
        EstimatedPosition = estimatedPosition;
        ObservedVelocity = observedVelocity;
        Confidence = Mathf.Clamp01(confidence);
        Salience = Mathf.Clamp01(salience);
        ThreatUrgency = Mathf.Clamp01(threatUrgency);
        AttentionScore = Mathf.Max(0f, attentionScore);
        LastObservedTick = lastObservedTick;
        AgeTicks = Mathf.Max(0, ageTicks);
        Source = source;
        Modality = modality;
        DirectObservation = directObservation;
    }

    internal bool Valid => Target != null && Confidence > 0.01f;

    internal DB_PerceptionTrack AsPredicted(int clock, float confidence, Vector2 estimatedPosition)
    {
        int age = LastObservedTick == int.MinValue ? int.MaxValue : Mathf.Max(0, clock - LastObservedTick);
        return new DB_PerceptionTrack(
            Target,
            ObservedPosition,
            estimatedPosition,
            ObservedVelocity,
            confidence,
            Salience,
            ThreatUrgency * confidence / Mathf.Max(0.01f, Confidence),
            AttentionScore * confidence / Mathf.Max(0.01f, Confidence),
            LastObservedTick,
            age,
            DB_PerceptionSource.Predicted,
            DB_PerceptionModality.Predicted,
            false);
    }
}

internal readonly struct DB_ProjectilePercept
{
    internal readonly DB_WeaponObservation Observation;
    internal readonly float TimeToClosestApproach;
    internal readonly float ClosestDistance;
    internal readonly float Risk;
    internal readonly float Confidence;

    internal DB_ProjectilePercept(
        in DB_WeaponObservation observation,
        float timeToClosestApproach,
        float closestDistance,
        float risk,
        float confidence)
    {
        Observation = observation;
        TimeToClosestApproach = Mathf.Max(0f, timeToClosestApproach);
        ClosestDistance = Mathf.Max(0f, closestDistance);
        Risk = Mathf.Clamp01(risk);
        Confidence = Mathf.Clamp01(confidence);
    }

    internal bool Valid => Observation.Weapon != null && Risk > 0f;
}

/// <summary>
/// Signal-derived beliefs live in Perception R2. Signals remain responsible for emission,
/// packet lifetime and relay transport; this value only describes what this receiver believes.
/// </summary>
internal readonly struct DB_PerceptionSignalContext
{
    internal readonly float AlarmPressure;
    internal readonly Vector2 AlarmOrigin;
    internal readonly Creature AlarmThreat;
    internal readonly float DistressInterest;
    internal readonly DB_Creature DistressSource;
    internal readonly float RallyInterest;
    internal readonly DB_Creature RallySource;
    internal readonly Creature RallyTarget;
    internal readonly float RoostInterest;
    internal readonly DB_Creature RoostSource;
    internal readonly float HarassInterest;
    internal readonly DB_Creature HarassSource;
    internal readonly Player HarassTarget;
    internal readonly float SafeConfidence;
    internal readonly DB_PerceptionModality LastModality;
    internal readonly int LastHop;
    internal readonly string LastReason;

    internal DB_PerceptionSignalContext(
        float alarmPressure,
        Vector2 alarmOrigin,
        Creature alarmThreat,
        float distressInterest,
        DB_Creature distressSource,
        float rallyInterest,
        DB_Creature rallySource,
        Creature rallyTarget,
        float roostInterest,
        DB_Creature roostSource,
        float harassInterest,
        DB_Creature harassSource,
        Player harassTarget,
        float safeConfidence,
        DB_PerceptionModality lastModality,
        int lastHop,
        string lastReason)
    {
        AlarmPressure = Mathf.Clamp01(alarmPressure);
        AlarmOrigin = alarmOrigin;
        AlarmThreat = alarmThreat;
        DistressInterest = Mathf.Clamp01(distressInterest);
        DistressSource = distressSource;
        RallyInterest = Mathf.Clamp01(rallyInterest);
        RallySource = rallySource;
        RallyTarget = rallyTarget;
        RoostInterest = Mathf.Clamp01(roostInterest);
        RoostSource = roostSource;
        HarassInterest = Mathf.Clamp01(harassInterest);
        HarassSource = harassSource;
        HarassTarget = harassTarget;
        SafeConfidence = Mathf.Clamp01(safeConfidence);
        LastModality = lastModality;
        LastHop = Mathf.Max(0, lastHop);
        LastReason = lastReason ?? string.Empty;
    }
}

internal readonly struct DB_PerceptionSnapshot
{
    internal readonly int Clock;
    internal readonly DB_PerceptionTrack PrimaryThreat;
    internal readonly DB_PerceptionTrack SecondaryThreat;
    internal readonly DB_PerceptionTrack LostThreat;
    internal readonly DB_ProjectilePercept IncomingProjectile;
    internal readonly DB_PerceptionSignalContext Signals;

    internal DB_PerceptionSnapshot(
        int clock,
        in DB_PerceptionTrack primaryThreat,
        in DB_PerceptionTrack secondaryThreat,
        in DB_PerceptionTrack lostThreat,
        in DB_ProjectilePercept incomingProjectile,
        in DB_PerceptionSignalContext signals)
    {
        Clock = clock;
        PrimaryThreat = primaryThreat;
        SecondaryThreat = secondaryThreat;
        LostThreat = lostThreat;
        IncomingProjectile = incomingProjectile;
        Signals = signals;
    }

    internal bool HasPrimaryThreat => PrimaryThreat.Valid;
    internal bool HasIncomingProjectile => IncomingProjectile.Valid;
}

internal readonly struct DB_PerceptionDebugState
{
    internal readonly DB_PerceptionSnapshot Snapshot;
    internal readonly int CreatureScanCount;
    internal readonly int ProjectileScanCount;
    internal readonly int SignalScanCount;
    internal readonly string LastAttentionReason;

    internal DB_PerceptionDebugState(
        in DB_PerceptionSnapshot snapshot,
        int creatureScanCount,
        int projectileScanCount,
        int signalScanCount,
        string lastAttentionReason)
    {
        Snapshot = snapshot;
        CreatureScanCount = Mathf.Max(0, creatureScanCount);
        ProjectileScanCount = Mathf.Max(0, projectileScanCount);
        SignalScanCount = Mathf.Max(0, signalScanCount);
        LastAttentionReason = lastAttentionReason ?? string.Empty;
    }
}
