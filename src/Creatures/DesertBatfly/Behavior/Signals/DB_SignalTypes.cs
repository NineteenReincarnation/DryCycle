using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_SignalKind
{
    AlarmFlutter,
    DistressCall,
    RallySignal,
    RoostCall,
    HarassSignal,
    SafeSignal
}

internal enum DB_SignalPerception
{
    None,
    Visual,
    CloseAcoustic
}

internal readonly struct DB_SignalInfluence
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
    internal readonly string LastReason;

    internal DB_SignalInfluence(
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
        LastReason = lastReason ?? string.Empty;
    }
}

internal readonly struct DB_SignalDisplayState
{
    internal readonly DB_SignalKind Kind;
    internal readonly float Intensity;
    internal readonly int TicksRemaining;
    internal readonly Vector2 Direction;

    internal DB_SignalDisplayState(
        DB_SignalKind kind,
        float intensity,
        int ticksRemaining,
        Vector2 direction)
    {
        Kind = kind;
        Intensity = Mathf.Clamp01(intensity);
        TicksRemaining = Mathf.Max(0, ticksRemaining);
        Direction = direction;
    }
}

internal readonly struct DB_SignalDebugState
{
    internal readonly DB_SignalInfluence Influence;
    internal readonly int LastGeneration;
    internal readonly DB_SignalKind LastKind;
    internal readonly DB_SignalPerception LastPerception;
    internal readonly int LastHop;
    internal readonly string LastDecision;
    internal readonly int ActiveRoomSignals;

    internal DB_SignalDebugState(
        DB_SignalInfluence influence,
        int lastGeneration,
        DB_SignalKind lastKind,
        DB_SignalPerception lastPerception,
        int lastHop,
        string lastDecision,
        int activeRoomSignals)
    {
        Influence = influence;
        LastGeneration = lastGeneration;
        LastKind = lastKind;
        LastPerception = lastPerception;
        LastHop = lastHop;
        LastDecision = lastDecision ?? string.Empty;
        ActiveRoomSignals = Mathf.Max(0, activeRoomSignals);
    }
}
