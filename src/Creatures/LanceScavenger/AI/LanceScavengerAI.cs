using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAI : ScavengerAI
{
    private readonly LanceScavenger _owner;
    private int _planAge;
    private WorldCoordinate? _staging;
    internal bool SkipNextUpdate;
    internal Creature Target { get; private set; }
    internal ViolenceType TargetViolence { get; private set; } = ViolenceType.None;
    internal bool TargetAfraid { get; private set; }
    internal ChargeLane Lane { get; private set; }
    internal Vector2 Aim => Target != null ? Lane.Aim : _owner.lookPoint;

    internal LanceScavengerAI(AbstractCreature creature, World world) : base(creature, world)
    {
        _owner = (LanceScavenger)creature.realizedCreature;
        // Social memory, reputation, fear, warnings and lethal escalation all remain
        // vanilla ScavengerAI responsibilities. This class only chooses lance tactics.
    }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        Target = null;
        TargetViolence = ViolenceType.None;
        TargetAfraid = false;
        _staging = null;
        _owner.Combat.ResetForRoom();
    }

    public override void Update()
    {
        if (SkipNextUpdate) { SkipNextUpdate = false; return; }

        // Vanilla keeps full ownership of pursuit/flee target selection. The lance layer
        // watches that same target and takes over only when a charge opportunity exists.
        base.Update();
        if (_owner.room == null) return;

        SelectVanillaCombatTarget();
        bool armed = _owner.Lance != null;
        bool active = _owner.Consious && _owner.grabbedBy.Count == 0 && !_owner.safariControlled &&
            !_owner.enteringShortCut.HasValue && !_owner.inShortcut && _owner.Submersion < 0.25f;
        float distance = Target == null ? 999f : Vector2.Distance(_owner.mainBodyChunk.pos, Target.mainBodyChunk.pos);
        Lane = Target == null ? new ChargeLane(false, _owner.lookPoint, "no target") :
            ChargeLanePlanner.Evaluate(_owner, _owner.mainBodyChunk.pos, Target);
        bool laneClear = Lane.Clear;
        if (_owner.Combat.State == LanceState.Charge)
            laneClear = !ChargeLanePlanner.FriendInPath(_owner, _owner.mainBodyChunk.pos,
                _owner.mainBodyChunk.pos + _owner.Motor.Direction * 65f, Target);

        _owner.Combat.Tick(new LanceSituation(active, armed, Target != null, TargetViolence, TargetAfraid,
            distance, laneClear));

        if (!armed) { RecoverWeapon(); return; }
        LanceState state = _owner.Combat.State;

        // Lethal + Attacks keeps pursuing with vanilla locomotion until the lance layer
        // can either charge from the current position or find a reachable staging point.
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
            // If no staging point exists, leave the vanilla chase destination intact.
        }
        else if (state == LanceState.Brace || state == LanceState.Recover || state == LanceState.CloseDefense)
        {
            creature.abstractAI.SetDestination(creature.pos);
            _staging = null;
        }
        else
        {
            _staging = null;
        }
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

        float distance = Vector2.Distance(candidate.mainBodyChunk.pos, _owner.mainBodyChunk.pos);
        float tacticRange = Mathf.Max(480f, ChargeLanePlanner.MaximumChargeDistance(_owner) + 40f);
        if (distance > tacticRange) return;

        // Do not require current visual contact here. Vanilla scavengers can remember and
        // pursue a target; hard terrain clearance in ChargeLane decides whether the lance
        // can actually launch when that pursuit creates an opportunity.
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
            if (_owner.grasps[0] != null) _owner.ReleaseGrasp(0);
            _owner.Grab(nearest, 0, 0, Creature.Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
        }
    }
}
