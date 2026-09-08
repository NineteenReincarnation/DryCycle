using System;
using System.Collections.Generic;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// High-level cross-room travel state. It owns destination/room route only.
/// Realized room-local movement is still FlyAI.LeaveRoom/AImap/shortcut; no travel code
/// writes body velocity or maintains a second tile pathfinder.
/// </summary>
internal static class DB_TravelRuntime
{
    private const int NativeExitTimeoutTicks = 1200;
    private const int ReplanCooldownTicks = 160;
    private const int RefugeGoalRefreshTicks = 80;

    private static Dictionary<string, DB_TravelIntent> intents = new(StringComparer.Ordinal);
    private static HashSet<string> activeEvacuations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AbstractCreature> ownedScratch = new(64);
    private static WeakReference activeWorld;

    internal static void Reset()
    {
        intents = new Dictionary<string, DB_TravelIntent>(StringComparer.Ordinal);
        activeEvacuations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ownedScratch.Clear();
        activeWorld = null;
    }

    internal static void OnWorldChanged(World world)
    {
        if (world == null)
        {
            Reset();
            return;
        }
        if (activeWorld != null && activeWorld.IsAlive && ReferenceEquals(activeWorld.Target, world))
            return;

        activeWorld = new WeakReference(world);
        intents.Clear();
        activeEvacuations.Clear();
        RestorePendingMigrations(world);
    }

    internal static bool HasIntent(AbstractCreature creature) =>
        creature != null && intents.ContainsKey(Key(creature.ID));

    /// <summary>
    /// R3 proposal query. This method never decrements departure delay, replans, calls
    /// LeaveRoom or mutates DB_TravelIntent. Historical Suspended state is deliberately not a
    /// veto: once the current blocker clears, the arbiter may select Travel and the existing
    /// executor is allowed to resume/replan normally.
    /// </summary>
    internal static bool CanOwnRealizedFrame(DB_Creature bat, out string reason)
    {
        reason = "no active realized travel intent";
        if (bat?.abstractCreature == null || bat.AI == null || bat.room == null ||
            bat.dead || !bat.Consious || bat.inShortcut)
            return false;
        if (!intents.TryGetValue(Key(bat.abstractCreature.ID), out DB_TravelIntent intent))
            return false;

        if (intent.Purpose == DB_TravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
        {
            reason = "EmergencyRefuge hold remains Travel-owned";
            return true;
        }
        if (intent.DepartureDelay > 0)
        {
            reason = "scheduled / staggered departure remains Travel-owned";
            return true;
        }
        if (RestrainedByNonFly(bat))
        {
            reason = "Travel yielded: restrained by non-Fly";
            return false;
        }
        if (bat.DesertAI.HasImmediateDanger)
        {
            reason = "Travel yielded: immediate threat / escape";
            return false;
        }
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering)
        {
            reason = "Travel yielded: severe injury / recovery";
            return false;
        }
        if (!intent.Route.Valid)
        {
            reason = "Travel yielded: route currently invalid";
            return false;
        }

        reason = intent.Suspended
            ? "Travel eligible to resume after higher-priority suspension"
            : string.IsNullOrEmpty(intent.StatusReason) ? "active realized Travel intent" : intent.StatusReason;
        return true;
    }

    internal static bool TryGetDebugState(AbstractCreature creature, out DB_TravelDebugState state)
    {
        state = default;
        if (creature == null || !intents.TryGetValue(Key(creature.ID), out DB_TravelIntent intent))
            return false;

        int[] routeRooms = intent.Route.Valid ? new int[intent.Route.Rooms.Length] : Array.Empty<int>();
        if (routeRooms.Length > 0) Array.Copy(intent.Route.Rooms, routeRooms, routeRooms.Length);
        int next = -1;
        if (intent.Route.Valid && intent.RouteIndex + 1 < intent.Route.Rooms.Length)
            next = intent.Route.Rooms[intent.RouteIndex + 1];

        state = new DB_TravelDebugState(
            intent.Purpose, intent.HomeColony, intent.DestinationRoom, intent.RouteIndex,
            routeRooms, next, intent.DepartureDelay, intent.WaitingAtRefuge, intent.Suspended,
            intent.StatusReason, intent.Route.Valid ? intent.Route.Cost : float.PositiveInfinity,
            intent.Route.Valid ? intent.Route.Survivability : 0f, intent.RefugeNode);
        return true;
    }

    internal static void RequestPermanentMigration(
        AbstractCreature creature,
        string destinationRoom,
        in DB_WorldRoute route,
        int departureDelay)
    {
        if (!ValidCreature(creature) || string.IsNullOrWhiteSpace(destinationRoom) || !route.Valid)
            return;
        DB_ColonyRuntime.EnsureIndividualOwnership(creature);
        DB_ColonyRuntime.IndividualRecord record = DB_ColonyRuntime.RecordFor(creature);
        intents[Key(creature.ID)] = new DB_TravelIntent(
            creature, DB_TravelPurpose.ColonyMigration, record?.CurrentColony,
            destinationRoom, route, departureDelay, DB_WeatherEcologySample.None);
    }

    internal static void UpdateAbstractWorld(World world)
    {
        if (world == null || intents.Count == 0) return;
        OnWorldChangedIfNeeded(world);
        List<string> keys = new(intents.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!intents.TryGetValue(keys[i], out DB_TravelIntent intent)) continue;
            AbstractCreature creature = intent.Creature;
            if (!ValidCreature(creature) || creature.world != world)
            {
                intents.Remove(keys[i]);
                continue;
            }

            TickCooldownAbstract(intent);
            if (intent.DepartureDelay > 0)
            {
                intent.DepartureDelay = Mathf.Max(0, intent.DepartureDelay - 120);
                intent.StatusReason = "scheduled / staggered departure";
                continue;
            }
            if (intent.Purpose == DB_TravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
                continue;

            if (creature.realizedCreature is DB_Creature realized)
            {
                TryEmergeForTravel(realized, intent);
                continue;
            }

            int currentRoom = creature.pos.room;
            TickRoomProgress(intent, currentRoom, 120);
            if (ReachedDestination(intent, currentRoom))
            {
                HandleArrival(world, intent);
                continue;
            }

            float capability = creature.state is DB_State state ? PhysicalCapability(state) : 1f;
            if (!EnsureRouteSafety(world, intent, currentRoom, capability)) continue;
            if (intent.RouteIndex + 1 >= intent.Route.Rooms.Length)
            {
                HandleArrival(world, intent);
                continue;
            }

            int nextRoomIndex = intent.Route.Rooms[intent.RouteIndex + 1];
            AbstractRoom nextRoom = world.GetAbstractRoom(nextRoomIndex);
            AbstractRoom current = world.GetAbstractRoom(currentRoom);
            if (nextRoom == null || current == null)
            {
                Suspend(intent, "route blocked: next room missing");
                continue;
            }

            int arrivalNode = world.NodeInALeadingToB(nextRoom, current).abstractNode;
            if (arrivalNode < 0) arrivalNode = nextRoom.RandomRelevantNode(creature.creatureTemplate);
            if (arrivalNode < 0)
            {
                Suspend(intent, "route blocked: no relevant arrival node");
                continue;
            }

            creature.Move(new WorldCoordinate(nextRoomIndex, -1, -1, arrivalNode));
            intent.RouteIndex++;
            intent.Suspended = false;
            intent.StatusReason = "abstract transit -> " + nextRoom.name;
            intent.SameRoomTravelTicks = 0;
            intent.LastObservedRoom = creature.pos.room;
            if (ReachedDestination(intent, creature.pos.room)) HandleArrival(world, intent);
        }
    }

    internal static bool TryDriveRealized(DB_Creature bat)
    {
        if (bat?.abstractCreature == null || bat.AI == null || bat.room == null ||
            bat.dead || !bat.Consious || bat.inShortcut)
            return false;
        if (!intents.TryGetValue(Key(bat.abstractCreature.ID), out DB_TravelIntent intent)) return false;

        TickCooldownRealized(intent);
        if (intent.Purpose == DB_TravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
            return HoldAtRefuge(bat, intent);
        if (intent.DepartureDelay > 0)
        {
            intent.DepartureDelay--;
            intent.StatusReason = "scheduled / staggered departure";
            return true;
        }
        if (RestrainedByNonFly(bat))
        {
            Suspend(intent, "suspended: restrained by non-Fly");
            return false;
        }
        if (bat.DesertAI.HasImmediateDanger)
        {
            Suspend(intent, "suspended: immediate threat / escape");
            return false;
        }
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering)
        {
            Suspend(intent, "suspended: severe injury / recovery");
            return false;
        }

        World world = bat.abstractCreature.world;
        int currentRoom = bat.room.abstractRoom.index;
        TickRoomProgressRealized(intent, currentRoom);
        if (ReachedDestination(intent, currentRoom))
        {
            DB_TravelPurpose arrivedPurpose = intent.Purpose;
            HandleArrival(world, intent);
            return arrivedPurpose == DB_TravelPurpose.EmergencyRefuge;
        }
        if (!EnsureRouteSafety(world, intent, currentRoom, bat.Injury.PhysicalCapability)) return false;
        if (intent.RouteIndex + 1 >= intent.Route.Rooms.Length) return false;

        int nextRoom = intent.Route.Rooms[intent.RouteIndex + 1];
        AbstractRoom next = world.GetAbstractRoom(nextRoom);
        if (next == null)
        {
            Suspend(intent, "route blocked: next room missing");
            return false;
        }

        bat.AI.LeaveRoom(new WorldCoordinate(nextRoom, -1, -1, -1));
        bat.AI.afraid = Mathf.Max(bat.AI.afraid,
            intent.Purpose == DB_TravelPurpose.EmergencyRefuge ? 1.65f : 0.75f);
        intent.Suspended = false;
        intent.StatusReason = "traveling via native FlyAI.LeaveRoom -> " + next.name;
        return true;
    }

    internal static void EvaluateColonyWeather(World world)
    {
        if (world?.region == null || world.abstractRooms == null) return;
        OnWorldChangedIfNeeded(world);
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DB_Definition.CreatureType);
        if (template == null) return;

        List<DB_ColonyState> colonies = new();
        foreach (DB_ColonyState colony in DB_ColonyRuntime.Colonies)
            if (colony != null && string.Equals(colony.RegionName, world.region.name, StringComparison.OrdinalIgnoreCase))
                colonies.Add(colony);

        for (int c = 0; c < colonies.Count; c++)
        {
            DB_ColonyState colony = colonies[c];
            AbstractRoom home = DB_ColonyRuntime.FindRoom(world, colony.RoomName);
            if (home == null) continue;
            DB_WeatherEcologySample hazard = DB_WeatherEcology.Sample(world, home);
            DB_EnvironmentWeather environmentalWeather =
                DB_EnvironmentProfile.Classify(hazard);
            if (DB_EnvironmentalPolicy.ShouldRecallHomeForSandstorm(environmentalWeather, hazard))
                EndEvacuationAndReturn(world, colony.RoomName, template);

            string evacuationKey = colony.RoomName + "|" + hazard.HazardKind + "|" + hazard.HazardId;

            if (!hazard.HasHazard || hazard.ShelterUrgency < 0.50f)
            {
                EndEvacuationAndReturn(world, colony.RoomName, template);
                RemoveEvacuationKeys(colony.RoomName);
                continue;
            }

            float homeQuality = DB_RefugePolicy.HomeHiveShelterQuality(home, hazard.HazardKind, hazard.HazardId);
            if (homeQuality >= Mathf.Max(0.72f, hazard.ShelterUrgency + 0.05f))
            {
                EndEvacuationAndReturn(world, colony.RoomName, template);
                RemoveEvacuationKeys(colony.RoomName);
                continue;
            }
            if (activeEvacuations.Contains(evacuationKey)) continue;

            if (!DB_RefugePolicy.TryFindEmergencyRefuge(
                    world, home, template, hazard, 1f, colony.LastSuccessfulRefuge,
                    DB_ColonyRuntime.PredatorRisk, DB_ColonyRuntime.RefugeCrowding,
                    out DB_RefugeTarget sharedRefuge))
                continue;

            AbstractRoom sharedRefugeRoom = world.GetAbstractRoom(sharedRefuge.RoomIndex);
            if (sharedRefugeRoom == null) continue;

            DB_ColonyRuntime.CollectOwnedBats(colony.RoomName, ownedScratch);
            int started = 0;
            for (int i = 0; i < ownedScratch.Count; i++)
            {
                AbstractCreature creature = ownedScratch[i];
                if (!ValidCreature(creature)) continue;
                DB_ColonyRuntime.IndividualRecord record = DB_ColonyRuntime.RecordFor(creature, false);
                if (record == null || !string.IsNullOrEmpty(record.PendingMigrationColony)) continue;
                if (intents.TryGetValue(Key(creature.ID), out DB_TravelIntent existing) &&
                    existing.Purpose == DB_TravelPurpose.ColonyMigration)
                    continue;
                if (creature.state is not DB_State state) continue;

                float capability = PhysicalCapability(state);
                bool severe = state.WingMean >= 0.60f ||
                              Mathf.Max(state.LeftWingInjury, state.RightWingInjury) >= 0.82f ||
                              capability < 0.48f;
                if (severe) continue;

                DB_RefugeTarget personalRefuge = sharedRefuge;
                if (creature.pos.room != home.index)
                {
                    AbstractRoom actual = world.GetAbstractRoom(creature.pos.room);
                    if (actual == null || !DB_RefugePolicy.TryFindEmergencyRefugeFrom(
                            world, actual, home, template, hazard, capability, colony.LastSuccessfulRefuge,
                            DB_ColonyRuntime.PredatorRisk, DB_ColonyRuntime.RefugeCrowding,
                            -1, true, out personalRefuge))
                        continue;
                }

                int personalTravelTicks = DB_RefugePolicy.EstimateTravelTicks(personalRefuge.Route, capability);
                if (creature.pos.room == home.index &&
                    !DB_RefugePolicy.CanLeaveBeforeDanger(hazard, personalTravelTicks))
                    continue;

                int staggerSeed = BondDepartureSeed(creature.ID, state);
                int stagger = 20 + StableInt(staggerSeed ^ personalRefuge.RoomIndex, 0, 220);
                AbstractRoom refugeRoom = world.GetAbstractRoom(personalRefuge.RoomIndex);
                if (refugeRoom == null) continue;
                intents[Key(creature.ID)] = new DB_TravelIntent(
                    creature, DB_TravelPurpose.EmergencyRefuge, colony.RoomName,
                    refugeRoom.name, personalRefuge.Route, stagger, hazard,
                    personalRefuge.Score, personalRefuge.AbstractNode);
                started++;
            }

            if (started > 0)
            {
                activeEvacuations.Add(evacuationKey);
                float failure = Mathf.Clamp01(hazard.ShelterUrgency * (1f - homeQuality));
                DB_ColonyRuntime.ReportExternalRefuge(colony.RoomName, sharedRefugeRoom.name, failure);
            }
        }
    }

    private static void EndEvacuationAndReturn(World world, string homeColony, CreatureTemplate template)
    {
        AbstractRoom home = DB_ColonyRuntime.FindRoom(world, homeColony);
        if (home == null) return;

        List<string> keys = new(intents.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!intents.TryGetValue(keys[i], out DB_TravelIntent intent) ||
                intent.Purpose != DB_TravelPurpose.EmergencyRefuge ||
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
            if (!TryBuildReturnRoute(world, creature, home, template, out DB_WorldRoute route))
            {
                Suspend(intent, "return blocked: no safe route home");
                continue;
            }
            ConvertToReturnHome(intent, home, route);
        }

        DB_ColonyRuntime.CollectOwnedBats(homeColony, ownedScratch);
        for (int i = 0; i < ownedScratch.Count; i++)
        {
            AbstractCreature creature = ownedScratch[i];
            if (!ValidCreature(creature) || creature.pos.room == home.index) continue;
            DB_ColonyRuntime.IndividualRecord record = DB_ColonyRuntime.RecordFor(creature, false);
            if (record == null || !string.IsNullOrEmpty(record.PendingMigrationColony)) continue;
            if (intents.TryGetValue(Key(creature.ID), out DB_TravelIntent existing) &&
                existing.Purpose is DB_TravelPurpose.ColonyMigration or DB_TravelPurpose.ReturnHome)
                continue;
            if (!TryBuildReturnRoute(world, creature, home, template, out DB_WorldRoute route)) continue;
            intents[Key(creature.ID)] = new DB_TravelIntent(
                creature, DB_TravelPurpose.ReturnHome, homeColony, home.name, route,
                20 + StableInt(creature.ID.RandomSeed ^ 0x4D31, 0, 180),
                DB_WeatherEcologySample.None);
        }
    }

    private static bool TryBuildReturnRoute(
        World world, AbstractCreature creature, AbstractRoom home, CreatureTemplate template,
        out DB_WorldRoute route)
    {
        route = default;
        return creature != null && home != null && DB_WorldRoutePlanner.TryPlan(
            world, creature.pos.room, home.index, template, DB_TravelPurpose.ReturnHome,
            room => CurrentRouteRisk(world, room), DB_WorldRoutePlanner.MigrationMaxHops, out route);
    }

    private static void RestorePendingMigrations(World world)
    {
        if (world?.abstractRooms == null) return;
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DB_Definition.CreatureType);
        if (template == null) return;
        foreach (DB_ColonyState colony in DB_ColonyRuntime.Colonies)
        {
            if (colony == null || !string.Equals(colony.RegionName, world.region?.name, StringComparison.OrdinalIgnoreCase))
                continue;
            DB_ColonyRuntime.CollectOwnedBats(colony.RoomName, ownedScratch);
            for (int i = 0; i < ownedScratch.Count; i++)
            {
                AbstractCreature creature = ownedScratch[i];
                if (!ValidCreature(creature)) continue;
                DB_ColonyRuntime.IndividualRecord record = DB_ColonyRuntime.RecordFor(creature, false);
                if (record == null || string.IsNullOrWhiteSpace(record.PendingMigrationColony)) continue;
                AbstractRoom destination = DB_ColonyRuntime.FindRoom(world, record.PendingMigrationColony);
                if (destination == null || !DB_SwarmRoom.IsDB_SwarmRoom(destination)) continue;
                if (creature.pos.room == destination.index)
                {
                    DB_ColonyRuntime.CompletePermanentMigration(
                        creature, destination.name, DB_ColonyRuntime.CurrentCycle(world));
                    continue;
                }
                if (!DB_WorldRoutePlanner.TryPlan(
                        world, creature.pos.room, destination.index, template,
                        DB_TravelPurpose.ColonyMigration,
                        roomCandidate => CurrentRouteRisk(world, roomCandidate),
                        DB_WorldRoutePlanner.MigrationMaxHops, out DB_WorldRoute route))
                    continue;
                intents[Key(creature.ID)] = new DB_TravelIntent(
                    creature, DB_TravelPurpose.ColonyMigration, record.CurrentColony,
                    destination.name, route, StableInt(creature.ID.RandomSeed ^ 0x6A09E667, 0, 180),
                    DB_WeatherEcologySample.None);
            }
        }
    }

    private static bool EnsureRouteSafety(World world, DB_TravelIntent intent, int currentRoom, float physicalCapability)
    {
        if (world == null || intent == null || currentRoom < 0) return false;
        if (!TrySynchronizeRouteIndex(intent, currentRoom) &&
            !TryReplan(world, intent, currentRoom, "route desynchronized from physical room"))
        {
            Suspend(intent, "route blocked: unable to synchronize/replan");
            return false;
        }

        if (intent.Purpose == DB_TravelPurpose.EmergencyRefuge)
        {
            AbstractRoom refuge = DB_ColonyRuntime.FindRoom(world, intent.DestinationRoom);
            if (refuge == null || !DB_RefugePolicy.RefugeStillSuitable(
                    world, refuge, intent.Hazard.HazardKind, intent.Hazard.HazardId))
            {
                if (!TrySwitchEmergencyRefuge(world, intent, currentRoom, physicalCapability,
                        "committed refuge became unsafe"))
                {
                    Suspend(intent, "refuge invalid and no safer alternative reachable");
                    return false;
                }
            }
        }

        if (!intent.Route.Valid || intent.RouteIndex + 1 >= intent.Route.Rooms.Length)
            return intent.Route.Valid;
        AbstractRoom next = world.GetAbstractRoom(intent.Route.Rooms[intent.RouteIndex + 1]);
        float risk = RouteRiskForIntent(world, intent, next);
        if (!DB_WorldRoutePlanner.NeedsSafetyReplan(intent.Purpose, risk))
        {
            intent.Suspended = false;
            if (intent.SameRoomTravelTicks < NativeExitTimeoutTicks) return true;
        }

        string reason = intent.SameRoomTravelTicks >= NativeExitTimeoutTicks
            ? "native FlyAI exit progress timeout" : "next room became materially unsafe";
        if (intent.ReplanCooldown <= 0 && TryReplan(world, intent, currentRoom, reason))
        {
            if (intent.RouteIndex + 1 >= intent.Route.Rooms.Length) return true;
            AbstractRoom replannedNext = world.GetAbstractRoom(intent.Route.Rooms[intent.RouteIndex + 1]);
            float replannedRisk = RouteRiskForIntent(world, intent, replannedNext);
            if (!DB_WorldRoutePlanner.NeedsSafetyReplan(intent.Purpose, replannedRisk)) return true;
        }
        if (intent.Purpose == DB_TravelPurpose.EmergencyRefuge &&
            TrySwitchEmergencyRefuge(world, intent, currentRoom, physicalCapability, reason))
            return true;
        Suspend(intent, "suspended: " + reason + "; no safe replan");
        return false;
    }

    private static bool TrySwitchEmergencyRefuge(
        World world, DB_TravelIntent intent, int currentRoom, float physicalCapability, string reason)
    {
        if (world == null || intent == null || intent.Purpose != DB_TravelPurpose.EmergencyRefuge)
            return false;
        AbstractRoom home = DB_ColonyRuntime.FindRoom(world, intent.HomeColony);
        AbstractRoom current = world.GetAbstractRoom(currentRoom);
        if (home == null || current == null) return false;

        DB_WeatherEcologySample currentHazard = DB_WeatherEcology.Sample(world, home);
        float homeQuality = currentHazard.HasHazard
            ? DB_RefugePolicy.HomeHiveShelterQuality(home, currentHazard.HazardKind, currentHazard.HazardId)
            : 1f;
        if (!currentHazard.HasHazard || currentHazard.ShelterUrgency < 0.50f ||
            homeQuality >= Mathf.Max(0.72f, currentHazard.ShelterUrgency + 0.05f))
        {
            if (TryBuildReturnRoute(world, intent.Creature, home, intent.Creature.creatureTemplate,
                    out DB_WorldRoute homeRoute))
            {
                ConvertToReturnHome(intent, home, homeRoute);
                intent.StatusReason = "hazard changed; returning home instead of switching refuge";
                return true;
            }
        }

        DB_ColonyState colony = DB_ColonyRuntime.TryGetColony(intent.HomeColony);
        AbstractRoom oldDestination = DB_ColonyRuntime.FindRoom(world, intent.DestinationRoom);
        if (!DB_RefugePolicy.TryFindEmergencyRefugeFrom(
                world, current, home, intent.Creature.creatureTemplate,
                currentHazard.HasHazard ? currentHazard : intent.Hazard, physicalCapability,
                colony?.LastSuccessfulRefuge, DB_ColonyRuntime.PredatorRisk,
                DB_ColonyRuntime.RefugeCrowding, oldDestination?.index ?? -1, true,
                out DB_RefugeTarget replacement))
            return false;

        AbstractRoom replacementRoom = world.GetAbstractRoom(replacement.RoomIndex);
        if (replacementRoom == null) return false;
        intent.DestinationRoom = Normalize(replacementRoom.name);
        intent.Route = CopyRoute(replacement.Route);
        intent.RouteIndex = 0;
        intent.Hazard = currentHazard.HasHazard ? currentHazard : intent.Hazard;
        intent.RefugeScore = replacement.Score;
        intent.RefugeNode = replacement.AbstractNode;
        intent.WaitingAtRefuge = false;
        intent.Suspended = false;
        intent.ReplanCooldown = ReplanCooldownTicks;
        intent.SameRoomTravelTicks = 0;
        intent.LastObservedRoom = currentRoom;
        intent.StatusReason = "replanned refuge: " + reason + " -> " + replacementRoom.name;
        return true;
    }

    private static bool TryReplan(World world, DB_TravelIntent intent, int currentRoom, string reason)
    {
        if (world == null || intent == null || currentRoom < 0) return false;
        AbstractRoom destination = DB_ColonyRuntime.FindRoom(world, intent.DestinationRoom);
        if (destination == null) return false;
        int maxHops = intent.Purpose == DB_TravelPurpose.EmergencyRefuge
            ? DB_WorldRoutePlanner.RefugeMaxHops : DB_WorldRoutePlanner.MigrationMaxHops;
        if (!DB_WorldRoutePlanner.TryPlan(
                world, currentRoom, destination.index, intent.Creature.creatureTemplate, intent.Purpose,
                room => RouteRiskForIntent(world, intent, room), maxHops, out DB_WorldRoute route))
            return false;
        intent.Route = CopyRoute(route);
        intent.RouteIndex = 0;
        intent.ReplanCooldown = ReplanCooldownTicks;
        intent.SameRoomTravelTicks = 0;
        intent.LastObservedRoom = currentRoom;
        intent.Suspended = false;
        intent.StatusReason = "replanned: " + reason;
        return true;
    }

    private static void HandleArrival(World world, DB_TravelIntent intent)
    {
        if (intent?.Creature == null) return;
        string key = Key(intent.Creature.ID);
        switch (intent.Purpose)
        {
            case DB_TravelPurpose.ColonyMigration:
                DB_ColonyRuntime.CompletePermanentMigration(
                    intent.Creature, intent.DestinationRoom, DB_ColonyRuntime.CurrentCycle(world));
                intents.Remove(key);
                break;
            case DB_TravelPurpose.ReturnHome:
                intents.Remove(key);
                break;
            case DB_TravelPurpose.EmergencyRefuge:
                intent.WaitingAtRefuge = true;
                intent.Suspended = false;
                intent.SameRoomTravelTicks = 0;
                intent.StatusReason = "arrived; holding emergency refuge";
                break;
            default:
                intents.Remove(key);
                break;
        }
    }

    private static bool HoldAtRefuge(DB_Creature bat, DB_TravelIntent intent)
    {
        if (bat?.room == null || intent == null) return false;
        intent.Suspended = false;
        bat.AI.afraid = Mathf.Max(bat.AI.afraid, 1.25f);
        if (intent.RefugeGoalRefresh > 0) intent.RefugeGoalRefresh--;
        if (intent.RefugeGoalRefresh > 0) return true;
        intent.RefugeGoalRefresh = RefugeGoalRefreshTicks;

        // Prefer an authored/native abstract node. Route planning retains native Dijkstra
        // semantics; FlightMotor only centralizes the realized localGoal write.
        if (intent.RefugeNode >= 0 && bat.room.abstractRoom?.nodes != null &&
            intent.RefugeNode < bat.room.abstractRoom.nodes.Length)
        {
            AbstractRoomNode.Type nodeType = bat.room.abstractRoom.nodes[intent.RefugeNode].type;
            if (nodeType == AbstractRoomNode.Type.Den || nodeType == AbstractRoomNode.Type.BatHive)
            {
                int mapped = bat.room.abstractRoom.CommonToCreatureSpecificNodeIndex(
                    intent.RefugeNode, bat.Template);
                if (mapped >= 0)
                {
                    bat.AI.followingDijkstraMap = mapped;
                    Vector2 nextGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, mapped);
                    DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Travel, nextGoal);
                    intent.StatusReason = "holding refuge via native den/hive Dijkstra";
                    return true;
                }
            }
        }

        if (DB_RefugePolicy.TryGetKnownShelterPoint(bat.room, out Vector2 shelterPoint))
        {
            if (Vector2.Distance(bat.mainBodyChunk.pos, shelterPoint) > 55f &&
                Vector2.Distance(bat.AI.localGoal, shelterPoint) > 45f)
                DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Travel, shelterPoint);
            intent.StatusReason = "holding geometry refuge near covered point";
        }
        else
        {
            intent.StatusReason = "holding refuge; no local shelter point override";
        }
        return true;
    }

    private static void TryEmergeForTravel(DB_Creature bat, DB_TravelIntent intent)
    {
        if (bat?.room == null || intent == null || !bat.DesertState.InHive) return;
        try
        {
            DB_SwarmRoom colony = DB_SwarmRoom.For(bat.room);
            if (colony.Hive.inHive.Contains(bat))
            {
                colony.Hive.FlyEmergeFromHive(bat);
                intent.StatusReason = "leaving hive for committed travel";
            }
        }
        catch
        {
            intent.StatusReason = "waiting for compatible hive emergence";
        }
    }

    private static void ConvertToReturnHome(DB_TravelIntent intent, AbstractRoom home, in DB_WorldRoute route)
    {
        intent.Purpose = DB_TravelPurpose.ReturnHome;
        intent.DestinationRoom = Normalize(home.name);
        intent.Route = CopyRoute(route);
        intent.RouteIndex = 0;
        intent.DepartureDelay = 20 + StableInt(intent.Creature.ID.RandomSeed ^ 0x4D31, 0, 180);
        intent.WaitingAtRefuge = false;
        intent.Suspended = false;
        intent.ReplanCooldown = 0;
        intent.SameRoomTravelTicks = 0;
        intent.Hazard = DB_WeatherEcologySample.None;
        intent.RefugeNode = -1;
        intent.StatusReason = "weather safe; staggered return home";
    }

    private static float RouteRiskForIntent(World world, DB_TravelIntent intent, AbstractRoom room)
    {
        if (intent != null && intent.Purpose == DB_TravelPurpose.EmergencyRefuge && intent.Hazard.HasHazard)
            return DB_RefugePolicy.RouteRisk(world, room, intent.Hazard, DB_ColonyRuntime.PredatorRisk);
        return CurrentRouteRisk(world, room);
    }

    private static float CurrentRouteRisk(World world, AbstractRoom room)
    {
        if (room == null) return 8f;
        DB_WeatherEcologySample weather = DB_WeatherEcology.Sample(world, room);
        if (weather.LethalNow) return 8f;
        float predator = DB_ColonyRuntime.PredatorRisk(room);
        return Mathf.Clamp01(weather.TravelExposure * 0.78f + predator * 0.22f);
    }

    private static void TickRoomProgress(DB_TravelIntent intent, int roomIndex, int ticks)
    {
        if (intent == null) return;
        if (intent.LastObservedRoom != roomIndex)
        {
            intent.LastObservedRoom = roomIndex;
            intent.SameRoomTravelTicks = 0;
            return;
        }
        intent.SameRoomTravelTicks = Mathf.Min(NativeExitTimeoutTicks + 120, intent.SameRoomTravelTicks + ticks);
    }

    private static void TickRoomProgressRealized(DB_TravelIntent intent, int roomIndex)
    {
        if (intent == null) return;
        int frame = Time.frameCount;
        if (intent.LastObservedFrame == frame) return;
        intent.LastObservedFrame = frame;
        TickRoomProgress(intent, roomIndex, 1);
    }

    private static void TickCooldownAbstract(DB_TravelIntent intent)
    {
        if (intent?.ReplanCooldown > 0) intent.ReplanCooldown = Mathf.Max(0, intent.ReplanCooldown - 120);
    }

    private static void TickCooldownRealized(DB_TravelIntent intent)
    {
        if (intent?.ReplanCooldown > 0) intent.ReplanCooldown--;
    }

    private static bool TrySynchronizeRouteIndex(DB_TravelIntent intent, int currentRoom)
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

    private static bool ReachedDestination(DB_TravelIntent intent, int roomIndex)
    {
        if (intent == null || roomIndex < 0 || !intent.Route.Valid) return false;
        return intent.Route.Rooms[intent.Route.Rooms.Length - 1] == roomIndex;
    }

    private static bool RestrainedByNonFly(DB_Creature bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly) return true;
        }
        return false;
    }

    private static void RemoveEvacuationKeys(string homeColony)
    {
        if (string.IsNullOrWhiteSpace(homeColony) || activeEvacuations.Count == 0) return;
        string prefix = Normalize(homeColony) + "|";
        activeEvacuations.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static float PhysicalCapability(DB_State state)
    {
        if (state == null) return 0f;
        float wingSeverity = Mathf.SmoothStep(0f, 1f, state.WingMean);
        return Mathf.Clamp01(1f - 0.18f * (1f - Mathf.Clamp01(state.health)) -
            0.46f * wingSeverity - 0.15f * state.WingAsymmetry);
    }

    private static int BondDepartureSeed(EntityID id, DB_State state)
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

    private static void Suspend(DB_TravelIntent intent, string reason)
    {
        if (intent == null) return;
        intent.Suspended = true;
        intent.StatusReason = reason ?? "suspended";
    }

    private static bool ValidCreature(AbstractCreature creature) =>
        creature != null && creature.creatureTemplate?.type == DB_Definition.CreatureType &&
        creature.state?.alive != false && !creature.slatedForDeletion;

    private static void OnWorldChangedIfNeeded(World world)
    {
        if (activeWorld == null || !activeWorld.IsAlive || !ReferenceEquals(activeWorld.Target, world))
            OnWorldChanged(world);
    }

    private static DB_WorldRoute CopyRoute(in DB_WorldRoute route)
    {
        if (!route.Valid) return default;
        int[] rooms = new int[route.Rooms.Length];
        Array.Copy(route.Rooms, rooms, rooms.Length);
        return new DB_WorldRoute(rooms, route.Cost, route.Survivability);
    }

    private static string Key(EntityID id) => id.spawner + ":" + id.number;
    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

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