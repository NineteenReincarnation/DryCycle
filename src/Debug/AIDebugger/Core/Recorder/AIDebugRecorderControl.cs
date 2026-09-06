using System.Collections.Concurrent;

namespace DryCycle.Debugging.AI;

internal enum AIDebugRecorderControlKind : byte
{
    TogglePin
}

internal readonly struct AIDebugRecorderControlRequest
{
    internal readonly AIDebugRecorderControlKind Kind;
    internal readonly DebugEntityKey Key;

    internal AIDebugRecorderControlRequest(AIDebugRecorderControlKind kind, DebugEntityKey key)
    {
        Kind = kind;
        Key = key;
    }
}

// Present-thread -> Unity/main-thread control boundary for recorder-only operations.
// The queue is intentionally separate from AIDebugPresentationHub: recorder controls can
// be consumed by the recorder without touching Rain World from the RWImGUI Present thread.
internal static class AIDebugRecorderControl
{
    private static readonly ConcurrentQueue<AIDebugRecorderControlRequest> Requests = new();

    internal static void RequestTogglePin(DebugEntityKey key) =>
        Requests.Enqueue(new AIDebugRecorderControlRequest(AIDebugRecorderControlKind.TogglePin, key));

    internal static bool TryDequeue(out AIDebugRecorderControlRequest request) =>
        Requests.TryDequeue(out request);

    internal static void Clear()
    {
        while (Requests.TryDequeue(out _)) { }
    }
}
