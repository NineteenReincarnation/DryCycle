using DryCycle.DevUI.DevTool.Debug;
using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAI : ScavengerAI
{
    private readonly LanceScavenger _owner;
    private readonly TargetMotionTracker _motionTracker = new();
    private int _planAge;
    private WorldCoordinate? _staging;
    private bool _sidearmThrowPass;
    private bool _sidearmDrawn;
    private Creature _chargeTarget;
    private bool _chargeTargetAfraid;
    private Creature _recentAimTarget;
    private LanceAimSolution _recentAimSolution;
    private int _recentAimAge = int.MaxValue;
    internal bool SkipNextUpdate;
    internal Creature Target { get; private set; }
    internal ViolenceType TargetViolence { get; private set; } = ViolenceType.None;
    internal bool TargetAfraid { get; private set; }
    internal LanceAimSolution AimSolution { get; private set; }
    internal ChargeLane Lane { get; private set; }
    internal bool ChargePriority { get; private set; } = true;
    internal bool SidearmThrowPass => _sidearmThrowPass;
    internal bool SidearmDrawn => _sidearmDrawn;
    internal bool AllowVanillaSidearmCombat { get; private set; }
    internal TargetMotionTracker MotionTracker => _motionTracker;
    internal LanceAimSolution DebugRecentAimSolution => _recentAimSolution;
    internal int DebugRecentAimAge => _recentAimAge;
    internal bool DebugRecentAimReady => RecentAimSolutionValid(Target);
    internal bool DebugCommitReady { get; private set; }
    internal bool DebugHardBlocked { get; private set; }
    internal bool DebugFriendBlocked { get; private set; }
    internal bool DebugChargeOpportunity { get; private set; }
    internal Vector2 Aim => Target != null
        ? (AimSolution.Valid ? AimSolution.Aim : Target.mainBodyChunk.pos)
        : _owner.lookPoint;

    internal LanceScavengerAI(AbstractCreature creature, World world) : base(creature, world)
    {
        _owner = (LanceScavenger)creature.realizedCreature;
    }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        Target = null;
        _chargeTarget = null;
        _chargeTargetAfraid = false;
        TargetViolence = ViolenceType.None;
        TargetAfraid = false;
        AimSolution = default;
        Lane = default;
        ChargePriority = true;
        AllowVanillaSidearmCombat = false;
        DebugCommitReady = false;
        DebugHardBlocked = false;
        DebugFriendBlocked = false;
        DebugChargeOpportunity = false;
        _sidearmThrowPass = false;
        _sidearmDrawn = false;
        _staging = null;
        _motionTracker.Reset();
        ResetRecentAimSolution();
        _owner.Combat.ResetForRoom();
        _owner.EnsureWeaponSlots();
    }

    public override void Update()
    {
        if (SkipNextUpdate) { SkipNextUpdate = false; return; }

        _sidearmThrowPass = false;
        AllowVanillaSidearmCombat = false;
        _owner.EnsureWeaponSlots(_sidearmDrawn);
        base.Update();
        if (_owner.room == null) return;

        // Vanilla may change focus while the scavenger is already airborne. Once a charge has
        // launched, keep the attack target fixed for diagnostics/counter-sweep/follow-up purposes;
        // tactical relationship changes no longer rewrite an attack that is physically in flight.
        if (_owner.Combat.State == LanceState.Charge)
        {
            RestoreLockedChargeTarget();
        }
        else
        {
            SelectVanillaCombatTarget();
            if (_owner.Combat.State == LanceState.FollowUpThrow)
                RestoreChargeTargetForFollowUp();
        }

        _motionTracker.Update(Target);

        bool armed = _owner.Lance != null;
        bool sidearm = _owner.SidearmSpear != null;
        bool active = _owner.Consious && _owner.grabbedBy.Count == 0 && !_owner.safariControlled &&
            !_owner.enteringShortCut.HasValue && !_owner.inShortcut && _owner.Submersion < 0.25f;
        float distance = Target == null ? 999f : Vector2.Distance(_owner.mainBodyChunk.pos, Target.mainBodyChunk.pos);
        bool closeDanger = ImmediateCloseDanger(Target, distance);

        AimSolution = Target == null ? default :
            LanceAimSolver.Solve(_owner, _owner.mainBodyChunk.pos, Target, _motionTracker);
        Lane = Target == null ? new ChargeLane(false, _owner.lookPoint, "no target") :
            ChargeLanePlanner.Evaluate(_owner, _owner.mainBodyChunk.pos, Target, AimSolution);

        // Only the visible brace owns the commitment memory. Chasing aim may start the attack but
        // cannot silently satisfy a later brace window.
        if (_owner.Combat.State == LanceState.Brace)
            UpdateRecentAimSolution(Target, AimSolution);

        bool friendBlocked = Lane.Reason == "friend in lane";
        bool hardBlocked = IsHardChargeBlock(Lane, friendBlocked);
        bool commitReady = !hardBlocked && (AimSolution.Ready || RecentAimSolutionValid(Target));
        bool laneClear = Lane.Clear;
        if (_owner.Combat.State == LanceState.Charge)
            laneClear = !ChargeLanePlanner.FriendInPath(_owner, _owner.mainBodyChunk.pos,
                _owner.mainBodyChunk.pos + _owner.Motor.Direction * 65f, Target);

        // On the actual release frame, choose the best solution seen in the brace window, apply the
        // limited 6-degree release correction, and then run one final safety check using that exact
        // direction. This prevents checking one angle and launching with another.
        LanceAimSolution releaseSolution = default;
        bool releaseSolutionReady = false;
        bool releaseFrame = _owner.Combat.State == LanceState.Brace &&
            _owner.Combat.Age + 1 >= LanceCombatState.BraceFrames;
        if (releaseFrame && commitReady && Target != null)
        {
            releaseSolution = SelectCommittedAim(Target);
            if (releaseSolution.Ready)
            {
                releaseSolution = LanceAimSolver.CorrectForRelease(_owner, Target, _motionTracker, releaseSolution);
                ChargeLane releaseLane = ChargeLanePlanner.Evaluate(_owner, _owner.mainBodyChunk.pos, Target, releaseSolution);
                Lane = releaseLane;
                friendBlocked = releaseLane.Reason == "friend in lane";
                hardBlocked = IsHardChargeBlock(releaseLane, friendBlocked);
                laneClear = releaseLane.Clear;
                commitReady = releaseLane.Clear && !hardBlocked;
                releaseSolutionReady = commitReady;
            }
            else
            {
                commitReady = false;
                laneClear = false;
            }
        }

        ChargePriority = HasChargePriorityFor(Target);
        bool chargeOpportunity = active && armed && !closeDanger && ChargePriority && Target != null &&
            TargetViolence == ViolenceType.Lethal && distance >= ChargeLanePlanner.MinimumChargeDistance &&
            laneClear && _owner.Combat.Cooldown == 0;

        DebugFriendBlocked = friendBlocked;
        DebugHardBlocked = hardBlocked;
        DebugCommitReady = commitReady;
        DebugChargeOpportunity = chargeOpportunity;

        if (_sidearmDrawn && chargeOpportunity)
            CancelSidearmDraw();

        LanceState before = _owner.Combat.State;
        _owner.Combat.Tick(new LanceSituation(active, armed, sidearm, Target != null, TargetViolence, TargetAfraid,
            distance, laneClear, _owner.Motor.BackstepComplete, friendBlocked, ChargePriority, commitReady, hardBlocked,
            closeDanger));
        LanceState state = _owner.Combat.State;

        if (before != LanceState.Backstep && state == LanceState.Backstep)
            _owner.Motor.BeginBackstep(Target);

        if (before != LanceState.Brace && state == LanceState.Brace)
        {
            ResetRecentAimSolution();
            UpdateRecentAimSolution(Target, AimSolution);
        }

        if (before != LanceState.Charge && state == LanceState.Charge)
        {
            LanceAimSolution committed = releaseSolutionReady ? releaseSolution : SelectCommittedAim(Target);
            if (!releaseSolutionReady)
                committed = LanceAimSolver.CorrectForRelease(_owner, Target, _motionTracker, committed);
            _owner.Motor.CommitCharge(committed);
            _chargeTarget = Target;
            _chargeTargetAfraid = TargetAfraid;
            ResetRecentAimSolution();
        }
        else if (before == LanceState.Brace && state != LanceState.Brace)
        {
            ResetRecentAimSolution();
        }

        if (!armed)
        {
            if (_sidearmDrawn) CancelSidearmDraw();
            RecoverWeapon();
            LanceScavengerDebugPresentationHub.Publish(_owner, this);
            return;
        }

        if (_owner.Combat.FollowUpReady)
        {
            if (TryFollowUpThrow())
                _owner.Combat.CompleteFollowUp();
            LanceScavengerDebugPresentationHub.Publish(_owner, this);
            return;
        }

        state = _owner.Combat.State;
        bool reserved = state == LanceState.Backstep || state == LanceState.Brace || state == LanceState.Charge ||
            state == LanceState.FollowUpThrow || state == LanceState.CloseDefense || state == LanceState.Recover;

        AllowVanillaSidearmCombat = sidearm && behavior == Behavior.Attack && !TargetAfraid &&
            Target != null && TargetViolence == ViolenceType.Lethal &&
            distance >= ChargeLanePlanner.MinimumChargeDistance && !chargeOpportunity && !reserved;

        if (AllowVanillaSidearmCombat)
            RunSidearmThrowPass();
        else if (_sidearmDrawn && state != LanceState.FollowUpThrow)
            CancelSidearmDraw();

        if (ChargePriority && !TargetAfraid && TargetViolence == ViolenceType.Lethal &&
            (state == LanceState.CreateDistance || state == LanceState.AcquireChargeLane))
        {
            if (--_planAge <= 0 || !_staging.HasValue)
            {
                _planAge = 18;
                _staging = Target != null && ChargeLanePlanner.FindStagingPosition(_owner, Target, out WorldCoordinate spot)
                    ? spot : null;
            }
            if (_staging.HasValue)
            {
                creature.abstractAI.SetDestination(_staging.Value);
                runSpeedGoal = Mathf.Max(runSpeedGoal, 0.8f);
            }
        }
        else if (state == LanceState.Backstep || state == LanceState.Brace || state == LanceState.Recover ||
                 state == LanceState.FollowUpThrow)
        {
            creature.abstractAI.SetDestination(creature.pos);
            _staging = null;
        }
        else
        {
            // In CloseDefense we deliberately keep the destination chosen by vanilla ScavengerAI.
            // That lets an afraid scavenger flee/path while the lance motor independently guards it.
            _staging = null;
        }

        LanceScavengerDebugPresentationHub.Publish(_owner, this);
    }

    private LanceAimSolution SelectCommittedAim(Creature target)
    {
        bool recentReady = RecentAimSolutionValid(target);
        if (recentReady && (!AimSolution.Ready || _recentAimSolution.Quality >= AimSolution.Quality))
            return _recentAimSolution;
        return AimSolution;
    }

    private void UpdateRecentAimSolution(Creature target, LanceAimSolution aim)
    {
        if (target == null)
        {
            ResetRecentAimSolution();
            return;
        }
        if (_recentAimTarget != target)
        {
            ResetRecentAimSolution();
            _recentAimTarget = target;
        }

        if (_recentAimAge < int.MaxValue)
            _recentAimAge++;

        if (!aim.Ready) return;

        if (_recentAimAge > LanceCombatState.CommitWindowFrames ||
            _recentAimTarget != target ||
            aim.Quality >= _recentAimSolution.Quality)
        {
            _recentAimSolution = aim;
            _recentAimTarget = target;
            _recentAimAge = 0;
        }
    }

    private bool RecentAimSolutionValid(Creature target) =>
        target != null && _recentAimTarget == target &&
        _recentAimAge <= LanceCombatState.CommitWindowFrames &&
        _recentAimSolution.Ready;

    private static bool IsHardChargeBlock(ChargeLane lane, bool friendBlocked)
    {
        if (friendBlocked) return true;
        return lane.Reason == "distance" || lane.Reason == "wall / ceiling" || lane.Reason == "lance blocked";
    }

    private bool ImmediateCloseDanger(Creature target, float distance)
    {
        if (target == null || target.dead || !target.Consious || target.room != _owner.room)
            return false;
        if (distance <= LanceMotor.CloseDefenseRange)
            return true;
        if (distance > LanceMotor.CloseDefenseGuardRange)
            return false;

        Vector2 toTarget = target.mainBodyChunk.pos - _owner.mainBodyChunk.pos;
        if (toTarget.sqrMagnitude < 0.001f) return true;
        Vector2 targetVelocity = _motionTracker.Target == target
            ? _motionTracker.SmoothedVelocity(target.mainBodyChunk)
            : target.mainBodyChunk.vel;
        Vector2 relativeVelocity = targetVelocity - _owner.mainBodyChunk.vel;
        float closingSpeed = -Vector2.Dot(relativeVelocity, toTarget.normalized);
        return closingSpeed >= LanceMotor.CloseDefenseApproachSpeed;
    }

    private void ResetRecentAimSolution()
    {
        _recentAimTarget = null;
        _recentAimSolution = default;
        _recentAimAge = int.MaxValue;
    }

    private bool HasChargePriorityFor(Creature target)
    {
        if (target == null || _owner.room?.abstractRoom?.creatures == null) return true;

        int myIndex = int.MaxValue;
        for (int i = 0; i < _owner.room.abstractRoom.creatures.Count; i++)
            if (_owner.room.abstractRoom.creatures[i] == _owner.abstractCreature)
            { myIndex = i; break; }

        int myRank = IntentRank(_owner.Combat.State);
        float myScore = IntentScore(_owner, Lane, target);

        for (int i = 0; i < _owner.room.abstractRoom.creatures.Count; i++)
        {
            if (_owner.room.abstractRoom.creatures[i].realizedCreature is not LanceScavenger other ||
                other == _owner || other.dead || !other.Consious || other.room != _owner.room || other.Lance == null ||
                other.grabbedBy.Count > 0 || other.safariControlled || other.enteringShortCut.HasValue || other.inShortcut ||
                other.Submersion >= 0.25f || other.Combat.Cooldown > 0 ||
                other.Combat.State == LanceState.Recover || other.Combat.State == LanceState.FollowUpThrow ||
                other.Combat.State == LanceState.Disarmed)
                continue;

            LanceScavengerAI brain = other.Brain;
            if (brain == null || brain.Target != target || brain.TargetViolence != ViolenceType.Lethal)
                continue;

            int otherRank = IntentRank(other.Combat.State);
            if (otherRank > myRank) return false;
            if (otherRank < myRank) continue;

            float otherScore = IntentScore(other, brain.Lane, target);
            if (otherScore < myScore - 0.25f) return false;
            if (Mathf.Abs(otherScore - myScore) <= 0.25f && i < myIndex) return false;
        }
        return true;
    }

    private static int IntentRank(LanceState state) => state switch
    {
        LanceState.Charge => 3,
        LanceState.Brace => 2,
        LanceState.Backstep => 1,
        _ => 0
    };

    private static float IntentScore(LanceScavenger scav, ChargeLane lane, Creature target)
    {
        float score = lane.CanHit ? 0f : 1000f;
        if (!lane.PathClear) score += 500f;
        score += (1f - lane.Confidence) * 80f;
        score += lane.ImpactFrame > 0 ? lane.ImpactFrame : 40f;
        float horizontal = Mathf.Abs(target.mainBodyChunk.pos.x - scav.mainBodyChunk.pos.x);
        score += Mathf.Abs(horizontal - 180f) * 0.02f;
        return score;
    }

    private void RunSidearmThrowPass()
    {
        Spear spear = _owner.SidearmSpear;
        if (spear == null)
        {
            CancelSidearmDraw();
            return;
        }

        _sidearmDrawn = true;
        _owner.EnsureWeaponSlots(sidearmPrimary: true);
        if (_owner.grasps[0]?.grabbed != spear)
        {
            CancelSidearmDraw();
            return;
        }

        _sidearmThrowPass = true;
        try { CheckThrow(); }
        finally { _sidearmThrowPass = false; }

        bool stillHoldingSidearm = _owner.SidearmSpear != null && _owner.grasps[0]?.grabbed == _owner.SidearmSpear;
        bool chargingThrow = _owner.animation != null && _owner.animation.id == Scavenger.ScavengerAnimation.ID.ThrowCharge;
        if (!stillHoldingSidearm || !chargingThrow)
        {
            _sidearmDrawn = false;
            _owner.EnsureWeaponSlots();
        }
    }

    private void CancelSidearmDraw()
    {
        if (_owner.animation != null && _owner.animation.id == Scavenger.ScavengerAnimation.ID.ThrowCharge)
            _owner.animation = null;
        _sidearmDrawn = false;
        _owner.EnsureWeaponSlots();
    }

    private void RestoreLockedChargeTarget()
    {
        Target = null;
        TargetViolence = ViolenceType.None;
        TargetAfraid = false;

        Creature target = _chargeTarget;
        if (target == null || target.dead || !target.Consious || target.room != _owner.room)
            return;

        Target = target;
        TargetViolence = ViolenceType.Lethal;
        TargetAfraid = _chargeTargetAfraid;
        Tracker.CreatureRepresentation rep = tracker.RepresentationForCreature(target.abstractCreature, false);
        if (rep != null) focusCreature = rep;
    }

    private void RestoreChargeTargetForFollowUp()
    {
        Creature target = _chargeTarget;
        if (target == null || target.dead || !target.Consious || target.room != _owner.room)
            return;
        Tracker.CreatureRepresentation rep = tracker.RepresentationForCreature(target.abstractCreature, false);
        if (rep?.dynamicRelationship == null || rep.dynamicRelationship.state is not ScavengerTrackState)
            return;
        CreatureTemplate.Relationship relationship = rep.dynamicRelationship.currentRelationship;
        if (relationship.type != CreatureTemplate.Relationship.Type.Attacks &&
            relationship.type != CreatureTemplate.Relationship.Type.Afraid)
            return;
        ViolenceType violence = ViolenceTypeAgainstCreature(rep);
        if (violence != ViolenceType.Lethal) return;
        Target = target;
        TargetViolence = violence;
        TargetAfraid = relationship.type == CreatureTemplate.Relationship.Type.Afraid;
        focusCreature = rep;
    }

    private bool TryFollowUpThrow()
    {
        Spear spear = _owner.SidearmSpear;
        Creature target = _chargeTarget ?? Target;
        if (spear == null || target == null || target.dead || !target.Consious ||
            target.room != _owner.room || TargetViolence != ViolenceType.Lethal)
            return false;

        BodyChunk aimChunk = target.mainBodyChunk;
        if (!_owner.room.VisualContact(_owner.mainBodyChunk.pos, aimChunk.pos)) return false;
        Vector2 aim = _owner.AimPosForChunk(aimChunk, 0.45f, 0f);
        if (ChargeLanePlanner.FriendInPath(_owner, _owner.mainBodyChunk.pos, aim, target)) return false;
        Vector2 direction = Custom.DirVec(_owner.mainBodyChunk.pos, aim);
        if (Mathf.Abs(direction.x) < 0.1f) return false;

        _owner.EnsureWeaponSlots(sidearmPrimary: true);
        spear = _owner.SidearmSpear;
        if (spear == null || _owner.grasps[0]?.grabbed != spear)
        {
            _owner.EnsureWeaponSlots();
            return false;
        }

        _owner.lookPoint = aim;
        _owner.Throw(direction);
        _owner.EnsureWeaponSlots();
        _chargeTarget = null;
        _chargeTargetAfraid = false;
        return true;
    }

    private void SelectVanillaCombatTarget()
    {
        Target = null;
        TargetViolence = ViolenceType.None;
        TargetAfraid = false;

        Tracker.CreatureRepresentation rep = null;
        if (behavior == Behavior.Attack)
            rep = preyTracker.MostAttractivePrey;
        else if (behavior == Behavior.Flee)
            rep = threatTracker.mostThreateningCreature;
        rep ??= focusCreature;

        if (rep?.dynamicRelationship == null || rep.dynamicRelationship.state is not ScavengerTrackState)
            return;

        CreatureTemplate.Relationship relationship = rep.dynamicRelationship.currentRelationship;
        bool attacks = relationship.type == CreatureTemplate.Relationship.Type.Attacks;
        bool afraid = relationship.type == CreatureTemplate.Relationship.Type.Afraid;
        if (!attacks && !afraid) return;

        ViolenceType violence = ViolenceTypeAgainstCreature(rep);
        if (violence == ViolenceType.None) return;

        Creature candidate = rep.representedCreature.realizedCreature;
        if (candidate == null || candidate == _owner || candidate.room != _owner.room || candidate.dead || !candidate.Consious)
            return;

        Target = candidate;
        TargetViolence = violence;
        TargetAfraid = afraid;
    }

    private void RecoverWeapon()
    {
        if (_owner.Combat.State == LanceState.Recover || _owner.grabbedBy.Count > 0 || rainTracker.Utility() > 0.8f) return;
        ScavengerLance nearest = null;
        float distance = 280f;
        foreach (AbstractWorldEntity entity in _owner.room.abstractRoom.entities)
        {
            if (entity is not AbstractScavengerLance data || data.realizedObject is not ScavengerLance lance ||
                lance.grabbedBy.Count != 0 || lance.room != _owner.room) continue;
            float d = Vector2.Distance(lance.firstChunk.pos, _owner.mainBodyChunk.pos);
            if (d >= distance || !_owner.room.VisualContact(_owner.mainBodyChunk.pos, lance.firstChunk.pos) ||
                !pathFinder.CoordinateViable(_owner.room.GetWorldCoordinate(lance.firstChunk.pos))) continue;
            nearest = lance; distance = d;
        }
        if (nearest == null) return;

        behavior = Behavior.Travel;
        creature.abstractAI.SetDestination(_owner.room.GetWorldCoordinate(nearest.firstChunk.pos));
        if (distance < 32f)
        {
            int slot = _owner.grasps.Length > 1 && _owner.grasps[1] == null ? 1 : 0;
            if (_owner.grasps[slot] != null) _owner.ReleaseGrasp(slot);
            _owner.Grab(nearest, slot, 0, Creature.Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
            _owner.EnsureWeaponSlots();
        }
    }
}
