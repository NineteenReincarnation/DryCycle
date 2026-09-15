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

    internal const float CloseDefenseRange = 86f;
    internal const float CloseDefenseGuardRange = 106f;
    internal const float CloseDefenseApproachSpeed = 2.5f;
    private const float CloseLiftRange = 32f;
    private const float LizardHeadLiftRange = 48f;
    private const float CloseLiftDamage = 0.50f;
    private const int CloseLiftFrames = 8;
    private const int CloseLiftCooldown = 22;
    private const int CloseThrustFrames = 10;
    private const int CloseThrustCooldown = 26;

    private readonly LanceScavenger _owner;
    private int _launchedSerial = -1;
    private Vector2 _launchPoint;
    private Vector2 _committedLanceDirection = Vector2.right;
    private float _committedLaunchY = ChargeLanePlanner.MaximumChargeLaunchY;

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
    internal float ChargeLaunchY => _committedLaunchY;
    internal float RunUp => _owner.Combat.State == LanceState.Charge ?
        Mathf.Max(0f, Vector2.Dot(_owner.mainBodyChunk.pos - _launchPoint, Direction)) : 0f;

    // Backstep has been removed from the combat model. Keep these two compatibility values only so
    // older debug/AI call sites compiled against the previous surface cannot reintroduce movement.
    internal bool BackstepComplete => true;
    internal float BackstepDistance => 0f;

    internal bool CounterSweepActive => _counterSweepActive;
    internal bool CounterSweepAttempted => _counterSweepAttempted;
    internal float CounterSweepChance => LanceCombatMath.CounterSweepChance(_owner.abstractCreature.personality);

    // CloseDefense owns only the weapon. Vanilla Scavenger.Act must keep the body free to flee,
    // turn, jump and path around a predator instead of being pinned in StandStill while stabbing.
    internal bool OwnsMovement => _owner.Combat.State == LanceState.Brace ||
        _owner.Combat.State == LanceState.Charge || _owner.Combat.State == LanceState.FollowUpThrow ||
        _owner.Combat.State == LanceState.Recover;
    internal bool OwnsWeaponAction => OwnsMovement || _owner.Combat.State == LanceState.CloseDefense;

    internal LanceMotor(LanceScavenger owner) { _owner = owner; }

    internal void Reset()
    {
        _launchPoint = _owner.mainBodyChunk.pos;
        Direction = Vector2.right;
        LanceDirection = Vector2.right;
        _committedLanceDirection = Vector2.right;
        _committedLaunchY = ChargeLanePlanner.MaximumChargeLaunchY;
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
        _committedLaunchY = solution.Valid
            ? ChargeLanePlanner.ClampChargeLaunchY(solution.LaunchY)
            : ChargeLanePlanner.MaximumChargeLaunchY;
        LanceDirection = _committedLanceDirection;
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

        if (state != LanceState.CloseDefense)
        {
            _owner.animation = null;
            _owner.swingPos = null;
            _owner.movMode = Scavenger.MovementMode.StandStill;
            _owner.moving = false;
        }

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
                    chunk.vel = new Vector2(Direction.x * speed, _committedLaunchY);

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

        if (_counterSweepActive)
            EndCounterSweep();
        else if (_committedLanceDirection.sqrMagnitude > 0.001f)
            LanceDirection = _committedLanceDirection;

        if (state == LanceState.CloseDefense)
        {
            UpdateCloseDefense();
            return;
        }

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
    }

    private void UpdateCloseDefense()
    {
        Creature target = _owner.Brain?.Target;
        ScavengerLance lance = _owner.Lance;
        if (target == null || lance == null || target.dead || !target.Consious || target.room != _owner.room)
            return;

        BodyChunk aimChunk = SelectCloseDefenseChunk(target);
        if (aimChunk == null) return;

        Vector2 toTarget = aimChunk.pos - _owner.mainBodyChunk.pos;
        if (toTarget.sqrMagnitude < 0.001f) return;
        Vector2 aim = toTarget.normalized;
        float face = Mathf.Sign(aim.x);
        if (face == 0f) face = Mathf.Sign(target.mainBodyChunk.pos.x - _owner.mainBodyChunk.pos.x);
        if (face == 0f) face = 1f;
        Direction = new Vector2(face, 0f);
        LanceDirection = aim;
        _owner.lookPoint = aimChunk.pos;

        if (!lance.CanThrust) return;

        float surfaceDistance = Mathf.Max(0f, toTarget.magnitude - aimChunk.rad);
        bool armoredLizardHead = target.abstractCreature?.creatureTemplate?.IsLizard == true &&
            aimChunk.index == 0 && target.bodyChunks.Length > 1;

        if (surfaceDistance <= CloseLiftRange || (armoredLizardHead && surfaceDistance <= LizardHeadLiftRange))
        {
            // An attacker inside the blade's comfortable straight-thrust distance gets picked
            // upward/outward. The weapon itself rotates through this arc over eight live collision
            // frames, so the visible sweep and the damaging sweep are the same physical motion.
            Vector2 liftStart = new Vector2(face,
                Mathf.Clamp(aim.y - 0.22f, -0.28f, 0.10f)).normalized;
            Vector2 liftEnd = new Vector2(face * 0.52f,
                Mathf.Clamp(0.86f + Mathf.Max(0f, aim.y) * 0.12f, 0.78f, 0.96f)).normalized;
            if (!DefensiveSweepClear(lance, liftStart, liftEnd, target)) return;
            lance.RequestDefensiveLift(liftStart, liftEnd, CloseLiftDamage, CloseLiftFrames, CloseLiftCooldown);
            return;
        }

        // Do not repeatedly stab the armored lizard head just because it is the only visible chunk.
        // Keep guarding and let vanilla movement expose neck/body, or use the lift once it gets close.
        if (armoredLizardHead) return;

        if (surfaceDistance <= CloseDefenseRange)
        {
            if (!DefensiveLaneClear(lance, aim, target)) return;
            lance.RequestDefensiveThrust(aim, LanceCombatMath.LanceScavengerCloseThrustMaxDamage,
                CloseThrustFrames, CloseThrustCooldown);
        }
    }

    private BodyChunk SelectCloseDefenseChunk(Creature target)
    {
        if (target?.bodyChunks == null || target.bodyChunks.Length == 0) return null;
        bool lizard = target.abstractCreature?.creatureTemplate?.IsLizard == true;

        // For lizards first make a strict pass over non-head chunks. This mirrors vanilla scavenger
        // spear discipline: body/neck is worth attacking, while chunk 0 is the armored head and is a
        // poor straight-thrust target. Only fall back to the head when no body chunk is reasonably
        // available, allowing the close-range lift to create space instead of wasting a stab.
        BodyChunk preferred = BestCloseChunk(target, skipLizardHead: lizard && target.bodyChunks.Length > 1);
        return preferred ?? BestCloseChunk(target, skipLizardHead: false) ?? target.mainBodyChunk;
    }

    private BodyChunk BestCloseChunk(Creature target, bool skipLizardHead)
    {
        BodyChunk best = null;
        float bestScore = float.MaxValue;
        foreach (BodyChunk chunk in target.bodyChunks)
        {
            if (chunk == null || (skipLizardHead && chunk.index == 0)) continue;
            float score = Vector2.Distance(_owner.mainBodyChunk.pos, chunk.pos);
            if (!_owner.room.VisualContact(_owner.mainBodyChunk.pos, chunk.pos)) score += 100f;
            if (score >= bestScore) continue;
            bestScore = score;
            best = chunk;
        }

        // A completely occluded body chunk should not beat a visible fallback head merely because it
        // is geometrically a little closer.
        return bestScore < 100f + CloseDefenseGuardRange ? best : null;
    }

    private bool DefensiveSweepClear(ScavengerLance lance, Vector2 from, Vector2 to, Creature target)
    {
        float start = Custom.VecToDeg(from);
        float end = Custom.VecToDeg(to);
        for (int i = 0; i <= 5; i++)
        {
            Vector2 direction = Custom.DegToVec(Mathf.LerpAngle(start, end, i / 5f));
            if (!DefensiveLaneClear(lance, direction, target)) return false;
        }
        return true;
    }

    private bool DefensiveLaneClear(ScavengerLance lance, Vector2 direction, Creature target)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Direction;
        float face = Mathf.Sign(dir.x);
        if (face == 0f) face = Direction.x == 0f ? 1f : Mathf.Sign(Direction.x);
        Vector2 grip = _owner.mainBodyChunk.pos + new Vector2(face * 7f, -5f);
        Vector2 tip = grip + dir * LanceCombatMath.ForwardLength(lance.Length);
        return !ChargeLanePlanner.FriendInPath(_owner, grip, tip, target);
    }

    private void UpdateCounterSweep()
    {
        if (_counterSweepActive)
        {
            if (_counterSweepAge >= CounterSweepFrames)
            {
                EndCounterSweep();
                return;
            }
            AdvanceCounterSweep();
            return;
        }

        // Once the one-shot sweep has happened, keep asking the carry rig for the original charge
        // direction. The rig supplies the visible inertial return instead of leaving the lance frozen
        // at the last sweep angle or snapping the rendered weapon back in one frame.
        if (_counterSweepAttempted)
        {
            LanceDirection = _committedLanceDirection;
            return;
        }

        if (_counterTarget == null || _counterTarget.dead || !_counterTarget.Consious ||
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
        {
            LanceDirection = _committedLanceDirection;
            return;
        }

        Vector2 end = Custom.DegToVec(currentAngle + sweptDelta).normalized;
        if (!CounterSweepArcClear(LanceDirection, end, _counterTarget))
        {
            LanceDirection = _committedLanceDirection;
            return;
        }

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
            EndCounterSweep();
            return;
        }

        LanceDirection = next;
        _counterSweepAge = nextAge;
    }

    private void EndCounterSweep()
    {
        _counterSweepActive = false;
        _counterSweepAge = CounterSweepFrames;
        LanceDirection = _committedLanceDirection.sqrMagnitude > 0.001f
            ? _committedLanceDirection.normalized
            : Direction;
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
        // The hand is a fixed pivot during charge. A counter-sweep rotates the lance around that
        // pivot; it must not translate the whole weapon through the scavenger when direction.x changes.
        float face = Mathf.Sign(Direction.x);
        if (face == 0f) face = Mathf.Sign(direction.x);
        if (face == 0f) face = 1f;
        return _owner.mainBodyChunk.pos + new Vector2(face * 7f, -5f);
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
