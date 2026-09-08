namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Formal Extreme Vengeance domain boundary.
///
/// Fear and Vengeance intentionally still share one runtime state lifecycle during the
/// behavior-preserving migration: fear collapse and persistent trauma must be able to
/// cancel an armed vengeance state synchronously. External domains use this type for
/// Vengeance facts/execution; DB_FearRuntime remains the shared-state owner until that
/// coupling is extracted with dedicated state-contract tests.
/// </summary>
internal static class DB_VengeanceRuntime
{
    internal static bool IsActive(DB_Creature bat)
        => DB_FearRuntime.IsExtremeVengeanceActive(bat);

    internal static bool IsAvenger(DB_Creature bat)
        => DB_FearRuntime.IsVengeanceAvenger(bat);

    internal static bool TryGetTarget(DB_Creature bat, out Creature target)
        => DB_FearRuntime.TryGetVengeanceTarget(bat, out target);

    internal static bool ExecuteOwned(DB_Creature bat)
        => DB_FearRuntime.ExecuteVengeanceOwned(bat);
}

/// <summary>
/// Shared-state implementation half for Extreme Vengeance. This remains a partial
/// of DB_FearRuntime so fear collapse, trauma and vengeance cancellation continue to
/// mutate the same State instance during the behavior-preserving migration.
/// External callers must use DB_VengeanceRuntime.
/// </summary>
internal static partial class DB_FearRuntime
{
    internal static bool IsExtremeVengeanceActive(DB_Creature bat)
    {
        return bat != null && states.TryGetValue(bat, out State state) &&
               state.Active && state.Vengeance != VengeanceMode.None;
    }

    internal static bool IsVengeanceAvenger(DB_Creature bat)
    {
        return bat != null && states.TryGetValue(bat, out State state) && state.Active &&
               state.Vengeance != VengeanceMode.None &&
               state.Role == VengeanceParticipation.Avenger;
    }

    internal static bool TryGetVengeanceTarget(DB_Creature bat, out Creature target)
    {
        target = null;
        if (bat == null || !states.TryGetValue(bat, out State state) || !state.Active ||
            state.Vengeance == VengeanceMode.None || state.VengeanceTarget == null)
            return false;
        target = state.VengeanceTarget;
        return true;
    }

    internal static bool ExecuteVengeanceOwned(DB_Creature bat)
    {
        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance) ||
            !states.TryGetValue(bat, out State state) || !state.Active ||
            state.Vengeance == VengeanceMode.None)
            return false;

        UpdateVengeance(bat, state);
        TryDeactivate(bat, state);
        return true;
    }

    private static void ArmVengeanceGroup(
        List<DB_Creature> trueCandidates,
        List<DB_Creature> bats,
        int[] tier,
        DB_Creature victim,
        Creature threat,
        EventKind kind,
        LizardTongue rescueTongue,
        bool suppressNewVengeance)
    {
        if (suppressNewVengeance || !ValidThreat(threat, victim?.room))
            return;

        List<DB_Creature> leaders = new(MaxTrueAvengersPerEvent);
        int participants = 0;
        DB_Creature existingLeader = null;
        State existingLeaderState = null;

        for (int i = 0; i < bats.Count; i++)
        {
            DB_Creature bat = bats[i];
            if (!TryGetVengeanceState(bat, out State social) ||
                social.Vengeance == VengeanceMode.None ||
                social.VengeanceTarget != threat ||
                social.Role == VengeanceParticipation.None)
                continue;

            participants++;
            if (existingLeader == null && social.Role == VengeanceParticipation.Avenger)
            {
                existingLeader = bat;
                existingLeaderState = social;
            }
        }

        if (existingLeader != null)
        {
            leaders.Add(existingLeader);
            if (existingLeaderState.Vengeance == VengeanceMode.Withdraw)
                return;
        }

        if (participants >= DB_Tuning.SocialVengeanceGroupCap)
            return;

        if (leaders.Count == 0)
        {
            if (trueCandidates == null || trueCandidates.Count == 0)
                return;

            trueCandidates.Sort(
                (a, b) => (b.Personality.VengeanceAffinity + DB_SocialBond.Motivation(b, victim, threat))
                    .CompareTo(a.Personality.VengeanceAffinity + DB_SocialBond.Motivation(a, victim, threat)));

            for (int i = 0; i < trueCandidates.Count &&
                 leaders.Count < MaxTrueAvengersPerEvent &&
                 participants < DB_Tuning.SocialVengeanceGroupCap; i++)
            {
                DB_Creature bat = trueCandidates[i];
                if (bat.Injury.BlocksCombat || !DB_SocialBond.CanRespond(bat)) continue;
                State state = StateFor(bat);
                FearMemory fear = threat is Player ? state.PlayerFear : state.PredatorFear;
                float trauma = PersistentTraumaStrength(bat, threat);
                if (fear.Strength >= VengeanceCollapseStrength ||
                    trauma >= DB_Tuning.TraumaAggressionBlock)
                    continue;

                ArmVengeance(
                    bat,
                    state,
                    threat,
                    kind,
                    victim,
                    rescueTongue,
                    bat.Personality.VengeanceDrive,
                    1f,
                    false,
                    null);
                leaders.Add(bat);
                participants++;
            }
        }

        if (leaders.Count == 0 ||
            participants >= DB_Tuning.SocialVengeanceGroupCap)
            return;

        List<FollowerCandidate> followers = new(bats.Count);
        for (int i = 0; i < bats.Count; i++)
        {
            DB_Creature bat = bats[i];
            if (bat.Injury.BlocksCombat || !DB_SocialBond.CanRespond(bat) || tier[i] < 0 || tier[i] > 1 || bat.Personality.CanExtremeVengeance ||
                bat.Personality.Conformity < DB_Tuning.SocialFollowerMinConformity ||
                IsExtremeVengeanceActive(bat))
                continue;

            float trauma = PersistentTraumaStrength(bat, threat);
            if (trauma >= DB_Tuning.TraumaAggressionBlock) continue;

            DB_Creature bestLeader = null;
            float bestLeaderDrive = 0f;
            for (int l = 0; l < leaders.Count; l++)
            {
                DB_Creature leader = leaders[l];
                float distance = Vector2.Distance(
                    bat.mainBodyChunk.pos,
                    leader.mainBodyChunk.pos);
                bool sociallyVisible =
                    distance <= DB_Tuning.SocialFollowerRange ||
                    (distance <= DB_Tuning.SocialFollowerRange * 1.45f &&
                     bat.room.VisualContact(bat.mainBodyChunk.pos, leader.mainBodyChunk.pos));
                if (!sociallyVisible) continue;

                if (leader.Personality.VengeanceDrive > bestLeaderDrive)
                {
                    bestLeader = leader;
                    bestLeaderDrive = leader.Personality.VengeanceDrive;
                }
            }
            if (bestLeader == null) continue;

            State batState = StateFor(bat);
            FearMemory fear = threat is Player ? batState.PlayerFear : batState.PredatorFear;
            float score =
                bat.Personality.Conformity * 0.50f +
                bat.Personality.Temperament * 0.20f +
                bat.Personality.Nerve * 0.15f +
                bestLeaderDrive * 0.15f -
                fear.Strength * 0.28f -
                trauma * 0.65f + DB_SocialBond.Motivation(bat, victim, threat);

            if (score < 0.44f) continue;
            float probability = Mathf.InverseLerp(0.44f, 0.84f, score) *
                                Mathf.Lerp(0.55f, 1f, bat.Personality.Conformity);
            if (StableEvent01(bat, victim, threat, kind) > probability) continue;

            followers.Add(new FollowerCandidate(bat, bestLeader, score));
        }

        followers.Sort((a, b) => b.Score.CompareTo(a.Score));
        for (int i = 0; i < followers.Count &&
             participants < DB_Tuning.SocialVengeanceGroupCap; i++)
        {
            FollowerCandidate follower = followers[i];
            State state = StateFor(follower.Bat);
            float commitment = Mathf.Clamp01(
                Mathf.InverseLerp(0.44f, 0.90f, follower.Score));
            bool supportOnly = follower.Score < 0.66f ||
                               follower.Bat.Personality.Temperament < 0.50f;
            float damageScale = supportOnly
                ? Mathf.Lerp(0.20f, 0.34f, commitment)
                : Mathf.Lerp(0.35f, 0.65f, commitment);

            ArmVengeance(
                follower.Bat,
                state,
                threat,
                kind,
                victim,
                rescueTongue,
                Mathf.Lerp(0.38f, 0.72f, commitment),
                damageScale,
                supportOnly,
                follower.Leader);
            state.Commitment = commitment;
            participants++;
        }
    }

    private static void ArmVengeance(
        DB_Creature bat,
        State state,
        Creature threat,
        EventKind kind,
        DB_Creature victim,
        LizardTongue rescueTongue,
        float drive,
        float damageScale,
        bool supportOnly,
        DB_Creature leader)
    {
        float rage = Mathf.Clamp01(Mathf.Lerp(0.58f, 1f, drive) + DB_SocialBond.Motivation(bat, victim, threat));
        if (state.Vengeance != VengeanceMode.None && state.VengeanceTarget == threat)
        {
            state.Rage = Mathf.Max(state.Rage, rage);
            if (state.Role == VengeanceParticipation.Avenger)
            {
                state.PassesRemaining = Mathf.Max(
                    state.PassesRemaining,
                    drive > 0.70f ? 2 : 1);
                DB_SignalRuntime.EmitRally(
                    bat, threat, drive, "existing Avenger refreshes RallySignal");
            }
            return;
        }

        state.VengeanceTarget = threat;
        state.Leader = leader;
        state.RescueVictim = kind == EventKind.PredatorCapture ? victim : null;
        state.RescueTongue = kind == EventKind.PredatorCapture ? rescueTongue : null;
        state.RescueAttempted = false;
        state.WasRescuePlan = kind == EventKind.PredatorCapture;
        state.Rage = rage;
        state.DamageScale = damageScale;
        state.SupportOnly = supportOnly;
        state.PassesRemaining = supportOnly
            ? 0
            : (leader == null && drive > 0.70f ? 2 : 1);
        state.Role = leader == null ? VengeanceParticipation.Avenger : VengeanceParticipation.Supporter;
        state.Vengeance = VengeanceMode.Waiting;

        int minDelay = kind == EventKind.PredatorCapture
            ? VengeanceCaptureDelayMin
            : VengeanceKillDelayMin;
        int maxDelay = kind == EventKind.PredatorCapture
            ? VengeanceCaptureDelayMax
            : VengeanceKillDelayMax;
        int socialDelay = leader == null
            ? 0
            : Mathf.RoundToInt(Mathf.Lerp(26f, 8f, bat.Personality.Conformity));
        state.VengeanceTimer = Mathf.RoundToInt(
            Mathf.Lerp(maxDelay, minDelay, drive)) + socialDelay;

        // Deliver synchronously before ArmVengeanceGroup scores followers so Rally
        // interest can participate in SocialBond.Motivation without a detour around this method.
        if (state.Role == VengeanceParticipation.Avenger && !supportOnly && leader == null)
            DB_SignalRuntime.EmitRally(
                bat, threat, drive, "new Avenger armed -> immediate RallySignal");
    }

    private static void UpdateVengeance(DB_Creature bat, State state)
    {
        if (state.Vengeance == VengeanceMode.None) return;

        Creature target = state.VengeanceTarget;
        if (!ValidThreat(target, bat.room))
        {
            ClearVengeance(state);
            return;
        }

        if (state.Role == VengeanceParticipation.Supporter)
        {
            if (state.Leader == null || state.Leader.dead || state.Leader.room != bat.room)
            {
                AddTrauma(
                    bat,
                    target,
                    Mathf.Lerp(0.05f, 0.14f, bat.Personality.Conformity));
                ClearVengeance(state);
                bat.DesertAI.Threatened(target, false);
                return;
            }

            if (!TryGetVengeanceState(state.Leader, out State leaderState) ||
                leaderState.Role != VengeanceParticipation.Avenger ||
                leaderState.VengeanceTarget != target)
            {
                if (state.Vengeance != VengeanceMode.Withdraw)
                    StartWithdraw(state, CombatDrive(bat, state));
            }
            else if (leaderState.Vengeance == VengeanceMode.Withdraw &&
                     state.Vengeance != VengeanceMode.Withdraw)
            {
                StartWithdraw(state, CombatDrive(bat, state));
            }
        }

        if (PersistentTraumaStrength(bat, target) >= DB_Tuning.TraumaSevere)
        {
            ClearVengeance(state);
            bat.DesertAI.Threatened(target, false);
            return;
        }

        float drive = CombatDrive(bat, state);
        Vector2 head = target.mainBodyChunk.pos;

        switch (state.Vengeance)
        {
            case VengeanceMode.Waiting:
                if (--state.VengeanceTimer > 0) return;
                if (state.WasRescuePlan && RescueStillPossible(state, target))
                {
                    state.Vengeance = VengeanceMode.RescueCharge;
                    state.VengeanceTimer = 0;
                }
                else
                {
                    state.Vengeance = VengeanceMode.Observe;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceObserveMaxTicks,
                        VengeanceObserveMinTicks,
                        drive));
                }
                break;

            case VengeanceMode.Observe:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 150f, 80f),
                    Mathf.Lerp(6.5f, 8.5f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Vengeance = VengeanceMode.Circle;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceCircleMaxTicks,
                        VengeanceCircleMinTicks,
                        drive));
                }
                break;

            case VengeanceMode.Circle:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 105f, 60f),
                    Mathf.Lerp(7.5f, 10f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Vengeance = VengeanceMode.Feint;
                    state.VengeanceTimer = VengeanceFeintTicks;
                }
                break;

            case VengeanceMode.Feint:
            {
                float distance = Vector2.Distance(bat.mainBodyChunk.pos, head);
                Vector2 goal = distance < 58f
                    ? head + Vector2.up * 155f +
                      Custom.DirVec(head, bat.mainBodyChunk.pos) * 70f
                    : head + target.mainBodyChunk.vel * 0.75f;
                ForceFlight(bat, goal, distance < 58f ? 11f : 13f);

                if (--state.VengeanceTimer <= 0)
                {
                    if (state.SupportOnly)
                    {
                        StartWithdraw(state, drive);
                    }
                    else
                    {
                        state.Vengeance = VengeanceMode.Charge;
                        state.VengeanceTimer = VengeanceChargeTimeout;
                    }
                }
                break;
            }

            case VengeanceMode.RescueCharge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 0.95f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer++;
                if (TryVengeanceContact(bat, state, target, true))
                {
                    if (state.Vengeance == VengeanceMode.RescueCharge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer > VengeanceChargeTimeout)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
                }
                break;

            case VengeanceMode.Charge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 1.05f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer--;
                if (TryVengeanceContact(bat, state, target, false))
                {
                    if (state.Vengeance == VengeanceMode.Charge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer <= 0)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
                }
                break;

            case VengeanceMode.Withdraw:
                ForceFlight(
                    bat,
                    bat.mainBodyChunk.pos +
                    Custom.DirVec(head, bat.mainBodyChunk.pos) * 190f + Vector2.up * 65f,
                    Mathf.Lerp(8f, 10.5f, drive));
                if (--state.VengeanceTimer <= 0)
                    ClearVengeance(state);
                break;
        }
    }

    private static bool TryVengeanceContact(
        DB_Creature bat,
        State state,
        Creature target,
        bool rescue)
    {
        BodyChunk hitChunk = target.mainBodyChunk;
        if (hitChunk == null ||
            !Custom.DistLess(
                bat.mainBodyChunk.pos,
                hitChunk.pos,
                bat.mainBodyChunk.rad + hitChunk.rad + VengeanceHitExtraRadius))
            return false;

        float drive = CombatDrive(bat, state);
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, hitChunk.pos);
        float damageDrive = target is Player ? drive : drive * drive;
        float damage = target is Player
            ? Mathf.Lerp(PlayerVengeanceDamageMin, PlayerVengeanceDamageMax, damageDrive)
            : Mathf.Lerp(LizardVengeanceDamageMin, LizardVengeanceDamageMax, damageDrive);
        damage *= Mathf.Lerp(0.86f, 1.08f, state.Rage) * state.DamageScale;

        float stun = Mathf.Lerp(VengeanceStunMin, VengeanceStunMax, drive) *
                     Mathf.Lerp(0.82f, 1.05f, state.Rage) *
                     Mathf.Lerp(0.72f, 1f, state.DamageScale);
        Vector2 momentum = direction *
                           Mathf.Lerp(VengeanceImpactMin, VengeanceImpactMax, drive) *
                           Mathf.Lerp(0.65f, 1f, state.DamageScale);

        target.Violence(
            bat.mainBodyChunk,
            momentum,
            hitChunk,
            null,
            Creature.DamageType.Blunt,
            damage,
            stun);

        bat.mainBodyChunk.vel +=
            -direction * Mathf.Lerp(4.5f, 7.5f, drive) + Vector2.up * 2f;

        bool rescued = rescue && TryRescueVictim(bat, state, target);
        state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
        if (rescued) StartWithdraw(state, drive);
        return true;
    }

    private static bool TryRescueVictim(
        DB_Creature bat,
        State state,
        Creature target)
    {
        if (state.RescueAttempted || target is not Lizard lizard || !IsPeach(lizard) ||
            state.RescueVictim == null || state.RescueVictim.dead ||
            state.RescueVictim.room != lizard.room)
            return false;

        state.RescueAttempted = true;
        float drive = CombatDrive(bat, state);
        float chance = Mathf.Lerp(TongueRescueChanceMin, TongueRescueChanceMax, drive) *
                       Mathf.Lerp(0.92f, 1.10f, bat.Personality.Nerve) *
                       Mathf.Lerp(0.90f, 1.08f, state.Rage);
        if (state.Role == VengeanceParticipation.Supporter)
            chance *= Mathf.Lerp(0.72f, 0.96f, state.Commitment);

        DB_Creature victim = state.RescueVictim;
        bool tongueCatch = state.RescueTongue != null &&
            lizard.tongue == state.RescueTongue &&
            state.RescueTongue.attached?.owner == victim &&
            state.RescueTongue.state == LizardTongue.State.AttachedInSmallObject;
        bool graspCatch = lizard.grasps != null && lizard.grasps.Length > 0 &&
            lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim;
        if (!tongueCatch && !graspCatch) return false;

        if (graspCatch) chance *= GraspRescueChanceScale;
        if (UnityEngine.Random.value >= Mathf.Clamp01(chance)) return false;

        if (tongueCatch) state.RescueTongue.Retract();
        if (graspCatch && lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim)
            lizard.ReleaseGrasp(0);

        if (victim.dead || RescueStillPossible(state, target)) return false;
        victim.Injury.CheckCaptureRelease();
        DB_SocialBond.OnSuccessfulRescue(bat, victim);
        Vector2 away = Custom.DirVec(
            lizard.mainBodyChunk.pos,
            victim.mainBodyChunk.pos);
        victim.mainBodyChunk.vel += away * 6.5f + Vector2.up * 3f;
        victim.DesertAI.Threatened(lizard, true);
        return true;
    }

    private static bool RescueStillPossible(State state, Creature target)
    {
        if (target is not Lizard lizard || !IsPeach(lizard) ||
            state.RescueVictim == null || state.RescueVictim.dead ||
            state.RescueVictim.room != lizard.room)
            return false;

        bool tongueCatch = state.RescueTongue != null &&
            lizard.tongue == state.RescueTongue &&
            state.RescueTongue.attached?.owner == state.RescueVictim &&
            state.RescueTongue.state == LizardTongue.State.AttachedInSmallObject;
        bool graspCatch = lizard.grasps != null && lizard.grasps.Length > 0 &&
            lizard.grasps[0] != null && lizard.grasps[0].grabbed == state.RescueVictim;
        return tongueCatch || graspCatch;
    }

    private static void ContinueOrWithdraw(
        State state,
        Creature target,
        float drive)
    {
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.WasRescuePlan = false;

        if (state.Role == VengeanceParticipation.Avenger && state.PassesRemaining > 0 &&
            target != null && !target.dead)
        {
            state.Vengeance = VengeanceMode.Circle;
            state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                VengeanceCircleMaxTicks,
                VengeanceCircleMinTicks,
                drive));
            return;
        }

        StartWithdraw(state, drive);
    }

    private static void StartWithdraw(State state, float drive)
    {
        state.Vengeance = VengeanceMode.Withdraw;
        state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
            VengeanceWithdrawMaxTicks,
            VengeanceWithdrawMinTicks,
            drive));
    }

    private static float CombatDrive(DB_Creature bat, State state)
    {
        return state.Role == VengeanceParticipation.Supporter
            ? Mathf.Lerp(0.38f, 0.72f, state.Commitment)
            : bat.Personality.VengeanceDrive;
    }

    private static bool TryGetVengeanceState(
        DB_Creature bat,
        out State state)
    {
        state = null;
        return bat != null && states.TryGetValue(bat, out state) && state.Active;
    }

    private static void ClearVengeance(State state)
    {
        if (state == null) return;
        state.Vengeance = VengeanceMode.None;
        state.Role = VengeanceParticipation.None;
        state.VengeanceTarget = null;
        state.Leader = null;
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.Rage = 0f;
        state.Commitment = 0f;
        state.DamageScale = 1f;
        state.VengeanceTimer = 0;
        state.PassesRemaining = 0;
        state.RescueAttempted = false;
        state.WasRescuePlan = false;
        state.SupportOnly = false;
    }

    private static float StableEvent01(
        DB_Creature bat,
        DB_Creature victim,
        Creature threat,
        EventKind kind)
    {
        unchecked
        {
            uint x = (uint)bat.Personality.VisualSeed;
            x ^= (uint)(victim?.Personality.VisualSeed ?? 0) * 0x9E3779B9u;
            x ^= (uint)ThreatIdentity(threat) * 0x85EBCA6Bu;
            x ^= (uint)((int)kind + 1) * 0xC2B2AE35u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static Vector2 OrbitOffset(
        DB_Creature bat,
        float width,
        float height)
    {
        float angle =
            (bat.room.game.clock + (bat.Personality.VisualSeed & 1023)) * 0.033f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            45f + Mathf.Sin(angle) * height * 0.55f);
    }

    private static void ForceFlight(
        DB_Creature bat,
        Vector2 goal,
        float speed)
    {
        if (bat?.room == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))
            return;

        // R5: threat tactics are modifiers, not an internal RuntimeDetour. Vengeance owns
        // the frame and explicitly asks Threat Signature to refine the already-authorized
        // goal/speed before the single FlightMotor write boundary.
        if (TryGetVengeanceTarget(bat, out Creature vengeanceTarget) && vengeanceTarget is Player player)
            goal = DB_ThreatTactics.AdjustExtremeVengeanceGoal(bat, player, goal, ref speed);

        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (bat.room.GetTile(probe).Solid ||
            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))
        {
            goal = bat.mainBodyChunk.pos + Vector2.up * 75f;
            speed = Mathf.Min(speed, 7f);
        }

        DB_FlightMotor.TrySteer(
            bat,
            DB_BehaviorOwner.Vengeance,
            goal,
            speed,
            response: 0.28f);
    }
}
