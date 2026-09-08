using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal sealed class DB_EnvironmentDebugSource : IAIDebugSource
{
    private readonly DB_SignalDebugSource inner = new();

    public int Priority => 1500;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DB_Creature bat || bat.room == null)
            return snapshot;

        DB_EnvironmentRoomRuntime.TryPeekExisting(
            bat.room, out DB_EnvironmentRoomRuntime.RoomState roomState);
        DB_EnvironmentContext context = roomState?.Context ??
            DB_EnvironmentContext.Calm;
        bool hasInfluence = DB_EnvironmentRuntime.TryGetInfluence(
            bat, out DB_EnvironmentInfluence influence);
        if (!hasInfluence) influence = DB_EnvironmentInfluence.Neutral;

        int anchorId = 0;
        float anchorCrowding = 0f;
        float anchorWeatherQuality = 0f;
        if (roomState != null && influence.PreferredShelterPoint.HasValue)
        {
            Vector2 preferred = influence.PreferredShelterPoint.Value;
            float best = float.MaxValue;
            for (int i = 0; i < roomState.Anchors.Count; i++)
            {
                DB_ShelterAnchor anchor = roomState.Anchors[i];
                float dist = (anchor.Position - preferred).sqrMagnitude;
                if (dist >= best) continue;
                best = dist;
                anchorId = anchor.Id;
                anchorCrowding = anchor.Crowding;
                anchorWeatherQuality = DB_EnvironmentRoomRuntime.WeatherQuality(
                    anchor, context.Weather);
            }
        }

        bool hasFailure = DB_EnvironmentRoomRuntime.TryGetShelterFailureDebug(
            bat.room,
            out int failureTicks,
            out float failureSeverity,
            out string failureReason);
        DB_RoomContext.TryPeekExisting(bat.room, out DB_RoomContext roomContext);
        DB_SocialRoomRuntime.TryPeekExisting(
            bat.room, out DB_SocialRoomRuntime.RoomState socialRoomState);
        bool performanceActive = DB_PerformanceProbe.TryPeek(
            bat.room, out DB_PerformanceSnapshot performance);
        bool travelOwns = DB_TravelRuntime.HasIntent(bat.abstractCreature);

        snapshot.Sections.Add(new AIDebugSection("Environment Environment / 环境活动")
            .Add("Weather source valid / 天气源有效", "Environment.WeatherSourceValid",
                context.WeatherSourceValid)
            .Add("Weather / 天气", "Environment.Weather", context.Weather.ToString())
            .Add("Weather id / 天气ID", "Environment.WeatherId",
                string.IsNullOrEmpty(context.WeatherId) ? "—" : context.WeatherId)
            .Add("Active intensity / 当前强度", "Environment.ActiveIntensity",
                context.ActiveIntensity)
            .Add("Forecast ticks / 预警ticks", "Environment.ForecastTicks",
                context.ForecastTicks == int.MaxValue ? -1 : context.ForecastTicks)
            .Add("Room phase / 房间阶段", "Environment.Phase", context.Phase.ToString())
            .Add("Phase reason / 阶段原因", "Environment.PhaseReason", context.PhaseReason)
            .Add("Travel intent / Travel跨房意图", "Environment.TravelOwnsControl", travelOwns)
            .Add("Commitment / 环境承诺ticks", "Environment.CommitmentTicks",
                influence.CommitmentTicks)
            .Add("Shelter drive / 避险驱动", "Environment.ShelterDrive",
                influence.ShelterDrive)
            .Add("Open exposure aversion / 开放暴露厌恶", "Environment.OpenExposureAversion",
                influence.OpenExposureAversion)
            .Add("Roost multiplier / 倒挂倍率", "Environment.RoostMultiplier",
                influence.RoostMultiplier)
            .Add("Harass multiplier / 骚扰倍率", "Environment.HarassMultiplier",
                influence.HarassMultiplier)
            .Add("Social multiplier / 社交倍率", "Environment.SocialMultiplier",
                influence.SocialMultiplier)
            .Add("Play multiplier / 玩闹倍率", "Environment.PlayMultiplier",
                influence.PlayMultiplier)
            .Add("Group cohesion / 群体凝聚倍率", "Environment.GroupCohesionMultiplier",
                influence.GroupCohesionMultiplier)
            .Add("Activity radius / 活动半径倍率", "Environment.ActivityRadiusMultiplier",
                influence.ActivityRadiusMultiplier));

        snapshot.Sections.Add(new AIDebugSection("Environment Visibility & Heat / 视野与热")
            .Add("Visibility confidence / 视野信心", "Environment.VisibilityConfidence",
                influence.VisibilityConfidence)
            .Add("Navigation uncertainty / 导航不确定", "Environment.NavigationUncertainty",
                influence.NavigationUncertainty)
            .Add("Obstacle anticipation / 障碍预判", "Environment.ObstacleAnticipationScale",
                influence.ObstacleAnticipationScale)
            .Add("Heat agitation / 热躁", "Environment.HeatAgitation",
                influence.HeatAgitation)
            .Add("Heat shelter drive / 避热驱动", "Environment.HeatShelterDrive",
                influence.HeatShelterDrive)
            .Add("Thermal exhaustion / 热疲劳", "Environment.ThermalExhaustion",
                influence.ThermalExhaustion)
            .Add("Damage attack permission / 伤害攻击许可", "Environment.DamageAttackPermission",
                influence.DamageAttackPermission)
            .Add("Hard survival / 硬生存", "Environment.HardSurvival",
                influence.HardSurvival)
            .Add("Home return drive / 归巢驱动", "Environment.HomeReturnDrive",
                influence.HomeReturnDrive)
            .Add("Burrow drive / 钻沙驱动", "Environment.BurrowDrive",
                influence.BurrowDrive)
            .Add("Migration suppression / 迁徙抑制", "Environment.MigrationSuppression",
                influence.MigrationSuppression)
            .Add("Recovery progress / 恢复进度", "Environment.RecoveryProgress",
                influence.RecoveryProgress));

        snapshot.Sections.Add(new AIDebugSection("Environment Shelter / 局部避险")
            .Add("Anchor count / 避险点数量", "Environment.AnchorCount",
                roomState?.Anchors.Count ?? 0)
            .Add("Preferred anchor / 当前避险点", "Environment.PreferredAnchorId", anchorId)
            .Add("Preferred point / 目标位置", "Environment.PreferredShelterPoint",
                influence.PreferredShelterPoint.HasValue
                    ? influence.PreferredShelterPoint.Value.ToString()
                    : "—")
            .Add("Preferred quality / 避险质量", "Environment.PreferredShelterQuality",
                influence.PreferredShelterQuality)
            .Add("Weather quality / 天气专属质量", "Environment.AnchorWeatherQuality",
                anchorWeatherQuality)
            .Add("Crowding / 拥挤", "Environment.AnchorCrowding", anchorCrowding)
            .Add("Shelter failure active / 避险失败", "Environment.LocalShelterFailure",
                hasFailure)
            .Add("Shelter failure ticks / 失败持续", "Environment.LocalShelterFailureTicks",
                failureTicks)
            .Add("Shelter failure severity / 失败强度", "Environment.LocalShelterFailureSeverity",
                failureSeverity)
            .Add("Shelter failure reason / 失败原因", "Environment.LocalShelterFailureReason",
                string.IsNullOrEmpty(failureReason) ? "—" : failureReason)
            .Add("Decision reason / 当前环境决策", "Environment.DecisionReason",
                influence.DecisionReason));

        snapshot.Sections.Add(new AIDebugSection("Performance Cache / 性能缓存")
            .Add("Room context active / 房间缓存激活", "Performance.RoomContextActive",
                roomContext != null)
            .Add("Room cache age / 房间缓存年龄", "Performance.RoomCacheAge",
                DisplayAge(roomContext?.SnapshotAge ?? int.MaxValue))
            .Add("Weapon refresh age / 武器刷新年龄", "Performance.WeaponRefreshAge",
                DisplayAge(roomContext?.WeaponRefreshAge ?? int.MaxValue))
            .Add("Room refresh count / 房间刷新次数", "Performance.RoomRefreshCount",
                roomContext?.RefreshCount ?? 0)
            .Add("Creature scans / 生物扫描次数", "Performance.CreatureScanCount",
                roomContext?.CreatureScanCount ?? 0)
            .Add("Physical object scans / 物体扫描次数", "Performance.PhysicalObjectScanCount",
                roomContext?.PhysicalObjectScanCount ?? 0)
            .Add("Cached bats / 缓存蝠蝇", "Performance.CachedBats",
                roomContext?.CachedBatCount ?? 0)
            .Add("Cached players / 缓存玩家", "Performance.CachedPlayers",
                roomContext?.CachedPlayerCount ?? 0)
            .Add("Cached weapons / 缓存武器", "Performance.CachedWeapons",
                roomContext?.CachedWeaponCount ?? 0)
            .Add("Cached thrown weapons / 缓存投掷武器", "Performance.CachedThrownWeapons",
                roomContext?.CachedThrownWeaponCount ?? 0)
            .Add("Social refresh age / 社交刷新年龄", "Performance.SocialRefreshAge",
                DisplayAge(socialRoomState?.RefreshAge ?? int.MaxValue))
            .Add("Social candidates / 社交候选", "Performance.SocialCandidateCount",
                socialRoomState?.CachedCandidateCount ?? 0)
            .Add("Social reservations / 社交占位", "Performance.SocialReservationCount",
                socialRoomState?.CachedReservationCount ?? 0)
            .Add("Weather refresh age / 天气刷新年龄", "Performance.WeatherRefreshAge",
                DisplayAge(roomState?.WeatherSampleAge ?? int.MaxValue))
            .Add("Crowding refresh age / 拥挤刷新年龄", "Performance.CrowdingRefreshAge",
                DisplayAge(roomState?.CrowdingSampleAge ?? int.MaxValue))
            .Add("Shelter build count / 避险点构建次数", "Performance.ShelterBuildCount",
                roomState?.AnchorBuildCount ?? 0)
            .Add("Cached shelter anchors / 缓存避险点", "Performance.ShelterAnchorCount",
                roomState?.Anchors.Count ?? 0)
            .Add("Spike probe active / 峰值探针激活", "Performance.SpikeProbeActive",
                performanceActive)
            .Add("Threat cue refreshes this tick / 本tick威胁感知刷新", "Performance.ThreatCueThisTick",
                performance.ThreatCueThisTick)
            .Add("Threat cue steady peak / 威胁感知稳态峰值", "Performance.ThreatCuePeakAfterWarmup",
                performance.ThreatCuePeakAfterWarmup)
            .Add("Threat cue refresh total / 威胁感知刷新总数", "Performance.ThreatCueTotal",
                performance.ThreatCueTotal)
            .Add("Threat probe age / 威胁探针年龄", "Performance.ThreatCueAgeTicks",
                performance.ThreatCueAgeTicks)
            .Add("Trauma scans this tick / 本tick创伤扫描", "Performance.TraumaScanThisTick",
                performance.TraumaScanThisTick)
            .Add("Trauma steady peak / 创伤扫描稳态峰值", "Performance.TraumaScanPeakAfterWarmup",
                performance.TraumaScanPeakAfterWarmup)
            .Add("Trauma scan total / 创伤扫描总数", "Performance.TraumaScanTotal",
                performance.TraumaScanTotal)
            .Add("Trauma probe age / 创伤探针年龄", "Performance.TraumaScanAgeTicks",
                performance.TraumaScanAgeTicks));

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Environment environmental behavior / 环境行为",
            context.Phase == DB_EnvironmentPhase.Calm
                ? AIDebugDecisionState.Inactive
                : influence.HardSurvival || influence.ShelterDrive > 0.25f ||
                  influence.HeatAgitation > 0.20f || influence.NavigationUncertainty > 0.15f
                    ? AIDebugDecisionState.Active
                    : AIDebugDecisionState.Inactive,
            $"{context.Weather}/{context.Phase}; shelter={influence.ShelterDrive:0.00}; " +
            $"heat={influence.HeatAgitation:0.00}/{influence.ThermalExhaustion:0.00}; " +
            $"home={influence.HomeReturnDrive:0.00}; burrow={influence.BurrowDrive:0.00}; " +
            influence.DecisionReason,
            "DB_EnvironmentRuntime"));

        return snapshot;
    }

    private static int DisplayAge(int age) => age == int.MaxValue ? -1 : age;
}
