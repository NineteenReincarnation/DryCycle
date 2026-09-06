using System;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugBreakpointStatus
{
    internal readonly bool Enabled;
    internal readonly int Hits;
    internal readonly int LastTick;
    internal readonly DebugEntityKey LastKey;
    internal readonly string LastReason;

    internal AIDebugBreakpointStatus(bool enabled, int hits, int lastTick, DebugEntityKey lastKey, string lastReason)
    {
        Enabled = enabled;
        Hits = hits;
        LastTick = lastTick;
        LastKey = lastKey;
        LastReason = lastReason ?? string.Empty;
    }
}

// Breakpoints observe recorder/anomaly transitions after the real AI update has already
// completed. They never modify AI decisions; a hit only asks the existing debugger pause
// controller to pause subsequent simulation ticks.
internal static class AIDebugBreakpointManager
{
    private static bool enabled;
    private static int hits;
    private static int lastTick = int.MinValue;
    private static DebugEntityKey lastKey;
    private static string lastReason;

    internal static bool Enabled => enabled;

    internal static void Toggle() => enabled = !enabled;

    internal static void SetEnabled(bool value) => enabled = value;

    internal static void OnAnomaly(DebugEntityKey key, int tick, string reason)
    {
        if (!enabled) return;
        if (tick == lastTick && key == lastKey && string.Equals(reason, lastReason, StringComparison.Ordinal)) return;

        hits++;
        lastTick = tick;
        lastKey = key;
        lastReason = reason ?? "anomaly";
        AIDebugSimulationControl.PauseForBreakpoint();
    }

    internal static AIDebugBreakpointStatus GetStatus() =>
        new(enabled, hits, lastTick, lastKey, lastReason);

    internal static void Reset()
    {
        enabled = false;
        hits = 0;
        lastTick = int.MinValue;
        lastKey = default;
        lastReason = null;
    }
}
