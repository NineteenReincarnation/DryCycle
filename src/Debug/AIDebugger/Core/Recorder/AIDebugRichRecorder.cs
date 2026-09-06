using System;
using System.Collections.Generic;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugResolvedSnapshot
{
    internal readonly bool HasValue;
    internal readonly int Tick;
    internal readonly int AgeTicks;
    internal readonly AIDebugSnapshot Snapshot;
    internal readonly AIDebugUtilityRow[] Utilities;
    internal readonly int UtilityCount;
    internal readonly bool UtilityTruncated;
    internal readonly AIDebugPerceptionRow[] Perception;
    internal readonly int PerceptionCount;
    internal readonly bool PerceptionTruncated;
    internal readonly AIDebugPathState Path;

    internal AIDebugResolvedSnapshot(
        bool hasValue,
        int tick,
        int ageTicks,
        AIDebugSnapshot snapshot,
        AIDebugUtilityRow[] utilities,
        int utilityCount,
        bool utilityTruncated,
        AIDebugPerceptionRow[] perception,
        int perceptionCount,
        bool perceptionTruncated,
        AIDebugPathState path)
    {
        HasValue = hasValue;
        Tick = tick;
        AgeTicks = ageTicks;
        Snapshot = snapshot;
        Utilities = utilities;
        UtilityCount = utilityCount;
        UtilityTruncated = utilityTruncated;
        Perception = perception;
        PerceptionCount = perceptionCount;
        PerceptionTruncated = perceptionTruncated;
        Path = path;
    }
}

// Transitional V5 rich recorder. The old AIDebugSnapshot object model is still used as a
// compatibility payload so the current Inspector loses no fields while Presentation is
// migrated to schema/raw providers. Advanced Utility/Perception/Path data is captured in
// the same simulation-thread cadence and retained inside fixed Entry buffers; no ToArray()
// or per-snapshot row-array allocation occurs here.
internal static class AIDebugRichRecorder
{
    private const int SnapshotIntervalTicks = 8; // 5 Hz at Rain World's 40 Hz simulation.
    private const int SnapshotCapacity = 64;
    private const int UtilityCapacity = 32;
    private const int PerceptionCapacity = 96;

    private static readonly Entry[] Entries = new Entry[SnapshotCapacity];
    private static readonly List<AIDebugUtilityRow> UtilityScratch = new(UtilityCapacity);
    private static readonly List<AIDebugPerceptionRow> PerceptionScratch = new(PerceptionCapacity);

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
        internal readonly AIDebugUtilityRow[] Utilities = new AIDebugUtilityRow[UtilityCapacity];
        internal int UtilityCount;
        internal bool UtilityTruncated;
        internal readonly AIDebugPerceptionRow[] Perception = new AIDebugPerceptionRow[PerceptionCapacity];
        internal int PerceptionCount;
        internal bool PerceptionTruncated;
        internal AIDebugPathState Path;

        internal void Reset()
        {
            Tick = 0;
            Snapshot = null;
            if (UtilityCount > 0) Array.Clear(Utilities, 0, UtilityCount);
            UtilityCount = 0;
            UtilityTruncated = false;
            if (PerceptionCount > 0) Array.Clear(Perception, 0, PerceptionCount);
            PerceptionCount = 0;
            PerceptionTruncated = false;
            Path = default;
        }
    }

    internal static void Select(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return;
        EnsureGame(game);

        selectedKey = key;
        selectedHandle = AIDebugRegistry.Resolve(game, key);
        hasSelection = selectedHandle != null;
        nextCaptureTick = game.clock + SnapshotIntervalTicks;
        lastFastStateTick = int.MinValue;
        ClearEntries();

        // Selection is already a main-thread user action, so capture one compatibility
        // snapshot immediately. The Inspector never falls back to a second live capture.
        if (selectedHandle != null)
            CaptureAndAppend(game.clock, selectedHandle, game);
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
        UtilityScratch.Clear();
        PerceptionScratch.Clear();
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

        CaptureAndAppend(tick, creature, game);
    }

    internal static bool TryResolve(int cursorTick, out AIDebugResolvedSnapshot resolved)
    {
        if (count <= 0)
        {
            resolved = default;
            return false;
        }

        int bestTick = int.MinValue;
        Entry best = null;
        for (int i = 0; i < count; i++)
        {
            int index = writeIndex - 1 - i;
            if (index < 0) index += SnapshotCapacity;
            Entry entry = Entries[index];
            if (entry == null || entry.Snapshot == null || entry.Tick > cursorTick) continue;
            if (entry.Tick < bestTick) continue;
            bestTick = entry.Tick;
            best = entry;
            if (bestTick == cursorTick) break;
        }

        if (best == null)
        {
            resolved = default;
            return false;
        }

        resolved = ResolveEntry(best, cursorTick);
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
        resolved = ResolveEntry(entry, cursorTick);
        return true;
    }

    private static AIDebugResolvedSnapshot ResolveEntry(Entry entry, int cursorTick)
    {
        // References below never cross to RWImGUI directly. AIDebuggerHost immediately
        // copies only [0..Count) into detached Presentation arrays on this same main thread.
        return new AIDebugResolvedSnapshot(
            true,
            entry.Tick,
            Math.Max(0, cursorTick - entry.Tick),
            entry.Snapshot,
            entry.Utilities,
            entry.UtilityCount,
            entry.UtilityTruncated,
            entry.Perception,
            entry.PerceptionCount,
            entry.PerceptionTruncated,
            entry.Path);
    }

    private static void CaptureAndAppend(int tick, AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = AIDebugRegistry.Capture(creature, game);
        if (snapshot == null) return;

        AIDebugAdvancedCapture.CaptureUtilities(creature, UtilityScratch);
        // Keep tracker order while recording. Presentation is free to order a detached copy.
        AIDebugAdvancedCapture.CapturePerception(creature, PerceptionScratch, sortByPriority: false);
        AIDebugPathState path = AIDebugAdvancedCapture.CapturePath(creature);
        Append(tick, snapshot, UtilityScratch, PerceptionScratch, path);
    }

    private static void Append(
        int tick,
        AIDebugSnapshot snapshot,
        List<AIDebugUtilityRow> utilities,
        List<AIDebugPerceptionRow> perception,
        AIDebugPathState path)
    {
        Entry entry = Entries[writeIndex];
        if (entry == null)
        {
            entry = new Entry();
            Entries[writeIndex] = entry;
        }
        else
        {
            entry.Reset();
        }

        entry.Tick = tick;
        entry.Snapshot = snapshot;
        entry.Path = path;

        int utilityCount = Math.Min(utilities.Count, UtilityCapacity);
        for (int i = 0; i < utilityCount; i++) entry.Utilities[i] = utilities[i];
        entry.UtilityCount = utilityCount;
        entry.UtilityTruncated = utilities.Count > UtilityCapacity;

        int perceptionCount = Math.Min(perception.Count, PerceptionCapacity);
        for (int i = 0; i < perceptionCount; i++) entry.Perception[i] = perception[i];
        entry.PerceptionCount = perceptionCount;
        entry.PerceptionTruncated = perception.Count > PerceptionCapacity;

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
            Entries[i]?.Reset();
        writeIndex = 0;
        count = 0;
    }
}
