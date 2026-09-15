namespace DryCycle.Creatures.LanceScavenger;

internal enum LanceState { Observe, Threaten, CreateDistance, AcquireChargeLane, Brace, Charge, Recover, CloseDefense, Disarmed }

internal readonly struct LanceSituation
{
    internal LanceSituation(bool active, bool armed, bool target, ScavengerAI.ViolenceType violence, bool afraid,
        float distance, bool lane)
    {
        Active = active;
        Armed = armed;
        Target = target;
        Violence = violence ?? ScavengerAI.ViolenceType.None;
        Afraid = afraid;
        Distance = distance;
        Lane = lane;
    }

    internal readonly bool Active, Armed, Target, Afraid, Lane;
    internal readonly float Distance;
    internal readonly ScavengerAI.ViolenceType Violence;
}

/// <summary>Decision timing only; body velocity and weapon impacts live in their own modules.</summary>
internal sealed class LanceCombatState
{
    internal const int BraceFrames = 60;
    internal const int MaxChargeFrames = 32;
    internal const int RecoveryFrames = 44;
    internal const int WallRecoveryFrames = 82;
    internal LanceState State { get; private set; }
    internal int Age { get; private set; }
    internal int Cooldown { get; private set; }
    internal int AttackSerial { get; private set; }
    private int _recoveryDuration;

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
            if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }
        if (!s.Armed)
        {
            if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
            else Enter(LanceState.Disarmed);
            return;
        }
        if (!s.Target || s.Violence == ScavengerAI.ViolenceType.None)
        {
            if (State == LanceState.Charge) Recover(false);
            else Enter(LanceState.Observe);
            return;
        }
        if (s.Violence != ScavengerAI.ViolenceType.Lethal)
        {
            // Vanilla Warning / NonLethal / ForFun remain non-lethal social states.
            if (State == LanceState.Charge) Recover(false);
            else Enter(LanceState.Threaten);
            return;
        }
        if (State == LanceState.Charge)
        {
            if (Age >= MaxChargeFrames || !s.Lane) Recover(false);
            return;
        }

        // A vanilla Afraid relationship keeps ownership of locomotion. It can still
        // counter-charge the moment its current retreat path naturally forms a valid lane.
        if (s.Afraid)
        {
            if (s.Distance < ChargeLanePlanner.MinimumChargeDistance || !s.Lane || Cooldown > 0)
            {
                Enter(LanceState.Threaten);
                return;
            }
            if (State != LanceState.Brace) { Enter(LanceState.Brace); return; }
            if (Age >= BraceFrames) { AttackSerial++; Enter(LanceState.Charge); }
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
        if (State == LanceState.Recover) return;
        if (State == LanceState.Charge || State == LanceState.Brace) Recover(false);
        else Enter(LanceState.Observe);
    }

    private void Enter(LanceState next)
    { if (State == next) return; State = next; Age = 0; }
}
