using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DesertBatflyTravelDebugState
{
    internal readonly DesertBatflyTravelPurpose Purpose;
    internal readonly string HomeColony;
    internal readonly string DestinationRoom;
    internal readonly int RouteIndex;
    internal readonly int[] RouteRooms;
    internal readonly int NextRoom;
    internal readonly int DepartureDelay;
    internal readonly bool WaitingAtRefuge;

    internal DesertBatflyTravelDebugState(
        DesertBatflyTravelPurpose purpose,
        string homeColony,
        string destinationRoom,
        int routeIndex,
        int[] routeRooms,
        int nextRoom,
        int departureDelay,
        bool waitingAtRefuge)
    {
        Purpose = purpose;
        HomeColony = homeColony ?? string.Empty;
        DestinationRoom = destinationRoom ?? string.Empty;
        RouteIndex = routeIndex;
        RouteRooms = routeRooms ?? Array.Empty<int>();
        NextRoom = nextRoom;
        DepartureDelay = Mathf.Max(0, departureDelay);
        WaitingAtRefuge = waitingAtRefuge;
    }
}

/// <summary>
/// High-level cross-room travel state for Task 09. This never replaces FlyAI's
/// room-local locomotion: realized bats are pointed at the next room via LeaveRoom,
/// while unrealized bats advance one abstract room at a time with AbstractCreature.Move.
/// </summary>
internal static class DesertBatflyTravelNavigation
{
    private sealed class TravelIntent
    {
        internal readonly AbstractCreature Creature;
        internal DesertBatflyTravelPurpose Purpose;
        internal string HomeColony;
        internal string DestinationRoom;
        internal DesertBatflyWorldRoute Route;
        internal int RouteIndex;
        internal int DepartureDelay;
        internal bool WaitingAtRefuge;

        internal TravelIntent(
            AbstractCreature creature,
            DesertBatflyTravelPurpose purpose,
            string homeColony,
            string destinationRoom,
            in DesertBatflyWorldRoute route,
            int departureDelay)
        {
            Creature = creature;
            Purpose = purpose;
            HomeColony = Normalize(homeColony);
            DestinationRoom = Normalize(destinationRoom);
            Route = CopyRoute(route);
            RouteIndex = 0;
            DepartureDelay = Mathf.Max(0, departureDelay);
        }
    }

    private static Dictionary<string, TravelIntent> intents =
        new(StringComparer.Ordinal);
    private static HashSet<string> activeEvacuations =
        new(StringComparer.OrdinalIgnoreCase);
    private static WeakReference activeWorld;

    internal static void Reset()
    {
        intents = new Dictionary<string, TravelIntent>(StringComparer.Ordinal);
        activeEvacuations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        activeWorld = null;
    }

    internal static void OnWorldChanged(World world)
    {
        if (world == null)
        {
            Reset();
            return;
        }

        if (activeWorld != null && activeWorld.IsAlive &&
            ReferenceEquals(activeWorld.Target, world))
            return;

        activeWorld = new WeakReference(world);
        intents.Clear();
        activeEvacuations.Clear();
        RestorePendingMigrations(world);
    }

    internal static bool HasIntent(AbstractCreature creature) =>
        creature != null && intents.ContainsKey(Key(creature.ID));

    internal static bool TryGetDebugState(AbstractCreature creature, out DesertBatflyTravelDebugState state)
    {
        state = default;
        if (creature == null || !intents.TryGetValue(Key(creature.ID), out TravelIntent intent))
            return false;

        int[] routeRooms = intent.Route.Valid ? new int[intent.Route.Rooms.Length] : Array.Empty<int>();
        if (routeRooms.Length > 0) Array.Copy(intent.Route.Rooms, routeRooms, routeRooms.Length);
        int next = -1;
        if (intent.Route.Valid && intent.RouteIndex + 1 < intent.Route.Rooms.Length)
            next = intent.Route.Rooms[intent.RouteIndex + 1];

        state = new DesertBatflyTravelDebugState(
            intent.Purpose,
            intent.HomeColony,
            intent.DestinationRoom,
            intent.RouteIndex,
            routeRooms,
            next,
            intent.DepartureDelay,
            intent.WaitingAtRefuge);
        return true;
    }

    internal static void RequestPermanentMigration(
        AbstractCreature creature,
        string destinationRoom,
        in DesertBatflyWorldRoute route,
        int departureDelay)
    {
        if (!ValidCreature(creature) || string.IsNullOrWhiteSpace(destinationRoom) || !route.Valid)
            return;

        DesertBatflyColonyRuntime.EnsureIndividualOwnership(creature);
        DesertBatflyColonyRuntime.IndividualRecord record =
            DesertBatflyColonyRuntime.RecordFor(creature);

        intents[Key(creature.ID)] = new TravelIntent(
            creature,
            DesertBatflyTravelPurpose.ColonyMigration,
            record?.CurrentColony,
            destinationRoom,
            route,
            departureDelay);
    }

    internal static void UpdateAbstractWorld(World world)
    {
        if (world == null || intents.Count == 0) return;
        OnWorldChangedIfNeeded(world);

        List<string> keys = new(intents.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!intents.TryGetValue(keys[i], out TravelIntent intent)) continue;
            AbstractCreature creature = intent.Creature;
            if (!ValidCreature(creature) || creature.world != world)
            {
                intents.Remove(keys[i]);
                continue;
            }

            if (intent.DepartureDelay > 0)
            {
                intent.DepartureDelay = Mathf.Max(0, intent.DepartureDelay - 120);
                continue;
            }

            if (creature.realizedCreature != null || creature.InDen)
                continue;

            if (intent.Purpose == DesertBatflyTravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
                continue;

            int currentRoom = creature.pos.room;
            if (ReachedDestination(intent, currentRoom))
            {
                HandleArrival(world, intent);
                continue;
            }

            if (!TrySynchronizeRouteIndex(intent, currentRoom) &&
                !TryReplan(world, intent, currentRoom))
                continue;

            if (intent.RouteIndex + 1 >= intent.Route.Rooms.Length)
            {
                HandleArrival(world, intent);
                continue;
            }

            int nextRoomIndex = intent.Route.Rooms[intent.RouteIndex + 1];
            AbstractRoom nextRoom = world.GetAbstractRoom(nextRoomIndex);
            AbstractRoom current = world.GetAbstractRoom(currentRoom);
            if (nextRoom == null || current == null) continue;

            int arrivalNode = world.NodeInALeadingToB(nextRoom, current).abstractNode;
            if (arrivalNode < 0)
                arrivalNode = nextRoom.RandomRelevantNode(creature.creatureTemplate);
            if (arrivalNode < 0) continue;

            creature.Move(new WorldCoordinate(nextRoomIndex, -1, -1, arrivalNode));
            intent.RouteIndex++;

            if (ReachedDestination(intent, creature.pos.room))
                HandleArrival(world, intent);
        }
    }

    /// <summary>
    /// Called from the realized FlyAI hook. Returns true only when travel currently
    /// owns the cross-room destination. Immediate danger/injury may suspend travel
    /// without deleting the route.
    /// </summary>
    internal static bool TryDriveRealized(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null || bat.AI == null || bat.room == null ||
            bat.dead || !bat.Consious || bat.inShortcut)
            return false;

        if (!intents.TryGetValue(Key(bat.abstractCreature.ID), out TravelIntent intent))
            return false;

        if (intent.Purpose == DesertBatflyTravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
            return false;

        if (intent.DepartureDelay > 0)
        {
            intent.DepartureDelay--;
            return false;
        }

        if (bat.DesertAI.HasImmediateDanger || bat.Injury.IsSeverelyInjured ||
            bat.Injury.IsRecovering || RestrainedByNonFly(bat))
            return false;

        World world = bat.abstractCreature.world;
        int currentRoom = bat.room.abstractRoom.index;
        if (ReachedDestination(intent, currentRoom))
        {
            HandleArrival(world, intent);
            return false;
        }

        if (!TrySynchronizeRouteIndex(intent, currentRoom) &&
            !TryReplan(world, intent, currentRoom))
            return false;

        if (intent.RouteIndex + 1 >= intent.Route.Rooms.Length)
            return false;

        int nextRoom = intent.Route.Rooms[intent.RouteIndex + 1];
        if (world.GetAbstractRoom(nextRoom) == null) return false;

        bat.AI.LeaveRoom(new WorldCoordinate(nextRoom, -1, -1, -1));
        bat.AI.afraid = Mathf.Max(
            bat.AI.afraid,
            intent.Purpose == DesertBatflyTravelPurpose.EmergencyRefuge ? 1.65f : 0.75f);
        return true;
    }

    internal static void EvaluateColonyWeather(World world)
    {
        if (world?.region == null || world.abstractRooms == null) return;
        OnWorldChangedIfNeeded(world);

        CreatureTemplate template =
            StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        if (template == null) return;

        List<DesertBatflyColonyState> colonies = new();
        foreach (DesertBatflyColonyState colony in DesertBatflyColonyRuntime.Colonies)
        {
            if (colony != null && string.Equals(
                    colony.RegionName,
                    world.region.name,
                    StringComparison.OrdinalIgnoreCase))
                colonies.Add(colony);
        }

        for (int c = 0; c < colonies.Count; c++)
        {
            DesertBatflyColonyState colony = colonies[c];
            AbstractRoom home = DesertBatflyColonyRuntime.FindRoom(world, colony.RoomName);
            if (home == null) continue;

            DesertBatflyWeatherEcologySample hazard =
                DesertBatflyWeatherEcology.Sample(world, home);
            string evacuationKey = colony.RoomName + "|" + hazard.HazardKind + "|" + hazard.HazardId;

            if (!hazard.HasHazard || hazard.ShelterUrgency < 0.50f)
            {
                EndEvacuationAndReturn(world, colony.RoomName, template);
                RemoveEvacuationKeys(colony.RoomName);
                continue;
            }

            float homeQuality = DesertBatflyRefuge.HomeHiveShelterQuality(
                home, hazard.HazardKind, hazard.HazardId);
            if (homeQuality >= Mathf.Max(0.72f, hazard.ShelterUrgency + 0.05f))
            {
                EndEvacuationAndReturn(world, colony.RoomName, template);
                RemoveEvacuationKeys(colony.RoomName);
                continue;
            }

            if (activeEvacuations.Contains(evacuationKey)) continue;

            if (!DesertBatflyRefuge.TryFindEmergencyRefuge(
                    world,
                    home,
                    template,
                    hazard,
                    1f,
                    colony.LastSuccessfulRefuge,
                    DesertBatflyColonyRuntime.PredatorRisk,
                    DesertBatflyColonyRuntime.RefugeCrowding,
                    out DesertBatflyRefugeTarget refuge))
                continue;

            AbstractRoom refugeRoom = world.GetAbstractRoom(refuge.RoomIndex);
            if (refugeRoom == null) continue;

            int started = 0;
            for (int r = 0; r < world.abstractRooms.Length; r++)
            {
                AbstractRoom room = world.abstractRooms[r];
                if (room?.creatures == null) continue;
                for (int i = 0; i < room.creatures.Count; i++)
                {
                    AbstractCreature creature = room.creatures[i];
                    if (!ValidCreature(creature)) continue;
                    DesertBatflyColonyRuntime.EnsureIndividualOwnership(creature);
                    DesertBatflyColonyRuntime.IndividualRecord record =
                        DesertBatflyColonyRuntime.RecordFor(creature, false);
                    if (record == null || !string.Equals(
                            record.CurrentColony,
                            colony.RoomName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(record.PendingMigrationColony))
                        continue;
                    if (intents.TryGetValue(Key(creature.ID), out TravelIntent existing) &&
                        existing.Purpose == DesertBatflyTravelPurpose.ColonyMigration)
                        continue;
                    if (creature.state is not DesertBatflyState state)
                        continue;

                    float capability = PhysicalCapability(state);
                    bool severe = state.WingMean >= 0.60f ||
                                  Mathf.Max(state.LeftWingInjury, state.RightWingInjury) >= 0.82f ||
                                  capability < 0.48f;
                    if (severe)
                        continue;

                    int personalTravelTicks = DesertBatflyRefuge.EstimateTravelTicks(
                        refuge.Route, capability);
                    if (!DesertBatflyRefuge.CanLeaveBeforeDanger(hazard, personalTravelTicks))
                        continue;

                    int staggerSeed = BondDepartureSeed(creature.ID, state);
                    int stagger = 20 + StableInt(staggerSeed ^ refuge.RoomIndex, 0, 220);
                    intents[Key(creature.ID)] = new TravelIntent(
                        creature,
                        DesertBatflyTravelPurpose.EmergencyRefuge,
                        colony.RoomName,
                        refugeRoom.name,
                        refuge.Route,
                        stagger);
                    started++;
                }
            }

            if (started > 0)
            {
                activeEvacuations.Add(evacuationKey);
                float failure = Mathf.Clamp01(
                    hazard.ShelterUrgency * (1f - homeQuality));
                DesertBatflyColonyRuntime.ReportExternalRefuge(
                    colony.RoomName, refugeRoom.name, failure);
            }
        }
    }

    private static void EndEvacuationAndReturn(
        World world,
        string homeColony,
        CreatureTemplate template)
    {
        AbstractRoom home = DesertBatflyColonyRuntime.FindRoom(world, homeColony);
        if (home == null) return;

        // First convert live emergency intents into ordinary ReturnHome travel.
        List<string> keys = new(intents.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!intents.TryGetValue(keys[i], out TravelIntent intent) ||
                intent.Purpose != DesertBatflyTravelPurpose.EmergencyRefuge ||
                !string.Equals(intent.HomeColony, homeColony, StringComparison.OrdinalIgnoreCase))
                continue;

            AbstractCreature creature = intent.Creature;
            if (!ValidCreature(creature))
            {
                intents.Remove(keys[i]);
                continue;
            }

            if (creature.pos.room == home.index)
            {
                intents.Remove(keys[i]);
                continue;
            }

            if (!TryBuildReturnRoute(world, creature, home, template, out DesertBatflyWorldRoute route))
                continue;

            intent.Purpose = DesertBatflyTravelPurpose.ReturnHome;
            intent.DestinationRoom = home.name;
            intent.Route = CopyRoute(route);
            intent.RouteIndex = 0;
            intent.DepartureDelay = 20 + StableInt(creature.ID.RandomSeed ^ 0x4D31, 0, 180);
            intent.WaitingAtRefuge = false;
        }

        // A save/reload intentionally does not serialize temporary refuge intents. If an
        // owned bat is physically outside its Home Colony after the hazard has ended,
        // reconstruct ReturnHome from the actual current room rather than teleporting it.
        for (int r = 0; r < world.abstractRooms.Length; r++)
        {
            AbstractRoom room = world.abstractRooms[r];
            if (room?.creatures == null) continue;
            for (int i = 0; i < room.creatures.Count; i++)
            {
                AbstractCreature creature = room.creatures[i];
                if (!ValidCreature(creature) || creature.pos.room == home.index) continue;
                DesertBatflyColonyRuntime.IndividualRecord record =
                    DesertBatflyColonyRuntime.RecordFor(creature, false);
                if (record == null ||
                    !string.Equals(record.CurrentColony, homeColony, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(record.PendingMigrationColony))
                    continue;

                if (intents.TryGetValue(Key(creature.ID), out TravelIntent existing))
                {
                    if (existing.Purpose is DesertBatflyTravelPurpose.ColonyMigration or
                        DesertBatflyTravelPurpose.ReturnHome)
                        continue;
                }

                if (!TryBuildReturnRoute(world, creature, home, template, out DesertBatflyWorldRoute route))
                    continue;

                intents[Key(creature.ID)] = new TravelIntent(
                    creature,
                    DesertBatflyTravelPurpose.ReturnHome,
                    homeColony,
                    home.name,
                    route,
                    20 + StableInt(creature.ID.RandomSeed ^ 0x4D31, 0, 180));
            }
        }
    }

    private static bool TryBuildReturnRoute(
        World world,
        AbstractCreature creature,
        AbstractRoom home,
        CreatureTemplate template,
        out DesertBatflyWorldRoute route)
    {
        route = default;
        return creature != null && home != null &&
               DesertBatflyWorldRoutePlanner.TryPlan(
                   world,
                   creature.pos.room,
                   home.index,
                   template,
                   DesertBatflyTravelPurpose.ReturnHome,
                   room => CurrentRouteRisk(world, room),
                   DesertBatflyWorldRoutePlanner.MigrationMaxHops,
                   out route);
    }

    private static void RestorePendingMigrations(World world)
    {
        if (world?.abstractRooms == null) return;
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        if (template == null) return;

        for (int r = 0; r < world.abstractRooms.Length; r++)
        {
            AbstractRoom room = world.abstractRooms[r];
            if (room?.creatures == null) continue;
            for (int i = 0; i < room.creatures.Count; i++)
            {
                AbstractCreature creature = room.creatures[i];
                if (!ValidCreature(creature)) continue;
                DesertBatflyColonyRuntime.IndividualRecord record =
                    DesertBatflyColonyRuntime.RecordFor(creature, false);
                if (record == null || string.IsNullOrWhiteSpace(record.PendingMigrationColony))
                    continue;

                AbstractRoom destination = DesertBatflyColonyRuntime.FindRoom(
                    world, record.PendingMigrationColony);
                if (destination == null || !DesertSwarmRoom.IsDesertSwarmRoom(destination))
                    continue;

                if (creature.pos.room == destination.index)
                {
                    DesertBatflyColonyRuntime.CompletePermanentMigration(
                        creature,
                        destination.name,
                        DesertBatflyColonyRuntime.CurrentCycle(world));
                    continue;
                }

                if (!DesertBatflyWorldRoutePlanner.TryPlan(
                        world,
                        creature.pos.room,
                        destination.index,
                        template,
                        DesertBatflyTravelPurpose.ColonyMigration,
                        roomRisk: roomCandidate => CurrentRouteRisk(world, roomCandidate),
                        maxHops: DesertBatflyWorldRoutePlanner.MigrationMaxHops,
                        out DesertBatflyWorldRoute route))
                    continue;

                intents[Key(creature.ID)] = new TravelIntent(
                    creature,
                    DesertBatflyTravelPurpose.ColonyMigration,
                    record.CurrentColony,
                    destination.name,
                    route,
                    StableInt(creature.ID.RandomSeed ^ 0x6A09E667, 0, 180));
            }
        }
    }

    private static void HandleArrival(World world, TravelIntent intent)
    {
        if (intent?.Creature == null) return;
        string key = Key(intent.Creature.ID);

        switch (intent.Purpose)
        {
            case DesertBatflyTravelPurpose.ColonyMigration:
                DesertBatflyColonyRuntime.CompletePermanentMigration(
                    intent.Creature,
                    intent.DestinationRoom,
                    DesertBatflyColonyRuntime.CurrentCycle(world));
                intents.Remove(key);
                break;

            case DesertBatflyTravelPurpose.ReturnHome:
                intents.Remove(key);
                break;

            case DesertBatflyTravelPurpose.EmergencyRefuge:
                intent.WaitingAtRefuge = true;
                break;

            default:
                intents.Remove(key);
                break;
        }
    }

    private static bool TryReplan(World world, TravelIntent intent, int currentRoom)
    {
        if (world == null || intent == null || currentRoom < 0) return false;
        AbstractRoom destination = DesertBatflyColonyRuntime.FindRoom(
            world, intent.DestinationRoom);
        if (destination == null) return false;

        int maxHops = intent.Purpose == DesertBatflyTravelPurpose.EmergencyRefuge
            ? DesertBatflyWorldRoutePlanner.RefugeMaxHops
            : DesertBatflyWorldRoutePlanner.MigrationMaxHops;
        if (!DesertBatflyWorldRoutePlanner.TryPlan(
                world,
                currentRoom,
                destination.index,
                intent.Creature.creatureTemplate,
                intent.Purpose,
                room => CurrentRouteRisk(world, room),
                maxHops,
                out DesertBatflyWorldRoute route))
            return false;

        intent.Route = CopyRoute(route);
        intent.RouteIndex = 0;
        return true;
    }

    private static float CurrentRouteRisk(World world, AbstractRoom room)
    {
        if (room == null) return 8f;
        DesertBatflyWeatherEcologySample weather =
            DesertBatflyWeatherEcology.Sample(world, room);
        if (weather.LethalNow) return 8f;
        float predator = DesertBatflyColonyRuntime.PredatorRisk(room);
        return Mathf.Clamp01(weather.TravelExposure * 0.78f + predator * 0.22f);
    }

    private static bool TrySynchronizeRouteIndex(TravelIntent intent, int currentRoom)
    {
        if (intent?.Route.Rooms == null || intent.Route.Rooms.Length == 0) return false;
        int start = Mathf.Clamp(intent.RouteIndex, 0, intent.Route.Rooms.Length - 1);
        if (intent.Route.Rooms[start] == currentRoom)
        {
            intent.RouteIndex = start;
            return true;
        }

        for (int i = 0; i < intent.Route.Rooms.Length; i++)
        {
            if (intent.Route.Rooms[i] != currentRoom) continue;
            intent.RouteIndex = i;
            return true;
        }
        return false;
    }

    private static bool ReachedDestination(TravelIntent intent, int roomIndex)
    {
        if (intent == null || roomIndex < 0 || !intent.Route.Valid) return false;
        return intent.Route.Rooms[intent.Route.Rooms.Length - 1] == roomIndex;
    }

    private static bool RestrainedByNonFly(DesertBatfly bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }

    private static void RemoveEvacuationKeys(string homeColony)
    {
        if (string.IsNullOrWhiteSpace(homeColony) || activeEvacuations.Count == 0) return;
        string prefix = Normalize(homeColony) + "|";
        activeEvacuations.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static float PhysicalCapability(DesertBatflyState state)
    {
        if (state == null) return 0f;
        float wingSeverity = Mathf.SmoothStep(0f, 1f, state.WingMean);
        return Mathf.Clamp01(
            1f -
            0.18f * (1f - Mathf.Clamp01(state.health)) -
            0.46f * wingSeverity -
            0.15f * state.WingAsymmetry);
    }

    private static int BondDepartureSeed(EntityID id, DesertBatflyState state)
    {
        if (state?.SocialBondTarget is not EntityID partner || state.SocialBondStrength < 0.50f)
            return id.RandomSeed;

        unchecked
        {
            int a = id.number ^ (id.spawner * 397);
            int b = partner.number ^ (partner.spawner * 397);
            int lo = Mathf.Min(a, b);
            int hi = Mathf.Max(a, b);
            return lo * 486187739 ^ hi * 16777619;
        }
    }

    private static bool ValidCreature(AbstractCreature creature) =>
        creature != null &&
        creature.creatureTemplate?.type == DesertBatflyDefinition.CreatureType &&
        creature.state?.alive != false &&
        !creature.slatedForDeletion;

    private static void OnWorldChangedIfNeeded(World world)
    {
        if (activeWorld == null || !activeWorld.IsAlive ||
            !ReferenceEquals(activeWorld.Target, world))
            OnWorldChanged(world);
    }

    private static DesertBatflyWorldRoute CopyRoute(in DesertBatflyWorldRoute route)
    {
        if (!route.Valid) return default;
        int[] rooms = new int[route.Rooms.Length];
        Array.Copy(route.Rooms, rooms, rooms.Length);
        return new DesertBatflyWorldRoute(rooms, route.Cost, route.Survivability);
    }

    private static string Key(EntityID id) => id.spawner + ":" + id.number;
    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static int StableInt(int seed, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return minInclusive + (int)(x % (uint)(maxExclusive - minInclusive));
        }
    }
}
