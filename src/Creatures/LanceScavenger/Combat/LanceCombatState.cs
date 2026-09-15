namespace DryCycle.Creatures.LanceScavenger;

internal enum LanceState { Observe, Threaten, CreateDistance, AcquireChargeLane, Backstep, Brace, Charge, FollowUpThrow, Recover, CloseDefense, Disarmed }

internal readonly struct LanceSituation
{
    internal LanceSituation(bool active, bool armed, bool sidearm, bool target, ScavengerAI.ViolenceType violence, bool afraid,
        float distance, bool lane, bool backstepComplete = true)
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
    }

    internal readonly bool Active, Armed, Sidearm, Target, Afraid, Lane, BackstepComplete;
    internal readonly float Distance;
    internal readonly ScavengerAI.ViolenceType Violence;
}

/// <summary>Decision timing only; body velocity and weapon impacts live in their own modules.</summary>
internal sealed class LanceCombatState
{
    // Rain World runs at roughly 40 simulation updates per second: 38 frames = 0.95 s.
    internal const int BraceFrames = 38;
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
    private int _followUpLandingAge = -1;

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
        if (!s.Target || s.Violence == ScavengerAI.ViolenceType.None)
        {
            if (State == LanceState.Charge || State == LanceState.Brace || State == LanceState.Backstep) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }
        if (s.Violence != ScavengerAI.ViolenceType.Lethal)
        {
            if (State == LanceState.Charge) Recover(false);
            else Enter(LanceState.Threaten);
            return;
        }
        if (State == LanceState.Charge)
        {
            if (Age >= MaxChargeFrames) FinishCharge(false);
            else if (!s.Lane) Recover(false);
            return;
        }

        // The backstep is a real movement phase. Do not continually invalidate it while
        // the body is moving; once it completes, re-evaluate the lane before bracing.
        if (State == LanceState.Backstep)
        {
            if (!s.BackstepComplete) return;
            if (s.Distance < ChargeLanePlanner.MinimumChargeDistance)
            {
                Enter(s.Afraid ? LanceState.Threaten : LanceState.CloseDefense);
                return;
            }
            if (!s.Lane)
            {
                Enter(s.Afraid ? LanceState.Threaten : LanceState.AcquireChargeLane);
                return;
            }
            if (Cooldown > 0) { Enter(LanceState.Threaten); return; }
            Enter(LanceState.Brace);
            return;
        }

        if (s.Afraid)
        {
            if (s.Distance < ChargeLanePlanner.MinimumChargeDistance || !s.Lane || Cooldown > 0)
            {
                Enter(LanceState.Threaten);
                return;
            }
            if (State != LanceState.Brace) { Enter(LanceState.Backstep); return; }
            if (Age >= BraceFrames)
            {
                _followUpReserved = s.Sidearm && s.Lane;
                _chargeLanded = false;
                AttackSerial++;
                Enter(LanceState.Charge);
            }
            return;
        }

        if (State == LanceState.CloseDefense && Age < 16) return;
        if (s.Distance < ChargeLanePlanner.MinimumChargeDistance)
        {
            Enter(State == LanceState.CloseDefense || Cooldown > 0 ? LanceState.CreateDistance : LanceState.CloseDefense);
            if (State == LanceState.CloseDefense) Cooldown = 48;
            return;
        }
        if (!s.Lane) { Enter(LanceState.AcquireChargeLane); return; }
        if (Cooldown > 0) { Enter(LanceState.Threaten); return; }
        if (State != LanceState.Brace) { Enter(LanceState.Backstep); return; }
        if (Age >= BraceFrames)
        {
            _followUpReserved = s.Sidearm && s.Lane;
            _chargeLanded = false;
            AttackSerial++;
            Enter(LanceState.Charge);
        }
    }

    internal void MarkLanding()
    {
        if (State == LanceState.Charge)
            _chargeLanded = true;
        else if (State == LanceState.FollowUpThrow && _followUpLandingAge < 0)
            _followUpLandingAge = Age;
    }

    internal void FinishCharge(bool wall)
    {
        if (State != LanceState.Charge) return;
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
        _followUpLandingAge = -1;
        _recoveryDuration = wall ? WallRecoveryFrames : RecoveryFrames;
        Cooldown = wall ? 115 : 76;
        State = LanceState.Recover;
        Age = 0;
    }

    internal void ResetForRoom()
    {
        _followUpReserved = false;
        _chargeLanded = false;
        _followUpLandingAge = -1;
        if (State == LanceState.Recover) return;
        if (State == LanceState.Charge || State == LanceState.Brace || State == LanceState.Backstep ||
            State == LanceState.FollowUpThrow) Recover(false);
        else Enter(LanceState.Observe);
    }

    private void Enter(LanceState next)
    { if (State == next) return; State = next; Age = 0; }
}
