using System;

namespace DryCycle.Debugging.AI;

internal enum AIDebugRawValueKind : byte
{
    None,
    Bool,
    Int,
    Float,
    Vector2,
    EntityId,
    EnumToken,
    Flags
}

[Flags]
internal enum AIDebugFieldFlags : byte
{
    None = 0,
    Exact = 1 << 0,
    Retained = 1 << 1,
    Derived = 1 << 2,
    Species = 1 << 3
}

// Static schema metadata is created once, not copied into every historical sample.
internal readonly struct AIDebugFieldSchema
{
    internal readonly ushort FieldId;
    internal readonly string SectionKey;
    internal readonly string LabelKey;
    internal readonly string RawName;
    internal readonly AIDebugRawValueKind Kind;
    internal readonly AIDebugFieldFlags Flags;

    internal AIDebugFieldSchema(
        ushort fieldId,
        string sectionKey,
        string labelKey,
        string rawName,
        AIDebugRawValueKind kind,
        AIDebugFieldFlags flags)
    {
        FieldId = fieldId;
        SectionKey = sectionKey ?? string.Empty;
        LabelKey = labelKey ?? string.Empty;
        RawName = rawName ?? string.Empty;
        Kind = kind;
        Flags = flags;
    }
}

// Compact value union for recorder storage. No object, boxing, strings, collections, or
// Unity references are permitted. The schema determines how these four scalar slots are
// interpreted at presentation/export time.
internal readonly struct AIDebugRawValue : IEquatable<AIDebugRawValue>
{
    internal readonly AIDebugRawValueKind Kind;
    internal readonly int I0;
    internal readonly int I1;
    internal readonly float F0;
    internal readonly float F1;

    private AIDebugRawValue(AIDebugRawValueKind kind, int i0, int i1, float f0, float f1)
    {
        Kind = kind;
        I0 = i0;
        I1 = i1;
        F0 = f0;
        F1 = f1;
    }

    internal static AIDebugRawValue Bool(bool value) =>
        new(AIDebugRawValueKind.Bool, value ? 1 : 0, 0, 0f, 0f);

    internal static AIDebugRawValue Int(int value) =>
        new(AIDebugRawValueKind.Int, value, 0, 0f, 0f);

    internal static AIDebugRawValue Float(float value) =>
        new(AIDebugRawValueKind.Float, 0, 0, value, 0f);

    internal static AIDebugRawValue Vector2(float x, float y) =>
        new(AIDebugRawValueKind.Vector2, 0, 0, x, y);

    internal static AIDebugRawValue EntityId(int spawner, int number) =>
        new(AIDebugRawValueKind.EntityId, spawner, number, 0f, 0f);

    internal static AIDebugRawValue EnumToken(int value) =>
        new(AIDebugRawValueKind.EnumToken, value, 0, 0f, 0f);

    internal static AIDebugRawValue Flags(int value) =>
        new(AIDebugRawValueKind.Flags, value, 0, 0f, 0f);

    public bool Equals(AIDebugRawValue other) =>
        Kind == other.Kind && I0 == other.I0 && I1 == other.I1 &&
        BitConverter.SingleToInt32Bits(F0) == BitConverter.SingleToInt32Bits(other.F0) &&
        BitConverter.SingleToInt32Bits(F1) == BitConverter.SingleToInt32Bits(other.F1);

    public override bool Equals(object obj) => obj is AIDebugRawValue other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)Kind;
            hash = hash * 397 ^ I0;
            hash = hash * 397 ^ I1;
            hash = hash * 397 ^ BitConverter.SingleToInt32Bits(F0);
            hash = hash * 397 ^ BitConverter.SingleToInt32Bits(F1);
            return hash;
        }
    }

    public static bool operator ==(AIDebugRawValue left, AIDebugRawValue right) => left.Equals(right);
    public static bool operator !=(AIDebugRawValue left, AIDebugRawValue right) => !left.Equals(right);
}

// Reusable fixed-capacity writer used by Rich/Heavy providers. A provider fills the same
// buffer each capture; the recorder decides whether a versioned snapshot must be emitted.
internal sealed class AIDebugRawSnapshotBuffer
{
    private readonly AIDebugRawValue[] values;
    private readonly ulong[] validWords;
    private int count;

    internal int Count => count;
    internal int Capacity => values.Length;

    internal AIDebugRawSnapshotBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        values = new AIDebugRawValue[capacity];
        validWords = new ulong[(capacity + 63) >> 6];
    }

    internal void Begin(int fieldCount)
    {
        if ((uint)fieldCount > (uint)values.Length) throw new ArgumentOutOfRangeException(nameof(fieldCount));
        count = fieldCount;
        Array.Clear(validWords, 0, validWords.Length);
    }

    internal void Set(int fieldIndex, AIDebugRawValue value)
    {
        if ((uint)fieldIndex >= (uint)count) throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        values[fieldIndex] = value;
        validWords[fieldIndex >> 6] |= 1UL << (fieldIndex & 63);
    }

    internal bool IsValid(int fieldIndex)
    {
        if ((uint)fieldIndex >= (uint)count) return false;
        return (validWords[fieldIndex >> 6] & (1UL << (fieldIndex & 63))) != 0UL;
    }

    internal AIDebugRawValue Get(int fieldIndex)
    {
        if ((uint)fieldIndex >= (uint)count) return default;
        return values[fieldIndex];
    }

    internal bool ContentEquals(AIDebugRawSnapshotBuffer other)
    {
        if (other == null || other.count != count) return false;
        for (int i = 0; i < validWords.Length; i++)
            if (validWords[i] != other.validWords[i]) return false;
        for (int i = 0; i < count; i++)
        {
            if (!IsValid(i)) continue;
            if (values[i] != other.values[i]) return false;
        }
        return true;
    }

    internal void CopyFrom(AIDebugRawSnapshotBuffer source)
    {
        if (source == null || source.count > values.Length) throw new ArgumentException("Incompatible raw snapshot buffer.", nameof(source));
        count = source.count;
        Array.Copy(source.values, values, count);
        Array.Copy(source.validWords, validWords, validWords.Length);
    }
}

internal static class AIDebugCoreSchema
{
    internal const int Room = 0;
    internal const int EntityState = 1;
    internal const int DestinationRoom = 2;
    internal const int DestinationX = 3;
    internal const int DestinationY = 4;
    internal const int DestinationNode = 5;
    internal const int ModeToken = 6;
    internal const int Target = 7;
    internal const int FastFlags = 8;
    internal const int Position = 9;
    internal const int Velocity = 10;
    internal const int FieldCount = 11;

    internal static readonly AIDebugFieldSchema[] Fields =
    {
        new(Room, "section.identity", "field.room", "AbstractCreature.pos.room", AIDebugRawValueKind.Int, AIDebugFieldFlags.Exact),
        new(EntityState, "section.state", "field.entity_state", "AIDebugEntityState", AIDebugRawValueKind.EnumToken, AIDebugFieldFlags.Exact),
        new(DestinationRoom, "section.ai", "field.destination_room", "AbstractCreatureAI.destination.room", AIDebugRawValueKind.Int, AIDebugFieldFlags.Exact),
        new(DestinationX, "section.ai", "field.destination_x", "AbstractCreatureAI.destination.x", AIDebugRawValueKind.Int, AIDebugFieldFlags.Exact),
        new(DestinationY, "section.ai", "field.destination_y", "AbstractCreatureAI.destination.y", AIDebugRawValueKind.Int, AIDebugFieldFlags.Exact),
        new(DestinationNode, "section.ai", "field.destination_node", "AbstractCreatureAI.destination.abstractNode", AIDebugRawValueKind.Int, AIDebugFieldFlags.Exact),
        new(ModeToken, "section.ai", "field.mode", "Recorder.ModeToken", AIDebugRawValueKind.EnumToken, AIDebugFieldFlags.Retained),
        new(Target, "section.ai", "field.target", "Recorder.TargetEntityId", AIDebugRawValueKind.EntityId, AIDebugFieldFlags.Retained),
        new(FastFlags, "section.state", "field.flags", "Recorder.FastFlags", AIDebugRawValueKind.Flags, AIDebugFieldFlags.Retained),
        new(Position, "section.movement", "field.position", "mainBodyChunk.pos", AIDebugRawValueKind.Vector2, AIDebugFieldFlags.Exact),
        new(Velocity, "section.movement", "field.velocity", "mainBodyChunk.vel", AIDebugRawValueKind.Vector2, AIDebugFieldFlags.Exact)
    };

    internal static void Resolve(
        AIDebugResolvedFastState fast,
        AIDebugResolvedMotion motion,
        AIDebugRawSnapshotBuffer destination)
    {
        destination.Begin(FieldCount);
        if (fast.HasValue)
        {
            AIDebugFastState state = fast.State;
            destination.Set(Room, AIDebugRawValue.Int(state.Room));
            destination.Set(EntityState, AIDebugRawValue.EnumToken((int)state.EntityState));
            if (state.DestinationRoom != AIDebugFastState.UnknownToken)
                destination.Set(DestinationRoom, AIDebugRawValue.Int(state.DestinationRoom));
            if (state.DestinationX != AIDebugFastState.UnknownToken)
                destination.Set(DestinationX, AIDebugRawValue.Int(state.DestinationX));
            if (state.DestinationY != AIDebugFastState.UnknownToken)
                destination.Set(DestinationY, AIDebugRawValue.Int(state.DestinationY));
            if (state.DestinationNode != AIDebugFastState.UnknownToken)
                destination.Set(DestinationNode, AIDebugRawValue.Int(state.DestinationNode));
            if (state.ModeToken != AIDebugFastState.UnknownToken)
                destination.Set(ModeToken, AIDebugRawValue.EnumToken(state.ModeToken));
            if (state.TargetNumber != AIDebugFastState.UnknownToken)
                destination.Set(Target, AIDebugRawValue.EntityId(state.TargetSpawner, state.TargetNumber));
            destination.Set(FastFlags, AIDebugRawValue.Flags((int)state.Flags));
        }

        if (motion.HasValue)
        {
            destination.Set(Position, AIDebugRawValue.Vector2(motion.X, motion.Y));
            destination.Set(Velocity, AIDebugRawValue.Vector2(motion.VX, motion.VY));
        }
    }
}
