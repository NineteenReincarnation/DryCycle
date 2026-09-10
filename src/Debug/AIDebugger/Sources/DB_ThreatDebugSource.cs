using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Threat Observatory enrichment. Social remains the inner source so colony, travel
/// and neutral social diagnostics stay visible while this layer explains learned threat
/// signatures, current observable cues and short-lived acute events.
/// </summary>
internal sealed class DB_ThreatDebugSource : IAIDebugSource
{
    private readonly DB_SocialDebugSource inner = new();

    public int Priority => 1300;
    public bool CanInspect(AbstractCreature creature) => inner.CanInspect(creature);

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        AIDebugSnapshot snapshot = inner.Capture(creature, game);
        if (snapshot == null || creature?.realizedCreature is not DB_Creature bat)
            return snapshot;

        bool hasThreat = DB_ThreatRuntime.TryGetDebugState(
            bat, out DB_ThreatDebugState threat);

        var memory = new AIDebugSection("Threat Threat Signature Memory / 威胁特征记忆")
            .Add("Target player slot / 玩家槽", "ThreatMemory.PlayerSlot",
                hasThreat ? threat.PlayerSlot : -1)
            .Add("Confidence / 置信度", "ThreatMemory.Confidence",
                hasThreat ? threat.Confidence : 0f)
            .Add("Projectile / 投射", "ThreatMemory.ProjectilePressure",
                hasThreat ? threat.ProjectilePressure : 0f)
            .Add("Piercing / 穿刺", "ThreatMemory.PiercingPressure",
                hasThreat ? threat.PiercingPressure : 0f)
            .Add("Blunt stun / 钝击眩晕", "ThreatMemory.BluntStunPressure",
                hasThreat ? threat.BluntStunPressure : 0f)
            .Add("Explosion / 爆炸", "ThreatMemory.ExplosionPressure",
                hasThreat ? threat.ExplosionPressure : 0f)
            .Add("Startle / 惊吓", "ThreatMemory.StartlePressure",
                hasThreat ? threat.StartlePressure : 0f)
            .Add("Shock / 电击", "ThreatMemory.ShockPressure",
                hasThreat ? threat.ShockPressure : 0f)
            .Add("Area denial / 区域封锁", "ThreatMemory.AreaDenialPressure",
                hasThreat ? threat.AreaDenialPressure : 0f)
            .Add("Grab capture / 抓捕", "ThreatMemory.GrabCapturePressure",
                hasThreat ? threat.GrabCapturePressure : 0f)
            .Add("Pursuit / 追击", "ThreatMemory.PursuitPressure",
                hasThreat ? threat.PursuitPressure : 0f)
            .Add("Counter kill / 反杀", "ThreatMemory.CounterKillPressure",
                hasThreat ? threat.CounterKillPressure : 0f)
            .Add("Retreat tendency / 玩家退避", "ThreatMemory.RetreatTendency",
                hasThreat ? threat.RetreatTendency : 0f)
            .Add("Non-aggression / 非攻击信心", "ThreatMemory.NonAggressionConfidence",
                hasThreat ? threat.NonAggressionConfidence : 0f)
            .Add("Dominant signature / 主特征", "ThreatMemory.DominantSignature",
                hasThreat && !string.IsNullOrEmpty(threat.DominantSignature)
                    ? threat.DominantSignature : "None");
        snapshot.Sections.Add(memory);

        DB_ThreatCue cue = hasThreat ? threat.Cue : default;
        DB_PerceptionRuntime perception = bat.DesertAI?.Perception;
        Player observedPlayer = null;
        DB_HeldThreatObservation held = default;
        if (perception != null && perception.TryGetObservedPlayer(
                bat.DesertAI?.Target as Player, 430f, out observedPlayer))
            perception.TryGetHeldThreats(observedPlayer, out held);
        DB_ProjectilePercept incoming = perception?.Snapshot.IncomingProjectile ?? default;

        var current = new AIDebugSection("Perception Current Threat Facts / 当前威胁观察")
            .Add("Observed player / 可见玩家", "PerceptionThreat.ObservedPlayer",
                observedPlayer != null ? observedPlayer.playerState?.playerNumber ?? -1 : -1)
            .Add("Visible spear / 可见矛", "PerceptionThreat.VisibleSpear", held.VisibleSpear)
            .Add("Visible rock / 可见石头", "PerceptionThreat.VisibleRock", held.VisibleRock)
            .Add("Visible explosive / 可见爆炸物", "PerceptionThreat.VisibleExplosive", held.VisibleExplosive)
            .Add("Visible startle / 可见惊吽物", "PerceptionThreat.VisibleStartle", held.VisibleStartle)
            .Add("Visible shock / 可见电击物", "PerceptionThreat.VisibleShock", held.VisibleShock)
            .Add("Incoming projectile / 投射物威胁", "PerceptionThreat.ProjectileThreat", incoming.Valid)
            .Add("Projectile direction / 投射方向", "PerceptionThreat.ProjectileDirection",
                incoming.Valid ? incoming.Observation.Velocity.normalized.ToString() : "—");
        snapshot.Sections.Add(current);

        var temporal = new AIDebugSection("Threat Temporal Cue / 历史威胁线索")
            .Add("Recent spear throw / 近期投矛", "ThreatCue.RecentSpearThrow", cue.RecentSpearThrow)
            .Add("Recent rock throw / 近期投石", "ThreatCue.RecentRockThrow", cue.RecentRockThrow)
            .Add("Recent explosion / 近期爆炸", "ThreatCue.RecentExplosion", cue.RecentExplosion)
            .Add("Recent grab / 近期抓取", "ThreatCue.RecentGrabAttempt", cue.RecentGrabAttempt)
            .Add("Player retreating / 玩家退避", "ThreatCue.PlayerRetreating", cue.PlayerRetreating)
            .Add("Hazard center / 危险中心", "ThreatCue.CurrentHazardCenter",
                cue.CurrentHazardCenter.HasValue ? cue.CurrentHazardCenter.Value.ToString() : "—");
        snapshot.Sections.Add(temporal);

        var acute = new AIDebugSection("Threat Acute Event State / 急性事件")
            .Add("Explosion timer / 爆炸", "ThreatAcute.ExplosionTimer",
                hasThreat ? threat.AcuteExplosionTimer : 0)
            .Add("Startle timer / 惊吓", "ThreatAcute.StartleTimer",
                hasThreat ? threat.AcuteStartleTimer : 0)
            .Add("Mass casualty / 群体伤亡", "ThreatAcute.MassCasualtyTimer",
                hasThreat ? threat.AcuteMassCasualtyTimer : 0)
            .Add("Capture timer / 抓捕", "ThreatAcute.CaptureTimer",
                hasThreat ? threat.AcuteCaptureTimer : 0)
            .Add("Shock timer / 电击", "ThreatAcute.ShockTimer",
                hasThreat ? threat.AcuteShockTimer : 0)
            .Add("Hazard center / 危险中心", "ThreatAcute.HazardCenter",
                hasThreat && threat.HazardCenter.HasValue ? threat.HazardCenter.Value.ToString() : "—");
        snapshot.Sections.Add(acute);

        var decision = new AIDebugSection("Threat Tactical Adaptation / 战术修正")
            .Add("Modifier reason / 修正原因", "ThreatDecision.ModifierReason",
                hasThreat && !string.IsNullOrEmpty(threat.ModifierReason) ? threat.ModifierReason : "—")
            .Add("Geometry / 攻击几何", "ThreatDecision.AttackGeometryAdjustment",
                hasThreat && !string.IsNullOrEmpty(threat.AttackGeometryAdjustment)
                    ? threat.AttackGeometryAdjustment : "—")
            .Add("Attach suppression / 附着抑制", "ThreatDecision.AttachSuppression",
                hasThreat ? threat.AttachSuppression : 0f)
            .Add("Evade target / 规避目标", "ThreatDecision.EvadeTarget",
                hasThreat && threat.EvadeTarget.HasValue ? threat.EvadeTarget.Value.ToString() : "—")
            .Add("Last evidence / 最近证据", "ThreatDecision.LastEvidenceType",
                hasThreat && !string.IsNullOrEmpty(threat.LastEvidenceType) ? threat.LastEvidenceType : "—")
            .Add("Evidence strength / 证据强度", "ThreatDecision.LastEvidenceStrength",
                hasThreat ? threat.LastEvidenceStrength : 0f)
            .Add("Evidence player / 证据玩家", "ThreatDecision.LastEvidencePlayerSlot",
                hasThreat ? threat.LastEvidencePlayerSlot : -1)
            .Add("Witness reason / 目击原因", "ThreatDecision.LastWitnessReason",
                hasThreat && !string.IsNullOrEmpty(threat.LastWitnessReason) ? threat.LastWitnessReason : "—");
        snapshot.Sections.Add(decision);

        bool acuteActive = incoming.Valid || hasThreat &&
            (threat.AcuteExplosionTimer > 0 || threat.AcuteStartleTimer > 0 ||
             threat.AcuteMassCasualtyTimer > 0 || threat.AcuteCaptureTimer > 0 ||
             threat.AcuteShockTimer > 0);
        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "Threat learned threat / 威胁学习",
            acuteActive || (hasThreat && !string.IsNullOrEmpty(threat.ModifierReason))
                ? AIDebugDecisionState.Active
                : hasThreat && threat.Confidence > 0.02f
                    ? AIDebugDecisionState.Inactive
                    : AIDebugDecisionState.Blocked,
            hasThreat
                ? $"slot={threat.PlayerSlot}; {threat.DominantSignature}; confidence={threat.Confidence:0.00}; {threat.ModifierReason}"
                : "no realized Threat state",
            "DB_ThreatRuntime"));

        return snapshot;
    }
}
