using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DesertBatflyTravelPurpose
{
    None,
    EmergencyRefuge,
    ReturnHome,
    ColonyMigration
}

internal readonly struct DesertBatflyWorldRoute
{
    internal readonly int[] Rooms;
    internal readonly float Cost;
    internal readonly float Survivability;

    internal bool Valid => Rooms != null && Rooms.Length > 0;
    internal int HopCount => Valid ? Mathf.Max(0, Rooms.Length - 1) : int.MaxValue;

    internal DesertBatflyWorldRoute(int[] rooms, float cost, float survivability)
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
internal static class DesertBatflyWorldRoutePlanner
{
    internal const int RefugeMaxHops = 3;
    internal const int MigrationMaxHops = 5;

    internal static bool TryPlan(
        World world,
        int startRoom,
        int destinationRoom,
        CreatureTemplate template,
        DesertBatflyTravelPurpose purpose,
        Func<AbstractRoom, float> roomRisk,
        int maxHops,
        out DesertBatflyWorldRoute route)
    {
        route = default;
        if (world?.abstractRooms == null || template == null ||
            startRoom < 0 || destinationRoom < 0 ||
            startRoom == destinationRoom)
        {
            if (startRoom == destinationRoom && startRoom >= 0)
                route = new DesertBatflyWorldRoute(new[] { startRoom }, 0f, 1f);
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
                if (next < 0 || next >= count) continue;

                // Match vanilla FlyAI.LeaveRoom exactly: the creature-specific path map
                // is keyed by the real common abstract node that leads from current to
                // next, not by the position of that connection in room.connections[].
                WorldCoordinate exit = world.NodeInALeadingToB(currentRoom, next);
                if (exit.abstractNode < 0 ||
                    room.CommonToCreatureSpecificNodeIndex(exit.abstractNode, template) < 0)
                    continue;

                AbstractRoom nextRoom = world.GetAbstractRoom(next);
                if (nextRoom == null) continue;

                float rawRisk = roomRisk?.Invoke(nextRoom) ?? 0f;
                if (float.IsNaN(rawRisk) || float.IsPositiveInfinity(rawRisk) || rawRisk >= 8f)
                    continue;
                rawRisk = Mathf.Max(0f, rawRisk);
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
            float risk = roomRisk?.Invoke(r) ?? 0f;
            if (!float.IsNaN(risk) && !float.IsInfinity(risk)) worstRisk = Mathf.Max(worstRisk, risk);
            if (roomCursor == startRoom && hopCursor == 0) break;
            int previousRoom = parentRoom[roomCursor, hopCursor];
            int previousHop = parentHop[roomCursor, hopCursor];
            roomCursor = previousRoom;
            hopCursor = previousHop;
        }
        if (reversed.Count == 0 || reversed[reversed.Count - 1] != startRoom) return false;
        reversed.Reverse();
        route = new DesertBatflyWorldRoute(
            reversed.ToArray(), best[destinationRoom, goalHop], Mathf.Clamp01(1f - worstRisk));
        return true;
    }

    internal static float EdgeCost(DesertBatflyTravelPurpose purpose, float roomRisk)
    {
        roomRisk = Mathf.Max(0f, roomRisk);
        float riskWeight = purpose switch
        {
            DesertBatflyTravelPurpose.EmergencyRefuge => 2.60f,
            DesertBatflyTravelPurpose.ReturnHome => 1.90f,
            DesertBatflyTravelPurpose.ColonyMigration => 1.55f,
            _ => 1.70f
        };
        float distanceWeight = purpose == DesertBatflyTravelPurpose.EmergencyRefuge ? 0.82f : 1f;
        return distanceWeight + roomRisk * riskWeight;
    }

    internal static float NormalizedTravelCost(in DesertBatflyWorldRoute route, int maxHops)
    {
        if (!route.Valid || float.IsNaN(route.Cost) || float.IsInfinity(route.Cost)) return 1f;
        return Mathf.Clamp01(route.Cost / Mathf.Max(1f, maxHops * 2.5f));
    }
}
