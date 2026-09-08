using System;
using System.Text;
using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Observatory enrichment for Travel / Colony. It delegates all existing Desert Batfly
/// inspection to the normal source, then appends read-only ecology/travel state.
/// No debug read creates a colony, individual record or travel intent.
/// </summary>
internal sealed class DB_TravelDebugSource : IAIDebugSource
{
    private readonly DB_ObservatorySource inner = new();

    public int Priority => 1100;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DB_Creature bat)
            return snapshot;

        World world = creature.world ?? game?.world;
        DB_ColonyRuntime.IndividualRecord record =
            DB_ColonyRuntime.RecordFor(creature, false);
        string currentColony = record?.CurrentColony ?? string.Empty;
        DB_ColonyState colony = DB_ColonyRuntime.TryGetColony(currentColony);

        var ecology = new AIDebugSection("Travel / Colony Colony Ecology / 群落生态");
        ecology.Add("Current colony / 当前群落", "DB_ColonyRuntime.CurrentColony",
            string.IsNullOrEmpty(currentColony) ? "—" : currentColony);
        ecology.Add("Previous colony / 上一群落", "DB_ColonyRuntime.PreviousColony",
            string.IsNullOrEmpty(record?.PreviousColony) ? "—" : record.PreviousColony);
        ecology.Add("Pending migration / 待迁徙目标", "DB_ColonyRuntime.PendingMigrationColony",
            string.IsNullOrEmpty(record?.PendingMigrationColony) ? "—" : record.PendingMigrationColony);
        ecology.Add("Last migration cycle / 上次迁徙周期", "DB_ColonyRuntime.LastMigrationCycle",
            record == null || record.LastMigrationCycle == int.MinValue ? "—" : record.LastMigrationCycle);

        if (colony != null)
        {
            ecology.Add("Population / 当前人口", "DB_ColonyState.CurrentPopulation", colony.CurrentPopulation)
                .Add("Preferred / 理想人口", "DB_ColonyState.PreferredPopulation", colony.PreferredPopulation)
                .Add("Hard minimum / 最低保底", "DB_ColonyState.HardMinimumPersistence", colony.HardMinimumPersistence)
                .Add("Recovery ceiling / 自然恢复上限", "DB_ColonyState.NaturalRecoveryCeiling", colony.NaturalRecoveryCeiling)
                .Add("Soft capacity / 软容量", "DB_ColonyState.SoftCapacity", colony.SoftCapacity)
                .Add("Mortality pressure / 死亡压力", "DB_ColonyState.MortalityPressure", colony.MortalityPressure)
                .Add("Predator pressure / 捕食压力", "DB_ColonyState.PredatorPressure", colony.PredatorPressure)
                .Add("Environment pressure / 环境压力", "DB_ColonyState.EnvironmentalPressure", colony.EnvironmentalPressure)
                .Add("Shelter failure / 庇护失败", "DB_ColonyState.ShelterFailureMemory", colony.ShelterFailureMemory)
                .Add("Regional weather / 区域天气压力", "DB_ColonyState.RegionalWeatherStress", colony.RegionalWeatherStress)
                .Add("Relative habitat / 相对栖息地压力", "DB_ColonyState.RelativeHabitatStress", colony.RelativeHabitatStress)
                .Add("Migration pressure / 迁徙压力", "DB_ColonyState.MigrationPressure", colony.MigrationPressure)
                .Add("Migration active / 迁徙激活", "DB_ColonyState.MigrationActive", colony.MigrationActive)
                .Add("Colony cooldown / 群落冷却", "DB_ColonyState.ColonyMigrationCooldown", colony.ColonyMigrationCooldown)
                .Add("Known refuge / 已知避难所", "DB_ColonyState.LastSuccessfulRefuge",
                    string.IsNullOrEmpty(colony.LastSuccessfulRefuge) ? "—" : colony.LastSuccessfulRefuge);
        }
        snapshot.Sections.Add(ecology);

        AbstractRoom home = world != null && colony != null
            ? DB_ColonyRuntime.FindRoom(world, colony.RoomName)
            : null;
        if (world != null && home != null)
        {
            DB_WeatherEcologySample weather = DB_WeatherEcology.Sample(world, home);
            float shelter = weather.HasHazard
                ? DB_RefugePolicy.HomeHiveShelterQuality(home, weather.HazardKind, weather.HazardId)
                : 0f;
            snapshot.Sections.Add(new AIDebugSection("Travel / Colony Weather Refuge / 天气避难")
                .Add("Hazard / 危险天气", "DB_WeatherEcology.HazardId",
                    string.IsNullOrEmpty(weather.HazardId) ? "—" : weather.HazardId)
                .Add("Active intensity / 当前强度", "DB_WeatherEcology.ActiveIntensity", weather.ActiveIntensity)
                .Add("Immediate danger / 即时危险", "DB_WeatherEcology.ImmediateDanger", weather.ImmediateDanger)
                .Add("Shelter urgency / 避难紧迫度", "DB_WeatherEcology.ShelterUrgency", weather.ShelterUrgency)
                .Add("Migration stress / 天气迁徙压力", "DB_WeatherEcology.MigrationStress", weather.MigrationStress)
                .Add("Travel exposure / 路线暴露", "DB_WeatherEcology.TravelExposure", weather.TravelExposure)
                .Add("Time until danger / 距危险", "DB_WeatherEcology.TimeUntilDangerTicks",
                    weather.TimeUntilDangerTicks == int.MaxValue ? "—" : weather.TimeUntilDangerTicks)
                .Add("Home shelter quality / 本巢庇护质量", "DB_RefugePolicy.HomeHiveShelterQuality", shelter));
        }

        bool hasTravel = DB_TravelRuntime.TryGetDebugState(
            creature, out DB_TravelDebugState travel);
        var travelSection = new AIDebugSection("Travel / Colony Travel / 跨房旅行")
            .Add("Travel purpose / 旅行目的", "DB_TravelPurpose",
                hasTravel ? travel.Purpose.ToString() : "None")
            .Add("Destination / 目的房间", "DB_TravelRuntime.DestinationRoom",
                hasTravel && !string.IsNullOrEmpty(travel.DestinationRoom) ? travel.DestinationRoom : "—")
            .Add("Route index / 路线进度", "DB_TravelRuntime.RouteIndex",
                hasTravel ? travel.RouteIndex : 0)
            .Add("Departure delay / 出发延迟", "DB_TravelRuntime.DepartureDelay",
                hasTravel ? travel.DepartureDelay : 0)
            .Add("Waiting at refuge / 正在避难", "DB_TravelRuntime.WaitingAtRefuge",
                hasTravel && travel.WaitingAtRefuge)
            .Add("Travel suspended / 旅行暂停", "DB_TravelRuntime.Suspended",
                hasTravel && travel.Suspended)
            .Add("Travel reason / 旅行原因", "DB_TravelRuntime.StatusReason",
                hasTravel && !string.IsNullOrEmpty(travel.StatusReason) ? travel.StatusReason : "—")
            .Add("Route cost / 路线代价", "DB_TravelRuntime.RouteCost",
                hasTravel ? travel.RouteCost : 0f)
            .Add("Route survivability / 路线生存性", "DB_TravelRuntime.RouteSurvivability",
                hasTravel ? travel.RouteSurvivability : 0f);

        if (hasTravel)
        {
            travelSection.Add("Next room / 下一房间", "DB_TravelRuntime.NextRoom",
                RoomName(world, travel.NextRoom));
            travelSection.Add("Route / 世界图路线", "DB_TravelRuntime.RouteRooms",
                RouteNames(world, travel.RouteRooms));
        }
        snapshot.Sections.Add(travelSection);

        float propensity = 0f;
        if (colony != null && record != null && creature.state is DB_State state)
        {
            float bondAtHome = state.SocialBondStrength;
            propensity = DB_MigrationPolicy.IndividualPropensity(
                state.Personality,
                bat.Injury.PhysicalCapability,
                bat.Injury.IsSeverelyInjured,
                bat.Injury.IsRecovering,
                DB_MigrationPolicy.ActiveTrauma(state),
                bondAtHome,
                colony.ShelterFailureMemory,
                DB_ColonyRuntime.CurrentCycle(world),
                record.LastMigrationCycle);
        }
        travelSection.Add("Migration propensity / 个体迁徙倾向",
            "DB_MigrationPolicy.IndividualPropensity", propensity);

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Travel / Colony travel / 跨房旅行",
            hasTravel
                ? (travel.Suspended ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Active)
                : AIDebugDecisionState.Inactive,
            hasTravel
                ? $"{travel.Purpose} -> {travel.DestinationRoom}; routeIndex={travel.RouteIndex}; {travel.StatusReason}"
                : "no active DB_TravelIntent",
            "DB_TravelRuntime"));

        return snapshot;
    }

    private static string RoomName(World world, int index)
    {
        if (world == null || index < 0) return "—";
        return world.GetAbstractRoom(index)?.name ?? index.ToString();
    }

    private static string RouteNames(World world, int[] rooms)
    {
        if (rooms == null || rooms.Length == 0) return "—";
        StringBuilder b = new();
        for (int i = 0; i < rooms.Length; i++)
        {
            if (i > 0) b.Append(" -> ");
            b.Append(RoomName(world, rooms[i]));
        }
        return b.ToString();
    }
}
