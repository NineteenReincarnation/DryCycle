using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance : Weapon
{
    private const float ActualBladeHitPadding = 1.5f;

    private readonly HashSet<Creature> _hitCreatures = new();
    private readonly Dictionary<Creature, int> _shaftContacts = new();
    private Vector2 _previousTip;
    private Vector2 _previousGrip;
    private bool _havePreviousPose;
    private bool _wasCharging;
    private bool _gripValid;
    private LanceGrip _grip;
    private int _thrustFrames;
    private int _thrustCooldown;
    private int _flightFrames;
    private int _clock;
    private int _wallCooldown;
    private float _bend;
    private float _lastBend;
    private float _bendVelocity;
    private Vector2 _thrustDirection;
    private float _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;

    internal ScavengerLance(AbstractScavengerLance data, World world) : base(data, world)
    {
        bodyChunks = new[] { new BodyChunk(this, 0, Vector2.zero, 3f, 0.34f) };
        bodyChunkConnections = new BodyChunkConnection[0];
        airFriction = 0.997f;
        gravity = 0.85f;
        bounce = 0.12f;
        surfaceFriction = 0.65f;
        waterFriction = 0.93f;
        buoyancy = 0.45f;
        collisionLayer = 2;
        exitThrownModeSpeed = 6f;
    }

    internal float Length => ((AbstractScavengerLance)abstractPhysicalObject).Length;
    internal Creature Holder => grabbedBy.Count > 0 ? grabbedBy[0].grabber : null;
    internal Vector2 Tip => firstChunk.pos + rotation * LanceCombatMath.ForwardLength(Length);
    internal Vector2 BladeRoot => firstChunk.pos + rotation * LanceCombatMath.BladeRootDistance(Length);
    internal Vector2 Tail => firstChunk.pos - rotation * (Length * LanceCombatMath.GripFraction);
    internal bool CanThrust => _thrustCooldown == 0 && Holder != null;
    internal bool HasHitCreature(Creature creature) => creature != null && _hitCreatures.Contains(creature);
    public override bool HeavyWeapon => true;

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        _havePreviousPose = false;
        _gripValid = false;
        _wasCharging = false;
        _thrustFrames = _flightFrames = 0;
        _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;
        _hitCreatures.Clear();
        _shaftContacts.Clear();
        ResetCarryRig();
    }

    internal void RequestThrust(Vector2 direction, float maxDamage = LanceCombatMath.StandardThrustMaxDamage)
    {
        if (!CanThrust || Holder == null || !Holder.Consious) return;
        _thrustDirection = direction.sqrMagnitude > 0.01f ? direction.normalized : rotation;
        _thrustFrames = 12;
        _thrustCooldown = 44;
        _thrustMaxDamage = Mathf.Max(0.12f, maxDamage);
        _hitCreatures.Clear();
        rotation = _thrustDirection;
        setRotation = rotation;
        _previousTip = Tip;
        _previousGrip = firstChunk.pos;
        _havePreviousPose = true;
        ResetCarryRig();
    }

    public override void Thrown(Creature thrower, Vector2 pos, Vector2? traceFrom,
        IntVector2 direction, float force, bool eu)
    {
        base.Thrown(thrower, pos, traceFrom, direction, Mathf.Min(force, 0.38f), eu);
        ChangeMode(Mode.Free);
        rotation = direction.ToVector2().normalized;
        lastRotation = rotation;
        rotationSpeed = 0f;
        _flightFrames = 28;
        _thrustFrames = 0;
        _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;
        _hitCreatures.Clear();
        _havePreviousPose = false;
        ResetCarryRig();
    }

    public override void Update(bool eu)
    {
        _clock++;
        if (_thrustCooldown > 0) _thrustCooldown--;
        if (_wallCooldown > 0) _wallCooldown--;
        if (_flightFrames > 0) _flightFrames--;
        _lastBend = _bend;
        _bendVelocity = (_bendVelocity - _bend * 0.2f) * 0.72f;
        _bend = Mathf.Clamp(_bend + _bendVelocity, -5f, 5f);

        Creature holder = Holder;
        _gripValid = holder is ILanceWielder wielder && wielder.TryGetLanceGrip(this, out _grip);
        bool charging = _gripValid && _grip.Charging && holder.Consious;
        if (charging && !_wasCharging)
        {
            _hitCreatures.Clear();
            if (_havePreviousPose)
                _previousTip = _previousGrip + _grip.Direction.normalized * LanceCombatMath.ForwardLength(Length);
        }
        _wasCharging = charging;
        if (holder != null)
        {
            if (mode != Mode.Carried) ChangeMode(Mode.Carried);

            bool enhancedScavengerCarry = holder is Scavenger && _gripValid && _thrustFrames <= 0;
            if (enhancedScavengerCarry)
            {
                Vector2 carriedRotation = UpdateCarryRig(holder, _grip, rotation);
                rotation = carriedRotation;
                setRotation = carriedRotation;
            }
            else if (holder is not Player || _thrustFrames > 0)
            {
                ResetCarryRig();
                Vector2 desired = _thrustFrames > 0 ? _thrustDirection : _gripValid ? _grip.Direction : GenericDirection(holder);
                float turn = charging || _thrustFrames > 0 ? 180f :
                    (_gripValid && _grip.AimTracking ? 2.25f : _gripValid && _grip.Braced ? 12f : 8f);
                float angle = Mathf.MoveTowardsAngle(Custom.VecToDeg(rotation), Custom.VecToDeg(desired), turn);
                setRotation = Custom.DegToVec(angle);
            }
            else
            {
                ResetCarryRig();
            }
            rotationSpeed = 0f;
        }
        else
        {
            ResetCarryRig();
            if (mode == Mode.Carried) ChangeMode(Mode.Free);
        }

        base.Update(eu);
        if (room == null) { _havePreviousPose = false; return; }
        if (holder != null) SynchronizeGrip(eu);
        if (holder != null && (!holder.Consious || holder.enteringShortCut.HasValue || holder.inShortcut))
        {
            _thrustFrames = 0;
            _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;
            _havePreviousPose = false;
            return;
        }

        Vector2 currentTip = Tip;
        if (!_havePreviousPose || Vector2.Distance(_previousGrip, firstChunk.pos) > 160f)
        {
            _previousTip = currentTip;
            _previousGrip = firstChunk.pos;
            _havePreviousPose = true;
        }
        bool activeTerrainCollision = charging || _thrustFrames > 0 || _flightFrames > 0;
        float wallFraction = 1f;
        bool sweptWall = activeTerrainCollision && TraceSolid(_previousTip, currentTip, out wallFraction);
        bool rodWall = activeTerrainCollision && !PoseFits(firstChunk.pos, rotation);
        float speed = holder != null ? Vector2.Dot(holder.mainBodyChunk.vel, rotation) : Vector2.Dot(firstChunk.vel, rotation);

        if (charging || _thrustFrames > 0 || _flightFrames > 0)
            ResolveBlade(sweptWall ? wallFraction : 1f, charging, speed);
        ResolveShaft();
        if (activeTerrainCollision && (sweptWall || rodWall)) ResolveTerrain(holder, speed, charging);

        if (_thrustFrames > 0)
        {
            _thrustFrames--;
            if (_thrustFrames == 0) _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;
        }
        _previousTip = Tip;
        _previousGrip = firstChunk.pos;
        if (_clock % 240 == 0) _shaftContacts.Clear();
    }

    internal void SynchronizeGrip(bool eu)
    {
        Creature holder = Holder;
        if (holder == null || holder is Player) return;
        _gripValid = holder is ILanceWielder wielder && wielder.TryGetLanceGrip(this, out _grip);
        Vector2 position = _gripValid ? _grip.Position : holder.mainBodyChunk.pos + rotation * 9f;
        if (_thrustFrames > 0)
            position += rotation * (Mathf.Sin((12 - _thrustFrames) / 12f * Mathf.PI) * 17f);
        firstChunk.MoveFromOutsideMyUpdate(eu, position);
        firstChunk.vel = holder.mainBodyChunk.vel;
        setRotation = rotation;
    }

    private Vector2 GenericDirection(Creature holder)
    {
        float sign = Mathf.Abs(holder.mainBodyChunk.vel.x) > 0.3f ? Mathf.Sign(holder.mainBodyChunk.vel.x) : Mathf.Sign(rotation.x);
        return new Vector2(sign == 0f ? 1f : sign, 0.6f).normalized;
    }

    private void ResolveBlade(float maxFraction, bool charging, float holderSpeed)
    {
        Creature holder = Holder;
        BodyChunk nearest = null;
        float firstHit = maxFraction;
        float firstBladeT = 1f;
        float forwardLength = LanceCombatMath.ForwardLength(Length);
        Vector2 previousDirection = (_previousTip - _previousGrip).sqrMagnitude > 0.001f
            ? (_previousTip - _previousGrip).normalized : rotation;

        foreach (AbstractCreature abstractTarget in room.abstractRoom.creatures)
        {
            Creature target = abstractTarget.realizedCreature;
            if (target == null || target == holder || target.room != room || target.dead ||
                _hitCreatures.Contains(target) || (holder == null && target == thrownBy && _flightFrames > 20)) continue;
            foreach (BodyChunk chunk in target.bodyChunks)
            {
                if (!LanceCombatMath.SweepBlade(_previousGrip, previousDirection, firstChunk.pos, rotation,
                        forwardLength, chunk.lastPos, chunk.pos, chunk.rad, ActualBladeHitPadding,
                        out float hit, out float bladeT) || hit > firstHit ||
                    !room.VisualContact(firstChunk.pos, chunk.pos))
                    continue;
                firstHit = hit;
                firstBladeT = bladeT;
                nearest = chunk;
            }
        }
        if (nearest == null) return;

        Creature victim = (Creature)nearest.owner;
        Vector2 oldBladePoint = LanceCombatMath.BladePoint(_previousGrip, previousDirection, forwardLength, firstBladeT);
        Vector2 bladePoint = LanceCombatMath.BladePoint(firstChunk.pos, rotation, forwardLength, firstBladeT);
        Vector2 contactPoint = Vector2.Lerp(oldBladePoint, bladePoint, firstHit);
        Vector2 relative = bladePoint - oldBladePoint - (nearest.pos - nearest.lastPos);
        Vector2 targetDirection = Vector2.Lerp(nearest.lastPos, nearest.pos, firstHit) -
            Vector2.Lerp(_previousGrip, firstChunk.pos, firstHit);
        float alignment = Mathf.Min(Vector2.Dot(rotation, relative.normalized), Vector2.Dot(rotation, targetDirection.normalized));
        bool thrust = _thrustFrames > 0 || _flightFrames > 0;
        bool counterSweep = charging && _gripValid && _grip.CounterSweep;
        float speed = charging ? Mathf.Max(0f, holderSpeed) : Mathf.Max(0f, Vector2.Dot(relative, rotation));
        LanceImpact impact = counterSweep
            ? LanceCombatMath.CounterSweepImpact(holder?.TotalMass ?? TotalMass, victim.TotalMass,
                Mathf.Max(holderSpeed, relative.magnitude))
            : LanceCombatMath.Impact(speed, alignment, holder?.TotalMass ?? TotalMass,
                victim.TotalMass, charging, _gripValid ? _grip.RunUp : 0f, thrust, _thrustMaxDamage);
        if (impact.Damage <= 0f) return;

        float bladeScale = counterSweep ? 1f : LanceCombatMath.BladeDamageMultiplier(firstBladeT);
        float damage = impact.Damage * bladeScale;
        float stun = impact.Stun * bladeScale;
        float impulse = impact.Impulse * (counterSweep ? 1f : Mathf.Lerp(0.88f, 1f, bladeScale));

        _hitCreatures.Add(victim);
        victim.SetKillTag((holder ?? thrownBy)?.abstractCreature);
        victim.Violence(firstChunk, rotation * impulse, nearest, null, Creature.DamageType.Stab, damage, stun);
        nearest.vel += rotation * (impulse / Mathf.Max(0.25f, victim.TotalMass));
        if (charging && holder is ILanceWielder lanceWielder)
        {
            foreach (BodyChunk chunk in holder.bodyChunks)
                chunk.vel -= rotation * Mathf.Max(0f, Vector2.Dot(chunk.vel, rotation)) * (1f - impact.RetainedSpeed);
            lanceWielder.LanceImpact(false, Mathf.Max(speed, counterSweep ? 12f : speed), impact.RetainedSpeed);
        }
        else if (holder == null) { firstChunk.vel *= 0.35f; _flightFrames = 0; }
        _bendVelocity += Mathf.Min(3.5f, impulse * 0.4f);
        room.PlaySound(SoundID.Spear_Stick_In_Creature, contactPoint, 0.75f, 0.85f);
        if (holder != null) room.socialEventRecognizer?.WeaponAttack(this, holder, victim, true);
    }

    private void ResolveShaft()
    {
        Vector2 shaftEnd = BladeRoot - rotation * 1.5f;
        Creature holder = Holder;
        foreach (AbstractCreature abstractTarget in room.abstractRoom.creatures)
        {
            Creature target = abstractTarget.realizedCreature;
            if (target == null || target == holder || target.room != room || target.dead ||
                (_shaftContacts.TryGetValue(target, out int frame) && _clock - frame < 20)) continue;
            foreach (BodyChunk chunk in target.bodyChunks)
            {
                Vector2 close = LanceCombatMath.ClosestPoint(Tail, shaftEnd, chunk.pos);
                Vector2 away = chunk.pos - close;
                if (away.sqrMagnitude > (chunk.rad + 2f) * (chunk.rad + 2f) || !room.VisualContact(firstChunk.pos, chunk.pos)) continue;
                float motion = Mathf.Min(3.5f, (firstChunk.pos - _previousGrip).magnitude * 0.2f + (_thrustFrames > 0 ? 1.5f : 0f));
                if (motion < 0.4f) continue;
                chunk.vel += (away.sqrMagnitude > 0.01f ? away.normalized : Custom.PerpendicularVector(rotation)) *
                    motion / Mathf.Max(0.5f, target.TotalMass);
                if (_thrustFrames > 0 && target.TotalMass < 1.5f) target.Stun(3);
                _shaftContacts[target] = _clock;
                break;
            }
        }
    }

    private bool TraceSolid(Vector2 start, Vector2 end, out float fraction)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(start, end) / 3f));
        for (int i = 0; i <= steps; i++)
            if (room.GetTile(Vector2.Lerp(start, end, (float)i / steps)).Solid)
            { fraction = (float)i / steps; return true; }
        fraction = 1f;
        return false;
    }

    private bool PoseFits(Vector2 grip, Vector2 direction)
    {
        Vector2 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector2.right;
        float forwardLength = LanceCombatMath.ForwardLength(Length);
        if (TraceSolid(grip - dir * (Length * LanceCombatMath.GripFraction), grip + dir * forwardLength, out _))
            return false;

        Vector2 perp = Custom.PerpendicularVector(dir);
        for (int i = 0; i < 6; i++)
        {
            float t = i / 5f;
            Vector2 center = LanceCombatMath.BladePoint(grip, dir, forwardLength, t);
            float halfWidth = LanceCombatMath.BladeHalfWidth(t);
            if (room.GetTile(center + perp * halfWidth).Solid || room.GetTile(center - perp * halfWidth).Solid)
                return false;
        }
        return true;
    }

    private void ResolveTerrain(Creature holder, float speed, bool charging)
    {
        if (_wallCooldown == 0)
        {
            _bendVelocity += Mathf.Min(4f, Mathf.Abs(speed) * 0.18f + 0.3f);
            if (Mathf.Abs(speed) > 4f) room.PlaySound(SoundID.Spear_Bounce_Off_Wall, firstChunk.pos, 0.65f, 0.8f);
            if (charging && holder is ILanceWielder wielder) wielder.LanceImpact(true, speed, 0.12f);
            _wallCooldown = 14;
        }
        _flightFrames = _thrustFrames = 0;
        _thrustMaxDamage = LanceCombatMath.StandardThrustMaxDamage;
        if (charging && holder != null && speed > 0f)
            foreach (BodyChunk chunk in holder.bodyChunks) chunk.vel -= rotation * Vector2.Dot(chunk.vel, rotation) * 0.35f;
        else if (holder == null)
        { firstChunk.vel *= 0.6f; rotationSpeed *= -0.2f; }

        if (holder != null) return;
        float angle = Custom.VecToDeg(rotation);
        for (int step = 1; step <= 12; step++)
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2 candidate = Custom.DegToVec(angle + step * 15f * side);
                if (!PoseFits(firstChunk.pos, candidate)) continue;
                rotation = candidate;
                setRotation = candidate;
                return;
            }
        if (holder == null && PoseFits(_previousGrip, lastRotation))
        { firstChunk.pos = _previousGrip; rotation = lastRotation; }
    }
}
