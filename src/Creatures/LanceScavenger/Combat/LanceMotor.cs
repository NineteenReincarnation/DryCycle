using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal sealed class LanceMotor
{
    private readonly LanceScavenger _owner;
    private int _launchedSerial = -1;
    private Vector2 _launchPoint;
    internal Vector2 Direction { get; private set; } = Vector2.right;
    internal float RunUp => _owner.Combat.State == LanceState.Charge ?
        Mathf.Max(0f, Vector2.Dot(_owner.mainBodyChunk.pos - _launchPoint, Direction)) : 0f;
    internal bool OwnsMovement => _owner.Combat.State == LanceState.Brace || _owner.Combat.State == LanceState.Charge ||
        _owner.Combat.State == LanceState.Recover || _owner.Combat.State == LanceState.CloseDefense;

    internal LanceMotor(LanceScavenger owner) { _owner = owner; }
    internal void Reset() { _launchPoint = _owner.mainBodyChunk.pos; }

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
                Direction = new Vector2(Mathf.Sign(_owner.Brain.Aim.x - _launchPoint.x), 0f);
                float speed = Mathf.Lerp(17f, 19.5f, _owner.abstractCreature.personality.energy);
                foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel = new Vector2(Direction.x * speed, 3.2f);
                _owner.room.PlaySound(SoundID.Slugcat_Throw_Spear, _owner.mainBodyChunk.pos, 0.75f, 0.7f);
            }
            // A single launch impulse. No per-frame speed reset or airborne steering.
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
        foreach (BodyChunk chunk in _owner.bodyChunks) chunk.vel.x *= 0.65f;
        _owner.WeightedPush(1, 0, new Vector2(aim.x, -0.2f), state == LanceState.Brace ? 0.5f : 0.2f);
        _owner.bodyChunks[2].vel += new Vector2(aim.x * 0.3f, -0.15f);
        if (state == LanceState.Brace && _owner.Combat.Age == 1)
            _owner.room.PlaySound(SoundID.Scavenger_Knuckle_Hit_Ground, _owner.mainBodyChunk.pos, 0.55f, 0.7f);
        if (state == LanceState.CloseDefense) _owner.Lance?.RequestThrust(aim);
    }
}
