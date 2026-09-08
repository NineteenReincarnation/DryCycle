using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_TravelPurpose
{
    None,
    EmergencyRefuge,
    ReturnHome,
    ColonyMigration
}

internal readonly struct DB_WorldRoute
{
    internal readonly int[] Rooms;
    internal readonly float Cost;
    internal readonly float Survivability;

    internal bool Valid => Rooms != null && Rooms.Length > 0;
    internal int HopCount => Valid ? Mathf.Max(0, Rooms.Length - 1) : int.MaxValue;

    internal DB_WorldRoute(int[] rooms, float cost, float survivability)
    {
        Rooms = rooms ?? Array.Empty<int>();
        Cost = cost;
        Survivability = Mathf.Clamp01(survivability);
    }
}

/// <summary>
/// Room-graph weighted Dijkstra. This planner never sees room tiles; it chooses only
/// which connected rooms to traverse. Realized movement remains FlyAI/AImap/shortcut work.
/// </summary>
internal static class DB_WorldRoutePlanner
{
    internal const int RefugeMaxHops = 3;
    internal const int MigrationMaxHops = 5;

    internal static bool TryPlan(
        World world,
        int startRoom,
        int destinationRoom,
        CreatureTemplate template,
        DB_TravelPurpose purpose,
        Func<AbstractRoom, float> roomRisk,
        int maxHops,
        out DB_WorldRoute route)
    {
        route = default;
        if (world?.abstractRooms == null || template == null ||
            startRoom < 0 || destinationRoom < 0 ||
            startRoom == destinationRoom)
        {
            if (startRoom == destinationRoom && startRoom >= 0)
                route = new DB_WorldRoute(new[] { startRoom }, 0f, 1f);
            return route.Valid;
        }

        int count = world.abstractRooms.Length;
        if (startRoom >= count || destinationRoom >= count) return false;
        maxHops = Mathf.Clamp(maxHops, 1, 12);

        float[,] best = new float[count, maxHops + 1];
        bool[,] closed = new bool[count, maxHops + 1];
        int[,] parentRoom = new int[count, maxHops + 1];
        int[,] parentHop = new int[count, maxHops + 1];
        for (int r = 0; r < count; r++)
        for (int h = 0; h <= maxHops; h++)
        {
            best[r, h] = float.PositiveInfinity;
            parentRoom[r, h] = -1;
            parentHop[r, h] = -1;
        }
        best[startRoom, 0] = 0f;

        int goalHop = -1;
        for (;;)
        {
            int currentRoom = -1, currentHop = -1;
            float currentCost = float.PositiveInfinity;
            for (int r = 0; r < count; r++)
            for (int h = 0; h <= maxHops; h++)
            {
                if (closed[r, h] || best[r, h] >= currentCost) continue;
                currentCost = best[r, h];
                currentRoom = r;
                currentHop = h;
            }

            if (currentRoom < 0) break;
            if (currentRoom == destinationRoom)
            {
                goalHop = currentHop;
                break;
            }
            closed[currentRoom, currentHop] = true;
            if (currentHop >= maxHops) continue;

            AbstractRoom room = world.GetAbstractRoom(currentRoom);
            if (room?.connections == null) continue;
            for (int connection = 0; connection < room.connections.Length; connection++)
            {
                int next = room.connections[connection];
                if (!CanTraverse(world, currentRoom, next, template)) continue;
                AbstractRoom nextRoom = world.GetAbstractRoom(next);
                if (nextRoom == null) continue;

                float rawRisk = SanitizeRisk(roomRisk?.Invoke(nextRoom) ?? 0f);
                if (rawRisk >= 8f) continue;
                float edge = EdgeCost(purpose, rawRisk);
                int nextHop = currentHop + 1;
                float candidate = currentCost + edge;
                if (candidate >= best[next, nextHop]) continue;
                best[next, nextHop] = candidate;
                parentRoom[next, nextHop] = currentRoom;
                parentHop[next, nextHop] = currentHop;
            }
        }

        if (goalHop < 0) return false;
        List<int> reversed = new(maxHops + 1);
        int roomCursor = destinationRoom;
        int hopCursor = goalHop;
        float worstRisk = 0f;
        while (roomCursor >= 0)
        {
            reversed.Add(roomCursor);
            AbstractRoom r = world.GetAbstractRoom(roomCursor);
            float risk = SanitizeRisk(roomRisk?.Invoke(r) ?? 0f);
            if (risk < 8f) worstRisk = Mathf.Max(worstRisk, risk);
            if (roomCursor == startRoom && hopCursor == 0) break;
            int previousRoom = parentRoom[roomCursor, hopCursor];
            int previousHop = parentHop[roomCursor, hopCursor];
            roomCursor = previousRoom;
            hopCursor = previousHop;
        }
        if (reversed.Count == 0 || reversed[reversed.Count - 1] != startRoom) return false;
        reversed.Reverse();
        route = new DB_WorldRoute(
            reversed.ToArray(), best[destinationRoom, goalHop], Mathf.Clamp01(1f - worstRisk));
        return true;
    }

    /// <summary>
    /// Cheap bounded BFS used to discover candidate rooms before weighted planning.
    /// This avoids invoking Dijkstra once for every room in a region when Refuge range
    /// is only a few hops. The returned list includes startRoom as the first entry.
    /// </summary>
    internal static void CollectReachableRooms(
        World world,
        int startRoom,
        CreatureTemplate template,
        int maxHops,
        List<int> output)
    {
        output?.Clear();
        if (output == null || world?.abstractRooms == null || template == null ||
            startRoom < 0 || startRoom >= world.abstractRooms.Length)
            return;

        maxHops = Mathf.Clamp(maxHops, 0, 12);
        bool[] visited = new bool[world.abstractRooms.Length];
        Queue<int> rooms = new();
        Queue<int> hops = new();
        visited[startRoom] = true;
        rooms.Enqueue(startRoom);
        hops.Enqueue(0);

        while (rooms.Count > 0)
        {
            int current = rooms.Dequeue();
            int hop = hops.Dequeue();
            output.Add(current);
            if (hop >= maxHops) continue;

            AbstractRoom room = world.GetAbstractRoom(current);
            if (room?.connections == null) continue;
            for (int i = 0; i < room.connections.Length; i++)
            {
                int next = room.connections[i];
                if (next < 0 || next >= visited.Length || visited[next] ||
                    !CanTraverse(world, current, next, template))
                    continue;
                visited[next] = true;
                rooms.Enqueue(next);
                hops.Enqueue(hop + 1);
            }
        }
    }

    internal static float EdgeCost(DB_TravelPurpose purpose, float roomRisk)
    {
        roomRisk = Mathf.Max(0f, SanitizeRisk(roomRisk));
        float riskWeight = purpose switch
        {
            DB_TravelPurpose.EmergencyRefuge => 2.60f,
            DB_TravelPurpose.ReturnHome => 1.90f,
            DB_TravelPurpose.ColonyMigration => 1.55f,
            _ => 1.70f
        };
        float distanceWeight = purpose == DB_TravelPurpose.EmergencyRefuge ? 0.82f : 1f;
        return distanceWeight + roomRisk * riskWeight;
    }

    /// <summary>
    /// Runtime route commitment is kept until a room becomes materially dangerous.
    /// Small risk-score changes never cause A/B route oscillation.
    /// </summary>
    internal static bool NeedsSafetyReplan(DB_TravelPurpose purpose, float nextRoomRisk)
    {
        nextRoomRisk = SanitizeRisk(nextRoomRisk);
        if (nextRoomRisk >= 8f) return true;
        float threshold = purpose switch
        {
            DB_TravelPurpose.EmergencyRefuge => 0.76f,
            DB_TravelPurpose.ReturnHome => 0.84f,
            DB_TravelPurpose.ColonyMigration => 0.90f,
            _ => 0.84f
        };
        return nextRoomRisk >= threshold;
    }

    internal static float NormalizedTravelCost(in DB_WorldRoute route, int maxHops)
    {
        if (!route.Valid || float.IsNaN(route.Cost) || float.IsInfinity(route.Cost)) return 1f;
        return Mathf.Clamp01(route.Cost / Mathf.Max(1f, maxHops * 2.5f));
    }

    private static bool CanTraverse(World world, int currentRoom, int nextRoom, CreatureTemplate template)
    {
        if (world?.abstractRooms == null || template == null ||
            currentRoom < 0 || nextRoom < 0 ||
            currentRoom >= world.abstractRooms.Length || nextRoom >= world.abstractRooms.Length)
            return false;

        AbstractRoom current = world.GetAbstractRoom(currentRoom);
        AbstractRoom next = world.GetAbstractRoom(nextRoom);
        if (current == null || next == null) return false;

        WorldCoordinate exit = world.NodeInALeadingToB(currentRoom, nextRoom);
        return exit.abstractNode >= 0 &&
               current.CommonToCreatureSpecificNodeIndex(exit.abstractNode, template) >= 0;
    }

    private static float SanitizeRisk(float value)
    {
        if (float.IsNaN(value) || float.IsPositiveInfinity(value)) return 8f;
        if (float.IsNegativeInfinity(value)) return 0f;
        return Mathf.Max(0f, value);
    }
}
