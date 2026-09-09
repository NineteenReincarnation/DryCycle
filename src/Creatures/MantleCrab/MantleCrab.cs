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

    internal Vector2 Anchor(MantleCrabLimb limb) => bodyChunks[limb.AnchorChunk].pos +
        new Vector2(Axis.y, -Axis.x) * (limb.IsPincer ? 14f : 8f) +
        (limb.IsPincer ? Axis * limb.Side * 16f : Vector2.zero);

    public override void Update(bool eu)
    {
        base.Update(eu);
        if (room == null) return;
        SupportingFeet = 0;
        foreach (MantleCrabLimb leg in Legs)
        {
            leg.Update(this, Anchor(leg));
            if (leg.Planted) SupportingFeet++;
        }
        foreach (MantleCrabLimb pincer in Pincers) pincer.Update(this, Anchor(pincer));
        if (!Consious || SupportingFeet == 0) return;

        float totalMass = TotalMass;
        foreach (MantleCrabLimb leg in Legs)
        {
            if (!leg.Planted) continue;
            BodyChunk anchor = bodyChunks[leg.AnchorChunk];
            float extensionError = leg.StandHeight - (Anchor(leg).y - leg.Tip.y);
            float acceleration = MantleCrabRigMath.SupportAcceleration(extensionError,
                anchor.vel.y + gravity * room.gravity, gravity * room.gravity, SupportingFeet);
            // Forces act at the attached shell station; asymmetric contacts can tilt the body.
            anchor.vel.y += acceleration * totalMass / anchor.mass;
            if (SupportingFeet >= 2) anchor.vel.x -= Mathf.Clamp(anchor.vel.x * .08f, -.35f, .35f);
        }
    }

    public override void InitiateGraphicsModule()
    {
        graphicsModule ??= new MantleCrabGraphics(this);
    }
}
