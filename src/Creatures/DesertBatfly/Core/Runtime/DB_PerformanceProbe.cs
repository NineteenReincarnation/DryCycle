using System.Runtime.CompilerServices;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_PerformanceSnapshot
{
    internal readonly int ThreatCueThisTick;
    internal readonly int ThreatCuePeakAfterWarmup;
    internal readonly int ThreatCueTotal;
    internal readonly int ThreatCueAgeTicks;
    internal readonly int TraumaScanThisTick;
    internal readonly int TraumaScanPeakAfterWarmup;
    internal readonly int TraumaScanTotal;
    internal readonly int TraumaScanAgeTicks;

    internal DB_PerformanceSnapshot(
        int threatCueThisTick,
        int threatCuePeakAfterWarmup,
        int threatCueTotal,
        int threatCueAgeTicks,
        int traumaScanThisTick,
        int traumaScanPeakAfterWarmup,
        int traumaScanTotal,
        int traumaScanAgeTicks)
    {
        ThreatCueThisTick = threatCueThisTick;
        ThreatCuePeakAfterWarmup = threatCuePeakAfterWarmup;
        ThreatCueTotal = threatCueTotal;
        ThreatCueAgeTicks = threatCueAgeTicks;
        TraumaScanThisTick = traumaScanThisTick;
        TraumaScanPeakAfterWarmup = traumaScanPeakAfterWarmup;
        TraumaScanTotal = traumaScanTotal;
        TraumaScanAgeTicks = traumaScanAgeTicks;
    }
}

/// <summary>
/// Low-cost, allocation-free-after-first-use counters for the expensive per-bat work that R7
/// deliberately staggers. Observatory reads are strictly peek-only and never create room state.
/// </summary>
internal static class DB_PerformanceProbe
{
    internal const int WarmupTicks = 40;

    private sealed class RoomState
    {
        internal int CurrentClock = int.MinValue;
        internal int ThreatFirstClock = int.MinValue;
        internal int TraumaFirstClock = int.MinValue;
        internal int ThreatCueThisTick;
        internal int TraumaScanThisTick;
        internal int ThreatCuePeakAfterWarmup;
        internal int TraumaScanPeakAfterWarmup;
        internal int ThreatCueTotal;
        internal int TraumaScanTotal;
    }

    private static ConditionalWeakTable<Room, RoomState> rooms = new();

    internal static void Reset()
    {
        rooms = new ConditionalWeakTable<Room, RoomState>();
    }

    internal static void RecordThreatCue(Room room)
    {
        int clock = Clock(room);
        if (room == null || clock == int.MinValue) return;
        RoomState state = rooms.GetValue(room, _ => new RoomState());
        BeginTick(state, clock);
        if (state.ThreatFirstClock == int.MinValue) state.ThreatFirstClock = clock;
        state.ThreatCueThisTick++;
        state.ThreatCueTotal++;
        if (clock - state.ThreatFirstClock >= WarmupTicks &&
            state.ThreatCueThisTick > state.ThreatCuePeakAfterWarmup)
            state.ThreatCuePeakAfterWarmup = state.ThreatCueThisTick;
    }

    internal static void RecordTraumaThreatScan(Room room)
    {
        int clock = Clock(room);
        if (room == null || clock == int.MinValue) return;
        RoomState state = rooms.GetValue(room, _ => new RoomState());
        BeginTick(state, clock);
        if (state.TraumaFirstClock == int.MinValue) state.TraumaFirstClock = clock;
        state.TraumaScanThisTick++;
        state.TraumaScanTotal++;
        if (clock - state.TraumaFirstClock >= WarmupTicks &&
            state.TraumaScanThisTick > state.TraumaScanPeakAfterWarmup)
            state.TraumaScanPeakAfterWarmup = state.TraumaScanThisTick;
    }

    internal static bool TryPeek(Room room, out DB_PerformanceSnapshot snapshot)
    {
        if (room == null || !rooms.TryGetValue(room, out RoomState state))
        {
            snapshot = new DB_PerformanceSnapshot(0, 0, 0, -1, 0, 0, 0, -1);
            return false;
        }

        int clock = Clock(room);
        bool currentTick = clock != int.MinValue && state.CurrentClock == clock;
        snapshot = new DB_PerformanceSnapshot(
            currentTick ? state.ThreatCueThisTick : 0,
            state.ThreatCuePeakAfterWarmup,
            state.ThreatCueTotal,
            Age(clock, state.ThreatFirstClock),
            currentTick ? state.TraumaScanThisTick : 0,
            state.TraumaScanPeakAfterWarmup,
            state.TraumaScanTotal,
            Age(clock, state.TraumaFirstClock));
        return true;
    }

    private static void BeginTick(RoomState state, int clock)
    {
        if (state.CurrentClock == clock) return;
        state.CurrentClock = clock;
        state.ThreatCueThisTick = 0;
        state.TraumaScanThisTick = 0;
    }

    private static int Clock(Room room) => room?.game?.clock ?? int.MinValue;

    private static int Age(int clock, int firstClock)
    {
        if (clock == int.MinValue || firstClock == int.MinValue || clock < firstClock) return -1;
        return clock - firstClock;
    }
}
