using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceMotor
{
    private const float ChargeLaunchY = 7.3f;
    private const float MinimumBackstepDistance = 10f; // 0.5 tile
    private const float MaximumBackstepDistance = 30f; // 1.5 tiles
    private const int MaximumBackstepFrames = 18;
    private readonly LanceScavenger _owner;
    private int _launchedSerial = -1;
    private Vector2 _launchPoint;
    private bool _backstepActive;
    private bool _backstepComplete;
    private float _backstepStartX;
    private float _backstepDistance;
    private float _backstepDirection;
    private int _backstepFrames;
    internal Vector2 Direction { get; private set; } = Vector2.right;
    internal float RunUp => _owner.Combat.State == LanceState.Charge ?
        Mathf.Max(0f, Vector2.Dot(_owner.mainBodyChunk.pos - _launchPoint, Direction)) : 0f;
    internal bool BackstepComplete => _backstepActive && _backstepComplete;
    internal float BackstepDistance => _backstepDistance;
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
        // Nervous/cautious individuals take the longer retreat; brave/aggressive ones only
        // make a short spacing step before lowering the lance.
        float caution = Mathf.Clamp01(personality.nervous * 0.55f +
            (1f - personality.bravery) * 0.25f + (1f - personality.aggression) * 0.20f);
        float desired = Mathf.Lerp(MinimumBackstepDistance, MaximumBackstepDistance, caution);

        // Do not deliberately walk beyond this individual's maximum charge range.
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
                Direction = new Vector2(Mathf.Sign(_owner.Brain.Aim.x - _launchPoint.x), 0f);
                float speed = Mathf.Lerp(17f, 19.5f, _owner.abstractCreature.personality.energy);
                foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel = new Vector2(Direction.x * speed, ChargeLaunchY);
                _owner.room.PlaySound(SoundID.Slugcat_Throw_Spear, _owner.mainBodyChunk.pos, 0.75f, 0.7f);
            }
            _owner.WeightedPush(1, 0, Direction, 0.32f);
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
        if (state == LanceState.Brace && _owner.Combat.Age == 1)
            _owner.room.PlaySound(SoundID.Scavenger_Knuckle_Hit_Ground, _owner.mainBodyChunk.pos, 0.55f, 0.7f);
        if (state == LanceState.CloseDefense)
            _owner.Lance?.RequestThrust(aim, LanceCombatMath.LanceScavengerCloseThrustMaxDamage);
    }

    private bool BackstepBlocked()
    {
        Vector2 probe = _owner.mainBodyChunk.pos + Vector2.right * (_backstepDirection * 7f);
        return _owner.room.GetTile(probe).Solid ||
            _owner.room.GetTile(probe + Vector2.up * 12f).Solid ||
            _owner.room.GetTile(probe - Vector2.up * 8f).Solid;
    }
}
