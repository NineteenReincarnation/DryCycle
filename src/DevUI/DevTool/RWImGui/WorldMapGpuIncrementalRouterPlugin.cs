using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Incremental map-space routing for the retained GPU World Map.
///
/// The underlying orthogonal router already caches individual paths, but a live room drag still
/// submitted the complete connection set every frame. For large regions that means every retained
/// route repeatedly checks every obstacle and rebuilds occupancy even when one room is the only
/// moving object. This layer keeps a stable route set and submits only routes affected by the moved
/// obstacle while dragging. On mouse release it invalidates the scene topology hash once so the next
/// frame performs a full global settle pass with complete crossing/congestion information.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRetainedOptimizerPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuIncrementalRouterPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.IncrementalRouter";
    public const string PluginName = "DryCycle DevTool GPU World Map Incremental Router";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuIncrementalRouter.Enable(Logger);
    private void Update() => WorldMapGpuIncrementalRouter.Update();
    private void OnDisable() => WorldMapGpuIncrementalRouter.Disable();
}

internal static class WorldMapGpuIncrementalRouter
{
    private const float BoundsPadding = 24f;
    private const float CoordinateToleranceSq = 0.16f;

    private delegate WorldConnectionRouter.Route[] OrigBuildRoutes(
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles);
    private delegate WorldConnectionRouter.Route[] HookBuildRoutes(
        OrigBuildRoutes orig,
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles);

    private sealed class RetainedRoute
    {
        internal int StartRoom;
        internal int EndRoom;
        internal Num.Vector2 Start;
        internal Num.Vector2 End;
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal WorldConnectionRouter.Route Route;
        internal RouteBounds Bounds;
    }

    private readonly struct RouteBounds
    {
        internal RouteBounds(Num.Vector2 min, Num.Vector2 max)
        {
            Min = min;
            Max = max;
        }

        internal Num.Vector2 Min { get; }
        internal Num.Vector2 Max { get; }

        internal bool Intersects(Num.Vector2 min, Num.Vector2 max, float padding) =>
            Max.X >= min.X - padding && Min.X <= max.X + padding &&
            Max.Y >= min.Y - padding && Min.Y <= max.Y + padding;
    }

    private static readonly HookBuildRoutes BuildRoutesHookDelegate = BuildRoutesHook;
    private static readonly Dictionary<string, RetainedRoute> retained = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, WorldConnectionRouter.Obstacle> previousObstacles = new();

    private static ManualLogSource log;
    private static IDisposable buildRoutesHook;
    private static FieldInfo draggingRoomField;
    private static FieldInfo sceneTopologyHashField;
    private static bool enabled;
    private static bool forceFullNext;
    private static int previousDraggingRoom = -1;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo buildRoutes = typeof(WorldConnectionRouter).GetMethod(
                "BuildRoutes",
                flags,
                null,
                new[]
                {
                    typeof(IReadOnlyList<WorldConnectionRouter.Request>),
                    typeof(IReadOnlyList<WorldConnectionRouter.Obstacle>)
                },
                null);
            draggingRoomField = typeof(WorldMapView).GetField("draggingRoom", flags);
            sceneTopologyHashField = typeof(WorldMapGpuScene).GetField("lastTopologyHash", flags);
            if (buildRoutes == null || draggingRoomField == null || sceneTopologyHashField == null)
                throw new MissingMemberException("GPU World Map incremental route targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            buildRoutesHook = constructor.Invoke(new object[] { buildRoutes, BuildRoutesHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("GPU World Map incremental route set enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map incremental routing could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref buildRoutesHook);
        retained.Clear();
        previousObstacles.Clear();
        draggingRoomField = null;
        sceneTopologyHashField = null;
        forceFullNext = false;
        previousDraggingRoom = -1;
        enabled = false;
        log = null;
    }

    internal static void Update()
    {
        if (!enabled) return;
        int dragging = ReadDraggingRoom();
        if (previousDraggingRoom >= 0 && dragging < 0)
        {
            // A drag uses local incremental routing for responsiveness. Make the next retained scene
            // update run one global route pass so lane/crossing costs settle using the final layout.
            forceFullNext = true;
            try { sceneTopologyHashField?.SetValue(null, int.MinValue); }
            catch { }
        }
        previousDraggingRoom = dragging;
    }

    private static WorldConnectionRouter.Route[] BuildRoutesHook(
        OrigBuildRoutes orig,
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
        if (!ShouldOptimize(requests))
            return BuildFullAndStore(orig, requests, obstacles);

        int draggingRoom = ReadDraggingRoom();
        if (forceFullNext || draggingRoom < 0 || TopologyChanged(requests))
        {
            forceFullNext = false;
            return BuildFullAndStore(orig, requests, obstacles);
        }

        Dictionary<int, WorldConnectionRouter.Obstacle> currentObstacles = BuildObstacleIndex(obstacles);
        HashSet<int> changedRooms = FindChangedRooms(currentObstacles);
        if (changedRooms.Count == 0)
        {
            RememberObstacles(currentObstacles);
            return ReuseAll(requests);
        }

        List<int> dirtyIndices = new();
        List<WorldConnectionRouter.Request> dirtyRequests = new();
        for (int i = 0; i < requests.Count; i++)
        {
            WorldConnectionRouter.Request request = requests[i];
            if (request == null || string.IsNullOrEmpty(request.Id) ||
                !retained.TryGetValue(request.Id, out RetainedRoute cached) ||
                RequestChanged(request, cached) ||
                changedRooms.Contains(request.StartRoom) || changedRooms.Contains(request.EndRoom) ||
                RouteTouchesChangedObstacle(cached, changedRooms, currentObstacles))
            {
                dirtyIndices.Add(i);
                dirtyRequests.Add(request);
            }
        }

        if (dirtyRequests.Count == 0)
        {
            RememberObstacles(currentObstacles);
            return ReuseAll(requests);
        }

        // If most of the topology became dirty, a single global route pass is cheaper and gives
        // better congestion/crossing information than many tiny incremental searches.
        if (dirtyRequests.Count * 3 >= requests.Count * 2)
            return BuildFullAndStore(orig, requests, obstacles);

        WorldConnectionRouter.Route[] rebuilt = orig(dirtyRequests, obstacles) ??
                                                 Array.Empty<WorldConnectionRouter.Route>();
        WorldConnectionRouter.Route[] result = new WorldConnectionRouter.Route[requests.Count];
        int dirtyCursor = 0;
        int nextDirty = dirtyIndices.Count > 0 ? dirtyIndices[0] : -1;

        for (int i = 0; i < requests.Count; i++)
        {
            WorldConnectionRouter.Request request = requests[i];
            if (i == nextDirty)
            {
                WorldConnectionRouter.Route route = dirtyCursor < rebuilt.Length
                    ? rebuilt[dirtyCursor]
                    : null;
                if (route == null)
                {
                    // Defensive fallback: a router should return one route per request, but never
                    // let an unexpected partial result erase an otherwise valid retained path.
                    if (request != null && retained.TryGetValue(request.Id ?? string.Empty, out RetainedRoute old))
                        route = old.Route;
                }
                result[i] = route;
                if (request != null && route != null && !string.IsNullOrEmpty(request.Id))
                    retained[request.Id] = Capture(request, route);

                dirtyCursor++;
                nextDirty = dirtyCursor < dirtyIndices.Count ? dirtyIndices[dirtyCursor] : -1;
            }
            else if (request != null && retained.TryGetValue(request.Id ?? string.Empty, out RetainedRoute cached))
            {
                result[i] = cached.Route;
            }
        }

        RememberObstacles(currentObstacles);
        PruneTo(requests);
        return result;
    }

    private static bool ShouldOptimize(IReadOnlyList<WorldConnectionRouter.Request> requests)
    {
        if (!enabled || !WorldMapGpuScene.Ready || requests == null || requests.Count == 0)
            return false;
        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map) return false;

        // GPU route requests are deliberately prefixed so the fallback/legacy screen-space router
        // keeps its original behavior if it is ever re-enabled.
        for (int i = 0; i < requests.Count; i++)
        {
            string id = requests[i]?.Id ?? string.Empty;
            if (!id.StartsWith("gpu:", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static WorldConnectionRouter.Route[] BuildFullAndStore(
        OrigBuildRoutes orig,
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
        WorldConnectionRouter.Route[] routes = orig(requests, obstacles) ?? Array.Empty<WorldConnectionRouter.Route>();
        retained.Clear();
        int count = Math.Min(requests?.Count ?? 0, routes.Length);
        for (int i = 0; i < count; i++)
        {
            WorldConnectionRouter.Request request = requests[i];
            WorldConnectionRouter.Route route = routes[i];
            if (request == null || route == null || string.IsNullOrEmpty(request.Id)) continue;
            retained[request.Id] = Capture(request, route);
        }
        RememberObstacles(BuildObstacleIndex(obstacles));
        return routes;
    }

    private static RetainedRoute Capture(
        WorldConnectionRouter.Request request,
        WorldConnectionRouter.Route route)
    {
        WorldConnectionRouter.Route stable = CloneRoute(route);
        return new RetainedRoute
        {
            StartRoom = request.StartRoom,
            EndRoom = request.EndRoom,
            Start = request.Start,
            End = request.End,
            StartDirection = request.StartDirection,
            EndDirection = request.EndDirection,
            LaneOffset = request.LaneOffset,
            Route = stable,
            Bounds = ComputeBounds(stable?.Points)
        };
    }

    private static WorldConnectionRouter.Route CloneRoute(WorldConnectionRouter.Route route)
    {
        if (route == null) return null;
        return new WorldConnectionRouter.Route
        {
            Id = route.Id,
            Kind = route.Kind,
            Points = route.Points == null ? Array.Empty<Num.Vector2>() : (Num.Vector2[])route.Points.Clone(),
            StartDirection = route.StartDirection,
            EndDirection = route.EndDirection,
            LaneOffset = route.LaneOffset,
            Reused = true
        };
    }

    private static WorldConnectionRouter.Route[] ReuseAll(
        IReadOnlyList<WorldConnectionRouter.Request> requests)
    {
        WorldConnectionRouter.Route[] result = new WorldConnectionRouter.Route[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            string id = requests[i]?.Id ?? string.Empty;
            if (retained.TryGetValue(id, out RetainedRoute cached)) result[i] = cached.Route;
        }
        return result;
    }

    private static bool TopologyChanged(IReadOnlyList<WorldConnectionRouter.Request> requests)
    {
        if (requests == null || requests.Count != retained.Count) return true;
        for (int i = 0; i < requests.Count; i++)
        {
            WorldConnectionRouter.Request request = requests[i];
            if (request == null || string.IsNullOrEmpty(request.Id) || !retained.ContainsKey(request.Id))
                return true;
        }
        return false;
    }

    private static bool RequestChanged(WorldConnectionRouter.Request request, RetainedRoute cached) =>
        cached == null || cached.StartRoom != request.StartRoom || cached.EndRoom != request.EndRoom ||
        Num.Vector2.DistanceSquared(cached.Start, request.Start) > CoordinateToleranceSq ||
        Num.Vector2.DistanceSquared(cached.End, request.End) > CoordinateToleranceSq ||
        Num.Vector2.DistanceSquared(cached.StartDirection, request.StartDirection) > 0.01f ||
        Num.Vector2.DistanceSquared(cached.EndDirection, request.EndDirection) > 0.01f ||
        Math.Abs(cached.LaneOffset - request.LaneOffset) > 0.01f;

    private static Dictionary<int, WorldConnectionRouter.Obstacle> BuildObstacleIndex(
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
        Dictionary<int, WorldConnectionRouter.Obstacle> result = new(obstacles?.Count ?? 0);
        if (obstacles == null) return result;
        for (int i = 0; i < obstacles.Count; i++) result[obstacles[i].RoomIndex] = obstacles[i];
        return result;
    }

    private static HashSet<int> FindChangedRooms(
        Dictionary<int, WorldConnectionRouter.Obstacle> current)
    {
        HashSet<int> changed = new();
        foreach (KeyValuePair<int, WorldConnectionRouter.Obstacle> pair in current)
        {
            if (!previousObstacles.TryGetValue(pair.Key, out WorldConnectionRouter.Obstacle old) ||
                !ObstacleEqual(old, pair.Value))
                changed.Add(pair.Key);
        }
        foreach (int room in previousObstacles.Keys)
            if (!current.ContainsKey(room)) changed.Add(room);
        return changed;
    }

    private static bool RouteTouchesChangedObstacle(
        RetainedRoute route,
        HashSet<int> changedRooms,
        Dictionary<int, WorldConnectionRouter.Obstacle> current)
    {
        foreach (int room in changedRooms)
        {
            if (previousObstacles.TryGetValue(room, out WorldConnectionRouter.Obstacle old) &&
                route.Bounds.Intersects(old.Min, old.Max, BoundsPadding))
                return true;
            if (current.TryGetValue(room, out WorldConnectionRouter.Obstacle next) &&
                route.Bounds.Intersects(next.Min, next.Max, BoundsPadding))
                return true;
        }
        return false;
    }

    private static bool ObstacleEqual(
        WorldConnectionRouter.Obstacle a,
        WorldConnectionRouter.Obstacle b) =>
        Num.Vector2.DistanceSquared(a.Min, b.Min) <= CoordinateToleranceSq &&
        Num.Vector2.DistanceSquared(a.Max, b.Max) <= CoordinateToleranceSq;

    private static RouteBounds ComputeBounds(Num.Vector2[] points)
    {
        if (points == null || points.Length == 0) return new RouteBounds(Num.Vector2.Zero, Num.Vector2.Zero);
        Num.Vector2 min = points[0];
        Num.Vector2 max = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            min = Num.Vector2.Min(min, points[i]);
            max = Num.Vector2.Max(max, points[i]);
        }
        return new RouteBounds(min, max);
    }

    private static void RememberObstacles(Dictionary<int, WorldConnectionRouter.Obstacle> current)
    {
        previousObstacles.Clear();
        foreach (KeyValuePair<int, WorldConnectionRouter.Obstacle> pair in current)
            previousObstacles[pair.Key] = pair.Value;
    }

    private static void PruneTo(IReadOnlyList<WorldConnectionRouter.Request> requests)
    {
        HashSet<string> alive = new(StringComparer.Ordinal);
        for (int i = 0; i < requests.Count; i++)
            if (!string.IsNullOrEmpty(requests[i]?.Id)) alive.Add(requests[i].Id);
        if (alive.Count == retained.Count) return;
        List<string> stale = new();
        foreach (string id in retained.Keys) if (!alive.Contains(id)) stale.Add(id);
        for (int i = 0; i < stale.Count; i++) retained.Remove(stale[i]);
    }

    private static int ReadDraggingRoom()
    {
        try { return draggingRoomField?.GetValue(null) is int value ? value : -1; }
        catch { return -1; }
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
