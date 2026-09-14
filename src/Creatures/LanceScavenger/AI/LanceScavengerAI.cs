using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceScavengerAI : ScavengerAI
{
    private readonly LanceScavenger _owner;
    private Creature _recentAttacker;
    private int _hostilityMemory;
    private int _planAge;
    private WorldCoordinate? _staging;
    internal bool SkipNextUpdate;
    internal Creature Target { get; private set; }
    internal bool Hostile { get; private set; }
    internal ChargeLane Lane { get; private set; }
    internal Vector2 Aim => Target != null ? Lane.Aim : _owner.lookPoint;

    internal LanceScavengerAI(AbstractCreature creature, World world) : base(creature, world)
    {
        _owner = (LanceScavenger)creature.realizedCreature;
        // Alertness rises quickly, but social reputation and ordinary scavenger modules
        // still decide hostility. No Elite personality or pursuit parameters are copied.
    }

    internal void AttackedBy(Creature attacker)
    {
        if (attacker == null || attacker is Scavenger) return;
        _recentAttacker = attacker;
        _hostilityMemory = 240;
        tracker.SeeCreature(attacker.abstractCreature);
    }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        Target = null;
        _recentAttacker = null;
        _hostilityMemory = 0;
        _staging = null;
        _owner.Combat.ResetForRoom();
    }

    public override void Update()
    {
        if (SkipNextUpdate) { SkipNextUpdate = false; return; }
        base.Update();
        if (_owner.room == null) return;
        if (_hostilityMemory > 0) _hostilityMemory--;
        SelectTarget(out bool warning);
        bool armed = _owner.Lance != null;
        bool active = _owner.Consious && _owner.grabbedBy.Count == 0 && !_owner.safariControlled &&
            !_owner.enteringShortCut.HasValue && !_owner.inShortcut && _owner.Submersion < 0.25f;
        bool stable = _owner.IsStableForBrace;
        float distance = Target == null ? 999f : Vector2.Distance(_owner.mainBodyChunk.pos, Target.mainBodyChunk.pos);
        Lane = Target == null ? new ChargeLane(false, _owner.lookPoint, "no target") :
            ChargeLanePlanner.Evaluate(_owner, _owner.mainBodyChunk.pos, Target);
        bool laneClear = Lane.Clear;
        if (_owner.Combat.State == LanceState.Charge)
            laneClear = ChargeLanePlanner.CorridorBlock(_owner, _owner.mainBodyChunk.pos,
                _owner.mainBodyChunk.pos + _owner.Motor.Direction * 65f, Target) == null;
        _owner.Combat.Tick(new LanceSituation(active, armed, Target != null, Hostile, warning, distance, laneClear, stable));

        if (!armed) { RecoverWeapon(); return; }
        LanceState state = _owner.Combat.State;
        if (Target != null && (Hostile || warning))
        {
            agitation = Mathf.Min(0.8f, agitation + 0.025f);
            _owner.lookPoint = Target.DangerPos;
        }
        if (state == LanceState.CreateDistance || state == LanceState.AcquireChargeLane)
        {
            if (--_planAge <= 0 || !_staging.HasValue)
            {
                _planAge = 18;
                _staging = Target != null && ChargeLanePlanner.FindStagingPosition(_owner, Target, out WorldCoordinate spot) ? spot : null;
            }
            if (_staging.HasValue) SetDestination(_staging.Value);
            else SetDestination(creature.pos);
            // Keep ordinary terrain locomotion; the custom destination owns tactics.
            behavior = Behavior.Idle;
            runSpeedGoal = 0.8f;
        }
        else if (state != LanceState.Observe && state != LanceState.Disarmed)
        { SetDestination(creature.pos); _staging = null; }
        else if (behavior == Behavior.Attack && !Hostile)
        {
            behavior = Behavior.Idle;
            currentViolenceType = ViolenceType.None;
            SetDestination(creature.pos);
        }
    }

    private void SelectTarget(out bool warning)
    {
        Target = null; Hostile = false; warning = false;
        float best = float.MinValue;
        for (int i = 0; i < tracker.CreaturesCount; i++)
        {
            Tracker.CreatureRepresentation rep = tracker.GetRep(i);
            Creature candidate = rep.representedCreature.realizedCreature;
            if (candidate == null || candidate == _owner || candidate is Scavenger || candidate.room != _owner.room ||
                candidate.dead || !candidate.Consious || candidate.TotalMass < 0.18f || !rep.VisualContact) continue;
            float distance = Vector2.Distance(candidate.mainBodyChunk.pos, _owner.mainBodyChunk.pos);
            if (distance > 480f) continue;
            bool recent = candidate == _recentAttacker && _hostilityMemory > 0;
            ScavengerTrackState tracked = rep.dynamicRelationship?.state as ScavengerTrackState;
            bool lethal = tracked?.taggedViolenceType == ViolenceType.Lethal;
            CreatureTemplate.Relationship relationship = rep.dynamicRelationship?.currentRelationship ?? StaticRelationship(rep.representedCreature);
            bool hostile = recent || (lethal && (relationship.type == CreatureTemplate.Relationship.Type.Attacks ||
                relationship.type == CreatureTemplate.Relationship.Type.Afraid) && relationship.intensity >= 0.45f);
            if (candidate is Player && !recent)
                hostile &= rep.dynamicRelationship != null && LikeOfPlayer(rep.dynamicRelationship) < 0.4f;
            bool warn = !hostile && (tracked?.taggedViolenceType == ViolenceType.Warning || tracked?.taggedViolenceType == ViolenceType.NonLethal);
            if (!hostile && !warn) continue;
            // Let a retreating enemy leave instead of renewing an endless chase.
            float leaving = Vector2.Dot(candidate.mainBodyChunk.vel, (candidate.mainBodyChunk.pos - _owner.mainBodyChunk.pos).normalized);
            if (distance > 340f && leaving > 2f) continue;
            float score = (hostile ? 600f : 0f) + (recent ? 100f : 0f) - distance;
            if (score <= best) continue;
            best = score; Target = candidate; Hostile = hostile; warning = warn;
        }
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
                !pathFinder.CoordinateReachableAndGetbackable(_owner.room.GetWorldCoordinate(lance.firstChunk.pos))) continue;
            nearest = lance; distance = d;
        }
        if (nearest == null)
        {
            if (behavior == Behavior.Attack && (Target == null || !Hostile))
            { behavior = Behavior.Idle; currentViolenceType = ViolenceType.None; SetDestination(creature.pos); }
            return;
        }
        behavior = Behavior.Idle;
        SetDestination(_owner.room.GetWorldCoordinate(nearest.firstChunk.pos));
        if (distance < 32f)
        {
            if (_owner.grasps[0] != null) _owner.ReleaseGrasp(0);
            _owner.Grab(nearest, 0, 0, Creature.Grasp.Shareability.CanOnlyShareWithNonExclusive, 0.5f, false, false);
        }
    }
}
