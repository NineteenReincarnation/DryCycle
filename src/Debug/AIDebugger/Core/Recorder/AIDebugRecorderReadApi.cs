using System;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugResolvedMotion
{
    internal readonly bool HasValue;
    internal readonly int Tick;
    internal readonly int AgeTicks;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float VX;
    internal readonly float VY;

    internal AIDebugResolvedMotion(bool hasValue, int tick, int ageTicks, float x, float y, float vx, float vy)
    {
        HasValue = hasValue;
        Tick = tick;
        AgeTicks = ageTicks;
        X = x;
        Y = y;
        VX = vx;
        VY = vy;
    }
}

internal readonly struct AIDebugResolvedFastState
{
    internal readonly bool HasValue;
    internal readonly int Tick;
    internal readonly int AgeTicks;
    internal readonly uint Sequence;
    internal readonly AIDebugFastState State;

    internal AIDebugResolvedFastState(bool hasValue, int tick, int ageTicks, uint sequence, AIDebugFastState state)
    {
        HasValue = hasValue;
        Tick = tick;
        AgeTicks = ageTicks;
        Sequence = sequence;
        State = state;
    }
}

internal readonly struct AIDebugRecorderEntityStatus
{
    internal readonly bool Found;
    internal readonly DebugEntityKey Key;
    internal readonly AIDebugTrackedRole Role;
    internal readonly int LastTouchedTick;
    internal readonly long MotionSamples;
    internal readonly long StateChanges;

    internal AIDebugRecorderEntityStatus(
        bool found,
        DebugEntityKey key,
        AIDebugTrackedRole role,
        int lastTouchedTick,
        long motionSamples,
        long stateChanges)
    {
        Found = found;
        Key = key;
        Role = role;
        LastTouchedTick = lastTouchedTick;
        MotionSamples = motionSamples;
        StateChanges = stateChanges;
    }
}

// Detached read-only query surface for the V5 recorder. Queries never touch Rain World
// objects and never allocate. Presentation/historical code should migrate to this API
// rather than re-capturing live AI state.
internal static class AIDebugRecorderReadApi
{
    internal static bool TryGetEntityStatus(DebugEntityKey key, out AIDebugRecorderEntityStatus status) =>
        AIDebugRecorder.TryGetEntityStatus(key, out status);

    internal static bool TryResolveMotion(DebugEntityKey key, int cursorTick, out AIDebugResolvedMotion resolved) =>
        AIDebugRecorder.TryResolveMotion(key, cursorTick, out resolved);

    internal static bool TryResolveFastState(DebugEntityKey key, int cursorTick, out AIDebugResolvedFastState resolved) =>
        AIDebugRecorder.TryResolveFastState(key, cursorTick, out resolved);
}
