using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal sealed partial class WorldMapConnectionResourceStore
{
    private sealed class WorkResult
    {
        internal Dictionary<string, ConnectionRouteResource> Routes;
        internal bool Layout;
        internal WorldMapCorridorLaneApplyResult Lanes;
        internal WorldMapCrossingResolveResult Crossings;
        internal long BuildTicks, LaneTicks, CrossingTicks;
    }
    private Task<WorkResult> routeWork;
    private WorldMapOrthogonalRouter.CacheContext routingContext = new();
    private long workRevision, pendingWorkRevision, pendingRouteRevision;
    private int workGeneration, pendingWorkGeneration;
    private string[] pendingRouteIds = Array.Empty<string>();

    private void StartRouteWork(WorldMapWorldSpaceRouter.BuildInput input)
    {
        pendingWorkGeneration = workGeneration; pendingWorkRevision = workRevision;
        pendingRouteRevision = revision;
        pendingRouteIds = new string[input.Accepted.Count];
        for (int i = 0; i < pendingRouteIds.Length; i++) pendingRouteIds[i] = input.Accepted[i].Id;
        var context = routingContext;
        routeWork = Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            var result = WorldMapWorldSpaceRouter.Execute(input, context);
            return new WorkResult { Routes = result, BuildTicks = Stopwatch.GetTimestamp() - started };
        });
    }

    private void StartLayoutWork()
    {
        if (routeWork != null) return;
        var snapshot = new Dictionary<string, ConnectionRouteResource>(StringComparer.Ordinal);
        foreach (var pair in routes)
        {
            ConnectionRouteResource copy = pair.Value.CloneForWorker();
            endpointDensityTiers.TryGetValue(pair.Key, out copy.BaseDensityTier);
            snapshot.Add(pair.Key, copy);
        }
        var obstacles = new List<WorldMapOrthogonalRouter.Obstacle>(GetRoutingObstacleSnapshot());
        bool applyLanes = corridorLayoutDirty;
        pendingWorkGeneration = workGeneration; pendingWorkRevision = workRevision; pendingRouteRevision = revision;
        pendingRouteIds = Array.Empty<string>();
        routeWork = Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp(), localRevision = 0;
            var lanes = applyLanes ? WorldMapCorridorLaneAllocator.Apply(snapshot, obstacles, new HashSet<string>(), ref localRevision) :
                WorldMapCorridorLaneApplyResult.Empty;
            long laneTicks = Stopwatch.GetTimestamp() - started;
            started = Stopwatch.GetTimestamp();
            var crossings = WorldMapRouteCrossingResolver.Build(snapshot);
            return new WorkResult { Routes = snapshot, Layout = true, Lanes = lanes, Crossings = crossings,
                LaneTicks = laneTicks, CrossingTicks = Stopwatch.GetTimestamp() - started };
        });
    }

    private void DrainRouteWork(WorldMapScene scene)
    {
        if (routeWork == null || !routeWork.IsCompleted) return;
        Task<WorkResult> finished = routeWork; routeWork = null;
        if (finished.IsFaulted)
        {
            global::DryCycle.Plugin.Logger?.LogError("WorldMap background routing failed: " + finished.Exception);
            return;
        }
        WorkResult result = finished.Result;
        if (workGeneration != pendingWorkGeneration) return;
        // Geometry/author edits that happened during a solve must win over its old coordinates.
        if (workRevision != pendingWorkRevision || revision != pendingRouteRevision)
        {
            foreach (string id in pendingRouteIds)
                if (scene?.Connections.ContainsKey(id) == true) Enqueue(id);
            return;
        }
        foreach (var pair in result.Routes)
        {
            pair.Value.Revision = routes.TryGetValue(pair.Key, out var old) ? old.Revision + 1 : 1;
            routes[pair.Key] = pair.Value;
            routeChanged.Add(pair.Key);
        }
        unchecked { revision++; }
        MapRoomGeometryPresentationHub.MarkPersistentFrontendDirty();
        if (result.Layout)
        {
            corridorLayoutDirty = false; crossingLayoutDirty = false;
            crossings = result.Crossings.Marks;
            crossingBudgetLimited = result.Crossings.BudgetLimited; crossingCandidateChecks = result.Crossings.CandidateChecks;
            crossingRevision++;
            corridorPerfCount++; corridorPerfTotalTicks += result.LaneTicks;
            corridorPerfPeakTicks = Math.Max(corridorPerfPeakTicks, result.LaneTicks);
            crossingPerfCount++; crossingPerfTotalTicks += result.CrossingTicks;
            crossingPerfPeakTicks = Math.Max(crossingPerfPeakTicks, result.CrossingTicks);
            if (result.Lanes.HasRerouteCandidates) ScheduleCorridorReroutes(result.Lanes.RerouteRouteIds);
        }
        else
        {
            corridorLayoutDirty = crossingLayoutDirty = true;
            routeBuildPerfTotalTicks += result.BuildTicks;
            routeBuildPerfPeakTicks = Math.Max(routeBuildPerfPeakTicks, result.BuildTicks);
            routeBuildPerfBatches++; routeBuildPerfRoutes += result.Routes.Count;
        }
        UpdateRouteLoadSession();
    }
}
