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
    internal readonly int PinnedEntities;
    internal readonly long MotionSamples;
    internal readonly long StateChanges;
    internal readonly long OverwrittenBlocks;
    internal readonly long DroppedRecords;

    internal AIDebugRecorderStatus(
        AIDebugRecorderMode mode,
        int activeTracked,
        int retainedEntities,
        int pinnedEntities,
        long motionSamples,
        long stateChanges,
        long overwrittenBlocks,
        long droppedRecords)
    {
        Mode = mode;
        ActiveTracked = activeTracked;
        RetainedEntities = retainedEntities;
        PinnedEntities = pinnedEntities;
        MotionSamples = motionSamples;
        StateChanges = stateChanges;
        OverwrittenBlocks = overwrittenBlocks;
        DroppedRecords = droppedRecords;
    }
}

internal static class AIDebugRecorder
{
    private const int MaxRetainedSlots = 12;
    internal const int MaxPinnedEntities = 3;
    private const int MotionBlockCount = 16;
    private const int MotionSamplesPerBlock = 256;
    private const int StateBlockCount = 8;
    private const int StateSamplesPerBlock = 128;

    private static readonly AIDebugTrackedEntitySlot[] Slots = new AIDebugTrackedEntitySlot[MaxRetainedSlots];
    private static AIDebugRecorderMode mode = AIDebugRecorderMode.Armed;
    private static RainWorldGame boundGame;
    private static int selectedSlot = -1;
    private static int activeCaptureCount;
    private static int pinnedCount;
    private static int sequenceTick = int.MinValue;
    private static uint sequence;

    internal static AIDebugRecorderMode Mode => mode;

    internal static void SetMode(AIDebugRecorderMode value) => mode = value;

    internal static bool Select(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return false;
        EnsureGame(game);
        int existing = EnsureSlot(game, key);
        if (existing < 0) return false;

        if (selectedSlot >= 0 && selectedSlot != existing && Slots[selectedSlot] != null)
        {
            AIDebugTrackedEntitySlot previous = Slots[selectedSlot];
            ChangeRole(previous, previous.Pinned ? AIDebugTrackedRole.Pinned : AIDebugTrackedRole.Retained);
        }

        selectedSlot = existing;
        ChangeRole(Slots[existing], AIDebugTrackedRole.Selected);
        Slots[existing].LastTouchedTick = game.clock;
        return true;
    }

    internal static bool Pin(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return false;
        EnsureGame(game);
        int index = EnsureSlot(game, key);
        if (index < 0) return false;

        AIDebugTrackedEntitySlot slot = Slots[index];
        if (slot.Pinned) return true;
        if (pinnedCount >= MaxPinnedEntities) return false;

        slot.Pinned = true;
        pinnedCount++;
        if (index != selectedSlot) ChangeRole(slot, AIDebugTrackedRole.Pinned);
        slot.LastTouchedTick = game.clock;
        return true;
    }

    internal static void Unpin(DebugEntityKey key)
    {
        int index = FindSlot(key);
        if (index < 0) return;
        AIDebugTrackedEntitySlot slot = Slots[index];
        if (!slot.Pinned) return;

        slot.Pinned = false;
        if (pinnedCount > 0) pinnedCount--;
        if (index != selectedSlot) ChangeRole(slot, AIDebugTrackedRole.Retained);
    }

    internal static bool TogglePin(RainWorldGame game, DebugEntityKey key)
    {
        if (game == null) return false;
        EnsureGame(game);
        int index = FindSlot(key);
        if (index >= 0 && Slots[index].Pinned)
        {
            Unpin(key);
            return true;
        }

        return Pin(game, key);
    }

    internal static void ClearSelection()
    {
        if (selectedSlot >= 0 && Slots[selectedSlot] != null)
        {
            AIDebugTrackedEntitySlot selected = Slots[selectedSlot];
            ChangeRole(selected, selected.Pinned ? AIDebugTrackedRole.Pinned : AIDebugTrackedRole.Retained);
        }
        selectedSlot = -1;
    }

    internal static void OnSimulationTick(RainWorldGame game)
    {
        if (mode == AIDebugRecorderMode.Off || game == null) return;
        EnsureGame(game);
        DrainControlRequests();
        if (activeCaptureCount <= 0) return;

        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugTrackedEntitySlot slot = Slots[i];
            if (slot == null || !slot.IsCapturing) continue;
            if (slot.CaptureTick(game, game.clock)) continue;

            if (slot.Pinned)
            {
                slot.Pinned = false;
                if (pinnedCount > 0) pinnedCount--;
            }
            ChangeRole(slot, AIDebugTrackedRole.Retained);
            if (selectedSlot == i) selectedSlot = -1;
        }
    }

    internal static AIDebugRecorderStatus GetStatus()
    {
        DrainControlRequests();

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

        return new AIDebugRecorderStatus(
            mode, activeCaptureCount, retained, pinnedCount, motion, states, overwritten, dropped);
    }

    internal static bool TryGetSelectedKey(out DebugEntityKey key)
    {
        if (selectedSlot >= 0 && selectedSlot < Slots.Length)
        {
            AIDebugTrackedEntitySlot slot = Slots[selectedSlot];
            if (slot != null && slot.Role == AIDebugTrackedRole.Selected)
            {
                key = slot.Key;
                return true;
            }
        }

        key = default;
        return false;
    }

    internal static bool TryGetEntityStatus(DebugEntityKey key, out AIDebugRecorderEntityStatus status)
    {
        DrainControlRequests();

        int index = FindSlot(key);
        if (index < 0)
        {
            status = default;
            return false;
        }

        AIDebugTrackedEntitySlot slot = Slots[index];
        status = new AIDebugRecorderEntityStatus(
            true,
            slot.Key,
            slot.Role,
            slot.Pinned,
            slot.LastTouchedTick,
            slot.Motion.TotalWritten,
            slot.States.TotalWritten);
        return true;
    }

    internal static bool TryResolveMotion(DebugEntityKey key, int cursorTick, out AIDebugResolvedMotion resolved)
    {
        int index = FindSlot(key);
        if (index < 0 || !Slots[index].Motion.TryGetLatestAtOrBefore(cursorTick, out AIDebugMotionSample sample))
        {
            resolved = default;
            return false;
        }

        resolved = new AIDebugResolvedMotion(
            true, sample.Tick, Math.Max(0, cursorTick - sample.Tick),
            sample.X, sample.Y, sample.VX, sample.VY);
        return true;
    }

    internal static bool TryResolveFastState(DebugEntityKey key, int cursorTick, out AIDebugResolvedFastState resolved)
    {
        int index = FindSlot(key);
        if (index < 0 || !Slots[index].States.TryGetLatestAtOrBefore(cursorTick, out AIDebugFastStateSample sample))
        {
            resolved = default;
            return false;
        }

        resolved = new AIDebugResolvedFastState(
            true, sample.Tick, Math.Max(0, cursorTick - sample.Tick), sample.Sequence, sample.State);
        return true;
    }

    internal static int CopyMotionRange(
        DebugEntityKey key, int startTick, int endTick, AIDebugMotionSample[] destination, int destinationOffset = 0)
    {
        int index = FindSlot(key);
        return index < 0 ? 0 : Slots[index].Motion.CopyRange(startTick, endTick, destination, destinationOffset);
    }

    internal static int CopyFastStateRange(
        DebugEntityKey key, int startTick, int endTick, AIDebugFastStateSample[] destination, int destinationOffset = 0)
    {
        int index = FindSlot(key);
        return index < 0 ? 0 : Slots[index].States.CopyRange(startTick, endTick, destination, destinationOffset);
    }

    internal static bool TryGetRetainedTickRange(DebugEntityKey key, out int oldestTick, out int newestTick)
    {
        int index = FindSlot(key);
        if (index < 0)
        {
            oldestTick = 0;
            newestTick = 0;
            return false;
        }

        bool motion = Slots[index].Motion.TryGetRetainedTickRange(out int motionOldest, out int motionNewest);
        bool states = Slots[index].States.TryGetRetainedTickRange(out int stateOldest, out int stateNewest);
        if (!motion && !states)
        {
            oldestTick = 0;
            newestTick = 0;
            return false;
        }

        oldestTick = motion && states ? Math.Min(motionOldest, stateOldest) : (motion ? motionOldest : stateOldest);
        newestTick = motion && states ? Math.Max(motionNewest, stateNewest) : (motion ? motionNewest : stateNewest);
        return true;
    }

    internal static void Reset()
    {
        AIDebugRecorderControl.Clear();
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

    private static void DrainControlRequests()
    {
        RainWorldGame game = boundGame;
        if (game == null) return;

        while (AIDebugRecorderControl.TryDequeue(out AIDebugRecorderControlRequest request))
        {
            switch (request.Kind)
            {
                case AIDebugRecorderControlKind.TogglePin:
                    TogglePin(game, request.Key);
                    break;
            }
        }
    }

    private static int EnsureSlot(RainWorldGame game, DebugEntityKey key)
    {
        int existing = FindSlot(key);
        if (existing >= 0)
        {
            Slots[existing].RefreshHandle(game);
            return Slots[existing].HasHandle ? existing : -1;
        }

        int index = FindReusableSlot();
        if (index < 0) return -1;
        AbstractCreature creature = AIDebugRegistry.Resolve(game, key);
        if (creature == null) return -1;

        AIDebugTrackedEntitySlot slot = Slots[index];
        if (slot == null)
        {
            slot = new AIDebugTrackedEntitySlot(
                MotionBlockCount,
                MotionSamplesPerBlock,
                StateBlockCount,
                StateSamplesPerBlock);
            Slots[index] = slot;
        }
        slot.Bind(creature, key, game.clock);
        return index;
    }

    private static void EnsureGame(RainWorldGame game)
    {
        if (ReferenceEquals(boundGame, game)) return;
        AIDebugRecorderControl.Clear();
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
        pinnedCount = 0;
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
        private const int ResolveRetryTicks = 40;

        private AbstractCreature handle;
        private IAIDebugRecorderFastProvider provider;
        private bool hasFastState;
        private AIDebugFastState lastFastState;
        private int nextResolveTick;

        internal DebugEntityKey Key { get; private set; }
        internal AIDebugTrackedRole Role;
        internal bool Pinned;
        internal int LastTouchedTick;
        internal readonly AIDebugBlockRing<AIDebugMotionSample> Motion;
        internal readonly AIDebugBlockRing<AIDebugFastStateSample> States;

        internal bool IsCapturing => Role == AIDebugTrackedRole.Selected || Role == AIDebugTrackedRole.Pinned;
        internal bool HasHandle => handle != null && !handle.slatedForDeletion;

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
            nextResolveTick = tick;
        }

        internal void RefreshHandle(RainWorldGame game)
        {
            if (HasHandle && DebugEntityKey.From(handle) == Key) return;
            handle = AIDebugRegistry.Resolve(game, Key);
            provider = AIDebugRecorderProviderRegistry.Resolve(handle);
            nextResolveTick = (game?.clock ?? 0) + ResolveRetryTicks;
        }

        internal bool CaptureTick(RainWorldGame game, int tick)
        {
            AbstractCreature creature = handle;
            if (creature == null)
            {
                if (tick < nextResolveTick) return true;
                nextResolveTick = tick + ResolveRetryTicks;
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
                    new AIDebugFastStateSample(tick, AIDebugRecorder.NextSequence(tick), fastState), tick);
                lastFastState = fastState;
                hasFastState = true;
            }

            Creature realized = creature.realizedCreature;
            BodyChunk body = realized?.mainBodyChunk;
            if (body != null)
            {
                Motion.Append(
                    new AIDebugMotionSample(tick, body.pos.x, body.pos.y, body.vel.x, body.vel.y), tick);
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
            nextResolveTick = 0;
            Key = default;
            Role = AIDebugTrackedRole.None;
            Pinned = false;
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
