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
            // Normal command frames keep the snapshot index. Validate that address first so drag
            // updates remain O(1); only a real collection reorder pays the recovery scan.
            if (fallbackIndex >= 0 && fallbackIndex < items.Count)
            {
                PlacedObject fast = items[fallbackIndex];
                if (HasId(fast, stableId))
                    return fast;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (i == fallbackIndex) continue;
                PlacedObject candidate = items[i];
                if (HasId(candidate, stableId))
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
            if (fallbackIndex >= 0 && fallbackIndex < items.Count &&
                HasId(items[fallbackIndex], stableId))
                return fallbackIndex;

            for (int i = 0; i < items.Count; i++)
            {
                if (i == fallbackIndex) continue;
                if (HasId(items[i], stableId))
                    return i;
            }
            return -1;
        }

        return fallbackIndex >= 0 && fallbackIndex < items.Count
            ? fallbackIndex
            : -1;
    }

    private static bool HasId(PlacedObject item, long stableId) =>
        item != null &&
        Ids.TryGetValue(item, out IdentityBox box) &&
        box.Value == stableId;
}
