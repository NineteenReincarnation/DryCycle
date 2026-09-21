using System.Collections.Concurrent;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Render-thread -> Unity-main-thread delta channel.
/// </summary>
internal sealed class WorldMapSceneTransfer
{
    private readonly ConcurrentQueue<WorldMapSceneDelta> queue = new();

    internal int PendingCount => queue.Count;

    internal void Publish(WorldMapSceneDelta delta)
    {
        if (delta == null || delta.IsEmpty) return;
        queue.Enqueue(delta);
    }

    internal void Drain(WorldMapScene target, WorldMapDirtySet aggregate)
    {
        if (target == null || aggregate == null) return;
        while (queue.TryDequeue(out WorldMapSceneDelta delta))
            delta.Apply(target, aggregate);
    }

    internal void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
