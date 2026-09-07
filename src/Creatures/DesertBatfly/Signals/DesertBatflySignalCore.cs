using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DesertBatflySignalKind
{
    AlarmFlutter,
    DistressCall,
    RallySignal,
    RoostCall,
    HarassSignal,
    SafeSignal
}

internal enum DesertBatflySignalPerception
{
    None,
    Visual,
    CloseAcoustic
}

internal sealed class DesertBatflySignalPacket
{
    internal readonly int Generation;
    internal readonly DesertBatflySignalKind Kind;
    internal readonly DesertBatfly Emitter;
    internal readonly DesertBatfly Subject;
    internal readonly Creature Threat;
    internal readonly Player PlayerTarget;
    internal readonly Vector2 Origin;
    internal readonly Vector2 Direction;
    internal readonly int Hop;
    internal readonly int CreatedTick;
    internal int ExpiresTick;
    internal float Intensity;

    internal DesertBatflySignalPacket(
        int generation,
        DesertBatflySignalKind kind,
        DesertBatfly emitter,
        DesertBatfly subject,
        Creature threat,
        Player playerTarget,
        Vector2 origin,
        Vector2 direction,
        float intensity,
        int hop,
        int createdTick,
        int expiresTick)
    {
        Generation = generation;
        Kind = kind;
        Emitter = emitter;
        Subject = subject;
        Threat = threat;
        PlayerTarget = playerTarget;
        Origin = origin;
        Direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.zero;
        Intensity = Mathf.Clamp01(intensity);
        Hop = Mathf.Clamp(hop, 0, DesertBatflySignalRuntime.MaxAlarmHop);
        CreatedTick = createdTick;
        ExpiresTick = Mathf.Max(createdTick + 1, expiresTick);
    }

    internal bool Expired(int clock) => clock >= ExpiresTick ||
        Emitter == null || Emitter.dead || Emitter.slatedForDeletetion || Emitter.room == null;
}

internal readonly struct DesertBatflySignalInfluence
{
    internal readonly float AlarmPressure;
    internal readonly Vector2 AlarmOrigin;
    internal readonly Creature AlarmThreat;
    internal readonly float DistressInterest;
    internal readonly DesertBatfly DistressSource;
    internal readonly float RallyInterest;
    internal readonly DesertBatfly RallySource;
    internal readonly Creature RallyTarget;
    internal readonly float RoostInterest;
    internal readonly DesertBatfly RoostSource;
    internal readonly float HarassInterest;
    internal readonly DesertBatfly HarassSource;
    internal readonly Player HarassTarget;
    internal readonly float SafeConfidence;
    internal readonly string LastReason;

    internal DesertBatflySignalInfluence(
        float alarmPressure,
        Vector2 alarmOrigin,
        Creature alarmThreat,
        float distressInterest,
        DesertBatfly distressSource,
        float rallyInterest,
        DesertBatfly rallySource,
        Creature rallyTarget,
        float roostInterest,
        DesertBatfly roostSource,
        float harassInterest,
        DesertBatfly harassSource,
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

internal readonly struct DesertBatflySignalDisplayState
{
    internal readonly DesertBatflySignalKind Kind;
    internal readonly float Intensity;
    internal readonly int TicksRemaining;
    internal readonly Vector2 Direction;

    internal DesertBatflySignalDisplayState(
        DesertBatflySignalKind kind,
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

internal readonly struct DesertBatflySignalDebugState
{
    internal readonly DesertBatflySignalInfluence Influence;
    internal readonly int LastGeneration;
    internal readonly DesertBatflySignalKind LastKind;
    internal readonly DesertBatflySignalPerception LastPerception;
    internal readonly int LastHop;
    internal readonly string LastDecision;
    internal readonly int ActiveRoomSignals;

    internal DesertBatflySignalDebugState(
        DesertBatflySignalInfluence influence,
        int lastGeneration,
        DesertBatflySignalKind lastKind,
        DesertBatflySignalPerception lastPerception,
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
