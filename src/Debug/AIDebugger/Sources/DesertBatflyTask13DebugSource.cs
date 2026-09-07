using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal sealed class DesertBatflyTask13DebugSource : IAIDebugSource
{
    private readonly DesertBatflyTask12DebugSource inner = new();

    public int Priority => 1500;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DesertBatfly bat || bat.room == null)
            return snapshot;

        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState =
            DesertBatflyEnvironmentalRoomRuntime.For(bat.room);
        DesertBatflyEnvironmentalRoomContext context = roomState?.Context ??
            DesertBatflyEnvironmentalRoomContext.Calm;
        bool hasInfluence = DesertBatflyEnvironmentalBehavior.TryGetInfluence(
            bat, out DesertBatflyEnvironmentalInfluence influence);
        if (!hasInfluence) influence = DesertBatflyEnvironmentalInfluence.Neutral;

        int anchorId = 0;
        float anchorCrowding = 0f;
        float anchorWeatherQuality = 0f;
        if (roomState != null && influence.PreferredShelterPoint.HasValue)
        {
            Vector2 preferred = influence.PreferredShelterPoint.Value;
            float best = float.MaxValue;
            for (int i = 0; i < roomState.Anchors.Count; i++)
            {
                DesertBatflyShelterAnchor anchor = roomState.Anchors[i];
                float dist = (anchor.Position - preferred).sqrMagnitude;
                if (dist >= best) continue;
                best = dist;
                anchorId = anchor.Id;
                anchorCrowding = anchor.Crowding;
                anchorWeatherQuality = DesertBatflyEnvironmentalRoomRuntime.WeatherQuality(
                    anchor, context.Weather);
            }
        }

        bool hasFailure = DesertBatflyEnvironmentalRoomRuntime.TryGetShelterFailureDebug(
            bat.room,
            out int failureTicks,
            out float failureSeverity,
            out string failureReason);
        bool task09Owns = DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature);

        snapshot.Sections.Add(new AIDebugSection("Task 13 Environment / 环境活动")
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
            .Add("Task09 intent / Task09跨房意图", "Environment.Task09OwnsControl", task09Owns)
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

        snapshot.Sections.Add(new AIDebugSection("Task 13 Visibility & Heat / 视野与热")
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

        snapshot.Sections.Add(new AIDebugSection("Task 13 Shelter / 局部避险")
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

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Task 13 environmental behavior / 环境行为",
            context.Phase == DesertBatflyEnvironmentalPhase.Calm
                ? AIDebugDecisionState.Inactive
                : influence.HardSurvival || influence.ShelterDrive > 0.25f ||
                  influence.HeatAgitation > 0.20f || influence.NavigationUncertainty > 0.15f
                    ? AIDebugDecisionState.Active
                    : AIDebugDecisionState.Inactive,
            $"{context.Weather}/{context.Phase}; shelter={influence.ShelterDrive:0.00}; " +
            $"heat={influence.HeatAgitation:0.00}/{influence.ThermalExhaustion:0.00}; " +
            $"home={influence.HomeReturnDrive:0.00}; burrow={influence.BurrowDrive:0.00}; " +
            influence.DecisionReason,
            "DesertBatflyEnvironmentalBehavior"));

        return snapshot;
    }
}
