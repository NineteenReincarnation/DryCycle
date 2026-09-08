namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Extreme Vengeance domain runtime.
///
/// Vengeance owns its state machine, participation model, rescue/contact behavior and
/// FlightMotor execution. Its State object is embedded inside the single per-bat Fear
/// host so fear collapse, persistent trauma, Reset and Forget remain synchronous without
/// a second weak table or duplicated lifecycle.
/// </summary>
internal static class DB_VengeanceRuntime
{
    internal enum Mode { None, Waiting, Observe, Circle, Feint, RescueCharge, Charge, Withdraw }
    internal enum Participation { None, Avenger, Supporter }

    // One event has one actual leader. The runtime group cap of three means the normal
    // social form remains one true avenger plus zero, one or two followers even if
    // additional deaths/captures occur before the first group has finished withdrawing.
    private const int MaxTrueAvengersPerEvent = 1;
    internal const float CollapseStrength = 0.84f;
    private const int VengeanceCaptureDelayMin = 12;
    private const int VengeanceCaptureDelayMax = 30;
    private const int VengeanceKillDelayMin = 70;
    private const int VengeanceKillDelayMax = 155;
    private const int VengeanceObserveMinTicks = 18;
    private const int VengeanceObserveMaxTicks = 46;
    private const int VengeanceCircleMinTicks = 22;
    private const int VengeanceCircleMaxTicks = 48;
    private const int VengeanceFeintTicks = 18;
    private const int VengeanceChargeTimeout = 72;
    private const int VengeanceWithdrawMinTicks = 100;
    private const int VengeanceWithdrawMaxTicks = 210;
    private const float VengeanceChargeMinSpeed = 12.5f;
    private const float VengeanceChargeMaxSpeed = 18f;
    private const float VengeanceHitExtraRadius = 5f;

    private const float PlayerVengeanceDamageMin = 0.30f;
    private const float PlayerVengeanceDamageMax = 1.30f;
    private const float LizardVengeanceDamageMin = 0.12f;
    private const float LizardVengeanceDamageMax = 0.44f;
    private const float VengeanceStunMin = 14f;
    private const float VengeanceStunMax = 58f;
    private const float VengeanceImpactMin = 0.75f;
    private const float VengeanceImpactMax = 2.8f;

    private const float TongueRescueChanceMin = 0.18f;
    private const float TongueRescueChanceMax = 0.48f;
    private const float GraspRescueChanceScale = 0.62f;

    private readonly struct FollowerCandidate
    {
        internal readonly DB_Creature Bat;
        internal readonly DB_Creature Leader;
        internal readonly float Score;

        internal FollowerCandidate(DB_Creature bat, DB_Creature leader, float score)
        {
            Bat = bat;
            Leader = leader;
            Score = score;
        }
    }

    // Vengeance fields remain part of the exact same per-creature State object.
    // Splitting the declaration changes source ownership only, not lifetime or identity.
    internal sealed class State
    {
        internal Mode Mode;
        internal Participation Role;
        internal Creature VengeanceTarget;
        internal DB_Creature Leader;
        internal DB_Creature RescueVictim;
        internal LizardTongue RescueTongue;
        internal float Rage;
        internal float Commitment;
        internal float DamageScale = 1f;
        internal int VengeanceTimer;
        internal int PassesRemaining;
        internal bool RescueAttempted;
        internal bool WasRescuePlan;
        internal bool SupportOnly;
        internal bool Active => Mode != DB_VengeanceRuntime.Mode.None;

    }

    internal static bool ControlsMovement(State state)
    {
        return state?.Mode is Mode.Observe or Mode.Circle or Mode.Feint or
            Mode.RescueCharge or Mode.Charge or Mode.Withdraw;
    }

    internal static bool IsSupporterFor(State state, Creature threat)
    {
        return state != null && state.Active && state.Role == Participation.Supporter &&
               state.VengeanceTarget == threat;
    }

    internal static bool TargetMatches(State state, Creature threat)
    {
        return state != null && state.Active && state.VengeanceTarget == threat;
    }

    internal static bool IsActive(DB_Creature bat)
    {
        return bat != null && DB_FearRuntime.TryGetVengeanceState(bat, out State state) &&
               state.Active && state.Mode != Mode.None;
    }

    internal static bool IsAvenger(DB_Creature bat)
    {
        return bat != null && DB_FearRuntime.TryGetVengeanceState(bat, out State state) && state.Active &&
               state.Mode != Mode.None &&
               state.Role == Participation.Avenger;
    }

    internal static bool TryGetTarget(DB_Creature bat, out Creature target)
    {
        target = null;
        if (bat == null || !DB_FearRuntime.TryGetVengeanceState(bat, out State state) || !state.Active ||
            state.Mode == Mode.None || state.VengeanceTarget == null)
            return false;
        target = state.VengeanceTarget;
        return true;
    }

    internal static bool ExecuteOwned(DB_Creature bat)
    {
        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance) ||
            !DB_FearRuntime.TryGetVengeanceState(bat, out State state) || !state.Active ||
            state.Mode == Mode.None)
            return false;

        UpdateVengeance(bat, state);
        DB_FearRuntime.TryDeactivateAfterVengeance(bat);
        return true;
    }

    internal static void ArmGroup(
        List<DB_Creature> trueCandidates,
        List<DB_Creature> bats,
        int[] tier,
        DB_Creature victim,
        Creature threat,
        DB_FearRuntime.EventKind kind,
        LizardTongue rescueTongue,
        bool suppressNewVengeance)
    {
        if (suppressNewVengeance || !DB_FearRuntime.ValidThreat(threat, victim?.room))
            return;

        List<DB_Creature> leaders = new(MaxTrueAvengersPerEvent);
        int participants = 0;
        DB_Creature existingLeader = null;
        State existingLeaderState = null;

        for (int i = 0; i < bats.Count; i++)
        {
            DB_Creature bat = bats[i];
            if (!TryGetState(bat, out State social) ||
                social.Mode == Mode.None ||
                social.VengeanceTarget != threat ||
                social.Role == Participation.None)
                continue;

            participants++;
            if (existingLeader == null && social.Role == Participation.Avenger)
            {
                existingLeader = bat;
                existingLeaderState = social;
            }
        }

        if (existingLeader != null)
        {
            leaders.Add(existingLeader);
            if (existingLeaderState.Mode == Mode.Withdraw)
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
                State state = DB_FearRuntime.VengeanceStateFor(bat);
                float fearStrength = DB_FearRuntime.FearStrengthForVengeance(bat, threat);
                float trauma = DB_FearRuntime.PersistentTraumaStrength(bat, threat);
                if (fearStrength >= CollapseStrength ||
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
                IsActive(bat))
                continue;

            float trauma = DB_FearRuntime.PersistentTraumaStrength(bat, threat);
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

            State batState = DB_FearRuntime.VengeanceStateFor(bat);
            float fearStrength = DB_FearRuntime.FearStrengthForVengeance(bat, threat);
            float score =
                bat.Personality.Conformity * 0.50f +
                bat.Personality.Temperament * 0.20f +
                bat.Personality.Nerve * 0.15f +
                bestLeaderDrive * 0.15f -
                fearStrength * 0.28f -
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
            State state = DB_FearRuntime.VengeanceStateFor(follower.Bat);
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
        DB_FearRuntime.EventKind kind,
        DB_Creature victim,
        LizardTongue rescueTongue,
        float drive,
        float damageScale,
        bool supportOnly,
        DB_Creature leader)
    {
        float rage = Mathf.Clamp01(Mathf.Lerp(0.58f, 1f, drive) + DB_SocialBond.Motivation(bat, victim, threat));
        if (state.Mode != Mode.None && state.VengeanceTarget == threat)
        {
            state.Rage = Mathf.Max(state.Rage, rage);
            if (state.Role == Participation.Avenger)
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
        state.RescueVictim = kind == DB_FearRuntime.EventKind.PredatorCapture ? victim : null;
        state.RescueTongue = kind == DB_FearRuntime.EventKind.PredatorCapture ? rescueTongue : null;
        state.RescueAttempted = false;
        state.WasRescuePlan = kind == DB_FearRuntime.EventKind.PredatorCapture;
        state.Rage = rage;
        state.DamageScale = damageScale;
        state.SupportOnly = supportOnly;
        state.PassesRemaining = supportOnly
            ? 0
            : (leader == null && drive > 0.70f ? 2 : 1);
        state.Role = leader == null ? Participation.Avenger : Participation.Supporter;
        state.Mode = Mode.Waiting;

        int minDelay = kind == DB_FearRuntime.EventKind.PredatorCapture
            ? VengeanceCaptureDelayMin
            : VengeanceKillDelayMin;
        int maxDelay = kind == DB_FearRuntime.EventKind.PredatorCapture
            ? VengeanceCaptureDelayMax
            : VengeanceKillDelayMax;
        int socialDelay = leader == null
            ? 0
            : Mathf.RoundToInt(Mathf.Lerp(26f, 8f, bat.Personality.Conformity));
        state.VengeanceTimer = Mathf.RoundToInt(
            Mathf.Lerp(maxDelay, minDelay, drive)) + socialDelay;

        // Deliver synchronously before ArmGroup scores followers so Rally
        // interest can participate in SocialBond.Motivation without a detour around this method.
        if (state.Role == Participation.Avenger && !supportOnly && leader == null)
            DB_SignalRuntime.EmitRally(
                bat, threat, drive, "new Avenger armed -> immediate RallySignal");
    }

    private static void UpdateVengeance(DB_Creature bat, State state)
    {
        if (state.Mode == Mode.None) return;

        Creature target = state.VengeanceTarget;
        if (!DB_FearRuntime.ValidThreat(target, bat.room))
        {
            Clear(state);
            return;
        }

        if (state.Role == Participation.Supporter)
        {
            if (state.Leader == null || state.Leader.dead || state.Leader.room != bat.room)
            {
                DB_FearRuntime.AddTrauma(
                    bat,
                    target,
                    Mathf.Lerp(0.05f, 0.14f, bat.Personality.Conformity));
                Clear(state);
                bat.DesertAI.Threatened(target, false);
                return;
            }

            if (!TryGetState(state.Leader, out State leaderState) ||
                leaderState.Role != Participation.Avenger ||
                leaderState.VengeanceTarget != target)
            {
                if (state.Mode != Mode.Withdraw)
                    StartWithdraw(state, CombatDrive(bat, state));
            }
            else if (leaderState.Mode == Mode.Withdraw &&
                     state.Mode != Mode.Withdraw)
            {
                StartWithdraw(state, CombatDrive(bat, state));
            }
        }

        if (DB_FearRuntime.PersistentTraumaStrength(bat, target) >= DB_Tuning.TraumaSevere)
        {
            Clear(state);
            bat.DesertAI.Threatened(target, false);
            return;
        }

        float drive = CombatDrive(bat, state);
        Vector2 head = target.mainBodyChunk.pos;

        switch (state.Mode)
        {
            case Mode.Waiting:
                if (--state.VengeanceTimer > 0) return;
                if (state.WasRescuePlan && RescueStillPossible(state, target))
                {
                    state.Mode = Mode.RescueCharge;
                    state.VengeanceTimer = 0;
                }
                else
                {
                    state.Mode = Mode.Observe;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceObserveMaxTicks,
                        VengeanceObserveMinTicks,
                        drive));
                }
                break;

            case Mode.Observe:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 150f, 80f),
                    Mathf.Lerp(6.5f, 8.5f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Mode = Mode.Circle;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceCircleMaxTicks,
                        VengeanceCircleMinTicks,
                        drive));
                }
                break;

            case Mode.Circle:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 105f, 60f),
                    Mathf.Lerp(7.5f, 10f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Mode = Mode.Feint;
                    state.VengeanceTimer = VengeanceFeintTicks;
                }
                break;

            case Mode.Feint:
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
                        state.Mode = Mode.Charge;
                        state.VengeanceTimer = VengeanceChargeTimeout;
                    }
                }
                break;
            }

            case Mode.RescueCharge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 0.95f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer++;
                if (TryVengeanceContact(bat, state, target, true))
                {
                    if (state.Mode == Mode.RescueCharge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer > VengeanceChargeTimeout)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
                }
                break;

            case Mode.Charge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 1.05f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer--;
                if (TryVengeanceContact(bat, state, target, false))
                {
                    if (state.Mode == Mode.Charge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer <= 0)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
                }
                break;

            case Mode.Withdraw:
                ForceFlight(
                    bat,
                    bat.mainBodyChunk.pos +
                    Custom.DirVec(head, bat.mainBodyChunk.pos) * 190f + Vector2.up * 65f,
                    Mathf.Lerp(8f, 10.5f, drive));
                if (--state.VengeanceTimer <= 0)
                    Clear(state);
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
        if (state.RescueAttempted || target is not Lizard lizard || !DB_FearRuntime.IsPeach(lizard) ||
            state.RescueVictim == null || state.RescueVictim.dead ||
            state.RescueVictim.room != lizard.room)
            return false;

        state.RescueAttempted = true;
        float drive = CombatDrive(bat, state);
        float chance = Mathf.Lerp(TongueRescueChanceMin, TongueRescueChanceMax, drive) *
                       Mathf.Lerp(0.92f, 1.10f, bat.Personality.Nerve) *
                       Mathf.Lerp(0.90f, 1.08f, state.Rage);
        if (state.Role == Participation.Supporter)
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
        if (target is not Lizard lizard || !DB_FearRuntime.IsPeach(lizard) ||
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

        if (state.Role == Participation.Avenger && state.PassesRemaining > 0 &&
            target != null && !target.dead)
        {
            state.Mode = Mode.Circle;
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
        state.Mode = Mode.Withdraw;
        state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
            VengeanceWithdrawMaxTicks,
            VengeanceWithdrawMinTicks,
            drive));
    }

    private static float CombatDrive(DB_Creature bat, State state)
    {
        return state.Role == Participation.Supporter
            ? Mathf.Lerp(0.38f, 0.72f, state.Commitment)
            : bat.Personality.VengeanceDrive;
    }

    internal static bool TryGetState(
        DB_Creature bat,
        out State state)
    {
        state = null;
        return bat != null && DB_FearRuntime.TryGetVengeanceState(bat, out state);
    }

    internal static void Clear(State state)
    {
        if (state == null) return;
        state.Mode = Mode.None;
        state.Role = Participation.None;
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
        DB_FearRuntime.EventKind kind)
    {
        unchecked
        {
            uint x = (uint)bat.Personality.VisualSeed;
            x ^= (uint)(victim?.Personality.VisualSeed ?? 0) * 0x9E3779B9u;
            x ^= (uint)DB_FearRuntime.ThreatIdentity(threat) * 0x85EBCA6Bu;
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
        if (TryGetTarget(bat, out Creature vengeanceTarget) && vengeanceTarget is Player player)
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
            DB_BehaviorOwner.Mode,
            goal,
            speed,
            response: 0.28f);
    }
}
