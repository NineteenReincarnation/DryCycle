using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAI : ScavengerAI
{
    private readonly LanceScavenger _owner;
    private int _planAge;
    private WorldCoordinate? _staging;
    private bool _sidearmThrowPass;
    internal bool SkipNextUpdate;
    internal Creature Target { get; private set; }
    internal ViolenceType TargetViolence { get; private set; } = ViolenceType.None;
    internal bool TargetAfraid { get; private set; }
    internal ChargeLane Lane { get; private set; }
    internal bool SidearmThrowPass => _sidearmThrowPass;
    internal bool AllowVanillaSidearmCombat { get; private set; }
    internal Vector2 Aim => Target != null ? Lane.Aim : _owner.lookPoint;

    internal LanceScavengerAI(AbstractCreature creature, World world) : base(creature, world)
    {
        _owner = (LanceScavenger)creature.realizedCreature;
    }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        Target = null;
        TargetViolence = ViolenceType.None;
        TargetAfraid = false;
        AllowVanillaSidearmCombat = false;
        _sidearmThrowPass = false;
        _staging = null;
        _owner.Combat.ResetForRoom();
    }

    public override void Update()
    {
        if (SkipNextUpdate) { SkipNextUpdate = false; return; }

        // Let vanilla own social memory, target selection, Attack/Flee choice and pursuit.
        // CheckThrow is intercepted while base.Update runs; after the lance opportunity is
        // evaluated below, we explicitly give vanilla one sidearm-throw pass if appropriate.
        _sidearmThrowPass = false;
        AllowVanillaSidearmCombat = false;
        base.Update();
        if (_owner.room == null) return;

        _owner.EnsureWeaponSlots();
        SelectVanillaCombatTarget();
        bool armed = _owner.Lance != null;
        bool sidearm = _owner.SidearmSpear != null;
        bool active = _owner.Consious && _owner.grabbedBy.Count == 0 && !_owner.safariControlled &&
            !_owner.enteringShortCut.HasValue && !_owner.inShortcut && _owner.Submersion < 0.25f;
        float distance = Target == null ? 999f : Vector2.Distance(_owner.mainBodyChunk.pos, Target.mainBodyChunk.pos);
        Lane = Target == null ? new ChargeLane(false, _owner.lookPoint, "no target") :
            ChargeLanePlanner.Evaluate(_owner, _owner.mainBodyChunk.pos, Target);
        bool laneClear = Lane.Clear;
        if (_owner.Combat.State == LanceState.Charge)
            laneClear = !ChargeLanePlanner.FriendInPath(_owner, _owner.mainBodyChunk.pos,
                _owner.mainBodyChunk.pos + _owner.Motor.Direction * 65f, Target);

        bool chargeOpportunity = active && armed && Target != null && TargetViolence == ViolenceType.Lethal &&
            distance >= ChargeLanePlanner.MinimumChargeDistance && laneClear && _owner.Combat.Cooldown == 0;

        _owner.Combat.Tick(new LanceSituation(active, armed, sidearm, Target != null, TargetViolence, TargetAfraid,
            distance, laneClear));

        if (!armed) { RecoverWeapon(); return; }

        if (_owner.Combat.FollowUpReady)
        {
            if (TryFollowUpThrow())
                _owner.Combat.CompleteFollowUp();
            return;
        }

        LanceState state = _owner.Combat.State;
        bool reserved = state == LanceState.Brace || state == LanceState.Charge ||
            state == LanceState.FollowUpThrow || state == LanceState.CloseDefense || state == LanceState.Recover;

        // Lethal + Attacks: the lance wins whenever a valid charge window exists.
        // Otherwise grasp-0's ordinary spear is handed back to the complete vanilla
        // throwing logic. Under 3 tiles, CloseDefense keeps priority over the sidearm.
        AllowVanillaSidearmCombat = sidearm && behavior == Behavior.Attack && !TargetAfraid &&
            Target != null && TargetViolence == ViolenceType.Lethal &&
            distance >= ChargeLanePlanner.MinimumChargeDistance && !chargeOpportunity && !reserved &&
            _owner.grasps[0]?.grabbed == _owner.SidearmSpear;

        if (AllowVanillaSidearmCombat)
        {
            _sidearmThrowPass = true;
            try { CheckThrow(); }
            finally { _sidearmThrowPass = false; }
        }

        // Lethal + Attacks keeps pursuing with vanilla locomotion. A reachable staging
        // point may refine that chase, but failing to find one never freezes pursuit.
        if (!TargetAfraid && TargetViolence == ViolenceType.Lethal &&
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
        else if (state == LanceState.Brace || state == LanceState.Recover || state == LanceState.CloseDefense ||
                 state == LanceState.FollowUpThrow)
        {
            creature.abstractAI.SetDestination(creature.pos);
            _staging = null;
        }
        else
        {
            _staging = null;
        }
    }

    private bool TryFollowUpThrow()
    {
        _owner.EnsureWeaponSlots();
        Spear spear = _owner.SidearmSpear;
        Creature target = Target;
        if (spear == null || _owner.grasps[0]?.grabbed != spear || target == null || target.dead || !target.Consious ||
            target.room != _owner.room || TargetViolence != ViolenceType.Lethal)
            return false;

        BodyChunk aimChunk = target.mainBodyChunk;
        if (!_owner.room.VisualContact(_owner.mainBodyChunk.pos, aimChunk.pos)) return false;
        Vector2 aim = _owner.AimPosForChunk(aimChunk, 0.45f, 0f);
        if (ChargeLanePlanner.FriendInPath(_owner, _owner.mainBodyChunk.pos, aim, target)) return false;
        Vector2 direction = Custom.DirVec(_owner.mainBodyChunk.pos, aim);
        if (Mathf.Abs(direction.x) < 0.1f) return false;

        _owner.lookPoint = aim;
        _owner.Throw(direction);
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

        // No custom range or visibility gate here. Vanilla may pursue remembered targets
        // across the whole room; ChargeLane and vanilla CheckThrow independently decide
        // when their respective attacks are actually legal.
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
