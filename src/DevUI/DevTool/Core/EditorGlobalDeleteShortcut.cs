using System.Threading;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Thread-safe edge bridge for the global X delete shortcut.
/// Unity input publishes the request once; the currently visible frontend that owns a
/// presentation-only selection (for example World Map or Cartography) consumes that edge.
/// Backend-owned selections such as Objects/Sound/Triggers are handled immediately by
/// EditorInputRouter and do not use this bridge.
/// </summary>
public static class EditorGlobalDeleteShortcut
{
    private static int revision;

    public static int Revision =>
        Volatile.Read(ref revision);

    internal static void Publish() =>
        Interlocked.Increment(ref revision);

    public static bool Consume(ref int observedRevision)
    {
        int current =
            Volatile.Read(ref revision);
        if (current == observedRevision)
            return false;

        observedRevision =
            current;
        return true;
    }

    internal static void Reset() =>
        Volatile.Write(ref revision, 0);
}
