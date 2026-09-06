using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    Flags,
    StringId,
    Coordinate
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

internal readonly struct AIDebugDecisionSchema
{
    internal readonly string LabelKey;
    internal readonly string RawName;
    internal readonly int Depth;

    internal AIDebugDecisionSchema(string labelKey, string rawName = null, int depth = 0)
    {
        LabelKey = labelKey ?? string.Empty;
        RawName = rawName ?? string.Empty;
        Depth = depth < 0 ? 0 : depth;
    }
}

internal sealed class AIDebugRichSchema
{
    internal readonly string Id;
    internal readonly string DisplayType;
    internal readonly AIDebugFieldSchema[] Fields;
    internal readonly AIDebugDecisionSchema[] Decisions;

    internal AIDebugRichSchema(
        string id,
        string displayType,
        AIDebugFieldSchema[] fields,
        AIDebugDecisionSchema[] decisions)
    {
        Id = id ?? "generic";
        DisplayType = displayType ?? "Creature";
        Fields = fields ?? Array.Empty<AIDebugFieldSchema>();
        Decisions = decisions ?? Array.Empty<AIDebugDecisionSchema>();
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct AIDebugFloatBits
{
    [FieldOffset(0)] internal float Float;
    [FieldOffset(0)] internal int Bits;

    internal static int Of(float value)
    {
        AIDebugFloatBits union = default;
        union.Float = value;
        return union.Bits;
    }
}

// Compact raw-value union used only by lower-frequency Rich/Heavy capture. No object,
// boxing, formatted string, collection, or Unity reference is retained in recorder data.
internal readonly struct AIDebugRawValue : IEquatable<AIDebugRawValue>
{
    internal readonly AIDebugRawValueKind Kind;
    internal readonly int I0;
    internal readonly int I1;
    internal readonly int I2;
    internal readonly int I3;
    internal readonly float F0;
    internal readonly float F1;

    private AIDebugRawValue(
        AIDebugRawValueKind kind,
        int i0,
        int i1,
        int i2,
        int i3,
        float f0,
        float f1)
    {
        Kind = kind;
        I0 = i0;
        I1 = i1;
        I2 = i2;
        I3 = i3;
        F0 = f0;
        F1 = f1;
    }

    internal static AIDebugRawValue Bool(bool value) =>
        new(AIDebugRawValueKind.Bool, value ? 1 : 0, 0, 0, 0, 0f, 0f);

    internal static AIDebugRawValue Int(int value) =>
        new(AIDebugRawValueKind.Int, value, 0, 0, 0, 0f, 0f);

    internal static AIDebugRawValue Float(float value) =>
        new(AIDebugRawValueKind.Float, 0, 0, 0, 0, value, 0f);

    internal static AIDebugRawValue Vector2(float x, float y) =>
        new(AIDebugRawValueKind.Vector2, 0, 0, 0, 0, x, y);

    internal static AIDebugRawValue EntityId(int spawner, int number) =>
        new(AIDebugRawValueKind.EntityId, spawner, number, 0, 0, 0f, 0f);

    internal static AIDebugRawValue EnumToken(int value) =>
        new(AIDebugRawValueKind.EnumToken, value, 0, 0, 0, 0f, 0f);

    internal static AIDebugRawValue Flags(int value) =>
        new(AIDebugRawValueKind.Flags, value, 0, 0, 0, 0f, 0f);

    internal static AIDebugRawValue StringId(int value) =>
        new(AIDebugRawValueKind.StringId, value, 0, 0, 0, 0f, 0f);

    internal static AIDebugRawValue Coordinate(int room, int x, int y, int node) =>
        new(AIDebugRawValueKind.Coordinate, room, x, y, node, 0f, 0f);

    public bool Equals(AIDebugRawValue other) =>
        Kind == other.Kind && I0 == other.I0 && I1 == other.I1 && I2 == other.I2 && I3 == other.I3 &&
        AIDebugFloatBits.Of(F0) == AIDebugFloatBits.Of(other.F0) &&
        AIDebugFloatBits.Of(F1) == AIDebugFloatBits.Of(other.F1);

    public override bool Equals(object obj) => obj is AIDebugRawValue other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)Kind;
            hash = hash * 397 ^ I0;
            hash = hash * 397 ^ I1;
            hash = hash * 397 ^ I2;
            hash = hash * 397 ^ I3;
            hash = hash * 397 ^ AIDebugFloatBits.Of(F0);
            hash = hash * 397 ^ AIDebugFloatBits.Of(F1);
            return hash;
        }
    }

    public static bool operator ==(AIDebugRawValue left, AIDebugRawValue right) => left.Equals(right);
    public static bool operator !=(AIDebugRawValue left, AIDebugRawValue right) => !left.Equals(right);
}

// Session-local-ish string interning for Rich/Heavy data. Capture stores integer ids;
// formatting/localization occurs only when main-thread Presentation materializes a view.
// All mutation is performed by the Unity/main-thread recorder.
internal static class AIDebugRawStringTable
{
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static readonly List<string> Values = new(128) { string.Empty };

    internal static int Intern(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (Ids.TryGetValue(value, out int existing)) return existing;
        int id = Values.Count;
        Values.Add(value);
        Ids[value] = id;
        return id;
    }

    internal static string Resolve(int id)
    {
        if ((uint)id >= (uint)Values.Count) return string.Empty;
        return Values[id] ?? string.Empty;
    }

    internal static void Reset()
    {
        Ids.Clear();
        Values.Clear();
        Values.Add(string.Empty);
    }
}

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
        if (source == null || source.count > values.Length)
            throw new ArgumentException("Incompatible raw snapshot buffer.", nameof(source));
        count = source.count;
        Array.Copy(source.values, values, count);
        Array.Clear(validWords, 0, validWords.Length);
        int words = (count + 63) >> 6;
        Array.Copy(source.validWords, validWords, words);
    }
}

internal readonly struct AIDebugRawDecision : IEquatable<AIDebugRawDecision>
{
    internal readonly AIDebugDecisionState State;
    internal readonly int DetailStringId;

    internal AIDebugRawDecision(AIDebugDecisionState state, int detailStringId = 0)
    {
        State = state;
        DetailStringId = detailStringId;
    }

    public bool Equals(AIDebugRawDecision other) =>
        State == other.State && DetailStringId == other.DetailStringId;

    public override bool Equals(object obj) => obj is AIDebugRawDecision other && Equals(other);
    public override int GetHashCode() => ((int)State * 397) ^ DetailStringId;
}

internal sealed class AIDebugRawDecisionBuffer
{
    private readonly AIDebugRawDecision[] values;
    private int count;

    internal int Count => count;
    internal int Capacity => values.Length;

    internal AIDebugRawDecisionBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        values = new AIDebugRawDecision[capacity];
    }

    internal void Begin(int decisionCount)
    {
        if ((uint)decisionCount > (uint)values.Length) throw new ArgumentOutOfRangeException(nameof(decisionCount));
        count = decisionCount;
    }

    internal void Set(int index, AIDebugDecisionState state, int detailStringId = 0)
    {
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        values[index] = new AIDebugRawDecision(state, detailStringId);
    }

    internal AIDebugRawDecision Get(int index) =>
        (uint)index < (uint)count ? values[index] : default;

    internal bool ContentEquals(AIDebugRawDecisionBuffer other)
    {
        if (other == null || other.count != count) return false;
        for (int i = 0; i < count; i++)
            if (!values[i].Equals(other.values[i])) return false;
        return true;
    }

    internal void CopyFrom(AIDebugRawDecisionBuffer source)
    {
        if (source == null || source.count > values.Length)
            throw new ArgumentException("Incompatible decision buffer.", nameof(source));
        count = source.count;
        Array.Copy(source.values, values, count);
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
