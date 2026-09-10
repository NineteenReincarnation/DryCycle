using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Perception R2 receiver. Owns current direct observations, ranked attention, bounded lost
/// tracks, projectile risk and received-signal beliefs. It never owns locomotion or behavior
/// arbitration. Signal transport still owns emission, packet lifetime and relay creation.
/// </summary>
internal class DB_PerceptionRuntime
{
    internal const int ScanIntervalTicks = 8;
    internal const float ProjectileScanRange = 240f;
    internal const float ProjectileMissRadius = 38f;
    internal const float ProjectileMinimumVelocitySqr = 1.6f;
    internal const int SignalGenerationHistorySize = 12;
    internal const int SignalScanMinTicks = 14;
    internal const int SignalScanMaxTicks = 24;
    internal const float ReportedThreatDangerThreshold = 0.30f;
    internal const float ReportedAnonymousHazardThreshold = 0.34f;

    private readonly DB_AI brain;
    private readonly DB_Creature fly;
    private const int ObservedPlayerCapacity = 4;
    private readonly int[] pursuitByPlayer = new int[ObservedPlayerCapacity];
    private readonly Player[] observedPlayers = new Player[ObservedPlayerCapacity];
    private readonly float[] observedPlayerDistances = new float[ObservedPlayerCapacity];
    private readonly DB_HeldThreatObservation[] observedHeldThreats = new DB_HeldThreatObservation[ObservedPlayerCapacity];
    private readonly int[] signalGenerations = new int[SignalGenerationHistorySize];

    private int scan;
    private int creatureScanCount;
    private int projectileScanCount;
    private int signalScanCount;
    private int observedPlayersTick = int.MinValue;
    private DB_PerceptionTrack primaryThreat;
    private DB_PerceptionTrack secondaryThreat;
    private DB_PerceptionTrack lostThreat;
    private DB_ProjectilePercept incomingProjectile;
    private string lastAttentionReason = "not scanned";

    private bool signalGenerationInitialized;
    private int signalGenerationCursor;
    private int nextSignalScan;
    private int signalScanSerial;
    private float alarmPressure;
    private Vector2 alarmOrigin;
    private Creature alarmThreat;
    private float distressInterest;
    private DB_Creature distressSource;
    private float rallyInterest;
    private DB_Creature rallySource;
    private Creature rallyTarget;
    private float roostInterest;
    private DB_Creature roostSource;
    private float harassInterest;
    private DB_Creature harassSource;
    private Player harassTarget;
    private float safeConfidence;
    private int lastSignalAlarmTick = int.MinValue;
    private int lastSignalGeneration;
    private DB_SignalKind lastSignalKind;
    private DB_PerceptionModality lastSignalModality;
    private int lastSignalHop;
    private string lastSignalDecision = "no signal received";

    internal DB_PerceptionRuntime(DB_AI brain, DB_Creature fly)
    {
        this.brain = brain;
        this.fly = fly;
        ResetScanPhase();
    }

    internal Creature Danger
    {
        get
        {
            if (primaryThreat.DirectObservation && primaryThreat.Valid) return primaryThreat.Target;
            if (alarmPressure >= ReportedThreatDangerThreshold &&
                alarmThreat != null && !alarmThreat.dead && alarmThreat.room == fly?.room)
                return alarmThreat;
            return null;
        }
    }

    internal bool IsScanFrame => scan == 0;
    internal int LastSignalAlarmTick => lastSignalAlarmTick;
    internal int LastSignalGeneration => lastSignalGeneration;
    internal DB_SignalKind LastSignalKind => lastSignalKind;
    internal DB_PerceptionModality LastSignalModality => lastSignalModality;
    internal int LastSignalHop => lastSignalHop;
    internal string LastSignalDecision => lastSignalDecision;
    internal bool HasReportedAnonymousHazard =>
        alarmPressure >= ReportedAnonymousHazardThreshold && alarmThreat == null;

    internal int PursuitTicks
    {
        get
        {
            int best = 0;
            for (int i = 0; i < pursuitByPlayer.Length; i++) best = Mathf.Max(best, pursuitByPlayer[i]);
            return best;
        }
    }

    internal DB_PerceptionSnapshot Snapshot => new(
        fly?.room?.game?.clock ?? 0,
        primaryThreat,
        secondaryThreat,
        lostThreat,
        incomingProjectile,
        BuildSignalContext());

    internal void Reset()
    {
        for (int i = 0; i < pursuitByPlayer.Length; i++) pursuitByPlayer[i] = 0;
        ClearObservedPlayers();
        for (int i = 0; i < signalGenerations.Length; i++) signalGenerations[i] = int.MinValue;
        primaryThreat = default;
        secondaryThreat = default;
        lostThreat = default;
        incomingProjectile = default;
        lastAttentionReason = "reset";
        signalGenerationInitialized = false;
        signalGenerationCursor = 0;
        nextSignalScan = 0;
        signalScanSerial = 0;
        alarmPressure = 0f;
        alarmOrigin = Vector2.zero;
        alarmThreat = null;
        distressInterest = 0f;
        distressSource = null;
        rallyInterest = 0f;
        rallySource = null;
        rallyTarget = null;
        roostInterest = 0f;
        roostSource = null;
        harassInterest = 0f;
        harassSource = null;
        harassTarget = null;
        safeConfidence = 0f;
        lastSignalAlarmTick = int.MinValue;
        lastSignalGeneration = 0;
        lastSignalKind = default;
        lastSignalModality = DB_PerceptionModality.None;
        lastSignalHop = 0;
        lastSignalDecision = "reset";
        ResetScanPhase();
    }

    internal void ClearPursuit()
    {
        for (int i = 0; i < pursuitByPlayer.Length; i++) pursuitByPlayer[i] = 0;
    }

    /// <summary>
    /// Per-frame receiver refresh. Projectile risk and lost-track decay are current; ordinary
    /// creature LOS and non-urgent signal packets retain independent staggered cadences.
    /// </summary>
    internal void RefreshState()
    {
        if (fly?.room == null || fly.mainBodyChunk == null)
        {
            incomingProjectile = default;
            ClearObservedPlayers();
            return;
        }

        RefreshIncomingProjectile();
        RefreshLostTrack();
        TickSignalInfluence();
        RefreshSignalReception();

        if (++scan < ScanIntervalTicks) return;
        scan = 0;
        ScanCreatures();
    }

    // Temporary call-shape compatibility while DB_AI is migrated to the R2 type directly.
    internal void UpdateScan() => RefreshState();

    internal bool Valid(Creature creature)
    {
        return creature != null && !creature.dead &&
               !creature.slatedForDeletetion && creature.room == fly.room &&
               !creature.inShortcut && creature.grabbedBy.Count == 0 &&
               (creature.abstractCreature.rippleLayer == fly.abstractCreature.rippleLayer ||
                creature.abstractCreature.rippleBothSides ||
                fly.abstractCreature.rippleBothSides);
    }

    internal bool TryGetIncomingProjectile(out DB_WeaponObservation observation)
    {
        observation = incomingProjectile.Observation;
        return incomingProjectile.Valid;
    }

    internal bool TryGetObservedPlayer(Player preferred, float maxDistance, out Player player)
    {
        player = null;
        if (!ObservedPlayersFresh() || maxDistance < 0f) return false;

        int preferredSlot = ObservedPlayerSlot(preferred);
        if (preferredSlot >= 0 &&
            ReferenceEquals(observedPlayers[preferredSlot], preferred) &&
            observedPlayerDistances[preferredSlot] <= maxDistance &&
            ValidObservedPlayer(preferred))
        {
            player = preferred;
            return true;
        }

        float bestDistance = float.MaxValue;
        int bestSlot = int.MaxValue;
        for (int i = 0; i < observedPlayers.Length; i++)
        {
            Player candidate = observedPlayers[i];
            float distance = observedPlayerDistances[i];
            if (!ValidObservedPlayer(candidate) || distance > maxDistance) continue;
            if (distance > bestDistance + 0.0001f ||
                (Mathf.Abs(distance - bestDistance) <= 0.0001f && i >= bestSlot))
                continue;
            player = candidate;
            bestDistance = distance;
            bestSlot = i;
        }
        return player != null;
    }

    internal bool TryGetObservedPlayerBySlot(int slot, float maxDistance, out Player player)
    {
        player = null;
        if (!ObservedPlayersFresh() || maxDistance < 0f ||
            slot < 0 || slot >= observedPlayers.Length)
            return false;
        Player candidate = observedPlayers[slot];
        if (!ValidObservedPlayer(candidate) || observedPlayerDistances[slot] > maxDistance)
            return false;
        player = candidate;
        return true;
    }

    internal bool TryGetHeldThreats(Player player, out DB_HeldThreatObservation observation)
    {
        observation = default;
        if (!ObservedPlayersFresh() || !ValidObservedPlayer(player)) return false;
        int slot = ObservedPlayerSlot(player);
        if (slot < 0 || !ReferenceEquals(observedPlayers[slot], player)) return false;
        observation = observedHeldThreats[slot];
        return true;
    }

    internal bool TryGetSignalContext(out DB_PerceptionSignalContext context)
    {
        context = BuildSignalContext();
        return alarmPressure > 0f || distressInterest > 0f || rallyInterest > 0f ||
               roostInterest > 0f || harassInterest > 0f || safeConfidence > 0f;
    }

    internal bool TryGetDebugState(out DB_PerceptionDebugState debug)
    {
        debug = new DB_PerceptionDebugState(
            Snapshot,
            creatureScanCount,
            projectileScanCount,
            signalScanCount,
            lastAttentionReason);
        return fly != null;
    }

    /// <summary>
    /// Receives an already transported packet. This method only updates perception beliefs and
    /// relay willingness; it never calls ThreatenedAt, cancels Social or writes movement.
    /// </summary>
    internal bool ReceiveSignal(DB_SignalPacket packet, out bool relayAlarm)
    {
        relayAlarm = false;
        if (!AvailableForSignals() || !fly.Consious || packet == null ||
            packet.Emitter == fly || packet.Emitter?.room != fly.room ||
            packet.Expired(fly.room.game?.clock ?? 0))
            return false;
        if (HasSignalGeneration(packet.Generation)) return false;
        if (!TryPerceiveSignal(packet, out DB_PerceptionModality modality, out float attenuation))
            return false;

        RememberSignalGeneration(packet.Generation);
        float response = SignalResponseStrength(packet, modality, attenuation);
        lastSignalGeneration = packet.Generation;
        lastSignalKind = packet.Kind;
        lastSignalModality = modality;
        lastSignalHop = packet.Hop;

        if (response <= 0.015f)
        {
            lastSignalDecision = "perceived but receiver confidence was negligible";
            TraceSignalReceive(packet, response);
            return true;
        }

        switch (packet.Kind)
        {
            case DB_SignalKind.AlarmFlutter:
                alarmPressure = Mathf.Max(alarmPressure, response);
                alarmOrigin = packet.Origin;
                alarmThreat = ValidReportedThreat(packet.Threat) ? packet.Threat : null;
                lastSignalAlarmTick = fly.room.game?.clock ?? 0;
                lastSignalDecision = alarmThreat != null
                    ? "accepted reported threat belief"
                    : "accepted anonymous hazard belief";
                DB_SignalDefinition definition = DB_SignalDefinition.For(packet.Kind);
                relayAlarm = definition.CanRelay && packet.Hop < definition.MaxRelayHops &&
                             ShouldRelayAlarm(packet, response);
                break;

            case DB_SignalKind.DistressCall:
                distressInterest = Mathf.Max(distressInterest, response);
                distressSource = packet.Subject ?? packet.Emitter;
                lastSignalDecision = "accepted distress interest; behavior domains retain authority";
                break;

            case DB_SignalKind.RallySignal:
                rallyInterest = Mathf.Max(rallyInterest, response);
                rallySource = packet.Emitter;
                rallyTarget = packet.Threat;
                lastSignalDecision = "accepted rally target belief";
                break;

            case DB_SignalKind.RoostCall:
                roostInterest = Mathf.Max(roostInterest, response);
                roostSource = packet.Emitter;
                lastSignalDecision = "accepted roost interest";
                break;

            case DB_SignalKind.HarassSignal:
                harassInterest = Mathf.Max(harassInterest, response);
                harassSource = packet.Emitter;
                harassTarget = packet.PlayerTarget;
                lastSignalDecision = "accepted harass target interest";
                break;

            case DB_SignalKind.SafeSignal:
                if (CanAcceptSafeSignal())
                {
                    safeConfidence = Mathf.Max(safeConfidence, response);
                    // Safe reports may only relax signal-derived uncertainty. Direct visual,
                    // projectile, event and persistent Threat memory facts are untouched.
                    alarmPressure *= Mathf.Lerp(1f, 0.48f, response);
                    lastSignalDecision = "accepted SafeSignal for reported hazard only";
                }
                else
                {
                    lastSignalDecision = "ignored SafeSignal because direct/current danger remains";
                }
                break;
        }

        TraceSignalReceive(packet, response);
        return true;
    }

    internal void NoteLocalAlarm(Creature threat, Vector2 origin, float intensity)
    {
        if (!AvailableForSignals()) return;
        alarmPressure = Mathf.Max(alarmPressure, Mathf.Clamp01(intensity));
        alarmOrigin = origin;
        alarmThreat = ValidReportedThreat(threat) ? threat : null;
        lastSignalAlarmTick = fly.room.game?.clock ?? 0;
        lastSignalDecision = "local direct alarm recorded as receiver context";
    }

    internal bool CanAcceptSafeSignal()
    {
        if (!AvailableForSignals() || !fly.Consious || brain == null) return false;
        if (primaryThreat.DirectObservation && primaryThreat.Valid) return false;
        if (incomingProjectile.Valid) return false;
        if (brain.Mode == DB_AI.Activity.Escape || brain.RetreatActive) return false;
        if (fly.Injury.IsSeverelyInjured || DB_TravelRuntime.HasIntent(fly.abstractCreature)) return false;
        if (DB_ThreatRuntime.TryGetDebugState(fly, out DB_ThreatDebugState threat) &&
            (threat.AcuteExplosionTimer > 0 || threat.AcuteStartleTimer > 0 ||
             threat.AcuteMassCasualtyTimer > 0 || threat.AcuteCaptureTimer > 0 ||
             threat.AcuteShockTimer > 0))
            return false;
        return !DB_FearRuntime.HasActiveFearSuppression(fly);
    }

    private void ScanCreatures()
    {
        creatureScanCount++;
        BeginObservedPlayerScan();
        DB_PerceptionTrack previousPrimary = primaryThreat;
        DB_PerceptionTrack directBest = default;
        DB_PerceptionTrack directSecond = default;
        DB_PerceptionTrack previousVisible = default;
        float bestScore = 0f;
        float secondScore = 0f;
        int bestKey = int.MaxValue;
        int secondKey = int.MaxValue;

        brain.Combat.BeginCandidateScan();
        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var creatures = context?.Creatures;
        if (creatures == null)
        {
            brain.Combat.CompleteCandidateScan(brain.RetreatActive);
            ClearDirectThreats(previousPrimary, false);
            return;
        }

        Vector2 origin = fly.mainBodyChunk.pos;
        float sightRangeSqr = DB_Tuning.SightRange * DB_Tuning.SightRange;
        float visibility = Mathf.Clamp01(DB_EnvironmentRuntime.VisibilityScale(fly));

        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            if (creature == fly || creature is DB_Creature || !Valid(creature) || creature.mainBodyChunk == null)
                continue;

            Vector2 delta = creature.mainBodyChunk.pos - origin;
            if (delta.sqrMagnitude > sightRangeSqr) continue;

            DB_VisibilityChannel channel = creature is Player
                ? DB_VisibilityChannel.Player
                : DB_VisibilityChannel.Creature;
            if (!DB_VisibilityPolicy.CanObserve(
                    fly, creature.mainBodyChunk.pos, DB_Tuning.SightRange, channel))
                continue;

            float distance = Vector2.Distance(origin, creature.mainBodyChunk.pos);
            brain.Combat.ConsiderCandidate(creature, distance);
            if (creature is Player observedPlayer)
            {
                RecordObservedPlayer(observedPlayer, distance);
                TrackPlayerApproach(observedPlayer, distance);
            }

            if (!TryBuildThreatTrack(creature, distance, visibility, out DB_PerceptionTrack track))
                continue;

            int key = StableCreatureKey(creature);
            if (previousPrimary.Target != null && ReferenceEquals(previousPrimary.Target, creature))
                previousVisible = track;

            if (DB_PerceptionScoring.BetterScore(track.AttentionScore, key, bestScore, bestKey))
            {
                directSecond = directBest;
                secondScore = bestScore;
                secondKey = bestKey;
                directBest = track;
                bestScore = track.AttentionScore;
                bestKey = key;
            }
            else if (DB_PerceptionScoring.BetterScore(track.AttentionScore, key, secondScore, secondKey))
            {
                directSecond = track;
                secondScore = track.AttentionScore;
                secondKey = key;
            }
        }

        brain.Combat.CompleteCandidateScan(brain.RetreatActive);

        if (previousVisible.Valid && directBest.Valid &&
            !ReferenceEquals(previousVisible.Target, directBest.Target) &&
            !DB_PerceptionScoring.ShouldSwitchAttention(
                previousVisible.AttentionScore,
                directBest.AttentionScore,
                previousVisible.Confidence,
                false))
        {
            DB_PerceptionTrack challenger = directBest;
            directBest = previousVisible;
            if (!ReferenceEquals(challenger.Target, directBest.Target)) directSecond = challenger;
            lastAttentionReason = "kept current direct threat by attention hysteresis";
        }
        else if (directBest.Valid)
        {
            lastAttentionReason = previousPrimary.Valid &&
                                  !ReferenceEquals(previousPrimary.Target, directBest.Target)
                ? "switched to higher-scoring direct threat"
                : "retained highest-scoring direct threat";
        }

        bool previousStillVisible = previousVisible.Valid;
        if (!previousStillVisible && previousPrimary.Valid && previousPrimary.DirectObservation)
            BeginLostTrack(previousPrimary);
        else if (previousStillVisible && ReferenceEquals(lostThreat.Target, previousVisible.Target))
            lostThreat = default;

        primaryThreat = directBest;
        secondaryThreat = directSecond;
        if (!directBest.Valid && !previousStillVisible)
            lastAttentionReason = lostThreat.Valid ? "direct threat lost; bounded prediction retained" : "no directly observed threat";
    }

    private void ClearDirectThreats(in DB_PerceptionTrack previousPrimary, bool previousStillVisible)
    {
        if (!previousStillVisible && previousPrimary.Valid && previousPrimary.DirectObservation)
            BeginLostTrack(previousPrimary);
        primaryThreat = default;
        secondaryThreat = default;
        lastAttentionReason = lostThreat.Valid ? "direct threat lost; bounded prediction retained" : "no directly observed threat";
    }

    private bool TryBuildThreatTrack(
        Creature creature,
        float distance,
        float visibility,
        out DB_PerceptionTrack track)
    {
        track = default;
        CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
        CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
        bool predator = creature is not Player &&
            (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
             reverse.type == CreatureTemplate.Relationship.Type.Eats ||
             reverse.type == CreatureTemplate.Relationship.Type.Attacks);

        float threatDistance;
        float relationshipDanger;
        float memoryBias = 0f;
        DB_PerceptionSource source = creature is Player
            ? DB_PerceptionSource.DirectPlayer
            : DB_PerceptionSource.DirectCreature;

        if (predator)
        {
            float ordinaryThreatDistance = Mathf.Lerp(90f, 260f, Mathf.Clamp01(creature.TotalMass));
            float nerveScale = Mathf.Lerp(1.15f, 0.58f, fly.Personality.Nerve);
            threatDistance = Mathf.Max(55f, ordinaryThreatDistance * nerveScale);
            if (distance >= threatDistance) return false;
            relationshipDanger = relation.type == CreatureTemplate.Relationship.Type.Afraid
                ? Mathf.Clamp01(Mathf.Max(0.45f, relation.intensity))
                : 0.72f;
        }
        else if (creature is Player player)
        {
            bool traumatized = brain.IsTraumatizedPlayer(player);
            bool remembered = !traumatized && brain.IsRememberedPlayer(player);
            if (!remembered || fly.Personality.Aggressive) return false;

            threatDistance = Mathf.Lerp(
                DB_Tuning.GrabFearMinDistance,
                DB_Tuning.GrabFearMaxDistance,
                fly.DesertState.GrabMemoryStrength);
            threatDistance *= Mathf.Lerp(1.12f, 0.72f, fly.Personality.Nerve);
            if (distance >= threatDistance) return false;
            relationshipDanger = Mathf.Lerp(0.46f, 0.88f, fly.DesertState.GrabMemoryStrength);
            memoryBias = fly.DesertState.GrabMemoryStrength;
        }
        else return false;

        Vector2 toFly = Custom.DirVec(creature.mainBodyChunk.pos, fly.mainBodyChunk.pos);
        float closing = Vector2.Dot(creature.mainBodyChunk.vel, toFly);
        float confidence = DB_PerceptionScoring.DirectConfidence(DB_Tuning.SightRange, distance, visibility);
        float actionDanger = Mathf.Clamp01(Mathf.InverseLerp(0.8f, 8f, closing));
        float score = DB_PerceptionScoring.ThreatAttentionScore(
            relationshipDanger,
            distance,
            threatDistance,
            closing,
            actionDanger,
            confidence,
            memoryBias,
            fly.Personality.Nerve);
        if (score <= 0f) return false;

        float urgency = Mathf.Clamp01(score / Mathf.Max(0.12f, confidence));
        int clock = fly.room.game?.clock ?? 0;
        track = new DB_PerceptionTrack(
            creature,
            creature.mainBodyChunk.pos,
            creature.mainBodyChunk.pos,
            creature.mainBodyChunk.vel,
            confidence,
            Mathf.Clamp01(0.35f + actionDanger * 0.35f + relationshipDanger * 0.30f),
            urgency,
            score,
            clock,
            0,
            source,
            DB_PerceptionModality.Visual,
            true);
        return true;
    }

    private void BeginObservedPlayerScan()
    {
        for (int i = 0; i < observedPlayers.Length; i++)
        {
            observedPlayers[i] = null;
            observedPlayerDistances[i] = float.MaxValue;
            observedHeldThreats[i] = default;
        }
        observedPlayersTick = fly?.room?.game?.clock ?? int.MinValue;
    }

    private void ClearObservedPlayers()
    {
        for (int i = 0; i < observedPlayers.Length; i++)
        {
            observedPlayers[i] = null;
            observedPlayerDistances[i] = float.MaxValue;
            observedHeldThreats[i] = default;
        }
        observedPlayersTick = int.MinValue;
    }

    private void RecordObservedPlayer(Player player, float distance)
    {
        int slot = ObservedPlayerSlot(player);
        if (slot < 0 || distance < 0f || distance > observedPlayerDistances[slot]) return;
        observedPlayers[slot] = player;
        observedPlayerDistances[slot] = distance;
        observedHeldThreats[slot] = ObserveHeldThreats(player);
    }

    private DB_HeldThreatObservation ObserveHeldThreats(Player player)
    {
        bool spear = false;
        bool rock = false;
        bool explosive = false;
        bool startle = false;
        bool shock = false;

        if (player?.grasps != null)
        {
            for (int i = 0; i < player.grasps.Length; i++)
            {
                PhysicalObject held = player.grasps[i]?.grabbed;
                if (held == null) continue;
                DB_ThreatEvidence evidence = DB_ThreatClassifier.Classify(held, null, 0f, 0f, false);
                spear |= held is Spear;
                rock |= held is Rock;
                explosive |= evidence.Explosion > 0.15f;
                startle |= evidence.Startle > 0.15f;
                shock |= evidence.Shock > 0.15f;
            }
        }

        return new DB_HeldThreatObservation(spear, rock, explosive, startle, shock);
    }

    private bool ObservedPlayersFresh()
    {
        int clock = fly?.room?.game?.clock ?? int.MinValue;
        return observedPlayersTick != int.MinValue && clock >= observedPlayersTick &&
               clock - observedPlayersTick <= ScanIntervalTicks + 1;
    }

    private bool ValidObservedPlayer(Player player)
        => player != null && !player.dead && !player.slatedForDeletetion &&
           player.room == fly?.room && !player.inShortcut;

    private static int ObservedPlayerSlot(Player player)
    {
        int slot = player?.playerState?.playerNumber ?? -1;
        return slot >= 0 && slot < ObservedPlayerCapacity ? slot : -1;
    }

    private void TrackPlayerApproach(Player player, float distance)
    {
        if (player == null) return;
        int slot = DB_ThreatRuntime.PlayerSlot(player);
        if (!DB_ThreatRuntime.ValidSlot(slot)) slot = Mathf.Clamp(player.playerState?.playerNumber ?? 0, 0, 3);

        bool traumatized = brain.IsTraumatizedPlayer(player);
        bool remembered = !traumatized && brain.IsRememberedPlayer(player);
        float reactionDistance = Mathf.Lerp(125f, 78f, fly.Personality.Nerve);
        float closingThreshold = Mathf.Lerp(2.1f, 4.4f, fly.Personality.Nerve);
        int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(16f, 44f, fly.Personality.Nerve));
        if (remembered && DB_EnvironmentalPolicy.AggressionAuthorized(fly))
        {
            reactionDistance *= 0.72f;
            closingThreshold *= 1.25f;
            pursuitThreshold = Mathf.RoundToInt(pursuitThreshold * 1.35f);
        }

        if (distance < reactionDistance)
        {
            float closing = Vector2.Dot(
                player.mainBodyChunk.vel,
                Custom.DirVec(player.mainBodyChunk.pos, fly.mainBodyChunk.pos));
            pursuitByPlayer[slot] = closing > closingThreshold
                ? Mathf.Min(320, pursuitByPlayer[slot] + 8)
                : Mathf.Max(0, pursuitByPlayer[slot] - 4);
            if (pursuitByPlayer[slot] >= pursuitThreshold)
            {
                brain.DisturbedByApproach(player);
                pursuitByPlayer[slot] = 0;
            }
        }
        else pursuitByPlayer[slot] = Mathf.Max(0, pursuitByPlayer[slot] - 2);
    }

    private void BeginLostTrack(in DB_PerceptionTrack previous)
    {
        if (!previous.Valid || previous.Target == null) return;
        int clock = fly.room?.game?.clock ?? previous.LastObservedTick;
        int age = previous.LastObservedTick == int.MinValue ? 1 : Mathf.Max(1, clock - previous.LastObservedTick);
        float confidence = DB_PerceptionScoring.LostConfidence(previous.Confidence, age);
        Vector2 estimated = DB_PerceptionScoring.PredictLostPosition(
            previous.ObservedPosition,
            previous.ObservedVelocity,
            age);
        lostThreat = new DB_PerceptionTrack(
            previous.Target,
            previous.ObservedPosition,
            estimated,
            previous.ObservedVelocity,
            confidence,
            previous.Salience,
            previous.ThreatUrgency * Mathf.Clamp01(confidence / Mathf.Max(0.01f, previous.Confidence)),
            previous.AttentionScore * Mathf.Clamp01(confidence / Mathf.Max(0.01f, previous.Confidence)),
            previous.LastObservedTick,
            age,
            DB_PerceptionSource.Predicted,
            DB_PerceptionModality.Predicted,
            false);
    }

    private void RefreshLostTrack()
    {
        if (!lostThreat.Valid || lostThreat.Target == null)
        {
            lostThreat = default;
            return;
        }
        if (!ValidLostTarget(lostThreat.Target))
        {
            lostThreat = default;
            return;
        }

        int clock = fly.room?.game?.clock ?? 0;
        int age = lostThreat.LastObservedTick == int.MinValue
            ? DB_PerceptionScoring.LostTrackMaxTicks
            : Mathf.Max(0, clock - lostThreat.LastObservedTick);
        if (age >= DB_PerceptionScoring.LostTrackMaxTicks)
        {
            lostThreat = default;
            return;
        }

        float priorLife = 1f - Mathf.Clamp01(lostThreat.AgeTicks / (float)DB_PerceptionScoring.LostTrackMaxTicks);
        float originalConfidence = priorLife > 0.001f
            ? Mathf.Clamp01(lostThreat.Confidence / (priorLife * priorLife))
            : 0f;
        float confidence = DB_PerceptionScoring.LostConfidence(originalConfidence, age);
        if (confidence <= 0.01f)
        {
            lostThreat = default;
            return;
        }

        Vector2 estimated = DB_PerceptionScoring.PredictLostPosition(
            lostThreat.ObservedPosition,
            lostThreat.ObservedVelocity,
            age);
        float ratio = confidence / Mathf.Max(0.01f, lostThreat.Confidence);
        lostThreat = new DB_PerceptionTrack(
            lostThreat.Target,
            lostThreat.ObservedPosition,
            estimated,
            lostThreat.ObservedVelocity,
            confidence,
            lostThreat.Salience,
            lostThreat.ThreatUrgency * Mathf.Clamp01(ratio),
            lostThreat.AttentionScore * Mathf.Clamp01(ratio),
            lostThreat.LastObservedTick,
            age,
            DB_PerceptionSource.Predicted,
            DB_PerceptionModality.Predicted,
            false);
    }

    private bool ValidLostTarget(Creature target)
    {
        return target != null && !target.dead && !target.slatedForDeletetion &&
               target.room == fly.room && !target.inShortcut;
    }

    private void RefreshIncomingProjectile()
    {
        projectileScanCount++;
        incomingProjectile = default;
        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var weapons = context?.ThrownWeapons;
        if (weapons == null || fly.mainBodyChunk == null) return;

        Vector2 origin = fly.mainBodyChunk.pos;
        float maxDistanceSqr = ProjectileScanRange * ProjectileScanRange;
        float missRadiusSqr = ProjectileMissRadius * ProjectileMissRadius;
        float visibility = Mathf.Clamp01(DB_EnvironmentRuntime.VisibilityScale(fly));
        float bestRisk = 0f;
        float bestClosest = float.MaxValue;
        float bestTime = float.MaxValue;
        int bestKey = int.MaxValue;
        DB_WeaponObservation bestObservation = default;
        float bestConfidence = 0f;

        for (int i = 0; i < weapons.Count; i++)
        {
            Weapon weapon = weapons[i];
            if (weapon == null || weapon.slatedForDeletetion || weapon.firstChunk == null ||
                weapon.mode != Weapon.Mode.Thrown || weapon.thrownBy == fly)
                continue;

            Vector2 position = weapon.firstChunk.pos;
            Vector2 velocity = weapon.firstChunk.vel;
            float velocitySqr = velocity.sqrMagnitude;
            if (velocitySqr < ProjectileMinimumVelocitySqr) continue;
            Vector2 delta = origin - position;
            if (delta.sqrMagnitude > maxDistanceSqr) continue;

            float time = Mathf.Clamp(Vector2.Dot(delta, velocity) / Mathf.Max(1f, velocitySqr), 0f, 5f);
            float closestSqr = (delta - velocity * time).sqrMagnitude;
            if (closestSqr >= missRadiusSqr) continue;
            if (!DB_VisibilityPolicy.CanObserve(
                    fly, position, ProjectileScanRange, DB_VisibilityChannel.Projectile, realProjectile: true))
                continue;

            float distance = Mathf.Sqrt(delta.sqrMagnitude);
            float confidence = DB_PerceptionScoring.DirectConfidence(
                ProjectileScanRange, distance, Mathf.Max(visibility, 0.38f));
            float closest = Mathf.Sqrt(closestSqr);
            float speed = Mathf.Sqrt(velocitySqr);
            Creature instigator = ResolveInstigator(weapon);
            float risk = DB_PerceptionScoring.ProjectileRisk(
                time, closest, ProjectileMissRadius, speed, ProjectileLethality(weapon),
                confidence, InstigatorThreatMemory(instigator));
            int key = StableWeaponKey(weapon);

            bool better = risk > bestRisk + 0.0001f ||
                          (Mathf.Abs(risk - bestRisk) <= 0.0001f &&
                           (time < bestTime - 0.0001f ||
                            (Mathf.Abs(time - bestTime) <= 0.0001f &&
                             (closest < bestClosest - 0.0001f ||
                              (Mathf.Abs(closest - bestClosest) <= 0.0001f && key < bestKey)))));
            if (!better) continue;

            bestRisk = risk;
            bestClosest = closest;
            bestTime = time;
            bestKey = key;
            bestConfidence = confidence;
            bestObservation = new DB_WeaponObservation(
                weapon, instigator, position, velocity, closestSqr, true, false);
        }

        if (bestObservation.Weapon != null)
            incomingProjectile = new DB_ProjectilePercept(
                bestObservation, bestTime, bestClosest, bestRisk, bestConfidence);
    }

    private void RefreshSignalReception()
    {
        if (!AvailableForSignals() || !fly.Consious) return;
        int clock = fly.room.game?.clock ?? 0;
        if (nextSignalScan == 0)
            nextSignalScan = clock + StableInt(0x2B19, SignalScanMinTicks, SignalScanMaxTicks + 1);
        if (clock < nextSignalScan) return;

        nextSignalScan = clock + StableInt(
            0x41C7 + ++signalScanSerial * 31,
            SignalScanMinTicks,
            SignalScanMaxTicks + 1);
        signalScanCount++;

        DB_SignalRoomRuntime.RoomState roomState = DB_SignalRoomRuntime.For(fly.room);
        roomState?.Prune(fly.room);
        if (roomState == null) return;
        for (int i = 0; i < roomState.ActiveSignals.Count; i++)
        {
            DB_SignalPacket packet = roomState.ActiveSignals[i];
            if (packet == null || packet.Emitter == fly || packet.Expired(clock)) continue;
            ReceiveSignal(packet, out _);
        }
    }

    private bool TryPerceiveSignal(
        DB_SignalPacket packet,
        out DB_PerceptionModality modality,
        out float attenuation)
    {
        modality = DB_PerceptionModality.None;
        attenuation = 0f;
        if (packet?.Emitter?.mainBodyChunk == null || fly.mainBodyChunk == null) return false;
        DB_SignalDefinition definition = DB_SignalDefinition.For(packet.Kind);
        float distance = Vector2.Distance(fly.mainBodyChunk.pos, packet.Emitter.mainBodyChunk.pos);
        float visibility = DB_EnvironmentRuntime.VisibilityScale(fly);
        float visualRadius = DB_VisibilityPolicy.EffectiveRange(
            definition.VisualRange, visibility, DB_VisibilityChannel.Signal);

        if (distance <= visualRadius && DB_VisibilityPolicy.CanObserve(
                fly, packet.Emitter.mainBodyChunk.pos, definition.VisualRange, DB_VisibilityChannel.Signal))
        {
            modality = DB_PerceptionModality.Visual;
            attenuation = Mathf.Lerp(1f, 0.34f, Mathf.Clamp01(distance / Mathf.Max(1f, visualRadius)));
            return true;
        }

        float acousticRadius = definition.CloseAcousticRange;
        if (acousticRadius <= 0f || distance > acousticRadius) return false;
        modality = DB_PerceptionModality.Acoustic;
        attenuation = Mathf.Lerp(0.62f, 0.30f, Mathf.Clamp01(distance / acousticRadius));
        return true;
    }

    private float SignalResponseStrength(
        DB_SignalPacket packet,
        DB_PerceptionModality modality,
        float attenuation)
    {
        float c = fly.Personality.Conformity;
        float n = fly.Personality.Nerve;
        float t = fly.Personality.Temperament;
        float bond = packet.Emitter != null ? DB_SocialBond.GetBondStrength(fly, packet.Emitter) : 0f;
        float confidence = DB_PerceptionScoring.SignalConfidence(
            packet.Intensity,
            attenuation,
            packet.Hop,
            c,
            n,
            modality == DB_PerceptionModality.Acoustic);

        float semanticScale = packet.Kind switch
        {
            DB_SignalKind.AlarmFlutter => 1f,
            DB_SignalKind.DistressCall => 0.62f + bond * 0.52f + t * 0.20f + n * 0.14f,
            DB_SignalKind.RallySignal => 0.40f + t * 0.34f + n * 0.24f + bond * 0.18f,
            DB_SignalKind.RoostCall => 0.44f + fly.Personality.RoostAffinity * 0.38f + bond * 0.18f,
            DB_SignalKind.HarassSignal => 0.30f + t * 0.38f + n * 0.22f,
            DB_SignalKind.SafeSignal => 0.44f + n * 0.26f,
            _ => 1f
        };
        float response = Mathf.Clamp01(confidence * semanticScale);

        Player player = packet.PlayerTarget ?? packet.Threat as Player;
        if (player != null)
        {
            int slot = DB_ThreatRuntime.PlayerSlot(player);
            if (DB_ThreatRuntime.ValidSlot(slot))
            {
                DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(fly.DesertState, slot);
                if (memory != null && memory.Confidence >= 0.04f)
                {
                    float caution = Mathf.Clamp01(
                        memory.PiercingPressure * 0.30f + memory.CounterKillPressure * 0.34f +
                        memory.ExplosionPressure * 0.17f + memory.GrabCapturePressure * 0.10f +
                        memory.PursuitPressure * 0.09f) * memory.Confidence;
                    response *= packet.Kind switch
                    {
                        DB_SignalKind.AlarmFlutter => 1f + caution * 0.24f,
                        DB_SignalKind.DistressCall => 1f - caution * 0.22f,
                        DB_SignalKind.RallySignal => 1f - caution * 0.52f,
                        DB_SignalKind.HarassSignal => 1f - caution * 0.62f,
                        _ => 1f
                    };
                }
            }
        }
        return Mathf.Clamp01(response);
    }

    private bool ShouldRelayAlarm(DB_SignalPacket packet, float response)
    {
        DB_SignalDefinition definition = DB_SignalDefinition.For(packet.Kind);
        if (!definition.CanRelay || packet.Hop >= definition.MaxRelayHops || response < 0.24f) return false;
        float chance = Mathf.Clamp01(
            0.14f + fly.Personality.Conformity * 0.58f +
            (1f - fly.Personality.Nerve) * 0.18f + response * 0.18f);
        return Stable01(packet.Generation ^ (packet.Hop + 1) * 0x6D2B) < chance;
    }

    private void TickSignalInfluence()
    {
        alarmPressure = Mathf.Max(0f, alarmPressure - 0.0032f);
        distressInterest = Mathf.Max(0f, distressInterest - 0.0050f);
        rallyInterest = Mathf.Max(0f, rallyInterest - 0.0038f);
        roostInterest = Mathf.Max(0f, roostInterest - 0.0022f);
        harassInterest = Mathf.Max(0f, harassInterest - 0.0030f);
        safeConfidence = Mathf.Max(0f, safeConfidence - 0.0035f);

        if (alarmPressure <= 0f) alarmThreat = null;
        if (distressInterest <= 0f) distressSource = null;
        if (rallyInterest <= 0f) { rallySource = null; rallyTarget = null; }
        if (roostInterest <= 0f) roostSource = null;
        if (harassInterest <= 0f) { harassSource = null; harassTarget = null; }
    }

    private DB_PerceptionSignalContext BuildSignalContext() => new(
        alarmPressure,
        alarmOrigin,
        alarmThreat,
        distressInterest,
        distressSource,
        rallyInterest,
        rallySource,
        rallyTarget,
        roostInterest,
        roostSource,
        harassInterest,
        harassSource,
        harassTarget,
        safeConfidence,
        lastSignalModality,
        lastSignalHop,
        lastSignalDecision);

    private bool HasSignalGeneration(int generation)
    {
        if (!signalGenerationInitialized) return false;
        for (int i = 0; i < signalGenerations.Length; i++)
            if (signalGenerations[i] == generation) return true;
        return false;
    }

    private void RememberSignalGeneration(int generation)
    {
        if (!signalGenerationInitialized)
        {
            for (int i = 0; i < signalGenerations.Length; i++) signalGenerations[i] = int.MinValue;
            signalGenerationInitialized = true;
        }
        signalGenerations[signalGenerationCursor] = generation;
        signalGenerationCursor = (signalGenerationCursor + 1) % signalGenerations.Length;
    }

    private float InstigatorThreatMemory(Creature instigator)
    {
        if (instigator is not Player player) return 0f;
        int slot = DB_ThreatRuntime.PlayerSlot(player);
        if (!DB_ThreatRuntime.ValidSlot(slot)) return 0f;
        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(fly.DesertState, slot);
        if (memory == null) return 0f;
        return Mathf.Clamp01(memory.Confidence * (
            memory.ProjectilePressure * 0.40f + memory.PiercingPressure * 0.30f +
            memory.CounterKillPressure * 0.20f + memory.BluntStunPressure * 0.10f));
    }

    private static float ProjectileLethality(Weapon weapon)
    {
        if (weapon is Spear) return 1f;
        if (weapon is Rock) return 0.34f;
        return 0.58f;
    }

    private static Creature ResolveInstigator(Weapon weapon)
    {
        if (weapon?.thrownBy != null) return weapon.thrownBy;
        if (weapon?.grabbedBy != null && weapon.grabbedBy.Count > 0)
            return weapon.grabbedBy[0]?.grabber;
        return null;
    }

    private bool ValidReportedThreat(Creature threat)
        => threat != null && !threat.dead && !threat.slatedForDeletetion && threat.room == fly.room;

    private bool AvailableForSignals()
        => fly != null && !fly.dead && !fly.slatedForDeletetion && fly.room != null && !fly.inShortcut;

    private static int StableCreatureKey(Creature creature)
        => creature?.abstractCreature?.ID.number ?? int.MaxValue;

    private static int StableWeaponKey(Weapon weapon)
        => weapon?.abstractPhysicalObject?.ID.number ?? int.MaxValue;

    private void ResetScanPhase()
    {
        scan = ScanIntervalTicks - ScanPhase(fly?.Personality?.VisualSeed ?? 0);
    }

    internal static int ScanPhase(int visualSeed)
    {
        unchecked
        {
            uint x = (uint)visualSeed ^ 0x6D2B79F5u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return 1 + (int)(x % (uint)ScanIntervalTicks);
        }
    }

    private int StableInt(int salt, int min, int max)
    {
        if (max <= min) return min;
        return min + Mathf.FloorToInt(Stable01(salt) * (max - min));
    }

    private float Stable01(int salt)
    {
        unchecked
        {
            uint x = (uint)((fly?.Personality?.VisualSeed ?? 0) * 1103515245 + salt * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777216f;
        }
    }

    private void TraceSignalReceive(DB_SignalPacket packet, float response)
    {
        if (fly?.abstractCreature == null ||
            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(fly.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            fly.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "SignalPerceived",
            $"{packet.Kind} gen={packet.Generation} hop={packet.Hop} via={lastSignalModality} confidence={response:0.00}",
            lastSignalDecision);
    }
}
