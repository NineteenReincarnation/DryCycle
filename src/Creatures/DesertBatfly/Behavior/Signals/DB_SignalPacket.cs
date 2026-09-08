using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DB_SignalPacket
{
    internal readonly int Generation;
    internal readonly DB_SignalKind Kind;
    internal readonly DB_Creature Emitter;
    internal readonly DB_Creature Subject;
    internal readonly Creature Threat;
    internal readonly Player PlayerTarget;
    internal readonly Vector2 Origin;
    internal readonly Vector2 Direction;
    internal readonly int Hop;
    internal readonly int CreatedTick;
    internal int ExpiresTick;
    internal float Intensity;

    internal DB_SignalPacket(
        int generation,
        DB_SignalKind kind,
        DB_Creature emitter,
        DB_Creature subject,
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
        Hop = Mathf.Clamp(hop, 0, DB_SignalRuntime.MaxAlarmHop);
        CreatedTick = createdTick;
        ExpiresTick = Mathf.Max(createdTick + 1, expiresTick);
    }

    internal bool Expired(int clock) => clock >= ExpiresTick ||
        Emitter == null || Emitter.dead || Emitter.slatedForDeletetion || Emitter.room == null;
}

