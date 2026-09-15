namespace DryCycle.Creatures.LanceScavenger;

internal enum LanceState { Observe, Threaten, CreateDistance, AcquireChargeLane, Brace, Charge, Recover, CloseDefense, Disarmed }

internal readonly struct LanceSituation
{
    internal LanceSituation(bool active, bool armed, bool target, bool hostile, bool warning, float distance, bool lane, bool stable)
    { Active = active; Armed = armed; Target = target; Hostile = hostile; Warning = warning; Distance = distance; Lane = lane; Stable = stable; }
    internal readonly bool Active, Armed, Target, Hostile, Warning, Lane, Stable;
    internal readonly float Distance;
}

/// <summary>Decision timing only; body velocity and weapon impacts live in their own modules.</summary>
internal sealed class LanceCombatState
{
    internal const int BraceFrames = 32;
    internal const int MaxChargeFrames = 32;
    internal const int RecoveryFrames = 44;
    internal const int WallRecoveryFrames = 82;
    internal LanceState State { get; private set; }
    internal int Age { get; private set; }
    internal int Cooldown { get; private set; }
    internal int AttackSerial { get; private set; }
    private int _recoveryDuration;
    private int _hostileFrames;
    private int _unstableFrames;

    internal void Tick(LanceSituation s)
    {
        Age++;
        if (Cooldown > 0) Cooldown--;
        if (State == LanceState.Recover)
        {
            if (Age >= _recoveryDuration) Enter(s.Armed ? LanceState.Observe : LanceState.Disarmed);
            return;
        }
        if (!s.Active)
        {
            _hostileFrames = 0;
            if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }
        if (!s.Armed)
        {
            _hostileFrames = 0;
            if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
            else Enter(LanceState.Disarmed);
            return;
        }
        if (!s.Target || !s.Hostile)
        {
            _hostileFrames = 0;
            if (State == LanceState.Charge) Recover(false);
            else Enter(s.Target && s.Warning ? LanceState.Threaten : LanceState.Observe);
            return;
        }
        _hostileFrames++;
        if (State == LanceState.Charge)
        {
            if (Age >= MaxChargeFrames || !s.Lane) Recover(false);
            return;
        }
        if (_hostileFrames < 18) { Enter(LanceState.Threaten); return; }
        if (State == LanceState.CloseDefense && Age < 16) return;
        if (s.Distance < ChargeLanePlanner.MinimumChargeDistance)
        {
            Enter(State == LanceState.CloseDefense || Cooldown > 0 ? LanceState.CreateDistance : LanceState.CloseDefense);
            if (State == LanceState.CloseDefense) Cooldown = 48;
            return;
        }
        if (!s.Lane) { Enter(LanceState.AcquireChargeLane); return; }
        if (!s.Stable)
        {
            // Procedural legs can briefly lift the torso during a planted brace.
            // Keep the wind-up through a short wobble, but never launch off balance.
            if (State == LanceState.Brace && ++_unstableFrames <= 6)
            {
                if (Age >= BraceFrames) Age = BraceFrames - 1;
                return;
            }
            Enter(LanceState.AcquireChargeLane);
            return;
        }
        _unstableFrames = 0;
        if (Cooldown > 0) { Enter(LanceState.Threaten); return; }
        if (State != LanceState.Brace) { Enter(LanceState.Brace); return; }
        if (Age >= BraceFrames) { AttackSerial++; Enter(LanceState.Charge); }
    }

    internal void Recover(bool wall)
    {
        _recoveryDuration = wall ? WallRecoveryFrames : RecoveryFrames;
        Cooldown = wall ? 115 : 76;
        State = LanceState.Recover;
        Age = 0;
    }

    internal void ResetForRoom()
    {
        _hostileFrames = 0;
        if (State == LanceState.Recover) return;
        if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
        else Enter(LanceState.Observe);
    }

    private void Enter(LanceState next)
    { if (State == next) return; State = next; Age = 0; _unstableFrames = 0; }
}
