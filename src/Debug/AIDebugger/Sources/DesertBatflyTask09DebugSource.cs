using System;
using System.Text;
using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Observatory enrichment for Task 09. It delegates all existing Desert Batfly
/// inspection to the normal source, then appends read-only ecology/travel state.
/// No debug read creates a colony, individual record or travel intent.
/// </summary>
internal sealed class DesertBatflyTask09DebugSource : IAIDebugSource
{
    private readonly DesertBatflyDebugSource inner = new();

    public int Priority => 1100;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DesertBatfly bat)
            return snapshot;

        World world = creature.world ?? game?.world;
        DesertBatflyColonyRuntime.IndividualRecord record =
            DesertBatflyColonyRuntime.RecordFor(creature, false);
        string currentColony = record?.CurrentColony ?? string.Empty;
        DesertBatflyColonyState colony = DesertBatflyColonyRuntime.TryGetColony(currentColony);

        var ecology = new AIDebugSection("Task 09 Colony Ecology / 群落生态");
        ecology.Add("Current colony / 当前群落", "DesertBatflyColonyRuntime.CurrentColony",
            string.IsNullOrEmpty(currentColony) ? "—" : currentColony);
        ecology.Add("Previous colony / 上一群落", "DesertBatflyColonyRuntime.PreviousColony",
            string.IsNullOrEmpty(record?.PreviousColony) ? "—" : record.PreviousColony);
        ecology.Add("Pending migration / 待迁徙目标", "DesertBatflyColonyRuntime.PendingMigrationColony",
            string.IsNullOrEmpty(record?.PendingMigrationColony) ? "—" : record.PendingMigrationColony);
        ecology.Add("Last migration cycle / 上次迁徙周期", "DesertBatflyColonyRuntime.LastMigrationCycle",
            record == null || record.LastMigrationCycle == int.MinValue ? "—" : record.LastMigrationCycle);

        if (colony != null)
        {
            ecology.Add("Population / 当前人口", "DesertBatflyColonyState.CurrentPopulation", colony.CurrentPopulation)
                .Add("Preferred / 理想人口", "DesertBatflyColonyState.PreferredPopulation", colony.PreferredPopulation)
                .Add("Hard minimum / 最低保底", "DesertBatflyColonyState.HardMinimumPersistence", colony.HardMinimumPersistence)
                .Add("Recovery ceiling / 自然恢复上限", "DesertBatflyColonyState.NaturalRecoveryCeiling", colony.NaturalRecoveryCeiling)
                .Add("Soft capacity / 软容量", "DesertBatflyColonyState.SoftCapacity", colony.SoftCapacity)
                .Add("Mortality pressure / 死亡压力", "DesertBatflyColonyState.MortalityPressure", colony.MortalityPressure)
                .Add("Predator pressure / 捕食压力", "DesertBatflyColonyState.PredatorPressure", colony.PredatorPressure)
                .Add("Environment pressure / 环境压力", "DesertBatflyColonyState.EnvironmentalPressure", colony.EnvironmentalPressure)
                .Add("Shelter failure / 庇护失败", "DesertBatflyColonyState.ShelterFailureMemory", colony.ShelterFailureMemory)
                .Add("Regional weather / 区域天气压力", "DesertBatflyColonyState.RegionalWeatherStress", colony.RegionalWeatherStress)
                .Add("Relative habitat / 相对栖息地压力", "DesertBatflyColonyState.RelativeHabitatStress", colony.RelativeHabitatStress)
                .Add("Migration pressure / 迁徙压力", "DesertBatflyColonyState.MigrationPressure", colony.MigrationPressure)
                .Add("Migration active / 迁徙激活", "DesertBatflyColonyState.MigrationActive", colony.MigrationActive)
                .Add("Colony cooldown / 群落冷却", "DesertBatflyColonyState.ColonyMigrationCooldown", colony.ColonyMigrationCooldown)
                .Add("Known refuge / 已知避难所", "DesertBatflyColonyState.LastSuccessfulRefuge",
                    string.IsNullOrEmpty(colony.LastSuccessfulRefuge) ? "—" : colony.LastSuccessfulRefuge);
        }
        snapshot.Sections.Add(ecology);

        AbstractRoom home = world != null && colony != null
            ? DesertBatflyColonyRuntime.FindRoom(world, colony.RoomName)
            : null;
        if (world != null && home != null)
        {
            DesertBatflyWeatherEcologySample weather = DesertBatflyWeatherEcology.Sample(world, home);
            float shelter = weather.HasHazard
                ? DesertBatflyRefuge.HomeHiveShelterQuality(home, weather.HazardKind, weather.HazardId)
                : 0f;
            snapshot.Sections.Add(new AIDebugSection("Task 09 Weather Refuge / 天气避难")
                .Add("Hazard / 危险天气", "DesertBatflyWeatherEcology.HazardId",
                    string.IsNullOrEmpty(weather.HazardId) ? "—" : weather.HazardId)
                .Add("Active intensity / 当前强度", "DesertBatflyWeatherEcology.ActiveIntensity", weather.ActiveIntensity)
                .Add("Immediate danger / 即时危险", "DesertBatflyWeatherEcology.ImmediateDanger", weather.ImmediateDanger)
                .Add("Shelter urgency / 避难紧迫度", "DesertBatflyWeatherEcology.ShelterUrgency", weather.ShelterUrgency)
                .Add("Migration stress / 天气迁徙压力", "DesertBatflyWeatherEcology.MigrationStress", weather.MigrationStress)
                .Add("Travel exposure / 路线暴露", "DesertBatflyWeatherEcology.TravelExposure", weather.TravelExposure)
                .Add("Time until danger / 距危险", "DesertBatflyWeatherEcology.TimeUntilDangerTicks",
                    weather.TimeUntilDangerTicks == int.MaxValue ? "—" : weather.TimeUntilDangerTicks)
                .Add("Home shelter quality / 本巢庇护质量", "DesertBatflyRefuge.HomeHiveShelterQuality", shelter));
        }

        bool hasTravel = DesertBatflyTravelNavigation.TryGetDebugState(
            creature, out DesertBatflyTravelDebugState travel);
        var travelSection = new AIDebugSection("Task 09 Travel / 跨房旅行")
            .Add("Travel purpose / 旅行目的", "DesertBatflyTravelPurpose",
                hasTravel ? travel.Purpose.ToString() : "None")
            .Add("Destination / 目的房间", "DesertBatflyTravelNavigation.DestinationRoom",
                hasTravel && !string.IsNullOrEmpty(travel.DestinationRoom) ? travel.DestinationRoom : "—")
            .Add("Route index / 路线进度", "DesertBatflyTravelNavigation.RouteIndex",
                hasTravel ? travel.RouteIndex : 0)
            .Add("Departure delay / 出发延迟", "DesertBatflyTravelNavigation.DepartureDelay",
                hasTravel ? travel.DepartureDelay : 0)
            .Add("Waiting at refuge / 正在避难", "DesertBatflyTravelNavigation.WaitingAtRefuge",
                hasTravel && travel.WaitingAtRefuge)
            .Add("Travel suspended / 旅行暂停", "DesertBatflyTravelNavigation.Suspended",
                hasTravel && travel.Suspended)
            .Add("Travel reason / 旅行原因", "DesertBatflyTravelNavigation.StatusReason",
                hasTravel && !string.IsNullOrEmpty(travel.StatusReason) ? travel.StatusReason : "—")
            .Add("Route cost / 路线代价", "DesertBatflyTravelNavigation.RouteCost",
                hasTravel ? travel.RouteCost : 0f)
            .Add("Route survivability / 路线生存性", "DesertBatflyTravelNavigation.RouteSurvivability",
                hasTravel ? travel.RouteSurvivability : 0f);

        if (hasTravel)
        {
            travelSection.Add("Next room / 下一房间", "DesertBatflyTravelNavigation.NextRoom",
                RoomName(world, travel.NextRoom));
            travelSection.Add("Route / 世界图路线", "DesertBatflyTravelNavigation.RouteRooms",
                RouteNames(world, travel.RouteRooms));
        }
        snapshot.Sections.Add(travelSection);

        float propensity = 0f;
        if (colony != null && record != null && creature.state is DesertBatflyState state)
        {
            float bondAtHome = state.SocialBondStrength;
            propensity = DesertBatflyColonyMigration.IndividualPropensity(
                state.Personality,
                bat.Injury.PhysicalCapability,
                bat.Injury.IsSeverelyInjured,
                bat.Injury.IsRecovering,
                DesertBatflyColonyMigration.ActiveTrauma(state),
                bondAtHome,
                colony.ShelterFailureMemory,
                DesertBatflyColonyRuntime.CurrentCycle(world),
                record.LastMigrationCycle);
        }
        travelSection.Add("Migration propensity / 个体迁徙倾向",
            "DesertBatflyColonyMigration.IndividualPropensity", propensity);

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Task 09 travel / 跨房旅行",
            hasTravel
                ? (travel.Suspended ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Active)
                : AIDebugDecisionState.Inactive,
            hasTravel
                ? $"{travel.Purpose} -> {travel.DestinationRoom}; routeIndex={travel.RouteIndex}; {travel.StatusReason}"
                : "no active TravelIntent",
            "DesertBatflyTravelNavigation"));

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
