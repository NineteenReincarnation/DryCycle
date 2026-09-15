namespace DryCycle.Creatures.LanceScavenger;

// Backstep remains as a legacy enum value so old debug/state consumers do not break during hot reload,
// but the combat state machine no longer enters or waits on it.
internal enum LanceState { Observe, Threaten, CreateDistance, AcquireChargeLane, Backstep, Brace, Charge, FollowUpThrow, Recover, CloseDefense, Disarmed }

internal readonly struct LanceSituation
{
    internal LanceSituation(bool active, bool armed, bool sidearm, bool target, ScavengerAI.ViolenceType violence, bool afraid,
        float distance, bool lane, bool backstepComplete = true, bool friendBlocked = false, bool chargePriority = true,
        bool commitReady = false, bool hardBlocked = false, bool closeDanger = false)
    {
        Active = active;
        Armed = armed;
        Sidearm = sidearm;
        Target = target;
        Violence = violence ?? ScavengerAI.ViolenceType.None;
        Afraid = afraid;
        Distance = distance;
        Lane = lane;
        BackstepComplete = backstepComplete;
        FriendBlocked = friendBlocked;
        ChargePriority = chargePriority;
        CommitReady = commitReady;
        HardBlocked = hardBlocked;
        CloseDanger = closeDanger;
    }

    internal readonly bool Active, Armed, Sidearm, Target, Afraid, Lane, BackstepComplete, FriendBlocked,
        ChargePriority, CommitReady, HardBlocked, CloseDanger;
    internal readonly float Distance;
    internal readonly ScavengerAI.ViolenceType Violence;
}

/// <summary>Decision timing only; body velocity and weapon impacts live in their own modules.</summary>
internal sealed class LanceCombatState
{
    // Rain World updates gameplay at roughly 40 ticks/s. 20 ticks = 0.5 second visible brace.
    internal const int BraceFrames = 20;
    internal const int CommitWindowFrames = BraceFrames;
    internal const int MaxChargeFrames = 32;
    internal const int FollowUpThrowFrames = 8;
    internal const int FollowUpThrowTimeout = 60;
    internal const int RecoveryFrames = 44;
    internal const int WallRecoveryFrames = 82;
    internal LanceState State { get; private set; }
    internal int Age { get; private set; }
    internal int Cooldown { get; private set; }
    internal int AttackSerial { get; private set; }
    internal bool FollowUpReady => State == LanceState.FollowUpThrow && _followUpLandingAge >= 0 &&
        Age - _followUpLandingAge >= FollowUpThrowFrames;
    private int _recoveryDuration;
    private bool _followUpReserved;
    private bool _chargeLanded;
    private bool _chargeAirborne;
    private int _followUpLandingAge = -1;
    private int _braceLostSolutionFrames;

    internal void Tick(LanceSituation s)
    {
        Age++;
        if (Cooldown > 0) Cooldown--;
        if (State == LanceState.Recover)
        {
            if (Age >= _recoveryDuration) Enter(s.Armed ? LanceState.Observe : LanceState.Disarmed);
            return;
        }
        if (State == LanceState.FollowUpThrow)
        {
            if (!s.Active || !s.Armed || !s.Sidearm || !s.Target || s.Violence != ScavengerAI.ViolenceType.Lethal ||
                Age >= FollowUpThrowTimeout)
                Recover(false);
            return;
        }
        if (!s.Active)
        {
            if (State == LanceState.Charge || State == LanceState.Brace || State == LanceState.Backstep) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }
        if (!s.Armed)
        {
            if (State == LanceState.Charge || State == LanceState.Brace || State == LanceState.Backstep) Recover(false);
            else Enter(LanceState.Disarmed);
            return;
        }

        // A launched charge is a physical commitment. Soft target/relationship changes do not
        // cancel it in mid-air; landing, wall impact, interruption or timeout ends the motion.
        if (State == LanceState.Charge)
        {
            if (Age >= MaxChargeFrames) FinishCharge(false);
            return;
        }

        // Backstep was removed from the combat chain. If an old live instance survives a hot reload
        // while still carrying that state, migrate it straight into the new half-second brace.
        if (State == LanceState.Backstep)
        {
            Enter(LanceState.Brace);
            return;
        }

        if (!s.Target || s.Violence == ScavengerAI.ViolenceType.None)
        {
            if (State == LanceState.Brace) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }

        // Self preservation comes before fear/charge tactics. A lethal creature already inside the
        // defensive envelope must be answered with the lance even when vanilla relationship logic
        // says the scavenger is afraid. Fear still decides what happens after distance is restored.
        if (s.CloseDanger && s.Violence == ScavengerAI.ViolenceType.Lethal)
        {
            Enter(LanceState.CloseDefense);
            return;
        }

        if (s.Violence != ScavengerAI.ViolenceType.Lethal)
        {
            Enter(LanceState.Threaten);
            return;
        }

        if (State == LanceState.Brace)
        {
            if (s.Distance < ChargeLanePlanner.MinimumChargeDistance)
            {
                Enter(LanceState.CloseDefense);
                return;
            }
            if (!s.ChargePriority || Cooldown > 0)
            {
                Enter(LanceState.Threaten);
                return;
            }
            if (s.HardBlocked)
            {
                Enter(s.Afraid || s.FriendBlocked ? LanceState.Threaten : LanceState.AcquireChargeLane);
                return;
            }

            if (s.Lane) _braceLostSolutionFrames = 0;
            else _braceLostSolutionFrames++;

            if (Age >= BraceFrames)
            {
                if (!s.CommitReady)
                {
                    Enter(s.Afraid || s.FriendBlocked ? LanceState.Threaten : LanceState.AcquireChargeLane);
                    return;
                }

                _followUpReserved = s.Sidearm;
                _chargeLanded = false;
                _chargeAirborne = false;
                AttackSerial++;
                Enter(LanceState.Charge);
            }
            return;
        }

        // Afraid remains a vanilla relationship result, but a lethal target with a valid lane no
        // longer spends another movement phase backing away. It braces immediately and commits.
        // CloseDanger has already been handled above, so an afraid scavenger still defends itself
        // before this branch is allowed to choose Threaten/Brace.
        if (s.Afraid)
        {
            if (!s.ChargePriority || s.Distance < ChargeLanePlanner.MinimumChargeDistance ||
                !s.Lane || Cooldown > 0)
            {
                Enter(LanceState.Threaten);
                return;
            }
            Enter(LanceState.Brace);
            return;
        }

        if (!s.ChargePriority)
        {
            Enter(LanceState.Threaten);
            return;
        }

        if (s.Distance < ChargeLanePlanner.MinimumChargeDistance)
        {
            Enter(LanceState.CloseDefense);
            return;
        }
        if (s.FriendBlocked)
        {
            Enter(LanceState.Threaten);
            return;
        }
        if (!s.Lane) { Enter(LanceState.AcquireChargeLane); return; }
        if (Cooldown > 0) { Enter(LanceState.Threaten); return; }

        // Valid attack lane -> stop and brace immediately. There is no preparatory backstep.
        Enter(LanceState.Brace);
    }

    internal void MarkAirborne()
    {
        if (State == LanceState.Charge)
            _chargeAirborne = true;
    }

    internal void MarkLanding()
    {
        if (State == LanceState.Charge)
        {
            if (!_chargeAirborne) return;
            _chargeLanded = true;
            FinishCharge(false);
        }
        else if (State == LanceState.FollowUpThrow && _followUpLandingAge < 0)
        {
            _followUpLandingAge = Age;
        }
    }

    // Called only in the transition frame, after the final corrected lance angle has been checked.
    // This is not a recovery: no launch happened, so no cooldown or recovery tax is applied.
    internal void CancelChargeBeforeLaunch(bool friendBlocked, bool afraid)
    {
        if (State != LanceState.Charge || _chargeAirborne) return;
        _followUpReserved = false;
        _chargeLanded = false;
        _chargeAirborne = false;
        if (AttackSerial > 0) AttackSerial--;
        Enter(afraid || friendBlocked ? LanceState.Threaten : LanceState.AcquireChargeLane);
    }

    internal void FinishCharge(bool wall)
    {
        if (State != LanceState.Charge) return;
        _chargeAirborne = false;
        if (!wall && _followUpReserved)
        {
            bool alreadyLanded = _chargeLanded;
            Enter(LanceState.FollowUpThrow);
            _followUpLandingAge = alreadyLanded ? 0 : -1;
            _chargeLanded = false;
            return;
        }
        Recover(wall);
    }

    internal void CompleteFollowUp()
    {
        if (State == LanceState.FollowUpThrow) Recover(false);
    }

    internal void Recover(bool wall)
    {
        _followUpReserved = false;
        _chargeLanded = false;
        _chargeAirborne = false;
        _followUpLandingAge = -1;
        _braceLostSolutionFrames = 0;
        _recoveryDuration = wall ? WallRecoveryFrames : RecoveryFrames;
        Cooldown = wall ? 115 : 76;
        State = LanceState.Recover;
        Age = 0;
    }

    internal void ResetForRoom()
    {
        _followUpReserved = false;
        _chargeLanded = false;
        _chargeAirborne = false;
        _followUpLandingAge = -1;
        _braceLostSolutionFrames = 0;
        if (State == LanceState.Recover) return;
        if (State == LanceState.Charge || State == LanceState.Brace || State == LanceState.Backstep ||
            State == LanceState.FollowUpThrow) Recover(false);
        else Enter(LanceState.Observe);
    }

    private void Enter(LanceState next)
    {
        if (State == next) return;
        State = next;
        Age = 0;
        _braceLostSolutionFrames = 0;
    }
}