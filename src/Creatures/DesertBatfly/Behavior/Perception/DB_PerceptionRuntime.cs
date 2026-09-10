using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Perception R2 receiver. Owns current direct observations, attention ranking, bounded lost
/// tracks and projectile risk. It never owns locomotion or behavior arbitration.
///
/// Signal-derived receiver state is intentionally represented in the same snapshot; transport
/// migration from DB_SignalRuntime is performed independently so packet emission/relay remains
/// a separate concern.
/// </summary>
internal class DB_PerceptionRuntime
{
    internal const int ScanIntervalTicks = 8;
    internal const float ProjectileScanRange = 240f;
    internal const float ProjectileMissRadius = 38f;
    internal const float ProjectileMinimumVelocitySqr = 1.6f;

    private readonly DB_AI brain;
    private readonly DB_Creature fly;
    private readonly int[] pursuitByPlayer = new int[4];

    private int scan;
    private int creatureScanCount;
    private int projectileScanCount;
    private int signalScanCount;
    private DB_PerceptionTrack primaryThreat;
    private DB_PerceptionTrack secondaryThreat;
    private DB_PerceptionTrack lostThreat;
    private DB_ProjectilePercept incomingProjectile;
    private DB_PerceptionSignalContext signalContext;
    private string lastAttentionReason = "not scanned";

    internal DB_PerceptionRuntime(DB_AI brain, DB_Creature fly)
    {
        this.brain = brain;
        this.fly = fly;
        ResetScanPhase();
    }

    internal Creature Danger => primaryThreat.DirectObservation && primaryThreat.Valid
        ? primaryThreat.Target
        : null;

    internal bool IsScanFrame => scan == 0;

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
        signalContext);

    internal void Reset()
    {
        for (int i = 0; i < pursuitByPlayer.Length; i++) pursuitByPlayer[i] = 0;
        primaryThreat = default;
        secondaryThreat = default;
        lostThreat = default;
        incomingProjectile = default;
        signalContext = default;
        lastAttentionReason = "reset";
        ResetScanPhase();
    }

    internal void ClearPursuit()
    {
        for (int i = 0; i < pursuitByPlayer.Length; i++) pursuitByPlayer[i] = 0;
    }

    /// <summary>
    /// Per-frame receiver refresh. Projectile risk and lost-track decay are cheap/current;
    /// creature LOS scans retain the old staggered eight-tick cadence.
    /// </summary>
    internal void RefreshState()
    {
        if (fly?.room == null || fly.mainBodyChunk == null)
        {
            incomingProjectile = default;
            return;
        }

        RefreshIncomingProjectile();
        RefreshLostTrack();

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

    private void ScanCreatures()
    {
        creatureScanCount++;
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

            // Keep the exact sqrt behind cheap range + LOS rejection.
            float distance = Vector2.Distance(origin, creature.mainBodyChunk.pos);
            brain.Combat.ConsiderCandidate(creature, distance);
            if (creature is Player observedPlayer) TrackPlayerApproach(observedPlayer, distance);

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
    }

    private void ClearDirectThreats(in DB_PerceptionTrack previousPrimary, bool previousStillVisible)
    {
        if (!previousStillVisible && previousPrimary.Valid && previousPrimary.DirectObservation)
            BeginLostTrack(previousPrimary);
        primaryThreat = default;
        secondaryThreat = default;
        lastAttentionReason = "no directly observed threat";
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

        float threatDistance = 0f;
        float relationshipDanger = 0f;
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
            relationshipDanger = relation.type == CreatureTemplate.Relationship.Type.Afraid ?
                Mathf.Clamp01(Mathf.Max(0.45f, relation.intensity)) : 0.72f;
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
        else
        {
            return false;
        }

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
        else
        {
            pursuitByPlayer[slot] = Mathf.Max(0, pursuitByPlayer[slot] - 2);
        }
    }

    private void BeginLostTrack(in DB_PerceptionTrack previous)
    {
        if (!previous.Valid || previous.Target == null) return;
        int clock = fly.room?.game?.clock ?? previous.LastObservedTick;
        float confidence = DB_PerceptionScoring.LostConfidence(previous.Confidence, 1);
        Vector2 estimated = DB_PerceptionScoring.PredictLostPosition(
            previous.ObservedPosition,
            previous.ObservedVelocity,
            1);
        lostThreat = previous.AsPredicted(clock, confidence, estimated);
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
        float confidence = DB_PerceptionScoring.LostConfidence(lostThreat.Confidence, age - lostThreat.AgeTicks);
        // Rebase from the original observation confidence when possible so decay is monotonic
        // and does not depend on how many Update calls happened in a frame.
        if (lostThreat.AgeTicks > 0)
        {
            float priorLife = 1f - Mathf.Clamp01(lostThreat.AgeTicks / (float)DB_PerceptionScoring.LostTrackMaxTicks);
            float baseConfidence = priorLife > 0.001f
                ? Mathf.Clamp01(lostThreat.Confidence / (priorLife * priorLife))
                : 0f;
            confidence = DB_PerceptionScoring.LostConfidence(baseConfidence, age);
        }
        if (confidence <= 0.01f)
        {
            lostThreat = default;
            return;
        }

        Vector2 estimated = DB_PerceptionScoring.PredictLostPosition(
            lostThreat.ObservedPosition,
            lostThreat.ObservedVelocity,
            age);
        lostThreat = new DB_PerceptionTrack(
            lostThreat.Target,
            lostThreat.ObservedPosition,
            estimated,
            lostThreat.ObservedVelocity,
            confidence,
            lostThreat.Salience,
            lostThreat.ThreatUrgency * 0.96f,
            lostThreat.AttentionScore * 0.96f,
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

            float time = Mathf.Clamp(
                Vector2.Dot(delta, velocity) / Mathf.Max(1f, velocitySqr),
                0f,
                5f);
            float closestSqr = (delta - velocity * time).sqrMagnitude;
            if (closestSqr >= missRadiusSqr) continue;
            if (!DB_VisibilityPolicy.CanObserve(
                    fly,
                    position,
                    ProjectileScanRange,
                    DB_VisibilityChannel.Projectile,
                    realProjectile: true))
                continue;

            float distance = Mathf.Sqrt(delta.sqrMagnitude);
            float confidence = DB_PerceptionScoring.DirectConfidence(
                ProjectileScanRange,
                distance,
                Mathf.Max(visibility, 0.38f));
            float closest = Mathf.Sqrt(closestSqr);
            float speed = Mathf.Sqrt(velocitySqr);
            Creature instigator = ResolveInstigator(weapon);
            float memory = InstigatorThreatMemory(instigator);
            float lethality = ProjectileLethality(weapon);
            float risk = DB_PerceptionScoring.ProjectileRisk(
                time,
                closest,
                ProjectileMissRadius,
                speed,
                lethality,
                confidence,
                memory);
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
                weapon,
                instigator,
                position,
                velocity,
                closestSqr,
                thrown: true,
                heldMovingSpear: false);
        }

        if (bestObservation.Weapon != null)
            incomingProjectile = new DB_ProjectilePercept(
                bestObservation,
                bestTime,
                bestClosest,
                bestRisk,
                bestConfidence);
    }

    private float InstigatorThreatMemory(Creature instigator)
    {
        if (instigator is not Player player) return 0f;
        int slot = DB_ThreatRuntime.PlayerSlot(player);
        if (!DB_ThreatRuntime.ValidSlot(slot)) return 0f;
        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(fly.DesertState, slot);
        if (memory == null) return 0f;
        return Mathf.Clamp01(memory.Confidence * (
            memory.ProjectilePressure * 0.40f +
            memory.PiercingPressure * 0.30f +
            memory.CounterKillPressure * 0.20f +
            memory.BluntStunPressure * 0.10f));
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

    // Signal migration uses this counter/debug surface without giving Signal locomotion authority.
    internal void NoteSignalScan() => signalScanCount++;
}
