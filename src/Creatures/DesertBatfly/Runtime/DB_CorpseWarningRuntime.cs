using System.Collections.Generic;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Owns the realized death-site warning objects created by fear/mortality reactions.
/// Room ownership still drives ordinary Update/Destroy, while this registry guarantees
/// Disable/Reset can actively destroy every surviving transient object instead of merely
/// forgetting how to reach it.
/// </summary>
internal static class DB_CorpseWarningRuntime
{
    private static readonly HashSet<UpdatableAndDeletable> active = new();

    internal static int ActiveCount => active.Count;

    internal static void Register(UpdatableAndDeletable warning)
    {
        if (warning != null)
            active.Add(warning);
    }

    internal static void Unregister(UpdatableAndDeletable warning)
    {
        if (warning != null)
            active.Remove(warning);
    }

    internal static void Reset()
    {
        if (active.Count == 0) return;

        UpdatableAndDeletable[] snapshot = new UpdatableAndDeletable[active.Count];
        active.CopyTo(snapshot);
        active.Clear();

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i]?.Destroy(); }
            catch { }
        }
    }
}
