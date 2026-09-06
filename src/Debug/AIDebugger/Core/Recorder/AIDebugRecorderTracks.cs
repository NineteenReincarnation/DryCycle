using System;

namespace DryCycle.Debugging.AI;

// Recorder records expose their simulation tick through a constrained interface. Calling
// this from AIDebugBlockRing<T> does not box the struct and lets the generic ring perform
// historical lookup without delegates, LINQ, temporary arrays, or per-query allocation.
internal interface IAIDebugTicked
{
    int Tick { get; }
}

// Hot-path records are deliberately value-only. Do not add strings, UnityEngine.Object
// references, collections, or formatted presentation data here. The recorder writes these
// structs on Rain World's simulation thread and Presentation/Export formats them later.
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

// A compact 40 Hz state fingerprint. It is not a complete inspector snapshot. Its job is
// to detect short-lived state transitions without allocating or formatting strings.
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

// Fixed block ring used by the high-frequency recorder. Blocks and their arrays are
// allocated once when an entity first becomes tracked; Append performs no allocation.
// PinCount is reserved for zero-copy trigger-capture windows. A pinned next block causes
// an explicit dropped-record counter rather than blocking Rain World's simulation thread.
internal sealed class AIDebugBlockRing<T> where T : struct, IAIDebugTicked
{
    private sealed class Block
    {
        internal readonly T[] Items;
        internal int Count;
        internal int StartTick;
        internal int EndTick;
        internal int PinCount;

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
        }
    }

    private readonly Block[] blocks;
    private readonly int itemsPerBlock;
    private int writeBlock;
    private int retainedCount;

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

    internal bool Append(T item, int tick)
    {
        Block block = blocks[writeBlock];
        if (block.Count >= itemsPerBlock)
        {
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

        if (block.Count == 0) block.StartTick = tick;
        block.Items[block.Count++] = item;
        block.EndTick = tick;
        retainedCount++;
        TotalWritten++;
        return true;
    }

    // Finds the newest retained real sample whose simulation tick is <= cursorTick.
    // Blocks are searched newest-to-oldest and samples within a crossing block are scanned
    // backwards. This is the historical resolver primitive; it allocates nothing.
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

    internal void Clear()
    {
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
}
