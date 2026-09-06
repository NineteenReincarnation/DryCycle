using System;
using System.Threading;

namespace DryCycle.Debugging.AI;

internal interface IAIDebugTicked
{
    int Tick { get; }
}

internal readonly struct AIDebugMotionSample : IAIDebugTicked
{
    internal int Tick { get; }
    internal readonly float X;
    internal readonly float Y;
    internal readonly float VX;
    internal readonly float VY;

    int IAIDebugTicked.Tick => Tick;

    internal AIDebugMotionSample(int tick, float x, float y, float vx, float vy)
    {
        Tick = tick;
        X = x;
        Y = y;
        VX = vx;
        VY = vy;
    }
}

[Flags]
internal enum AIDebugFastFlags
{
    None = 0,
    Dead = 1 << 0,
    Conscious = 1 << 1,
    InShortcut = 1 << 2,
    InDen = 1 << 3,
    FormalAttack = 1 << 4,
    HasImmediateDanger = 1 << 5
}

internal readonly struct AIDebugFastState : IEquatable<AIDebugFastState>
{
    internal const int UnknownToken = int.MinValue;

    internal readonly int Room;
    internal readonly AIDebugEntityState EntityState;
    internal readonly int DestinationRoom;
    internal readonly int DestinationX;
    internal readonly int DestinationY;
    internal readonly int DestinationNode;
    internal readonly int ModeToken;
    internal readonly int TargetSpawner;
    internal readonly int TargetNumber;
    internal readonly AIDebugFastFlags Flags;

    internal AIDebugFastState(
        int room,
        AIDebugEntityState entityState,
        int destinationRoom,
        int destinationX,
        int destinationY,
        int destinationNode,
        int modeToken,
        int targetSpawner,
        int targetNumber,
        AIDebugFastFlags flags)
    {
        Room = room;
        EntityState = entityState;
        DestinationRoom = destinationRoom;
        DestinationX = destinationX;
        DestinationY = destinationY;
        DestinationNode = destinationNode;
        ModeToken = modeToken;
        TargetSpawner = targetSpawner;
        TargetNumber = targetNumber;
        Flags = flags;
    }

    internal AIDebugFastState WithSpeciesState(
        int modeToken,
        int targetSpawner,
        int targetNumber,
        AIDebugFastFlags extraFlags)
    {
        return new AIDebugFastState(
            Room,
            EntityState,
            DestinationRoom,
            DestinationX,
            DestinationY,
            DestinationNode,
            modeToken,
            targetSpawner,
            targetNumber,
            Flags | extraFlags);
    }

    public bool Equals(AIDebugFastState other)
    {
        return Room == other.Room &&
               EntityState == other.EntityState &&
               DestinationRoom == other.DestinationRoom &&
               DestinationX == other.DestinationX &&
               DestinationY == other.DestinationY &&
               DestinationNode == other.DestinationNode &&
               ModeToken == other.ModeToken &&
               TargetSpawner == other.TargetSpawner &&
               TargetNumber == other.TargetNumber &&
               Flags == other.Flags;
    }

    public override bool Equals(object obj) => obj is AIDebugFastState other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Room;
            hash = hash * 397 ^ (int)EntityState;
            hash = hash * 397 ^ DestinationRoom;
            hash = hash * 397 ^ DestinationX;
            hash = hash * 397 ^ DestinationY;
            hash = hash * 397 ^ DestinationNode;
            hash = hash * 397 ^ ModeToken;
            hash = hash * 397 ^ TargetSpawner;
            hash = hash * 397 ^ TargetNumber;
            hash = hash * 397 ^ (int)Flags;
            return hash;
        }
    }

    public static bool operator ==(AIDebugFastState left, AIDebugFastState right) => left.Equals(right);
    public static bool operator !=(AIDebugFastState left, AIDebugFastState right) => !left.Equals(right);
}

internal readonly struct AIDebugFastStateSample : IAIDebugTicked
{
    internal int Tick { get; }
    internal readonly uint Sequence;
    internal readonly AIDebugFastState State;

    int IAIDebugTicked.Tick => Tick;

    internal AIDebugFastStateSample(int tick, uint sequence, AIDebugFastState state)
    {
        Tick = tick;
        Sequence = sequence;
        State = state;
    }
}

// A zero-copy lease pins one recorder block while a background consumer serializes it.
// The underlying array must never be retained after Release().
internal sealed class AIDebugSealedBlockLease<T> where T : struct, IAIDebugTicked
{
    private AIDebugBlockRing<T> owner;
    private readonly int blockIndex;
    private readonly int generation;
    private int released;

    internal readonly T[] Items;
    internal readonly int Count;
    internal readonly int StartTick;
    internal readonly int EndTick;

    internal AIDebugSealedBlockLease(
        AIDebugBlockRing<T> owner,
        int blockIndex,
        int generation,
        T[] items,
        int count,
        int startTick,
        int endTick)
    {
        this.owner = owner;
        this.blockIndex = blockIndex;
        this.generation = generation;
        Items = items;
        Count = count;
        StartTick = startTick;
        EndTick = endTick;
    }

    internal void Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        AIDebugBlockRing<T> target = Interlocked.Exchange(ref owner, null);
        target?.ReleaseLease(blockIndex, generation);
    }
}

internal readonly struct AIDebugPinnedRangeHandle
{
    internal readonly int Id;
    internal bool IsValid => Id > 0;

    internal AIDebugPinnedRangeHandle(int id) => Id = id;
}

// Fixed block ring used by the high-frequency recorder. Blocks and arrays are allocated
// once when an entity becomes tracked. Append and range/history queries allocate nothing.
// Sealed-block leases and range pins are block-granular so anomaly capture/writing never
// copies pre-roll data on the simulation tick that triggers a capture.
internal sealed class AIDebugBlockRing<T> where T : struct, IAIDebugTicked
{
    private const int MaxPinnedRanges = 12;

    private sealed class Block
    {
        internal readonly T[] Items;
        internal int Count;
        internal int StartTick;
        internal int EndTick;
        internal int PinCount;
        internal int Generation;
        internal bool Sealed;

        internal Block(int capacity)
        {
            Items = new T[capacity];
            StartTick = int.MaxValue;
            EndTick = int.MinValue;
        }

        internal void Reset()
        {
            Count = 0;
            StartTick = int.MaxValue;
            EndTick = int.MinValue;
            Sealed = false;
            Generation++;
        }
    }

    private struct PinRangeState
    {
        internal int Id;
        internal int StartTick;
        internal int EndTick;
        internal bool Active;
    }

    private readonly Block[] blocks;
    private readonly int itemsPerBlock;
    private readonly PinRangeState[] pinRanges = new PinRangeState[MaxPinnedRanges];
    private int writeBlock;
    private int retainedCount;
    private int nextPinId = 1;
    private Action<AIDebugSealedBlockLease<T>> sealedBlockSink;

    internal long TotalWritten { get; private set; }
    internal long OverwrittenBlocks { get; private set; }
    internal long DroppedRecords { get; private set; }
    internal int RetainedCount => retainedCount;
    internal int Capacity => blocks.Length * itemsPerBlock;

    internal AIDebugBlockRing(int blockCount, int itemsPerBlock)
    {
        if (blockCount <= 0) throw new ArgumentOutOfRangeException(nameof(blockCount));
        if (itemsPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(itemsPerBlock));

        this.itemsPerBlock = itemsPerBlock;
        blocks = new Block[blockCount];
        for (int i = 0; i < blocks.Length; i++)
            blocks[i] = new Block(itemsPerBlock);
    }

    internal void SetSealedBlockSink(Action<AIDebugSealedBlockLease<T>> sink) => sealedBlockSink = sink;

    internal bool Append(T item, int tick)
    {
        Block block = blocks[writeBlock];
        if (block.Count >= itemsPerBlock)
        {
            SealBlock(writeBlock);

            int next = (writeBlock + 1) % blocks.Length;
            Block nextBlock = blocks[next];
            if (nextBlock.PinCount > 0)
            {
                DroppedRecords++;
                return false;
            }

            if (nextBlock.Count > 0)
            {
                retainedCount -= nextBlock.Count;
                OverwrittenBlocks++;
            }

            nextBlock.Reset();
            writeBlock = next;
            block = nextBlock;
        }

        if (block.Count == 0)
        {
            block.StartTick = tick;
            int rangePins = CountActiveRangesContaining(tick);
            if (rangePins > 0) block.PinCount += rangePins;
        }
        block.Items[block.Count++] = item;
        block.EndTick = tick;
        retainedCount++;
        TotalWritten++;
        return true;
    }

    internal AIDebugPinnedRangeHandle PinRange(int startTick, int endTick)
    {
        if (endTick < startTick)
        {
            int swap = startTick;
            startTick = endTick;
            endTick = swap;
        }

        int slot = -1;
        for (int i = 0; i < pinRanges.Length; i++)
        {
            if (!pinRanges[i].Active)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return default;

        int id = nextPinId++;
        if (nextPinId <= 0) nextPinId = 1;
        pinRanges[slot] = new PinRangeState { Id = id, StartTick = startTick, EndTick = endTick, Active = true };

        for (int i = 0; i < blocks.Length; i++)
        {
            Block block = blocks[i];
            if (BlockOverlaps(block, startTick, endTick)) block.PinCount++;
        }
        return new AIDebugPinnedRangeHandle(id);
    }

    internal void UnpinRange(AIDebugPinnedRangeHandle handle)
    {
        if (!handle.IsValid) return;
        for (int i = 0; i < pinRanges.Length; i++)
        {
            PinRangeState range = pinRanges[i];
            if (!range.Active || range.Id != handle.Id) continue;

            for (int b = 0; b < blocks.Length; b++)
            {
                Block block = blocks[b];
                if (BlockOverlaps(block, range.StartTick, range.EndTick) && block.PinCount > 0)
                    block.PinCount--;
            }
            pinRanges[i] = default;
            return;
        }
    }

    // Emits zero-copy leases for all currently retained blocks intersecting a range. The
    // consumer owns each lease and must Release it after durable serialization.
    internal int LeaseRange(int startTick, int endTick, Action<AIDebugSealedBlockLease<T>> consumer)
    {
        if (consumer == null) throw new ArgumentNullException(nameof(consumer));
        if (endTick < startTick)
        {
            int swap = startTick;
            startTick = endTick;
            endTick = swap;
        }

        int emitted = 0;
        int first = (writeBlock + 1) % blocks.Length;
        for (int offset = 0; offset < blocks.Length; offset++)
        {
            int index = first + offset;
            if (index >= blocks.Length) index -= blocks.Length;
            Block block = blocks[index];
            if (!BlockOverlaps(block, startTick, endTick)) continue;

            block.PinCount++;
            var lease = new AIDebugSealedBlockLease<T>(
                this, index, block.Generation, block.Items, block.Count, block.StartTick, block.EndTick);
            try
            {
                consumer(lease);
                emitted++;
            }
            catch
            {
                lease.Release();
                throw;
            }
        }
        return emitted;
    }

    internal bool TryGetLatestAtOrBefore(int cursorTick, out T value)
    {
        if (retainedCount <= 0)
        {
            value = default;
            return false;
        }

        for (int offset = 0; offset < blocks.Length; offset++)
        {
            int blockIndex = writeBlock - offset;
            if (blockIndex < 0) blockIndex += blocks.Length;
            Block block = blocks[blockIndex];
            if (block.Count <= 0 || block.StartTick > cursorTick) continue;

            if (block.EndTick <= cursorTick)
            {
                value = block.Items[block.Count - 1];
                return true;
            }

            for (int i = block.Count - 1; i >= 0; i--)
            {
                T candidate = block.Items[i];
                if (candidate.Tick > cursorTick) continue;
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal bool TryGetNewest(out T value)
    {
        if (retainedCount <= 0)
        {
            value = default;
            return false;
        }

        for (int offset = 0; offset < blocks.Length; offset++)
        {
            int blockIndex = writeBlock - offset;
            if (blockIndex < 0) blockIndex += blocks.Length;
            Block block = blocks[blockIndex];
            if (block.Count <= 0) continue;
            value = block.Items[block.Count - 1];
            return true;
        }

        value = default;
        return false;
    }

    internal int CopyRange(int startTick, int endTick, T[] destination, int destinationOffset = 0)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (destinationOffset < 0 || destinationOffset > destination.Length)
            throw new ArgumentOutOfRangeException(nameof(destinationOffset));
        if (retainedCount <= 0 || endTick < startTick || destinationOffset == destination.Length)
            return 0;

        int written = 0;
        int capacity = destination.Length - destinationOffset;
        int first = (writeBlock + 1) % blocks.Length;
        for (int blockOffset = 0; blockOffset < blocks.Length && written < capacity; blockOffset++)
        {
            int blockIndex = first + blockOffset;
            if (blockIndex >= blocks.Length) blockIndex -= blocks.Length;
            Block block = blocks[blockIndex];
            if (block.Count <= 0 || block.EndTick < startTick || block.StartTick > endTick) continue;

            for (int i = 0; i < block.Count && written < capacity; i++)
            {
                T candidate = block.Items[i];
                if (candidate.Tick < startTick) continue;
                if (candidate.Tick > endTick) break;
                destination[destinationOffset + written++] = candidate;
            }
        }

        return written;
    }

    internal bool TryGetRetainedTickRange(out int oldestTick, out int newestTick)
    {
        oldestTick = int.MaxValue;
        newestTick = int.MinValue;
        if (retainedCount <= 0) return false;

        for (int i = 0; i < blocks.Length; i++)
        {
            Block block = blocks[i];
            if (block.Count <= 0) continue;
            if (block.StartTick < oldestTick) oldestTick = block.StartTick;
            if (block.EndTick > newestTick) newestTick = block.EndTick;
        }

        return oldestTick != int.MaxValue;
    }

    internal void Clear()
    {
        for (int i = 0; i < pinRanges.Length; i++) pinRanges[i] = default;
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i].PinCount = 0;
            blocks[i].Reset();
        }

        writeBlock = 0;
        retainedCount = 0;
        TotalWritten = 0;
        OverwrittenBlocks = 0;
        DroppedRecords = 0;
    }

    internal void ReleaseLease(int blockIndex, int generation)
    {
        if ((uint)blockIndex >= (uint)blocks.Length) return;
        Block block = blocks[blockIndex];
        if (block.Generation != generation) return;
        if (block.PinCount > 0) block.PinCount--;
    }

    private void SealBlock(int blockIndex)
    {
        Block block = blocks[blockIndex];
        if (block.Count <= 0 || block.Sealed) return;
        block.Sealed = true;
        Action<AIDebugSealedBlockLease<T>> sink = sealedBlockSink;
        if (sink == null) return;

        block.PinCount++;
        var lease = new AIDebugSealedBlockLease<T>(
            this, blockIndex, block.Generation, block.Items, block.Count, block.StartTick, block.EndTick);
        try
        {
            sink(lease);
        }
        catch
        {
            lease.Release();
            throw;
        }
    }

    private int CountActiveRangesContaining(int tick)
    {
        int count = 0;
        for (int i = 0; i < pinRanges.Length; i++)
        {
            PinRangeState range = pinRanges[i];
            if (range.Active && tick >= range.StartTick && tick <= range.EndTick) count++;
        }
        return count;
    }

    private static bool BlockOverlaps(Block block, int startTick, int endTick) =>
        block.Count > 0 && block.EndTick >= startTick && block.StartTick <= endTick;
}
