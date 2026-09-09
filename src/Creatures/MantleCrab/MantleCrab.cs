using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>Passive shell and standing rig. All real collision masses belong to the shell.</summary>
public sealed class MantleCrab : Creature
{
    internal static readonly Vector2[] ShellRest =
    [new(-84, 0), new(-44, 5), new(0, 8), new(44, 5), new(84, 0)];
    private static readonly float[] Radii = [17, 26, 30, 26, 17];
    internal readonly MantleCrabLimb[] Legs = new MantleCrabLimb[4];
    internal readonly MantleCrabLimb[] Pincers = new MantleCrabLimb[2];
    private readonly float[] supportAccelerations = new float[4];
    private readonly Vector2[] frameCorrections = new Vector2[5];
    internal float ShellScale = 1f;
    internal readonly Rendering.MantleCrabVisualPhenotype Phenotype;
    internal int SupportingFeet { get; private set; }
    internal Vector2 Axis => (bodyChunks[4].pos - bodyChunks[0].pos).normalized;

    public MantleCrab(AbstractCreature creature, World world) : base(creature, world)
    {
        Phenotype = new Rendering.MantleCrabVisualPhenotype(new Rendering.MantleCrabVisualGenome(creature));
        ShellScale = Phenotype.ShellWidth;
        bodyChunks = new BodyChunk[5];
        for (int i = 0; i < 5; i++)
            bodyChunks[i] = new BodyChunk(this, i, ShellRest[i] * ShellScale, Radii[i] * ShellScale, i == 2 ? 3f : 1.8f);
        // Complete distance graph, as in MirosBird's torso: adjacent AND cross-node braces.
        bodyChunkConnections = new BodyChunkConnection[10];
        int connection = 0;
        for (int i = 0; i < 5; i++)
        for (int j = i + 1; j < 5; j++)
            bodyChunkConnections[connection++] = new BodyChunkConnection(bodyChunks[i], bodyChunks[j],
                Vector2.Distance(ShellRest[i], ShellRest[j]) * ShellScale, BodyChunkConnection.Type.Normal, .85f, -1f);
        for (int i = 0; i < 4; i++) Legs[i] = new MantleCrabLimb(i, false);
        for (int i = 0; i < 2; i++) Pincers[i] = new MantleCrabLimb(i, true);
        airFriction = .995f;
        gravity = .9f;
        bounce = .05f;
        surfaceFriction = .45f;
        waterFriction = .96f;
        buoyancy = .8f;
        collisionLayer = 1;
        GoThroughFloors = false;
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);
        Vector2 center = placeRoom.MiddleOfTile(abstractCreature.pos.Tile);
        for (int i = 0; i < 5; i++) bodyChunks[i].HardSetPosition(center + ShellRest[i] * ShellScale);
        ResetLimbs();
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        ResetLimbs();
    }

    private void ResetLimbs()
    {
        foreach (MantleCrabLimb leg in Legs) leg.Reset(Anchor(leg));
        foreach (MantleCrabLimb pincer in Pincers) pincer.Reset(Anchor(pincer));
        graphicsModule?.Reset();
    }

    internal Vector2 Anchor(MantleCrabLimb limb) => bodyChunks[2].pos +
        Axis * limb.Rest[0].x + new Vector2(-Axis.y, Axis.x) * limb.Rest[0].y;

    public override void Update(bool eu)
    {
        base.Update(eu);
        if (room == null) return;
        StabilizeShell();
        SupportingFeet = 0;
        foreach (MantleCrabLimb leg in Legs)
        {
            leg.Update(this, Anchor(leg));
            if (leg.Planted) SupportingFeet++;
        }
        foreach (MantleCrabLimb pincer in Pincers) pincer.Update(this, Anchor(pincer));
        if (!Consious || SupportingFeet == 0) return;
        ApplySupport(gravity * room.gravity);
    }

    internal void ApplySupport(float effectiveGravity)
    {
        float totalMass = TotalMass;
        Vector2 center = Vector2.zero, velocity = Vector2.zero;
        foreach (BodyChunk chunk in bodyChunks)
        { center += chunk.pos * chunk.mass; velocity += chunk.vel * chunk.mass; }
        center /= totalMass; velocity /= totalMass;
        float angularMomentum = 0f, inertia = 0f;
        foreach (BodyChunk chunk in bodyChunks)
        {
            Vector2 offset = chunk.pos - center, relativeVelocity = chunk.vel - velocity;
            angularMomentum += chunk.mass * (offset.x * relativeVelocity.y - offset.y * relativeVelocity.x);
            inertia += chunk.mass * offset.sqrMagnitude;
        }
        float angularVelocity = angularMomentum / Mathf.Max(1f, inertia);
        for (int i = 0; i < Legs.Length; i++)
        {
            MantleCrabLimb leg = Legs[i];
            supportAccelerations[i] = 0f;
            if (!leg.Planted) continue;
            BodyChunk anchor = bodyChunks[leg.AnchorChunk];
            float extensionError = leg.StandHeight - (Anchor(leg).y - leg.Tip.y);
            // Distance constraints exchange local velocities even at rest. Damping the fitted
            // rigid-frame velocity avoids interpreting those impulses as upward body motion.
            float stationVelocity = velocity.y + angularVelocity * (anchor.pos.x - center.x);
            supportAccelerations[i] = MantleCrabRigMath.SupportAcceleration(extensionError,
                stationVelocity, effectiveGravity, SupportingFeet);
        }
        // Evaluate every leg against the same velocity snapshot. Paired legs share a chunk;
        // applying the first force before evaluating the second made support order-dependent.
        for (int i = 0; i < Legs.Length; i++)
        {
            MantleCrabLimb leg = Legs[i];
            if (!leg.Planted) continue;
            BodyChunk anchor = bodyChunks[leg.AnchorChunk];
            // Forces act at the attached shell station; asymmetric contacts can tilt the body.
            anchor.vel.y += supportAccelerations[i] * totalMass / anchor.mass;
            if (SupportingFeet >= 2) anchor.vel.x -= Mathf.Clamp(anchor.vel.x * .08f, -.35f, .35f);
        }
    }

    internal void StabilizeShell()
    {
        // A nearly collinear distance graph can bow far with very little length error.
        // Fit the species frame to the current axis, then restore only its deformation.
        // This is internal elasticity, not a world-space orientation or height lock.
        Vector2 center = Vector2.zero, velocity = Vector2.zero, restCenter = Vector2.zero;
        float mass = TotalMass;
        for (int i = 0; i < 5; i++)
        {
            center += bodyChunks[i].pos * bodyChunks[i].mass;
            velocity += bodyChunks[i].vel * bodyChunks[i].mass;
            restCenter += ShellRest[i] * ShellScale * bodyChunks[i].mass;
        }
        center /= mass; velocity /= mass; restCenter /= mass;
        Vector2 axis = Axis, up = new(-axis.y, axis.x);
        Vector2 net = Vector2.zero;
        float limit = 1f;
        for (int i = 0; i < 5; i++)
        {
            Vector2 local = ShellRest[i] * ShellScale - restCenter;
            Vector2 error = center + axis * local.x + up * local.y - bodyChunks[i].pos;
            // Only the component normal to the shell corrects bowing; the main links own span.
            frameCorrections[i] = up * (.32f * Vector2.Dot(error, up) -
                .16f * Vector2.Dot(bodyChunks[i].vel - velocity, up));
            net += frameCorrections[i] * bodyChunks[i].mass;
        }
        net /= mass;
        for (int i = 0; i < 5; i++)
        {
            frameCorrections[i] -= net;
            limit = Mathf.Min(limit, 4f / Mathf.Max(4f, frameCorrections[i].magnitude));
        }
        for (int i = 0; i < 5; i++) bodyChunks[i].vel += frameCorrections[i] * limit;
    }

    public override void InitiateGraphicsModule()
    {
        graphicsModule ??= new MantleCrabGraphics(this);
    }
}
