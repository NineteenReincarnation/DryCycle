using System;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugResolvedSnapshot
{
    internal readonly bool HasValue;
    internal readonly int Tick;
    internal readonly int AgeTicks;
    internal readonly AIDebugSnapshot Snapshot;

    internal AIDebugResolvedSnapshot(bool hasValue, int tick, int ageTicks, AIDebugSnapshot snapshot)
    {
        HasValue = hasValue;
        Tick = tick;
        AgeTicks = ageTicks;
        Snapshot = snapshot;
    }
}

// Transitional V5 rich recorder. The old AIDebugSnapshot object model is still used as a
// compatibility payload so the current Inspector loses no fields while Presentation is
// migrated away from live re-capture. Capture happens only on Rain World's simulation
// thread, at a bounded low frequency or on an observed fast-state transition.
//
// This class is intentionally isolated: the final V5 raw/schema provider can replace the
// payload without changing the simulation-tick ownership or the Presentation read path.
internal static class AIDebugRichRecorder
{
    private const int SnapshotIntervalTicks = 8; // 5 Hz at Rain World's 40 Hz simulation.
    private const int SnapshotCapacity = 64;

    private static readonly Entry[] Entries = new Entry[SnapshotCapacity];
    private static RainWorldGame boundGame;
    private static DebugEntityKey selectedKey;
    private static AbstractCreature selectedHandle;
    private static bool hasSelection;
    private static int nextCaptureTick;
    private static int writeIndex;
    private static int count;
    private static int lastFastStateTick = int.MinValue;

    private sealed class Entry
    {
        internal int Tick;
        internal AIDebugSnapshot Snapshot;
    }

    internal static void Select(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return;
        EnsureGame(game);

        selectedKey = key;
        selectedHandle = AIDebugRegistry.Resolve(game, key);
        hasSelection = selectedHandle != null;
        nextCaptureTick = game.clock;
        lastFastStateTick = int.MinValue;
        ClearEntries();
    }

    internal static void ClearSelection()
    {
        hasSelection = false;
        selectedKey = default;
        selectedHandle = null;
        nextCaptureTick = 0;
        lastFastStateTick = int.MinValue;
        ClearEntries();
    }

    internal static void Reset()
    {
        ClearSelection();
        boundGame = null;
    }

    internal static void OnSimulationTick(RainWorldGame game)
    {
        if (game == null || !hasSelection) return;
        EnsureGame(game);
        if (!hasSelection) return;

        int tick = game.clock;
        bool fastChangedThisTick = false;
        if (AIDebugRecorderReadApi.TryResolveFastState(selectedKey, tick, out AIDebugResolvedFastState fast) &&
            fast.HasValue && fast.Tick == tick && fast.Tick != lastFastStateTick)
        {
            lastFastStateTick = fast.Tick;
            fastChangedThisTick = true;
        }

        if (!fastChangedThisTick && tick < nextCaptureTick) return;
        nextCaptureTick = tick + SnapshotIntervalTicks;

        AbstractCreature creature = selectedHandle;
        if (creature == null || creature.slatedForDeletion || DebugEntityKey.From(creature) != selectedKey)
        {
            creature = AIDebugRegistry.Resolve(game, selectedKey);
            selectedHandle = creature;
        }

        if (creature == null)
        {
            hasSelection = false;
            return;
        }

        AIDebugSnapshot snapshot = AIDebugRegistry.Capture(creature, game);
        if (snapshot == null) return;
        Append(tick, snapshot);
    }

    internal static bool TryResolve(int cursorTick, out AIDebugResolvedSnapshot resolved)
    {
        if (count <= 0)
        {
            resolved = default;
            return false;
        }

        int bestTick = int.MinValue;
        AIDebugSnapshot best = null;
        for (int i = 0; i < count; i++)
        {
            int index = writeIndex - 1 - i;
            if (index < 0) index += SnapshotCapacity;
            Entry entry = Entries[index];
            if (entry == null || entry.Snapshot == null || entry.Tick > cursorTick) continue;
            if (entry.Tick < bestTick) continue;
            bestTick = entry.Tick;
            best = entry.Snapshot;
            if (bestTick == cursorTick) break;
        }

        if (best == null)
        {
            resolved = default;
            return false;
        }

        resolved = new AIDebugResolvedSnapshot(true, bestTick, Math.Max(0, cursorTick - bestTick), best);
        return true;
    }

    internal static bool TryGetLatest(out AIDebugResolvedSnapshot resolved)
    {
        if (count <= 0)
        {
            resolved = default;
            return false;
        }

        int index = writeIndex - 1;
        if (index < 0) index += SnapshotCapacity;
        Entry entry = Entries[index];
        if (entry == null || entry.Snapshot == null)
        {
            resolved = default;
            return false;
        }

        int cursorTick = boundGame?.clock ?? entry.Tick;
        resolved = new AIDebugResolvedSnapshot(true, entry.Tick, Math.Max(0, cursorTick - entry.Tick), entry.Snapshot);
        return true;
    }

    private static void Append(int tick, AIDebugSnapshot snapshot)
    {
        Entry entry = Entries[writeIndex];
        if (entry == null)
        {
            entry = new Entry();
            Entries[writeIndex] = entry;
        }

        entry.Tick = tick;
        entry.Snapshot = snapshot;
        writeIndex++;
        if (writeIndex >= SnapshotCapacity) writeIndex = 0;
        if (count < SnapshotCapacity) count++;
    }

    private static void EnsureGame(RainWorldGame game)
    {
        if (ReferenceEquals(boundGame, game)) return;
        boundGame = game;
        hasSelection = false;
        selectedKey = default;
        selectedHandle = null;
        nextCaptureTick = 0;
        lastFastStateTick = int.MinValue;
        ClearEntries();
    }

    private static void ClearEntries()
    {
        for (int i = 0; i < Entries.Length; i++)
        {
            if (Entries[i] == null) continue;
            Entries[i].Tick = 0;
            Entries[i].Snapshot = null;
        }
        writeIndex = 0;
        count = 0;
    }
}
