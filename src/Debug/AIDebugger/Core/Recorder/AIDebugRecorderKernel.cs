using System;

namespace DryCycle.Debugging.AI;

internal enum AIDebugRecorderMode
{
    Off,
    Armed,
    Recording
}

internal enum AIDebugTrackedRole
{
    None,
    Retained,
    Selected,
    Pinned
}

internal readonly struct AIDebugRecorderStatus
{
    internal readonly AIDebugRecorderMode Mode;
    internal readonly int ActiveTracked;
    internal readonly int RetainedEntities;
    internal readonly long MotionSamples;
    internal readonly long StateChanges;
    internal readonly long OverwrittenBlocks;
    internal readonly long DroppedRecords;

    internal AIDebugRecorderStatus(
        AIDebugRecorderMode mode,
        int activeTracked,
        int retainedEntities,
        long motionSamples,
        long stateChanges,
        long overwrittenBlocks,
        long droppedRecords)
    {
        Mode = mode;
        ActiveTracked = activeTracked;
        RetainedEntities = retainedEntities;
        MotionSamples = motionSamples;
        StateChanges = stateChanges;
        OverwrittenBlocks = overwrittenBlocks;
        DroppedRecords = droppedRecords;
    }
}

// V5 recorder kernel. The important contract is that the 40 Hz path owns only compact
// value tracks. It does not build AIDebugSnapshot, format strings, sort perception rows,
// touch ImGui, perform disk IO, or allocate after a slot has been created.
internal static class AIDebugRecorder
{
    private const int MaxRetainedSlots = 12;
    private const int MotionBlockCount = 16;
    private const int MotionSamplesPerBlock = 256;
    private const int StateBlockCount = 8;
    private const int StateSamplesPerBlock = 128;

    private static readonly AIDebugTrackedEntitySlot[] Slots = new AIDebugTrackedEntitySlot[MaxRetainedSlots];
    private static AIDebugRecorderMode mode = AIDebugRecorderMode.Armed;
    private static RainWorldGame boundGame;
    private static int selectedSlot = -1;
    private static int activeCaptureCount;
    private static int sequenceTick = int.MinValue;
    private static uint sequence;

    internal static AIDebugRecorderMode Mode => mode;

    internal static void SetMode(AIDebugRecorderMode value)
    {
        mode = value;
    }

    internal static bool Select(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return false;
        EnsureGame(game);

        int existing = FindSlot(key);
        if (existing < 0)
        {
            existing = FindReusableSlot();
            if (existing < 0) return false;

            AbstractCreature creature = AIDebugRegistry.Resolve(game, key);
            if (creature == null) return false;

            AIDebugTrackedEntitySlot slot = Slots[existing];
            if (slot == null)
            {
                slot = new AIDebugTrackedEntitySlot(
                    MotionBlockCount,
                    MotionSamplesPerBlock,
                    StateBlockCount,
                    StateSamplesPerBlock);
                Slots[existing] = slot;
            }
            slot.Bind(creature, key, game.clock);
        }
        else
        {
            Slots[existing].RefreshHandle(game);
        }

        if (selectedSlot >= 0 && selectedSlot != existing && Slots[selectedSlot] != null)
            ChangeRole(Slots[selectedSlot], AIDebugTrackedRole.Retained);

        selectedSlot = existing;
        ChangeRole(Slots[existing], AIDebugTrackedRole.Selected);
        Slots[existing].LastTouchedTick = game.clock;
        return true;
    }

    internal static void ClearSelection()
    {
        if (selectedSlot >= 0 && Slots[selectedSlot] != null)
            ChangeRole(Slots[selectedSlot], AIDebugTrackedRole.Retained);
        selectedSlot = -1;
    }

    internal static void OnSimulationTick(RainWorldGame game)
    {
        if (mode == AIDebugRecorderMode.Off || game == null) return;
        EnsureGame(game);

        // ARMED + zero tracked entities is the common idle path. Keep it to two cheap
        // branches: no slot scan, no world scan, no allocation, and no presentation work.
        if (activeCaptureCount <= 0) return;

        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugTrackedEntitySlot slot = Slots[i];
            if (slot == null || !slot.IsCapturing) continue;
            if (slot.CaptureTick(game, game.clock)) continue;

            ChangeRole(slot, AIDebugTrackedRole.Retained);
            if (selectedSlot == i) selectedSlot = -1;
        }
    }

    internal static AIDebugRecorderStatus GetStatus()
    {
        int retained = 0;
        long motion = 0;
        long states = 0;
        long overwritten = 0;
        long dropped = 0;

        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugTrackedEntitySlot slot = Slots[i];
            if (slot == null || slot.Role == AIDebugTrackedRole.None) continue;
            retained++;
            motion += slot.Motion.TotalWritten;
            states += slot.States.TotalWritten;
            overwritten += slot.Motion.OverwrittenBlocks + slot.States.OverwrittenBlocks;
            dropped += slot.Motion.DroppedRecords + slot.States.DroppedRecords;
        }

        return new AIDebugRecorderStatus(mode, activeCaptureCount, retained, motion, states, overwritten, dropped);
    }

    internal static void Reset()
    {
        ClearSlots();
        mode = AIDebugRecorderMode.Armed;
        boundGame = null;
    }

    internal static uint NextSequence(int tick)
    {
        if (sequenceTick != tick)
        {
            sequenceTick = tick;
            sequence = 0;
        }
        return sequence++;
    }

    private static void EnsureGame(RainWorldGame game)
    {
        if (ReferenceEquals(boundGame, game)) return;

        // AbstractCreature handles are only valid for the RainWorldGame/world that owns
        // them. Never carry a tracked handle into a new session just because its numeric
        // EntityID happens to match an entity from the previous world.
        ClearSlots();
        boundGame = game;
    }

    private static void ClearSlots()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            Slots[i]?.Clear();
            Slots[i] = null;
        }

        selectedSlot = -1;
        activeCaptureCount = 0;
        sequenceTick = int.MinValue;
        sequence = 0;
    }

    private static void ChangeRole(AIDebugTrackedEntitySlot slot, AIDebugTrackedRole next)
    {
        if (slot == null || slot.Role == next) return;

        bool wasCapturing = slot.IsCapturing;
        slot.Role = next;
        bool isCapturing = slot.IsCapturing;
        if (wasCapturing == isCapturing) return;
        activeCaptureCount += isCapturing ? 1 : -1;
        if (activeCaptureCount < 0) activeCaptureCount = 0;
    }

    private static int FindSlot(DebugEntityKey key)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugTrackedEntitySlot slot = Slots[i];
            if (slot != null && slot.Role != AIDebugTrackedRole.None && slot.Key == key)
                return i;
        }
        return -1;
    }

    private static int FindReusableSlot()
    {
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i] == null || Slots[i].Role == AIDebugTrackedRole.None)
                return i;

        int oldestIndex = -1;
        int oldestTick = int.MaxValue;
        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugTrackedEntitySlot slot = Slots[i];
            if (slot == null || slot.IsCapturing) continue;
            if (slot.LastTouchedTick >= oldestTick) continue;
            oldestTick = slot.LastTouchedTick;
            oldestIndex = i;
        }

        if (oldestIndex >= 0) Slots[oldestIndex].Clear();
        return oldestIndex;
    }

    private sealed class AIDebugTrackedEntitySlot
    {
        private AbstractCreature handle;
        private IAIDebugRecorderFastProvider provider;
        private bool hasFastState;
        private AIDebugFastState lastFastState;

        internal DebugEntityKey Key { get; private set; }
        internal AIDebugTrackedRole Role;
        internal int LastTouchedTick;
        internal readonly AIDebugBlockRing<AIDebugMotionSample> Motion;
        internal readonly AIDebugBlockRing<AIDebugFastStateSample> States;

        internal bool IsCapturing => Role == AIDebugTrackedRole.Selected || Role == AIDebugTrackedRole.Pinned;

        internal AIDebugTrackedEntitySlot(
            int motionBlockCount,
            int motionSamplesPerBlock,
            int stateBlockCount,
            int stateSamplesPerBlock)
        {
            Motion = new AIDebugBlockRing<AIDebugMotionSample>(motionBlockCount, motionSamplesPerBlock);
            States = new AIDebugBlockRing<AIDebugFastStateSample>(stateBlockCount, stateSamplesPerBlock);
        }

        internal void Bind(AbstractCreature creature, DebugEntityKey key, int tick)
        {
            Clear();
            handle = creature;
            Key = key;
            provider = AIDebugRecorderProviderRegistry.Resolve(creature);
            Role = AIDebugTrackedRole.Retained;
            LastTouchedTick = tick;
        }

        internal void RefreshHandle(RainWorldGame game)
        {
            if (handle != null && !handle.slatedForDeletion && DebugEntityKey.From(handle) == Key) return;
            handle = AIDebugRegistry.Resolve(game, Key);
            provider = AIDebugRecorderProviderRegistry.Resolve(handle);
        }

        // Returns false only when this tracked entity has been explicitly deleted and the
        // active role should be retired. Temporary inability to resolve an AbstractCreature
        // leaves the slot active so a normal realize/unrealize transition can recover.
        internal bool CaptureTick(RainWorldGame game, int tick)
        {
            AbstractCreature creature = handle;
            if (creature == null)
            {
                creature = AIDebugRegistry.Resolve(game, Key);
                handle = creature;
                provider = AIDebugRecorderProviderRegistry.Resolve(creature);
            }

            if (creature == null) return true;

            AIDebugFastState fastState = CaptureBaseFastState(creature);
            IAIDebugRecorderFastProvider nextProvider = AIDebugRecorderProviderRegistry.Resolve(creature);
            if (!ReferenceEquals(provider, nextProvider)) provider = nextProvider;
            provider?.CaptureFast(creature, ref fastState);

            if (!hasFastState || fastState != lastFastState)
            {
                States.Append(
                    new AIDebugFastStateSample(tick, AIDebugRecorder.NextSequence(tick), fastState),
                    tick);
                lastFastState = fastState;
                hasFastState = true;
            }

            Creature realized = creature.realizedCreature;
            BodyChunk body = realized?.mainBodyChunk;
            if (body != null)
            {
                Motion.Append(
                    new AIDebugMotionSample(tick, body.pos.x, body.pos.y, body.vel.x, body.vel.y),
                    tick);
            }

            LastTouchedTick = tick;
            if (!creature.slatedForDeletion) return true;

            handle = null;
            provider = null;
            return false;
        }

        internal void Clear()
        {
            handle = null;
            provider = null;
            hasFastState = false;
            lastFastState = default;
            Key = default;
            Role = AIDebugTrackedRole.None;
            LastTouchedTick = 0;
            Motion.Clear();
            States.Clear();
        }

        private static AIDebugFastState CaptureBaseFastState(AbstractCreature creature)
        {
            AIDebugEntityState entityState = AIDebugRegistry.EntityState(creature);
            int destinationRoom = AIDebugFastState.UnknownToken;
            int destinationX = AIDebugFastState.UnknownToken;
            int destinationY = AIDebugFastState.UnknownToken;
            int destinationNode = AIDebugFastState.UnknownToken;

            if (creature.abstractAI != null)
            {
                WorldCoordinate destination = creature.abstractAI.destination;
                destinationRoom = destination.room;
                destinationX = destination.x;
                destinationY = destination.y;
                destinationNode = destination.abstractNode;
            }

            AIDebugFastFlags flags = AIDebugFastFlags.None;
            Creature realized = creature.realizedCreature;
            bool dead = realized?.dead ?? creature.state?.dead ?? false;
            if (dead) flags |= AIDebugFastFlags.Dead;
            if (realized?.Consious == true) flags |= AIDebugFastFlags.Conscious;
            if (realized?.inShortcut == true) flags |= AIDebugFastFlags.InShortcut;
            if (creature.InDen) flags |= AIDebugFastFlags.InDen;

            return new AIDebugFastState(
                creature.pos.room,
                entityState,
                destinationRoom,
                destinationX,
                destinationY,
                destinationNode,
                AIDebugFastState.UnknownToken,
                AIDebugFastState.UnknownToken,
                AIDebugFastState.UnknownToken,
                flags);
        }
    }
}
