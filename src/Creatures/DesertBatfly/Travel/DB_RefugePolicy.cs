using System;
using System.Collections.Generic;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_RefugeTarget
{
    internal readonly int RoomIndex;
    internal readonly int AbstractNode;
    internal readonly float ShelterQuality;
    internal readonly float Score;
    internal readonly int EstimatedTravelTicks;
    internal readonly DB_WorldRoute Route;

    internal bool Valid => RoomIndex >= 0 && Route.Valid;

    internal DB_RefugeTarget(int roomIndex, int abstractNode, float quality,
        float score, int estimatedTravelTicks, DB_WorldRoute route)
    {
        RoomIndex = roomIndex;
        AbstractNode = abstractNode;
        ShelterQuality = Mathf.Clamp01(quality);
        Score = score;
        EstimatedTravelTicks = Mathf.Max(0, estimatedTravelTicks);
        Route = route;
    }
}

internal static class DB_RefugePolicy
{
    internal const float MinimumRefugeQuality = 0.62f;
    private const float MinimumImprovement = 0.12f;
    private const int SafetyMarginTicks = 600;

    private static Dictionary<string, float> autoShelterQuality =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, IntVector2> autoShelterTile =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<int> candidateScratch = new(32);

    internal static void Reset()
    {
        autoShelterQuality = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        autoShelterTile = new Dictionary<string, IntVector2>(StringComparer.OrdinalIgnoreCase);
        candidateScratch.Clear();
    }

    internal static void ObserveRoom(Room room)
    {
        if (room?.abstractRoom == null || room.world == null) return;
        string key = RoomKey(room.abstractRoom);
        if (autoShelterQuality.ContainsKey(key)) return;

        float quality = AnalyzeRoomShelter(room, out IntVector2 bestTile);
        autoShelterQuality[key] = quality;
        if (quality >= 0.40f) autoShelterTile[key] = bestTile;
    }

    internal static bool TryGetKnownShelterPoint(Room room, out Vector2 point)
    {
        point = default;
        if (room?.abstractRoom == null) return false;
        ObserveRoom(room);
        if (!autoShelterTile.TryGetValue(RoomKey(room.abstractRoom), out IntVector2 tile))
            return false;
        point = room.MiddleOfTile(tile);
        return true;
    }

    internal static float HomeHiveShelterQuality(
        AbstractRoom room,
        WeatherScheduleEventKind hazardKind,
        string hazardId)
    {
        if (room == null) return 0f;
        if (TryTagQuality(room, "DESERTHIVESHELTER=", out float tagged)) return tagged;
        if (room.shelter) return 0.96f;

        float baseQuality = room.batHives > 0 ? 0.68f : 0.42f;
        if (room.realizedRoom != null)
        {
            if (room.realizedRoom.hives != null && room.realizedRoom.hives.Length > 0)
                baseQuality = Mathf.Max(baseQuality, RealizedHiveCoverage(room.realizedRoom));
            ObserveRoom(room.realizedRoom);
            if (autoShelterQuality.TryGetValue(RoomKey(room), out float terrain))
                baseQuality = Mathf.Max(baseQuality, terrain * 0.92f);
        }

        float demand = DesertBatflyWeatherEcology.HazardShelterDemand(hazardKind, hazardId);
        return Mathf.Clamp01(Mathf.Lerp(1f, baseQuality, demand));
    }

    internal static float RefugeShelterQuality(
        AbstractRoom room,
        WeatherScheduleEventKind hazardKind,
        string hazardId)
    {
        if (room == null) return 0f;
        if (TryTagQuality(room, "DESERTREFUGE=", out float tagged)) return tagged;
        if (HasTag(room, "DESERTREFUGE")) return 0.90f;
        if (room.shelter) return 0.96f;
        if (room.batHives > 0)
            return Mathf.Clamp01(HomeHiveShelterQuality(room, hazardKind, hazardId) - 0.03f);

        if (room.realizedRoom != null) ObserveRoom(room.realizedRoom);
        if (autoShelterQuality.TryGetValue(RoomKey(room), out float automatic))
            return automatic;

        if (room.nodes != null)
            for (int i = 0; i < room.nodes.Length; i++)
                if (room.nodes[i].type == AbstractRoomNode.Type.Den)
                    return 0.72f;

        return 0.20f;
    }

    internal static bool TryFindEmergencyRefuge(
        World world,
        AbstractRoom home,
        CreatureTemplate template,
        DesertBatflyWeatherEcologySample hazard,
        float physicalCapability,
        string knownRefuge,
        Func<AbstractRoom, float> predatorRisk,
        Func<AbstractRoom, float> crowding,
        out DB_RefugeTarget target)
    {
        DB_EnvironmentWeather weather = DB_EnvironmentProfile.Classify(hazard);
        bool sandstorm = weather is DB_EnvironmentWeather.Sandstorm or
                         DB_EnvironmentWeather.DeathSandstorm;
        if (!sandstorm)
            return TryFindEmergencyRefugeFrom(
                world, home, home, template, hazard, physicalCapability, knownRefuge,
                predatorRisk, crowding, -1, false, out target);

        target = default;
        if (!DB_EnvironmentalPolicy.CanConsiderSandstormOutwardRefuge(
                home, weather, hazard, out float homeQuality))
            return false;
        if (!TryFindEmergencyRefugeFrom(
                world, home, home, template, hazard, physicalCapability, knownRefuge,
                predatorRisk, crowding, -1, false, out DB_RefugeTarget candidate))
            return false;
        if (!DB_EnvironmentalPolicy.AcceptSandstormEmergencyRefuge(
                weather, hazard, homeQuality, candidate))
            return false;

        target = candidate;
        return true;
    }

    /// <summary>
    /// Replans from the bat's actual current room while retaining the original Home
    /// Colony as the shelter-quality baseline. Used when a committed refuge route or
    /// destination becomes unsafe after departure. Candidate discovery is bounded BFS.
    /// </summary>
    internal static bool TryFindEmergencyRefugeFrom(
        World world,
        AbstractRoom start,
        AbstractRoom home,
        CreatureTemplate template,
        DesertBatflyWeatherEcologySample hazard,
        float physicalCapability,
        string knownRefuge,
        Func<AbstractRoom, float> predatorRisk,
        Func<AbstractRoom, float> crowding,
        int excludedRoom,
        bool alreadyEvacuating,
        out DB_RefugeTarget target)
    {
        target = default;
        if (world?.abstractRooms == null || start == null || home == null ||
            template == null || !hazard.HasHazard)
            return false;

        float homeQuality = HomeHiveShelterQuality(home, hazard.HazardKind, hazard.HazardId);
        if (!alreadyEvacuating &&
            homeQuality >= Mathf.Max(0.72f, hazard.ShelterUrgency + 0.05f))
            return false;

        DB_WorldRoutePlanner.CollectReachableRooms(
            world, start.index, template, DB_WorldRoutePlanner.RefugeMaxHops,
            candidateScratch);

        float bestScore = float.NegativeInfinity;
        for (int c = 0; c < candidateScratch.Count; c++)
        {
            int roomIndex = candidateScratch[c];
            if (roomIndex == start.index || roomIndex == excludedRoom) continue;
            AbstractRoom candidate = world.GetAbstractRoom(roomIndex);
            if (candidate == null) continue;

            float quality = RefugeShelterQuality(candidate, hazard.HazardKind, hazard.HazardId);
            float requiredQuality = alreadyEvacuating
                ? MinimumRefugeQuality
                : Mathf.Max(MinimumRefugeQuality, homeQuality + MinimumImprovement);
            if (quality < requiredQuality) continue;

            if (!DB_WorldRoutePlanner.TryPlan(
                    world,
                    start.index,
                    candidate.index,
                    template,
                    DB_TravelPurpose.EmergencyRefuge,
                    room => RouteRisk(world, room, hazard, predatorRisk),
                    DB_WorldRoutePlanner.RefugeMaxHops,
                    out DB_WorldRoute route))
                continue;

            int travelTicks = EstimateTravelTicks(route, physicalCapability);
            if (!CanReachRefuge(hazard, travelTicks, route, alreadyEvacuating)) continue;

            float routeCost = DB_WorldRoutePlanner.NormalizedTravelCost(
                route, DB_WorldRoutePlanner.RefugeMaxHops);
            float pred = Mathf.Clamp01(predatorRisk?.Invoke(candidate) ?? 0f);
            float crowd = Mathf.Clamp01(crowding?.Invoke(candidate) ?? 0f);
            float familiarity = string.Equals(candidate.name, knownRefuge,
                StringComparison.OrdinalIgnoreCase) ? 0.06f : 0f;
            float score = quality * 0.58f - routeCost * 0.20f - pred * 0.12f - crowd * 0.10f + familiarity;
            if (score <= bestScore) continue;

            bestScore = score;
            target = new DB_RefugeTarget(
                candidate.index,
                ChooseRefugeNode(candidate, template),
                quality,
                score,
                travelTicks,
                route);
        }
        return target.Valid;
    }

    internal static int EstimateTravelTicks(in DB_WorldRoute route, float physicalCapability)
    {
        if (!route.Valid) return int.MaxValue;
        physicalCapability = Mathf.Clamp(physicalCapability, 0.25f, 1f);
        float perHop = 760f + route.Cost * 95f;
        float injuryScale = Mathf.Lerp(1.85f, 1f, physicalCapability);
        long ticks = (long)Mathf.Ceil(perHop * Mathf.Max(1, route.HopCount) * injuryScale);
        return ticks >= int.MaxValue ? int.MaxValue : (int)ticks;
    }

    internal static bool CanLeaveBeforeDanger(
        in DesertBatflyWeatherEcologySample hazard,
        int estimatedTravelTicks)
    {
        if (estimatedTravelTicks == int.MaxValue) return false;
        if (hazard.LethalNow) return false;
        if (!hazard.ForecastDanger)
            return hazard.ImmediateDanger < 0.72f;
        if (hazard.TimeUntilDangerTicks <= 0)
            return hazard.ImmediateDanger < 0.58f;
        long required = (long)estimatedTravelTicks + SafetyMarginTicks;
        return required < hazard.TimeUntilDangerTicks;
    }

    internal static bool CanArriveBeforeDanger(int estimatedTravelTicks, int timeUntilDangerTicks)
    {
        if (estimatedTravelTicks < 0 || estimatedTravelTicks == int.MaxValue ||
            timeUntilDangerTicks <= 0 || timeUntilDangerTicks == int.MaxValue)
            return false;
        return (long)estimatedTravelTicks + SafetyMarginTicks < timeUntilDangerTicks;
    }

    internal static int ChooseRefugeNode(AbstractRoom room, CreatureTemplate template)
    {
        if (room?.nodes == null || room.nodes.Length == 0 || template == null) return -1;
        int fallback = -1;
        for (int i = 0; i < room.nodes.Length; i++)
        {
            int mapped = room.CommonToCreatureSpecificNodeIndex(i, template);
            if (mapped < 0) continue;
            if (fallback < 0) fallback = i;
            AbstractRoomNode.Type type = room.nodes[i].type;
            if (type == AbstractRoomNode.Type.BatHive || type == AbstractRoomNode.Type.Den)
                return i;
        }
        return fallback;
    }

    internal static bool RefugeStillSuitable(
        World world,
        AbstractRoom room,
        WeatherScheduleEventKind hazardKind,
        string hazardId)
    {
        if (world == null || room == null) return false;
        DesertBatflyWeatherEcologySample local = DesertBatflyWeatherEcology.Sample(world, room);
        if (local.LethalNow) return false;
        return RefugeShelterQuality(room, hazardKind, hazardId) >= MinimumRefugeQuality;
    }

    internal static float RouteRisk(World world, AbstractRoom room,
        in DesertBatflyWeatherEcologySample hazard, Func<AbstractRoom, float> predatorRisk)
    {
        if (room == null) return 8f;
        DesertBatflyWeatherEcologySample local = DesertBatflyWeatherEcology.Sample(world, room);
        if (local.LethalNow) return 8f;
        float weather = local.TravelExposure;
        if (hazard.ForecastDanger &&
            DryCycle.Weather.Spatial.WeatherSpatialRegistry.IsAllowed(
                world.region?.name, room.name, hazard.HazardKind, hazard.HazardId))
            weather = Mathf.Max(weather,
                DesertBatflyWeatherEcology.HazardShelterDemand(hazard.HazardKind, hazard.HazardId) * 0.46f);
        float pred = Mathf.Clamp01(predatorRisk?.Invoke(room) ?? 0f);
        return Mathf.Clamp01(weather * 0.78f + pred * 0.22f);
    }

    private static bool CanReachRefuge(
        in DesertBatflyWeatherEcologySample hazard,
        int estimatedTravelTicks,
        in DB_WorldRoute route,
        bool alreadyEvacuating)
    {
        if (!alreadyEvacuating) return CanLeaveBeforeDanger(hazard, estimatedTravelTicks);
        if (!route.Valid || estimatedTravelTicks == int.MaxValue) return false;
        if (route.Survivability < 0.36f) return false;
        if (!hazard.LethalNow) return true;
        // Once already displaced, a one-hop retreat to a materially safer refuge is
        // preferable to freezing in a route that has become lethal. Longer late trips
        // remain forbidden.
        return route.HopCount <= 1 && route.Survivability >= 0.58f;
    }

    private static float RealizedHiveCoverage(Room room)
    {
        int samples = 0, covered = 0;
        for (int h = 0; h < room.hives.Length; h++)
        {
            IntVector2[] hive = room.hives[h];
            if (hive == null) continue;
            int stride = Mathf.Max(1, hive.Length / 12);
            for (int i = 0; i < hive.Length; i += stride)
            {
                IntVector2 tile = hive[i];
                samples++;
                bool roof = false;
                for (int y = 1; y <= 7 && tile.y + y < room.TileHeight; y++)
                {
                    if (!room.GetTile(new IntVector2(tile.x, tile.y + y)).Solid) continue;
                    roof = true;
                    break;
                }
                if (roof) covered++;
            }
        }
        if (samples == 0) return 0.55f;
        return Mathf.Lerp(0.50f, 0.92f, covered / (float)samples);
    }

    private static float AnalyzeRoomShelter(Room room, out IntVector2 bestTile)
    {
        bestTile = new IntVector2(Mathf.Max(1, room.TileWidth / 2), Mathf.Max(1, room.TileHeight / 3));
        if (room.TileWidth < 4 || room.TileHeight < 4) return 0.20f;

        int stepX = Mathf.Max(1, room.TileWidth / 14);
        int stepY = Mathf.Max(1, room.TileHeight / 10);
        int samples = 0, safeSamples = 0;
        float best = 0f;

        for (int y = 2; y < room.TileHeight - 2; y += stepY)
        for (int x = 2; x < room.TileWidth - 2; x += stepX)
        {
            IntVector2 tile = new IntVector2(x, y);
            if (room.GetTile(tile).Solid) continue;
            float point = ShelterPointScore(room, tile);
            samples++;
            if (point >= 0.62f) safeSamples++;
            if (point <= best) continue;
            best = point;
            bestTile = tile;
        }

        if (samples == 0) return 0.20f;
        float safeRatio = safeSamples / (float)samples;
        float quality = best * 0.72f + Mathf.Sqrt(safeRatio) * 0.28f;
        return Mathf.Clamp(quality, 0.20f, 0.93f);
    }

    private static float ShelterPointScore(Room room, IntVector2 tile)
    {
        int roofDistance = -1;
        for (int y = 1; y <= 9 && tile.y + y < room.TileHeight; y++)
        {
            if (!room.GetTile(new IntVector2(tile.x, tile.y + y)).Solid) continue;
            roofDistance = y;
            break;
        }
        if (roofDistance < 0) return 0.08f;

        float score = 0.52f + Mathf.InverseLerp(9f, 1f, roofDistance) * 0.18f;
        bool left = false, right = false;
        for (int x = 1; x <= 6; x++)
        {
            if (!left && tile.x - x >= 0 && room.GetTile(new IntVector2(tile.x - x, tile.y)).Solid)
                left = true;
            if (!right && tile.x + x < room.TileWidth && room.GetTile(new IntVector2(tile.x + x, tile.y)).Solid)
                right = true;
            if (left && right) break;
        }
        if (left) score += 0.09f;
        if (right) score += 0.09f;
        if (tile.y < room.TileHeight * 0.45f) score += 0.05f;
        return Mathf.Clamp01(score);
    }

    private static bool HasTag(AbstractRoom room, string exact)
    {
        if (room?.roomTags == null) return false;
        for (int i = 0; i < room.roomTags.Count; i++)
            if (string.Equals(room.roomTags[i]?.Trim(), exact, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool TryTagQuality(AbstractRoom room, string prefix, out float quality)
    {
        quality = 0f;
        if (room?.roomTags == null) return false;
        for (int i = 0; i < room.roomTags.Count; i++)
        {
            string tag = room.roomTags[i]?.Trim();
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string raw = tag.Substring(prefix.Length);
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) &&
                !float.IsNaN(value) && !float.IsInfinity(value))
            {
                quality = Mathf.Clamp01(value);
                return true;
            }
        }
        return false;
    }

    private static string RoomKey(AbstractRoom room) =>
        ((room?.world?.region?.name ?? room?.world?.name ?? string.Empty).Trim().ToUpperInvariant()) + "|" +
        ((room?.name ?? string.Empty).Trim().ToUpperInvariant());
}
