using System;
using UnityEngine;

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

// V5 lower-frequency recorder. Heavy inspector state is schema/raw and change-deduplicated;
// Utility/Perception and Path have independent rates so the 40 Hz Fast/Motion path never
// pays for them. Selected and Pinned entities retain independent histories.
internal static class AIDebugRichRecorder
{
    private const int MaxSlots = 12;
    private const int HeavyIntervalTicks = 20;      // 2 Hz + important Fast transitions
    private const int TableIntervalTicks = 8;       // 5 Hz Utility / Perception
    private const int PathIntervalTicks = 4;        // 10 Hz Path summary
    private const int ResolveRetryTicks = 40;
    private const int HeavyCapacity = 64;
    private const int TableCapacity = 64;
    private const int PathCapacity = 128;
    private const int UtilityCapacity = 32;
    private const int PerceptionCapacity = 96;
    private const int RawFieldCapacity = 96;
    private const int DecisionCapacity = 40;

    private static readonly RichSlot[] Slots = new RichSlot[MaxSlots];
    private static readonly AIDebugRawSnapshotBuffer CaptureFields = new(RawFieldCapacity);
    private static readonly AIDebugRawDecisionBuffer CaptureDecisions = new(DecisionCapacity);
    private static readonly AIDebugUtilityRow[] EmptyUtilities = Array.Empty<AIDebugUtilityRow>();
    private static readonly AIDebugPerceptionRow[] EmptyPerception = Array.Empty<AIDebugPerceptionRow>();
    private static RainWorldGame boundGame;
    private static int selectedSlot = -1;

    internal static void Select(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return;
        EnsureGame(game);
        int index = EnsureSlot(game, key);
        if (index < 0) return;
        selectedSlot = index;
        RichSlot slot = Slots[index];
        slot.LastTouchedTick = game.clock;
        slot.RefreshHandle(game, force: true);
        slot.CaptureAll(game, game.clock, immediate: true);
    }

    internal static void ClearSelection()
    {
        selectedSlot = -1;
    }

    internal static void Reset()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i]?.Clear();
            Slots[i] = null;
        }
        selectedSlot = -1;
        boundGame = null;
        AIDebugRawStringTable.Reset();
    }

    internal static void OnSimulationTick(RainWorldGame game)
    {
        if (game == null) return;
        EnsureGame(game);
        int tick = game.clock;

        for (int i = 0; i < Slots.Length; i++)
        {
            RichSlot slot = Slots[i];
            if (slot == null || !slot.Bound) continue;

            if (!AIDebugRecorderReadApi.TryGetEntityStatus(slot.Key, out AIDebugRecorderEntityStatus status))
                continue;
            if (status.Role != AIDebugTrackedRole.Selected && status.Role != AIDebugTrackedRole.Pinned)
                continue;

            slot.LastTouchedTick = tick;
            slot.CaptureAll(game, tick, immediate: false);
        }
    }

    internal static bool TryResolve(int cursorTick, out AIDebugResolvedSnapshot resolved)
    {
        if (selectedSlot < 0 || selectedSlot >= Slots.Length || Slots[selectedSlot] == null)
        {
            resolved = default;
            return false;
        }
        return Slots[selectedSlot].TryResolve(cursorTick, out resolved);
    }

    internal static bool TryGetLatest(out AIDebugResolvedSnapshot resolved)
    {
        if (selectedSlot < 0 || selectedSlot >= Slots.Length || Slots[selectedSlot] == null)
        {
            resolved = default;
            return false;
        }
        int cursor = boundGame?.clock ?? Slots[selectedSlot].LastTouchedTick;
        return Slots[selectedSlot].TryResolve(cursor, out resolved);
    }

    private static void EnsureGame(RainWorldGame game)
    {
        if (ReferenceEquals(boundGame, game)) return;
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i]?.Clear();
            Slots[i] = null;
        }
        selectedSlot = -1;
        boundGame = game;
        AIDebugRawStringTable.Reset();
    }

    private static int EnsureSlot(RainWorldGame game, DebugEntityKey key)
    {
        int existing = FindSlot(key);
        if (existing >= 0) return existing;

        int reusable = -1;
        int oldest = int.MaxValue;
        for (int i = 0; i < Slots.Length; i++)
        {
            RichSlot slot = Slots[i];
            if (slot == null || !slot.Bound)
            {
                reusable = i;
                break;
            }

            bool capturing = AIDebugRecorderReadApi.TryGetEntityStatus(slot.Key, out AIDebugRecorderEntityStatus status) &&
                             (status.Role == AIDebugTrackedRole.Selected || status.Role == AIDebugTrackedRole.Pinned);
            if (capturing || slot.LastTouchedTick >= oldest) continue;
            oldest = slot.LastTouchedTick;
            reusable = i;
        }
        if (reusable < 0) return -1;

        RichSlot target = Slots[reusable] ?? new RichSlot(reusable);
        Slots[reusable] = target;
        target.Bind(game, key);
        return reusable;
    }

    private static int FindSlot(DebugEntityKey key)
    {
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i] != null && Slots[i].Bound && Slots[i].Key == key) return i;
        return -1;
    }

    private sealed class RichSlot
    {
        private readonly int phase;
        private readonly HeavyStore heavy = new();
        private readonly TableStore tables = new();
        private readonly PathStore paths = new();
        private AbstractCreature handle;
        private IAIDebugRecorderRichProvider provider;
        private int nextResolveTick;
        private int nextHeavyTick;
        private int nextTableTick;
        private int nextPathTick;
        private int lastFastChangeTick = int.MinValue;

        internal DebugEntityKey Key { get; private set; }
        internal int LastTouchedTick { get; set; }
        internal bool Bound { get; private set; }

        internal RichSlot(int index) => phase = index & 3;

        internal void Bind(RainWorldGame game, DebugEntityKey key)
        {
            Clear();
            Key = key;
            Bound = true;
            LastTouchedTick = game.clock;
            nextResolveTick = game.clock;
            nextHeavyTick = game.clock + phase;
            nextPathTick = game.clock + phase;
            nextTableTick = game.clock + phase * 2;
            RefreshHandle(game, force: true);
        }

        internal void Clear()
        {
            Key = default;
            Bound = false;
            LastTouchedTick = 0;
            handle = null;
            provider = null;
            nextResolveTick = 0;
            nextHeavyTick = 0;
            nextTableTick = 0;
            nextPathTick = 0;
            lastFastChangeTick = int.MinValue;
            heavy.Clear();
            tables.Clear();
            paths.Clear();
        }

        internal void RefreshHandle(RainWorldGame game, bool force)
        {
            if (!force && handle != null && !handle.slatedForDeletion && DebugEntityKey.From(handle) == Key) return;
            if (!force && game.clock < nextResolveTick) return;
            handle = AIDebugRegistry.Resolve(game, Key);
            provider = AIDebugRecorderRichProviderRegistry.Resolve(handle);
            nextResolveTick = game.clock + ResolveRetryTicks;
        }

        internal void CaptureAll(RainWorldGame game, int tick, bool immediate)
        {
            RefreshHandle(game, force: false);
            AbstractCreature creature = handle;
            if (creature == null) return;

            IAIDebugRecorderRichProvider nextProvider = AIDebugRecorderRichProviderRegistry.Resolve(creature);
            if (!ReferenceEquals(provider, nextProvider))
            {
                provider = nextProvider;
                immediate = true;
            }

            bool fastChanged = false;
            if (AIDebugRecorderReadApi.TryResolveFastState(Key, tick, out AIDebugResolvedFastState fast) &&
                fast.HasValue && fast.Tick == tick && fast.Tick != lastFastChangeTick)
            {
                lastFastChangeTick = fast.Tick;
                fastChanged = true;
            }

            if (immediate || fastChanged || tick >= nextHeavyTick)
            {
                CaptureFields.Begin(provider.Schema.Fields.Length);
                CaptureDecisions.Begin(provider.Schema.Decisions.Length);
                provider.Capture(creature, game, CaptureFields, CaptureDecisions, out int ownerId);
                heavy.StoreIfChanged(
                    tick,
                    Key,
                    AIDebugRegistry.EntityState(creature),
                    provider.Schema,
                    CaptureFields,
                    CaptureDecisions,
                    ownerId);
                nextHeavyTick = tick + HeavyIntervalTicks;
            }

            if (immediate || tick >= nextPathTick)
            {
                paths.Append(tick, AIDebugAdvancedCapture.CapturePath(creature));
                nextPathTick = tick + PathIntervalTicks;
            }

            if (immediate || tick >= nextTableTick)
            {
                tables.Capture(tick, creature);
                nextTableTick = tick + TableIntervalTicks;
            }
        }

        internal bool TryResolve(int cursorTick, out AIDebugResolvedSnapshot resolved)
        {
            if (!heavy.TryResolve(cursorTick, out HeavyEntry heavyEntry))
            {
                resolved = default;
                return false;
            }

            AIDebugSnapshot snapshot = heavyEntry.Materialize(Key);
            tables.TryResolve(cursorTick, out TableEntry table);
            paths.TryResolve(cursorTick, out PathEntry path);

            int age = Math.Max(0, cursorTick - heavyEntry.Tick);
            if (table != null) age = Math.Max(age, Math.Max(0, cursorTick - table.Tick));
            if (path.HasValue) age = Math.Max(age, Math.Max(0, cursorTick - path.Tick));

            resolved = new AIDebugResolvedSnapshot(
                true,
                heavyEntry.Tick,
                age,
                snapshot,
                table?.Utilities ?? EmptyUtilities,
                table?.UtilityCount ?? 0,
                table?.UtilityTruncated ?? false,
                table?.Perception ?? EmptyPerception,
                table?.PerceptionCount ?? 0,
                table?.PerceptionTruncated ?? false,
                path.HasValue ? path.Path : default);
            return true;
        }
    }

    private sealed class HeavyEntry
    {
        internal int Id;
        internal int Tick;
        internal AIDebugEntityState EntityState;
        internal AIDebugRichSchema Schema;
        internal int ControlOwnerStringId;
        internal readonly AIDebugRawSnapshotBuffer Fields = new(RawFieldCapacity);
        internal readonly AIDebugRawDecisionBuffer Decisions = new(DecisionCapacity);
        private AIDebugSnapshot materialized;

        internal void Store(
            int id,
            int tick,
            AIDebugEntityState entityState,
            AIDebugRichSchema schema,
            AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions,
            int controlOwnerStringId)
        {
            Id = id;
            Tick = tick;
            EntityState = entityState;
            Schema = schema;
            ControlOwnerStringId = controlOwnerStringId;
            Fields.CopyFrom(fields);
            Decisions.CopyFrom(decisions);
            materialized = null;
        }

        internal bool Same(
            AIDebugEntityState entityState,
            AIDebugRichSchema schema,
            AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions,
            int ownerId) =>
            ReferenceEquals(Schema, schema) && EntityState == entityState && ControlOwnerStringId == ownerId &&
            Fields.ContentEquals(fields) && Decisions.ContentEquals(decisions);

        internal AIDebugSnapshot Materialize(DebugEntityKey key)
        {
            materialized ??= AIDebugRecorderRichProviderRegistry.Materialize(
                key, EntityState, Schema, Fields, Decisions, ControlOwnerStringId);
            return materialized;
        }

        internal void Reset()
        {
            Id = 0;
            Tick = 0;
            EntityState = default;
            Schema = null;
            ControlOwnerStringId = 0;
            Fields.Begin(0);
            Decisions.Begin(0);
            materialized = null;
        }
    }

    private sealed class HeavyStore
    {
        private readonly HeavyEntry[] entries = new HeavyEntry[HeavyCapacity];
        private int write;
        private int count;
        private int nextId = 1;

        internal int StoreIfChanged(
            int tick,
            DebugEntityKey key,
            AIDebugEntityState state,
            AIDebugRichSchema schema,
            AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions,
            int ownerId)
        {
            if (count > 0)
            {
                int lastIndex = write - 1;
                if (lastIndex < 0) lastIndex += entries.Length;
                HeavyEntry last = entries[lastIndex];
                if (last != null && last.Same(state, schema, fields, decisions, ownerId)) return last.Id;
            }

            HeavyEntry entry = entries[write] ?? new HeavyEntry();
            entries[write] = entry;
            int id = nextId++;
            if (nextId <= 0) nextId = 1;
            entry.Store(id, tick, state, schema, fields, decisions, ownerId);
            write++;
            if (write >= entries.Length) write = 0;
            if (count < entries.Length) count++;
            return id;
        }

        internal bool TryResolve(int cursorTick, out HeavyEntry best)
        {
            best = null;
            int bestTick = int.MinValue;
            for (int i = 0; i < count; i++)
            {
                int index = write - 1 - i;
                if (index < 0) index += entries.Length;
                HeavyEntry candidate = entries[index];
                if (candidate == null || candidate.Schema == null || candidate.Tick > cursorTick) continue;
                if (candidate.Tick < bestTick) continue;
                best = candidate;
                bestTick = candidate.Tick;
                if (candidate.Tick == cursorTick) break;
            }
            return best != null;
        }

        internal void Clear()
        {
            for (int i = 0; i < entries.Length; i++) entries[i]?.Reset();
            write = 0;
            count = 0;
            nextId = 1;
        }
    }

    private sealed class TableEntry
    {
        internal int Tick;
        internal readonly AIDebugUtilityRow[] Utilities = new AIDebugUtilityRow[UtilityCapacity];
        internal int UtilityCount;
        internal bool UtilityTruncated;
        internal readonly AIDebugPerceptionRow[] Perception = new AIDebugPerceptionRow[PerceptionCapacity];
        internal int PerceptionCount;
        internal bool PerceptionTruncated;

        internal void Reset()
        {
            Tick = 0;
            if (UtilityCount > 0) Array.Clear(Utilities, 0, UtilityCount);
            if (PerceptionCount > 0) Array.Clear(Perception, 0, PerceptionCount);
            UtilityCount = 0;
            PerceptionCount = 0;
            UtilityTruncated = false;
            PerceptionTruncated = false;
        }
    }

    private sealed class TableStore
    {
        private readonly TableEntry[] entries = new TableEntry[TableCapacity];
        private int write;
        private int count;

        internal void Capture(int tick, AbstractCreature creature)
        {
            TableEntry entry = entries[write] ?? new TableEntry();
            entries[write] = entry;
            entry.Reset();
            entry.Tick = tick;
            CaptureUtilities(creature, entry);
            CapturePerception(creature, entry);
            write++;
            if (write >= entries.Length) write = 0;
            if (count < entries.Length) count++;
        }

        internal bool TryResolve(int cursorTick, out TableEntry best)
        {
            best = null;
            for (int i = 0; i < count; i++)
            {
                int index = write - 1 - i;
                if (index < 0) index += entries.Length;
                TableEntry candidate = entries[index];
                if (candidate == null || candidate.Tick > cursorTick) continue;
                best = candidate;
                return true;
            }
            return false;
        }

        internal void Clear()
        {
            for (int i = 0; i < entries.Length; i++) entries[i]?.Reset();
            write = 0;
            count = 0;
        }

        private static void CaptureUtilities(AbstractCreature creature, TableEntry output)
        {
            UtilityComparer comparer = creature?.abstractAI?.RealAI?.utilityComparer;
            if (comparer?.uTrackers == null) return;
            for (int i = 0; i < comparer.uTrackers.Count; i++)
            {
                UtilityComparer.UtilityTracker tracker = comparer.uTrackers[i];
                if (tracker == null) continue;
                if (output.UtilityCount >= output.Utilities.Length)
                {
                    output.UtilityTruncated = true;
                    break;
                }

                string name = tracker.module?.GetType().Name ?? "<null>";
                float weighted = tracker.smoother != null ? tracker.smoothedUtility : float.NaN;
                float nonWeighted = tracker.smoother != null && Mathf.Abs(tracker.weight) > 0.000001f
                    ? tracker.smoothedUtility / tracker.weight : float.NaN;
                output.Utilities[output.UtilityCount++] = new AIDebugUtilityRow(
                    name, float.NaN, nonWeighted, tracker.weight, weighted, tracker.continuationBonus,
                    ReferenceEquals(tracker, comparer.highestUtilityTracker));
            }
        }

        private static void CapturePerception(AbstractCreature creature, TableEntry output)
        {
            Tracker tracker = creature?.abstractAI?.RealAI?.tracker;
            if (tracker?.creatures == null) return;
            for (int i = 0; i < tracker.creatures.Count; i++)
            {
                Tracker.CreatureRepresentation rep = tracker.creatures[i];
                AbstractCreature other = rep?.representedCreature;
                if (rep == null || other == null) continue;
                if (output.PerceptionCount >= output.Perception.Length)
                {
                    output.PerceptionTruncated = true;
                    break;
                }

                CreatureTemplate.Relationship relationship = rep.dynamicRelationship?.currentRelationship ?? default;
                string relationshipName = rep.dynamicRelationship == null ? "—" : relationship.type?.value ?? "—";
                float intensity = rep.dynamicRelationship == null ? 0f : relationship.intensity;
                WorldCoordinate bestGuess = rep.lastSeenCoord;
                if (rep is Tracker.ElaborateCreatureRepresentation elaborate &&
                    !elaborate.bestGhostDirty && elaborate.bestGhost != null)
                {
                    WorldCoordinate cached = elaborate.bestGhost.coord;
                    bestGuess = cached.room == creature.pos.room ? cached.WashNode() : cached;
                }

                DebugEntityKey key = DebugEntityKey.From(other);
                string type = other.creatureTemplate?.type?.value ?? "Creature";
                string name = EntityDisplayNameCache.Get(key, type, other.ID.number);
                output.Perception[output.PerceptionCount++] = new AIDebugPerceptionRow(
                    key, name, rep.VisualContact, rep.TicksSinceSeen, rep.EstimatedChanceOfFinding,
                    rep.priority, rep.lastSeenCoord, bestGuess, relationshipName, intensity);
            }
        }
    }

    private readonly struct PathEntry
    {
        internal readonly bool HasValue;
        internal readonly int Tick;
        internal readonly AIDebugPathState Path;
        internal PathEntry(bool hasValue, int tick, AIDebugPathState path)
        {
            HasValue = hasValue;
            Tick = tick;
            Path = path;
        }
    }

    private sealed class PathStore
    {
        private readonly int[] ticks = new int[PathCapacity];
        private readonly AIDebugPathState[] values = new AIDebugPathState[PathCapacity];
        private int write;
        private int count;

        internal void Append(int tick, AIDebugPathState value)
        {
            ticks[write] = tick;
            values[write] = value;
            write++;
            if (write >= ticks.Length) write = 0;
            if (count < ticks.Length) count++;
        }

        internal bool TryResolve(int cursorTick, out PathEntry result)
        {
            for (int i = 0; i < count; i++)
            {
                int index = write - 1 - i;
                if (index < 0) index += ticks.Length;
                if (ticks[index] > cursorTick) continue;
                result = new PathEntry(true, ticks[index], values[index]);
                return true;
            }
            result = default;
            return false;
        }

        internal void Clear()
        {
            Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(values, 0, values.Length);
            write = 0;
            count = 0;
        }
    }

    private static class EntityDisplayNameCache
    {
        private const int Capacity = 256;
        private static readonly DebugEntityKey[] Keys = new DebugEntityKey[Capacity];
        private static readonly string[] Names = new string[Capacity];
        private static int count;
        private static int replace;

        internal static string Get(DebugEntityKey key, string type, int number)
        {
            for (int i = 0; i < count; i++)
                if (Keys[i] == key) return Names[i];

            string name = (type ?? "Creature") + " #" + number;
            int index;
            if (count < Capacity) index = count++;
            else
            {
                index = replace++;
                if (replace >= Capacity) replace = 0;
            }
            Keys[index] = key;
            Names[index] = name;
            return name;
        }
    }
}
