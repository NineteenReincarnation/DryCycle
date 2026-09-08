using System;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal sealed class DB_TravelIntent
{
    internal readonly AbstractCreature Creature;
    internal DB_TravelPurpose Purpose;
    internal string HomeColony;
    internal string DestinationRoom;
    internal DB_WorldRoute Route;
    internal int RouteIndex;
    internal int DepartureDelay;
    internal bool WaitingAtRefuge;
    internal bool Suspended;
    internal string StatusReason;
    internal int ReplanCooldown;
    internal int SameRoomTravelTicks;
    internal int LastObservedRoom = int.MinValue;
    internal int LastObservedFrame = int.MinValue;
    internal int RefugeGoalRefresh;
    internal DB_WeatherEcologySample Hazard;
    internal float RefugeScore;
    internal int RefugeNode;

    internal DB_TravelIntent(
        AbstractCreature creature,
        DB_TravelPurpose purpose,
        string homeColony,
        string destinationRoom,
        in DB_WorldRoute route,
        int departureDelay,
        in DB_WeatherEcologySample hazard,
        float refugeScore = 0f,
        int refugeNode = -1)
    {
        Creature = creature;
        Purpose = purpose;
        HomeColony = Normalize(homeColony);
        DestinationRoom = Normalize(destinationRoom);
        Route = CopyRoute(route);
        RouteIndex = 0;
        DepartureDelay = Mathf.Max(0, departureDelay);
        Hazard = hazard;
        RefugeScore = refugeScore;
        RefugeNode = refugeNode;
        StatusReason = departureDelay > 0 ? "scheduled / staggered departure" : "route committed";
    }


    private static DB_WorldRoute CopyRoute(in DB_WorldRoute route)
    {
        if (!route.Valid) return default;
        int[] rooms = new int[route.Rooms.Length];
        Array.Copy(route.Rooms, rooms, rooms.Length);
        return new DB_WorldRoute(rooms, route.Cost, route.Survivability);
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
