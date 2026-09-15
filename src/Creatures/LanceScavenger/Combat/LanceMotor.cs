using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceMotor
{
    private const float MinimumBackstepDistance = 10f; // 0.5 tile
    private const float MaximumBackstepDistance = 30f; // 1.5 tiles
    private const int MaximumBackstepFrames = 18;

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
    private bool _backstepActive;
    private bool _backstepComplete;
    private float _backstepStartX;
    private float _backstepDistance;
    private float _backstepDirection;
    private int _backstepFrames;
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
    internal bool BackstepComplete => _backstepActive && _backstepComplete;
    internal float BackstepDistance => _backstepDistance;
    internal bool CounterSweepActive => _counterSweepActive;
    internal bool CounterSweepAttempted => _counterSweepAttempted;
    internal float CounterSweepChance => LanceCombatMath.CounterSweepChance(_owner.abstractCreature.personality);
    internal bool OwnsMovement => _owner.Combat.State == LanceState.Backstep || _owner.Combat.State == LanceState.Brace ||
        _owner.Combat.State == LanceState.Charge || _owner.Combat.State == LanceState.FollowUpThrow ||
        _owner.Combat.State == LanceState.Recover || _owner.Combat.State == LanceState.CloseDefense;

    internal LanceMotor(LanceScavenger owner) { _owner = owner; }

    internal void Reset()
    {
        _launchPoint = _owner.mainBodyChunk.pos;
        _backstepActive = false;
        _backstepComplete = false;
        _backstepFrames = 0;
        Direction = Vector2.right;
        LanceDirection = Vector2.right;
        _committedLanceDirection = Vector2.right;
        ResetCounterSweep();
    }

    internal void CommitCharge(ChargeLane solution)
    {
        Vector2 solved = solution.CanHit ? solution.LanceDirection : Vector2.zero;
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

    internal void BeginBackstep(Creature target)
    {
        _backstepActive = true;
        _backstepComplete = false;
        _backstepFrames = 0;
        _backstepStartX = _owner.mainBodyChunk.pos.x;

        if (target == null || _owner.room == null)
        {
            _backstepDistance = 0f;
            _backstepComplete = true;
            return;
        }

        float away = Mathf.Sign(_owner.mainBodyChunk.pos.x - target.mainBodyChunk.pos.x);
        if (away == 0f) away = -Mathf.Sign(_owner.lookPoint.x - _owner.mainBodyChunk.pos.x);
        if (away == 0f) away = -1f;
        _backstepDirection = away;

        AbstractCreature.Personality personality = _owner.abstractCreature.personality;
        float caution = Mathf.Clamp01(personality.nervous * 0.55f +
            (1f - personality.bravery) * 0.25f + (1f - personality.aggression) * 0.20f);
        float desired = Mathf.Lerp(MinimumBackstepDistance, MaximumBackstepDistance, caution);

        float currentHorizontal = Mathf.Abs(target.mainBodyChunk.pos.x - _owner.mainBodyChunk.pos.x);
        float rangeRoom = ChargeLanePlanner.MaximumChargeDistance(_owner) - currentHorizontal - 4f;
        _backstepDistance = Mathf.Min(desired, Mathf.Max(0f, rangeRoom));
        if (_backstepDistance < 1f) _backstepComplete = true;
    }

    internal void Act()
    {
        LanceState state = _owner.Combat.State;
        _owner.animation = null;
        _owner.swingPos = null;
        _owner.movMode = Scavenger.MovementMode.StandStill;
        _owner.moving = false;

        if (state == LanceState.Backstep)
        {
            if (!_backstepActive) BeginBackstep(_owner.Brain?.Target);
            if (_backstepComplete)
            {
                foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel.x *= 0.55f;
                return;
            }

            float travelled = Mathf.Abs(_owner.mainBodyChunk.pos.x - _backstepStartX);
            float remaining = Mathf.Max(0f, _backstepDistance - travelled);
            if (remaining <= 1f || _backstepFrames++ >= MaximumBackstepFrames || BackstepBlocked())
            {
                _backstepComplete = true;
                foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel.x *= 0.45f;
                return;
            }

            float speed = Mathf.Min(3.4f, Mathf.Max(1.6f, remaining * 0.32f));
            foreach (BodyChunk chunk in _owner.bodyChunks)
                chunk.vel.x = Mathf.Lerp(chunk.vel.x, _backstepDirection * speed, 0.68f);
            _owner.WeightedPush(1, 0, new Vector2(_backstepDirection, 0f), 0.16f);
            return;
        }

        _backstepActive = false;
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
                    _counterTargetLaunchVelocity = _counterTarget.mainBodyChunk.vel;
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
        if (state == LanceState.Brace && _owner.Combat.Age == 1)
            _owner.room.PlaySound(SoundID.Scavenger_Knuckle_Hit_Ground, _owner.mainBodyChunk.pos, 0.55f, 0.7f);
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

        // Only a dodge that actually requires an angular correction consumes the one roll.
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
        Vector2 expected = _counterTargetLaunchPos +
            Vector2.ClampMagnitude(_counterTargetLaunchVelocity * chargeAge, 90f);
        Vector2 deviation = chunk.pos - expected;
        Vector2 perpendicular = Custom.PerpendicularVector(Direction);
        float lateral = Mathf.Abs(Vector2.Dot(deviation, perpendicular));
        float longitudinal = Mathf.Abs(Vector2.Dot(deviation, Direction));
        bool escapedPrediction = lateral >= Mathf.Max(MinimumDodgeDeviation, chunk.rad + 5f) ||
            longitudinal >= 18f;
        bool targetBehind = Vector2.Dot(chunk.pos - _owner.mainBodyChunk.pos, Direction) < -4f;
        return escapedPrediction || TargetReversed() || targetBehind;
    }

    private bool TargetReversed()
    {
        Vector2 oldVelocity = _counterTargetLaunchVelocity;
        Vector2 currentVelocity = _counterTarget?.mainBodyChunk.vel ?? Vector2.zero;
        if (oldVelocity.magnitude < 1.5f || currentVelocity.magnitude < 1.5f) return false;
        return Vector2.Dot(oldVelocity.normalized, currentVelocity.normalized) < -0.25f;
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

    private bool BackstepBlocked()
    {
        Vector2 probe = _owner.mainBodyChunk.pos + Vector2.right * (_backstepDirection * 7f);
        return _owner.room.GetTile(probe).Solid ||
            _owner.room.GetTile(probe + Vector2.up * 12f).Solid ||
            _owner.room.GetTile(probe - Vector2.up * 8f).Solid;
    }
}
