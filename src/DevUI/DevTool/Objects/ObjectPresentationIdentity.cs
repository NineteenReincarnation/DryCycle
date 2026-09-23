using System.Runtime.CompilerServices;
using System.Threading;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Runtime-only stable identity for PlacedObject presentation.
///
/// RoomSettings list indices are command addresses, not identities: insertion/deletion can move an
/// existing object to a different index. Frontend retained state uses this ID so hover/layout rows do
/// not jump to another object when collection membership changes. IDs are never serialized.
/// </summary>
internal static class ObjectPresentationIdentity
{
    private sealed class IdentityBox
    {
        internal long Value;
    }

    private static readonly ConditionalWeakTable<PlacedObject, IdentityBox> Ids = new();
    private static long nextId;

    internal static long Get(PlacedObject item)
    {
        if (item == null) return 0L;
        return Ids.GetValue(item, _ => new IdentityBox
        {
            Value = Interlocked.Increment(ref nextId)
        }).Value;
    }
}
