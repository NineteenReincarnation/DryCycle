using System.Collections.Generic;
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

    internal static PlacedObject Resolve(List<PlacedObject> items, int fallbackIndex, long stableId)
    {
        if (items == null) return null;

        if (stableId != 0L)
        {
            for (int i = 0; i < items.Count; i++)
            {
                PlacedObject candidate = items[i];
                if (candidate != null &&
                    Ids.TryGetValue(candidate, out IdentityBox box) &&
                    box.Value == stableId)
                    return candidate;
            }
            return null;
        }

        return fallbackIndex >= 0 && fallbackIndex < items.Count
            ? items[fallbackIndex]
            : null;
    }

    internal static int ResolveIndex(List<PlacedObject> items, int fallbackIndex, long stableId)
    {
        if (items == null) return -1;

        if (stableId != 0L)
        {
            for (int i = 0; i < items.Count; i++)
            {
                PlacedObject candidate = items[i];
                if (candidate != null &&
                    Ids.TryGetValue(candidate, out IdentityBox box) &&
                    box.Value == stableId)
                    return i;
            }
            return -1;
        }

        return fallbackIndex >= 0 && fallbackIndex < items.Count
            ? fallbackIndex
            : -1;
    }
}
