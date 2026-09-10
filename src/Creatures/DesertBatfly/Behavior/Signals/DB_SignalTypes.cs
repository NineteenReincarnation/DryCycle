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
