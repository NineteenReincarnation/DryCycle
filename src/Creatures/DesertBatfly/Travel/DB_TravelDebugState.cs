using System;
using System.Collections.Generic;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_TravelDebugState
{
    internal readonly DB_TravelPurpose Purpose;
    internal readonly string HomeColony;
    internal readonly string DestinationRoom;
    internal readonly int RouteIndex;
    internal readonly int[] RouteRooms;
    internal readonly int NextRoom;
    internal readonly int DepartureDelay;
    internal readonly bool WaitingAtRefuge;
    internal readonly bool Suspended;
    internal readonly string StatusReason;
    internal readonly float RouteCost;
    internal readonly float RouteSurvivability;
    internal readonly int RefugeNode;

    internal DB_TravelDebugState(
        DB_TravelPurpose purpose,
        string homeColony,
        string destinationRoom,
        int routeIndex,
        int[] routeRooms,
        int nextRoom,
        int departureDelay,
        bool waitingAtRefuge,
        bool suspended,
        string statusReason,
        float routeCost,
        float routeSurvivability,
        int refugeNode)
    {
        Purpose = purpose;
        HomeColony = homeColony ?? string.Empty;
        DestinationRoom = destinationRoom ?? string.Empty;
        RouteIndex = routeIndex;
        RouteRooms = routeRooms ?? Array.Empty<int>();
        NextRoom = nextRoom;
        DepartureDelay = Mathf.Max(0, departureDelay);
        WaitingAtRefuge = waitingAtRefuge;
        Suspended = suspended;
        StatusReason = statusReason ?? string.Empty;
        RouteCost = routeCost;
        RouteSurvivability = Mathf.Clamp01(routeSurvivability);
        RefugeNode = refugeNode;
    }
}

