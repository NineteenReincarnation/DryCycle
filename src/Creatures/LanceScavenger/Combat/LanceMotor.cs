using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceMotor
{
    private const int CounterSweepFrames = 8;
    private const int MinimumCounterSweepRemainingFrames = 8;
    private const float MinimumDodgeDeviation = 13f;
    private const float MinimumCounterSweepAngle = 14f;
    private const float NormalCounterSweepArc = 80f;
    private const float ReversalCounterSweepArc = 120f;
    private const float CounterSweepOvershoot = 10f;

    private readonly LanceScavenger _owner;
    private int _launchedSerial = -1;
    private Vector2 _launchPoint;
    private Vector2 _committedLanceDirection = Vector2.right;

    private Creature _counterTarget;
    private Vector2 _counterTargetLaunchPos;
    private Vector2 _counterTargetLaunchVelocity;
    private bool _counterSweepAttempted;
    private bool _counterSweepActive;
    private int _counterSweepAge;
    private Vector2 _counterSweepStartDirection = Vector2.right;
    private Vector2 _counterSweepEndDirection = Vector2.right;

    internal Vector2 Direction { get; private set; } = Vector2.right;
    internal Vector2 LanceDirection { get; private set; } = Vector2.right;
    internal float RunUp => _owner.Combat.State == LanceState.Charge ?
        Mathf.Max(0f, Vector2.Dot(_owner.mainBodyChunk.pos - _launchPoint, Direction)) : 0f;

    // Backstep has been removed from the combat model. Keep these two compatibility values only so
    // older debug/AI call sites compiled against the previous surface cannot reintroduce movement.
    internal bool BackstepComplete => true;
    internal float BackstepDistance => 0f;

    internal bool CounterSweepActive => _counterSweepActive;
    internal bool CounterSweepAttempted => _counterSweepAttempted;
    internal float CounterSweepChance => LanceCombatMath.CounterSweepChance(_owner.abstractCreature.personality);
    internal bool OwnsMovement => _owner.Combat.State == LanceState.Brace ||
        _owner.Combat.State == LanceState.Charge || _owner.Combat.State == LanceState.FollowUpThrow ||
        _owner.Combat.State == LanceState.Recover || _owner.Combat.State == LanceState.CloseDefense;

    internal LanceMotor(LanceScavenger owner) { _owner = owner; }

    internal void Reset()
    {
        _launchPoint = _owner.mainBodyChunk.pos;
        Direction = Vector2.right;
        LanceDirection = Vector2.right;
        _committedLanceDirection = Vector2.right;
        ResetCounterSweep();
    }

    internal void CommitCharge(LanceAimSolution solution)
    {
        Vector2 solved = solution.Valid ? solution.LanceDirection : Vector2.zero;
        if (solved.sqrMagnitude < 0.001f)
        {
            float sign = Mathf.Sign((_owner.Brain?.Aim.x ?? _owner.lookPoint.x) - _owner.mainBodyChunk.pos.x);
            solved = new Vector2(sign == 0f ? 1f : sign, 0f);
        }
        _committedLanceDirection = solved.normalized;
        _counterTarget = _owner.Brain?.Target;
        _counterSweepAttempted = false;
        _counterSweepActive = false;
        _counterSweepAge = 0;
    }

    // Compatibility no-op. The state machine no longer enters Backstep and this method never moves
    // the creature; it only prevents stale callers from failing to compile during the transition.
    internal void BeginBackstep(Creature target) { }

    internal void Act()
    {
        LanceState state = _owner.Combat.State;
        _owner.animation = null;
        _owner.swingPos = null;
        _owner.movMode = Scavenger.MovementMode.StandStill;
        _owner.moving = false;

        if (state == LanceState.Charge)
        {
            if (_launchedSerial != _owner.Combat.AttackSerial)
            {
                _launchedSerial = _owner.Combat.AttackSerial;
                _launchPoint = _owner.mainBodyChunk.pos;
                LanceDirection = _committedLanceDirection.sqrMagnitude > 0.001f
                    ? _committedLanceDirection.normalized : Vector2.right;
                float sign = Mathf.Sign(LanceDirection.x);
                Direction = new Vector2(sign == 0f ? 1f : sign, 0f);
                float speed = ChargeLanePlanner.ChargeSpeed(_owner);
                foreach (BodyChunk chunk in _owner.bodyChunks)
                    chunk.vel = new Vector2(Direction.x * speed, ChargeLanePlanner.ChargeLaunchY);

                _counterTarget ??= _owner.Brain?.Target;
                if (_counterTarget != null)
                {
                    _counterTargetLaunchPos = _counterTarget.mainBodyChunk.pos;
                    TargetMotionTracker tracker = _owner.Brain?.MotionTracker;
                    _counterTargetLaunchVelocity = tracker != null && tracker.Target == _counterTarget
                        ? tracker.SmoothedVelocity(_counterTarget.mainBodyChunk)
                        : _counterTarget.mainBodyChunk.vel;
                }
                _counterSweepAttempted = false;
                _counterSweepActive = false;
                _counterSweepAge = 0;
                _owner.room.PlaySound(SoundID.Slugcat_Throw_Spear, _owner.mainBodyChunk.pos, 0.75f, 0.7f);
            }

            UpdateCounterSweep();
            _owner.WeightedPush(1, 0, Direction, 0.32f);
            return;
        }

        if (_counterSweepActive) _counterSweepActive = false;
        if (state == LanceState.Recover)
        {
            if (_owner.IsStableForBrace)
                foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel.x *= 0.9f;
            return;
        }

        Vector2 aim = _owner.Brain.Target == null ? Vector2.right :
            Custom.DirVec(_owner.mainBodyChunk.pos, _owner.Brain.Target.mainBodyChunk.pos);
        foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel.x *= state == LanceState.FollowUpThrow ? 0.78f : 0.65f;
        _owner.WeightedPush(1, 0, new Vector2(aim.x, 0f), state == LanceState.Brace ? 0.15f : 0.08f);
        if (state == LanceState.CloseDefense)
            _owner.Lance?.RequestThrust(aim, LanceCombatMath.LanceScavengerCloseThrustMaxDamage);
    }

    private void UpdateCounterSweep()
    {
        if (_counterSweepActive)
        {
            if (_counterSweepAge >= CounterSweepFrames)
            {
                _counterSweepActive = false;
                return;
            }
            AdvanceCounterSweep();
            return;
        }
        if (_counterSweepAttempted || _counterTarget == null || _counterTarget.dead || !_counterTarget.Consious ||
            _counterTarget.room != _owner.room || _owner.Lance == null ||
            _owner.Lance.HasHitCreature(_counterTarget))
            return;

        int chargeAge = _owner.Combat.Age;
        int remaining = LanceCombatState.MaxChargeFrames - chargeAge;
        if (chargeAge < 2 || remaining < MinimumCounterSweepRemainingFrames || !TargetDodged(chargeAge))
            return;

        Vector2 grip = ChargeGrip(LanceDirection);
        Vector2 toTarget = _counterTarget.mainBodyChunk.pos - grip;
        if (toTarget.sqrMagnitude < 16f)
            return;

        float currentAngle = Custom.VecToDeg(LanceDirection);
        float targetAngle = Custom.VecToDeg(toTarget.normalized);
        float delta = Mathf.DeltaAngle(currentAngle, targetAngle);
        bool targetBehind = Vector2.Dot(_counterTarget.mainBodyChunk.pos - _owner.mainBodyChunk.pos, Direction) < 0f;
        bool reversed = TargetReversed();
        float maxArc = targetBehind || reversed ? ReversalCounterSweepArc : NormalCounterSweepArc;
        if (Mathf.Abs(delta) < MinimumCounterSweepAngle && !targetBehind)
            return;

        float sign = delta == 0f ? (Custom.PerpendicularVector(Direction).y >= 0f ? 1f : -1f) : Mathf.Sign(delta);
        float sweptDelta = Mathf.Clamp(delta + sign * CounterSweepOvershoot, -maxArc, maxArc);
        if (Mathf.Abs(sweptDelta) < MinimumCounterSweepAngle)
            return;

        _counterSweepAttempted = true;
        if (UnityEngine.Random.value > CounterSweepChance)
            return;

        Vector2 end = Custom.DegToVec(currentAngle + sweptDelta).normalized;
        if (!CounterSweepArcClear(LanceDirection, end, _counterTarget))
            return;

        _counterSweepStartDirection = LanceDirection;
        _counterSweepEndDirection = end;
        _counterSweepAge = 0;
        _counterSweepActive = true;
        _owner.room.PlaySound(SoundID.Slugcat_Throw_Spear, _owner.mainBodyChunk.pos, 0.55f, 1.25f);
        AdvanceCounterSweep();
    }

    private bool TargetDodged(int chargeAge)
    {
        BodyChunk chunk = _counterTarget.mainBodyChunk;
        // Do not cap the total expected displacement at 65 px. That cap is useful for pre-launch
        // tactical aiming, but during a real charge it made an honestly running target appear to
        // "dodge" as soon as its steady displacement exceeded 65 px. Cap only implausible launch
        // speed, then keep the expected point moving for the whole flight.
        Vector2 expectedVelocity = Vector2.ClampMagnitude(_counterTargetLaunchVelocity, 12f);
        Vector2 expected = _counterTargetLaunchPos + expectedVelocity * chargeAge;
        Vector2 deviation = chunk.pos - expected;
        Vector2 perpendicular = Custom.PerpendicularVector(Direction);
        float lateral = Mathf.Abs(Vector2.Dot(deviation, perpendicular));
        float longitudinal = Mathf.Abs(Vector2.Dot(deviation, Direction));
        bool escapedPrediction = lateral >= Mathf.Max(MinimumDodgeDeviation, chunk.rad + 5f) || longitudinal >= 18f;
        bool targetBehind = Vector2.Dot(chunk.pos - _owner.mainBodyChunk.pos, Direction) < -4f;
        TargetMotionTracker tracker = _owner.Brain?.MotionTracker;
        float trackedDodge = tracker != null && tracker.Target == _counterTarget ? tracker.DodgeSeverity : 0f;
        return escapedPrediction || trackedDodge >= 0.55f || TargetReversed() || targetBehind;
    }

    private bool TargetReversed()
    {
        TargetMotionTracker tracker = _owner.Brain?.MotionTracker;
        if (tracker != null && tracker.Target == _counterTarget)
            return tracker.ReversedFrom(_counterTargetLaunchVelocity, _counterTarget.mainBodyChunk);

        Vector2 currentVelocity = _counterTarget?.mainBodyChunk.vel ?? Vector2.zero;
        if (_counterTargetLaunchVelocity.magnitude < 1.5f || currentVelocity.magnitude < 1.5f) return false;
        return Vector2.Dot(_counterTargetLaunchVelocity.normalized, currentVelocity.normalized) < -0.25f;
    }

    private void AdvanceCounterSweep()
    {
        if (!_counterSweepActive) return;

        int nextAge = Mathf.Min(CounterSweepFrames, _counterSweepAge + 1);
        float t = Mathf.Clamp01((float)nextAge / CounterSweepFrames);
        float eased = t * t * (3f - 2f * t);
        float startAngle = Custom.VecToDeg(_counterSweepStartDirection);
        float endAngle = Custom.VecToDeg(_counterSweepEndDirection);
        Vector2 next = Custom.DegToVec(Mathf.LerpAngle(startAngle, endAngle, eased)).normalized;

        if (!CounterSweepPoseClear(next, _counterTarget))
        {
            _counterSweepActive = false;
            return;
        }

        LanceDirection = next;
        _counterSweepAge = nextAge;
    }

    private bool CounterSweepArcClear(Vector2 from, Vector2 to, Creature target)
    {
        float start = Custom.VecToDeg(from);
        float end = Custom.VecToDeg(to);
        for (int i = 1; i <= 6; i++)
        {
            Vector2 direction = Custom.DegToVec(Mathf.LerpAngle(start, end, i / 6f));
            if (!CounterSweepPoseClear(direction, target)) return false;
        }
        return true;
    }

    private bool CounterSweepPoseClear(Vector2 direction, Creature target)
    {
        if (_owner.room == null || _owner.Lance == null) return false;
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Direction;
        Vector2 grip = ChargeGrip(dir);
        float length = _owner.Lance.Length;
        float forward = LanceCombatMath.ForwardLength(length);
        Vector2 tail = grip - dir * (length * LanceCombatMath.GripFraction);
        if (_owner.room.GetTile(tail).Solid) return false;

        Vector2 perp = Custom.PerpendicularVector(dir);
        for (int i = 0; i < LanceCombatMath.BladeSweepSamples; i++)
        {
            float bladeT = LanceCombatMath.BladeSweepSamples == 1 ? 1f :
                (float)i / (LanceCombatMath.BladeSweepSamples - 1);
            Vector2 point = LanceCombatMath.BladePoint(grip, dir, forward, bladeT);
            float halfWidth = LanceCombatMath.BladeHalfWidth(bladeT);
            if (_owner.room.GetTile(point).Solid ||
                _owner.room.GetTile(point + perp * halfWidth).Solid ||
                _owner.room.GetTile(point - perp * halfWidth).Solid)
                return false;
        }

        Vector2 bladeRoot = LanceCombatMath.BladePoint(grip, dir, forward, 0f);
        Vector2 tip = LanceCombatMath.BladePoint(grip, dir, forward, 1f);
        return !ChargeLanePlanner.FriendInPath(_owner, bladeRoot, tip, target);
    }

    private Vector2 ChargeGrip(Vector2 direction)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Direction;
        return _owner.mainBodyChunk.pos + new Vector2(dir.x * 7f, -5f);
    }

    private void ResetCounterSweep()
    {
        _counterTarget = null;
        _counterTargetLaunchPos = Vector2.zero;
        _counterTargetLaunchVelocity = Vector2.zero;
        _counterSweepAttempted = false;
        _counterSweepActive = false;
        _counterSweepAge = 0;
        _counterSweepStartDirection = Vector2.right;
        _counterSweepEndDirection = Vector2.right;
    }
}
