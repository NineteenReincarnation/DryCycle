using System;
using System.Collections.Generic;
using System.Linq;
using Num = System.Numerics;
using DryCycle.DevUI.DevTool.RWImGui;

internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        try
        {
            StraightAndStaggered();
            ObstacleDetour();
            NearbyRoutesDoNotOvershoot();
            ParallelShortLanes();
            DenseObstacleLanes();
            CacheReuse();

            Console.WriteLine(
                "PASS: " + assertions +
                " assertions; World Map routing keeps short/direct paths, obstacle avoidance, " +
                "parallel lane separation, bounded detours and cache reuse.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void StraightAndStaggered()
    {
        WorldMapOrthogonalRouter.Clear();
        var obstacles = BaseObstacles(144f);
        var request = Request(
            "straight",
            new Num.Vector2(75f, 50f),
            new Num.Vector2(149f, 50f),
            144f,
            0f);

        WorldMapOrthogonalRouter.Route route =
            Solve(new[] { request }, obstacles)[0];

        Check(
            route.Points.All(p => Math.Abs(p.Y - 50f) < 0.01f),
            "Facing aligned sockets must remain a straight line.");
        Check(
            Math.Abs(Length(route.Points) - 74f) < 0.01f,
            "Straight route must use the exact socket-to-socket distance.");

        request = Request(
            "staggered",
            new Num.Vector2(75f, 50f),
            new Num.Vector2(149f, 72f),
            144f,
            0f);
        route = Solve(new[] { request }, obstacles)[0];

        Check(
            route.Points[0] == new Num.Vector2(75f, 50f) &&
            route.Points[route.Points.Length - 1] == new Num.Vector2(149f, 72f),
            "Staggered route must preserve the real socket anchors.");
        Check(
            Math.Abs(Length(route.Points) - 96f) < 0.01f,
            "Staggered nearby sockets must choose a shortest orthogonal corridor.");
        Check(!HasReversal(route.Points), "Staggered route must not reverse direction.");
    }

    private static void ObstacleDetour()
    {
        WorldMapOrthogonalRouter.Clear();
        var obstacles = BaseObstacles(260f);
        obstacles.Add(
            new WorldMapOrthogonalRouter.Obstacle(
                2,
                new Num.Vector2(130f, 20f),
                new Num.Vector2(210f, 100f)));

        var request = Request(
            "blocked",
            new Num.Vector2(75f, 60f),
            new Num.Vector2(265f, 60f),
            260f,
            0f);

        WorldMapOrthogonalRouter.Route route =
            Solve(new[] { request }, obstacles)[0];

        Check(!HasReversal(route.Points), "Blocked route must not reverse toward its source.");
        Check(
            !Crosses(route.Points, new Num.Vector2(130f, 20f), new Num.Vector2(210f, 100f)),
            "Blocked route must not cut through the obstacle room.");
        Check(
            Length(route.Points) < 520f,
            "Blocked route must stay locally bounded instead of taking a screen-spanning detour.");
    }

    private static void NearbyRoutesDoNotOvershoot()
    {
        WorldMapOrthogonalRouter.Clear();

        var request = new WorldMapOrthogonalRouter.Request
        {
            Id = "nearby-left-down",
            StartRoom = 0,
            EndRoom = 1,
            StartRoomMin = new Num.Vector2(200f, 0f),
            StartRoomMax = new Num.Vector2(280f, 100f),
            EndRoomMin = new Num.Vector2(0f, 140f),
            EndRoomMax = new Num.Vector2(80f, 240f),
            Start = new Num.Vector2(205f, 50f),
            End = new Num.Vector2(75f, 190f),
            StartDirection = -Num.Vector2.UnitX,
            EndDirection = Num.Vector2.UnitX,
            LaneOffset = 0f,
            StartTerminalLaneIndex = 0,
            StartTerminalLaneCount = 1,
            EndTerminalLaneIndex = 0,
            EndTerminalLaneCount = 1
        };

        var obstacles = new List<WorldMapOrthogonalRouter.Obstacle>
        {
            new(
                0,
                request.StartRoomMin,
                request.StartRoomMax),
            new(
                1,
                request.EndRoomMin,
                request.EndRoomMax)
        };

        Num.Vector2[] points =
            Solve(new[] { request }, obstacles)[0].Points;

        float minX = points.Min(p => p.X);
        float maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y);
        float maxY = points.Max(p => p.Y);
        float manhattan =
            Math.Abs(request.Start.X - request.End.X) +
            Math.Abs(request.Start.Y - request.End.Y);

        Check(
            minX >= Math.Min(request.Start.X, request.End.X) - 0.01f &&
            maxX <= Math.Max(request.Start.X, request.End.X) + 0.01f,
            "A clear nearby link must not travel horizontally away from both endpoints before coming back.");
        Check(
            minY >= Math.Min(request.Start.Y, request.End.Y) - 0.01f &&
            maxY <= Math.Max(request.Start.Y, request.End.Y) + 0.01f,
            "A clear nearby link must not travel vertically away from both endpoints before coming back.");
        Check(
            Length(points) <= manhattan + 0.01f,
            "A clear nearby link must use a Manhattan-shortest route instead of an unnecessary outer detour.");
    }

    private static void ParallelShortLanes()
    {
        WorldMapOrthogonalRouter.Clear();
        var obstacles = BaseObstacles(144f);
        float[] offsets = { -12f, 0f, 12f };
        var requests = offsets
            .Select((offset, i) =>
                Request(
                    "short-lane-" + i,
                    new Num.Vector2(75f, 50f),
                    new Num.Vector2(149f, 50f),
                    144f,
                    offset))
            .ToArray();

        WorldMapOrthogonalRouter.Route[] routes =
            Solve(requests, obstacles);

        var ys = new List<float>();
        for (int i = 0; i < routes.Length; i++)
        {
            Num.Vector2[] points = routes[i].Points;
            Check(
                points[0] == new Num.Vector2(75f, 50f) &&
                points[points.Length - 1] == new Num.Vector2(149f, 50f),
                "Parallel short routes must retain real socket anchors.");
            Check(!HasReversal(points), "Parallel short routes must never backtrack.");

            float longestHorizontal = -1f;
            float corridorY = points[0].Y;
            for (int p = 0; p + 1 < points.Length; p++)
            {
                if (Math.Abs(points[p].Y - points[p + 1].Y) > 0.01f)
                    continue;

                float length = Math.Abs(points[p + 1].X - points[p].X);
                if (length <= longestHorizontal)
                    continue;

                longestHorizontal = length;
                corridorY = points[p].Y;
            }
            ys.Add(corridorY);
        }

        Check(
            ys.Max() - ys.Min() >= 18f,
            "Parallel short links must preserve visibly separated lane corridors.");
    }

    private static void DenseObstacleLanes()
    {
        WorldMapOrthogonalRouter.Clear();
        var obstacles = BaseObstacles(260f);
        obstacles.Add(
            new WorldMapOrthogonalRouter.Obstacle(
                2,
                new Num.Vector2(130f, 20f),
                new Num.Vector2(210f, 100f)));

        float[] offsets = { -18f, -6f, 6f, 18f };
        var requests = offsets
            .Select((offset, i) =>
                Request(
                    "dense-" + i,
                    new Num.Vector2(75f, 60f),
                    new Num.Vector2(265f, 60f),
                    260f,
                    offset))
            .ToArray();

        WorldMapOrthogonalRouter.Route[] routes =
            Solve(requests, obstacles);

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorldMapOrthogonalRouter.Route route in routes)
        {
            Num.Vector2[] points = route.Points;
            Check(!HasReversal(points), "Dense obstacle routes must not reverse direction.");
            Check(
                !Crosses(points, new Num.Vector2(130f, 20f), new Num.Vector2(210f, 100f)),
                "Dense obstacle routes must not enter the blocking room.");
            Check(
                Length(points) < 520f,
                "Dense obstacle routes must stay inside the bounded local detour envelope.");

            signatures.Add(
                string.Join(
                    ";",
                    points.Select(
                        point =>
                            Math.Round(point.X, 1) + "," +
                            Math.Round(point.Y, 1))));
        }

        Console.WriteLine(
            "dense lanes: " +
            string.Join(
                " | ",
                routes.Select(
                    route =>
                        string.Join(
                            " -> ",
                            route.Points.Select(
                                point =>
                                    "(" + point.X.ToString("F1") + "," +
                                    point.Y.ToString("F1") + ")")))));

        Check(
            signatures.Count >= 3,
            "Four dense parallel connections must retain multiple distinct corridors.");
    }

    private static void CacheReuse()
    {
        WorldMapOrthogonalRouter.Clear();
        var obstacles = BaseObstacles(144f);
        var request = Request(
            "cache",
            new Num.Vector2(75f, 50f),
            new Num.Vector2(149f, 72f),
            144f,
            0f);

        _ = Solve(new[] { request }, obstacles)[0];
        WorldMapOrthogonalRouter.Route second =
            Solve(new[] { request }, obstacles)[0];

        Check(second.Reused, "Stable route must be reused from the router cache.");
    }

    private static WorldMapOrthogonalRouter.Request Request(
        string id,
        Num.Vector2 start,
        Num.Vector2 end,
        float endRoomX,
        float laneOffset)
    {
        return new WorldMapOrthogonalRouter.Request
        {
            Id = id,
            StartRoom = 0,
            EndRoom = 1,
            StartRoomMin = new Num.Vector2(0f, 0f),
            StartRoomMax = new Num.Vector2(80f, 120f),
            EndRoomMin = new Num.Vector2(endRoomX, 0f),
            EndRoomMax = new Num.Vector2(endRoomX + 80f, 120f),
            Start = start,
            End = end,
            StartDirection = Num.Vector2.UnitX,
            EndDirection = -Num.Vector2.UnitX,
            LaneOffset = laneOffset,
            StartTerminalLaneIndex = 0,
            StartTerminalLaneCount = 1,
            StartTerminalExtraDepth = 0f,
            EndTerminalLaneIndex = 0,
            EndTerminalLaneCount = 1,
            EndTerminalExtraDepth = 0f
        };
    }

    private static List<WorldMapOrthogonalRouter.Obstacle> BaseObstacles(
        float endRoomX)
    {
        return new List<WorldMapOrthogonalRouter.Obstacle>
        {
            new(
                0,
                new Num.Vector2(0f, 0f),
                new Num.Vector2(80f, 120f)),
            new(
                1,
                new Num.Vector2(endRoomX, 0f),
                new Num.Vector2(endRoomX + 80f, 120f))
        };
    }

    private static WorldMapOrthogonalRouter.Route[] Solve(
        IReadOnlyList<WorldMapOrthogonalRouter.Request> requests,
        IReadOnlyList<WorldMapOrthogonalRouter.Obstacle> obstacles) =>
        WorldMapOrthogonalRouter.BuildRoutesCore(
            requests,
            obstacles,
            sourceObstaclesAlreadyInflated: false,
            occupancySeedPaths: null,
            avoidanceSeedPaths: null);

    private static bool HasReversal(Num.Vector2[] points)
    {
        for (int i = 1; i + 1 < points.Length; i++)
        {
            Num.Vector2 before = points[i] - points[i - 1];
            Num.Vector2 after = points[i + 1] - points[i];
            if (Num.Vector2.Dot(before, after) < -0.01f)
                return true;
        }
        return false;
    }

    private static bool Crosses(
        Num.Vector2[] points,
        Num.Vector2 min,
        Num.Vector2 max)
    {
        for (int i = 0; i + 1 < points.Length; i++)
        {
            if (SegmentIntersectsRect(points[i], points[i + 1], min, max))
                return true;
        }
        return false;
    }

    private static bool SegmentIntersectsRect(
        Num.Vector2 a,
        Num.Vector2 b,
        Num.Vector2 min,
        Num.Vector2 max)
    {
        if (Math.Abs(a.X - b.X) < 0.01f)
        {
            if (a.X <= min.X || a.X >= max.X) return false;
            return Math.Max(a.Y, b.Y) > min.Y &&
                   Math.Min(a.Y, b.Y) < max.Y;
        }

        if (Math.Abs(a.Y - b.Y) < 0.01f)
        {
            if (a.Y <= min.Y || a.Y >= max.Y) return false;
            return Math.Max(a.X, b.X) > min.X &&
                   Math.Min(a.X, b.X) < max.X;
        }

        // Router output is expected to be orthogonal. Treat any unexpected diagonal conservatively
        // using segment AABB overlap so this test still fails if it enters the blocker.
        Num.Vector2 segMin = Num.Vector2.Min(a, b);
        Num.Vector2 segMax = Num.Vector2.Max(a, b);
        return segMax.X > min.X && segMin.X < max.X &&
               segMax.Y > min.Y && segMin.Y < max.Y;
    }

    private static float Length(Num.Vector2[] points)
    {
        float length = 0f;
        for (int i = 0; i + 1 < points.Length; i++)
            length += Num.Vector2.Distance(points[i], points[i + 1]);
        return length;
    }

    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value)
            throw new InvalidOperationException(message);
    }
}
