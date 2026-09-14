using System;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Logical data channels for the rebuilt DevTool. A revision changes only when the semantic data
/// consumed by that presentation channel may have changed. Presentation hubs can therefore skip
/// rebuilding immutable snapshots on stable frames without polling every model field.
/// </summary>
internal enum EditorRevisionKind
{
    Shell,
    Objects,
    Room,
    Sound,
    Triggers,
    Map,
    Dialog,
    Relationships,
    Count
}

/// <summary>
/// Main-thread revision clock owned by one EditorSession.
///
/// Revisions are intentionally session-local rather than global. Reopening DevUI creates a fresh
/// session with a fresh set of revisions, so presentation caches can use session identity plus one
/// channel number as a complete invalidation key. Values never use zero: zero is reserved by
/// consumers as the "not observed" sentinel.
/// </summary>
internal sealed class EditorRevisionTracker
{
    private readonly long[] revisions = new long[(int)EditorRevisionKind.Count];

    internal EditorRevisionTracker()
    {
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = 1L;
    }

    internal long Get(EditorRevisionKind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < revisions.Length ? revisions[index] : 0L;
    }

    internal void Mark(EditorRevisionKind kind)
    {
        int index = (int)kind;
        if (index < 0 || index >= revisions.Length)
            return;

        revisions[index] = Next(revisions[index]);
    }

    internal void MarkWorkspace(EditorToolMode mode) => Mark(WorkspaceKind(mode));

    internal void MarkShellAndWorkspace(EditorToolMode mode)
    {
        Mark(EditorRevisionKind.Shell);
        MarkWorkspace(mode);
    }

    internal void MarkAll()
    {
        for (int i = 0; i < revisions.Length; i++)
            revisions[i] = Next(revisions[i]);
    }

    internal static EditorRevisionKind WorkspaceKind(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Objects => EditorRevisionKind.Objects,
        EditorToolMode.Room => EditorRevisionKind.Room,
        EditorToolMode.Sound => EditorRevisionKind.Sound,
        EditorToolMode.Triggers => EditorRevisionKind.Triggers,
        EditorToolMode.Map => EditorRevisionKind.Map,
        EditorToolMode.Dialog => EditorRevisionKind.Dialog,
        EditorToolMode.Relationships => EditorRevisionKind.Relationships,
        _ => EditorRevisionKind.Shell
    };

    private static long Next(long value) => value >= long.MaxValue ? 1L : value + 1L;
}
