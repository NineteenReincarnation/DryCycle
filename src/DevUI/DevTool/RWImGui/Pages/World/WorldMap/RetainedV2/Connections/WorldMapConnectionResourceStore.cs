using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retained connection-route owner. Route invalidation follows topology/room dependencies, never
/// pan/zoom. Only affected routes are rebuilt after room movement; topology changes rebuild the
/// dependency/lane index and route set.
/// </summary>
internal sealed class WorldMapConnectionResourceStore
{
    private const int IdleRoutesPerFrame = 24;
    private const int InteractiveRoutesPerFrame = 8;
    private const float LaneSpacing = 8f;
    private const float MaxLaneOffset = 24f;

    private readonly Dictionary<string, ConnectionRouteResource> routes =
        new(StringComparer.Ordinal);
    private readonly ConnectionDependencyIndex dependencies = new();
    private readonly Dictionary<string, float> laneOffsets =
        new(StringComparer.Ordinal);
    private readonly Queue<string> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly HashSet<string> routeChanged = new(StringComparer.Ordinal);
    private readonly List<WorldMapScene.ConnectionNode> buildBatch = new();
    private long revision;

    internal IReadOnlyDictionary<string, ConnectionRouteResource> Routes => routes;
    internal int Count => routes.Count;
    internal int PendingCount => queue.Count;
    internal long Revision => revision;


    internal bool TryGet(string id, out ConnectionRouteResource route)
    {
        route = null;
        return !string.IsNullOrEmpty(id) && routes.TryGetValue(id, out route);
    }

    internal bool HasCompleteRoutes(WorldMapScene scene)
    {
        if (scene == null) return false;
        if (scene.Connections.Count == 0) return true;
        if (routes.Count < scene.Connections.Count) return false;

        foreach (string id in scene.Connections.Keys)
        {
            if (!routes.TryGetValue(id, out ConnectionRouteResource route) ||
                route?.Points == null ||
                route.Points.Length < 2)
                return false;
        }

        return true;
    }

    internal void DrainRouteChanges(List<string> output)
    {
        if (output == null) return;
        output.Clear();
        if (routeChanged.Count == 0) return;
        output.AddRange(routeChanged);
        routeChanged.Clear();
    }

    internal void ApplyDirty(WorldMapScene scene, WorldMapDirtySet dirty)
    {
        if (scene == null || dirty == null) return;

        foreach (string id in dirty.RemovedConnections)
        {
            if (routes.Remove(id))
            {
                routeChanged.Add(id);
                unchecked { revision++; }
            }
            queued.Remove(id);
        }

        if (dirty.FullRebuild || dirty.TopologyChanged)
        {
            dependencies.Rebuild(scene);
            RebuildLaneOffsets(scene);
            EnqueueAll(scene);
            return;
        }

        foreach (string id in dirty.Connections)
            Enqueue(id);

        InvalidateRooms(dirty.RoomTransforms);
        InvalidateRooms(dirty.RoomPorts);
        InvalidateRooms(dirty.RemovedRooms);
    }

    internal void InvalidateRooms(IEnumerable<int> roomIndices)
    {
        if (roomIndices == null) return;
        foreach (int roomIndex in roomIndices)
        {
            foreach (string id in dependencies.GetForRoom(roomIndex))
                Enqueue(id);
        }
    }

    internal void Update(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        if (scene == null || queue.Count == 0) return;

        int budget = WorldMapBackgroundBudget.InteractionActive
            ? InteractiveRoutesPerFrame
            : IdleRoutesPerFrame;

        buildBatch.Clear();
        while (budget > 0 && queue.Count > 0)
        {
            string id = queue.Dequeue();
            queued.Remove(id);
            if (!scene.TryGetConnection(id, out WorldMapScene.ConnectionNode connection))
            {
                routes.Remove(id);
                continue;
            }

            buildBatch.Add(connection);
            budget--;
        }

        if (buildBatch.Count == 0) return;

        Dictionary<string, ConnectionRouteResource> rebuilt =
            WorldMapWorldSpaceRouter.Build(
                scene,
                roomResources,
                buildBatch,
                laneOffsets);

        for (int i = 0; i < buildBatch.Count; i++)
        {
            WorldMapScene.ConnectionNode connection = buildBatch[i];
            if (!rebuilt.TryGetValue(connection.Id, out ConnectionRouteResource route))
                continue;

            if (routes.TryGetValue(connection.Id, out ConnectionRouteResource previous))
                route.Revision = previous.Revision + 1L;
            else
                route.Revision = 1L;

            routes[connection.Id] = route;
            routeChanged.Add(connection.Id);
            unchecked { revision++; }
        }
    }

    internal void Reset()
    {
        routes.Clear();
        dependencies.Reset();
        laneOffsets.Clear();
        queue.Clear();
        queued.Clear();
        routeChanged.Clear();
        buildBatch.Clear();
        WorldMapOrthogonalRouter.Clear();
        revision = 0L;
    }

    private void EnqueueAll(WorldMapScene scene)
    {
        foreach (string id in scene.Connections.Keys)
            Enqueue(id);

        List<string> stale = null;
        foreach (string id in routes.Keys)
        {
            if (scene.Connections.ContainsKey(id)) continue;
            stale ??= new List<string>();
            stale.Add(id);
        }

        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++)
        {
            if (routes.Remove(stale[i]))
            {
                routeChanged.Add(stale[i]);
                unchecked { revision++; }
            }
        }
    }

    private void Enqueue(string id)
    {
        if (string.IsNullOrEmpty(id) || !queued.Add(id)) return;
        queue.Enqueue(id);
    }

    private void RebuildLaneOffsets(WorldMapScene scene)
    {
        laneOffsets.Clear();
        Dictionary<long, List<string>> groups = new();

        foreach (WorldMapScene.ConnectionNode connection in scene.Connections.Values)
        {
            int a = Math.Min(connection.FromRoomIndex, connection.ToRoomIndex);
            int b = Math.Max(connection.FromRoomIndex, connection.ToRoomIndex);
            long key = ((long)(uint)a << 32) | (uint)b;
            if (!groups.TryGetValue(key, out List<string> ids))
            {
                ids = new List<string>();
                groups.Add(key, ids);
            }
            ids.Add(connection.Id);
        }

        foreach (List<string> ids in groups.Values)
        {
            ids.Sort(StringComparer.Ordinal);
            float center = (ids.Count - 1) * 0.5f;
            for (int i = 0; i < ids.Count; i++)
            {
                float offset = (i - center) * LaneSpacing;
                if (offset < -MaxLaneOffset) offset = -MaxLaneOffset;
                if (offset > MaxLaneOffset) offset = MaxLaneOffset;
                laneOffsets[ids[i]] = offset;
            }
        }
    }
}
