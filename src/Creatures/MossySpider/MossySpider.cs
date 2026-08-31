using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 7;
    internal const float SegmentSpacing = 44f;
    internal const int LegCount = 10;

    private static readonly float[] SegmentRadii =
    [
        23f,
        28f,
        32f,
        34f,
        32f,
        28f,
        23f
    ];

    public float IdleMotion;
    public float LastIdleMotion;

    internal MossySpiderLeg[] SupportLegs { get; }

    public BodyChunk MiddleChunk => bodyChunks[SegmentCount / 2];

    public MossySpider(AbstractCreature abstractCreature, World world) : base(abstractCreature, world)
    {
        bodyChunks = new BodyChunk[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            float edge = Mathf.Abs(i - (SegmentCount - 1) * 0.5f) / ((SegmentCount - 1) * 0.5f);
            float mass = Mathf.Lerp(5.4f, 3.7f, edge);
            bodyChunks[i] = new BodyChunk(this, i, Vector2.zero, SegmentRadii[i], mass);
        }

        // The physical core is a braced platform rather than a free worm chain.
        // Adjacent links carry collision forces; second- and third-neighbour chords
        // resist local folding until the real multi-leg locomotion system is added.
        int adjacentCount = SegmentCount - 1;
        int secondNeighbourCount = SegmentCount - 2;
        int thirdNeighbourCount = SegmentCount - 3;
        bodyChunkConnections = new BodyChunkConnection[
            adjacentCount + secondNeighbourCount + thirdNeighbourCount];

        int connection = 0;
        for (int i = 0; i < SegmentCount - 1; i++)
        {
            bodyChunkConnections[connection++] = new BodyChunkConnection(
                bodyChunks[i],
                bodyChunks[i + 1],
                SegmentSpacing,
                BodyChunkConnection.Type.Normal,
                1f,
                -1f);
        }

        for (int i = 0; i < SegmentCount - 2; i++)
        {
            bodyChunkConnections[connection++] = new BodyChunkConnection(
                bodyChunks[i],
                bodyChunks[i + 2],
                SegmentSpacing * 2f,
                BodyChunkConnection.Type.Normal,
                0.90f,
                -1f);
        }

        for (int i = 0; i < SegmentCount - 3; i++)
        {
            bodyChunkConnections[connection++] = new BodyChunkConnection(
                bodyChunks[i],
                bodyChunks[i + 3],
                SegmentSpacing * 3f,
                BodyChunkConnection.Type.Normal,
                0.58f,
                -1f);
        }

        // Ten actual support legs, arranged as two legs at five torso stations. Each
        // leg is anchored to a real BodyChunk base, owns a terrain foot contact, and
        // pushes support force back into that base chunk. The visual leg segments are
        // not extra BodyChunks; that would make a ten-legged creature unstable.
        SupportLegs = new MossySpiderLeg[LegCount];
        for (int station = 0; station < 5; station++)
        {
            float stationT = station / 4f;
            float bodyU = Mathf.Lerp(0.12f, 0.88f, stationT);
            int baseChunk = Mathf.Clamp(
                Mathf.RoundToInt(bodyU * (SegmentCount - 1)),
                1,
                SegmentCount - 2);

            for (int layer = 0; layer < 2; layer++)
            {
                int leg = station * 2 + layer;
                float restLength = 82f + Mathf.Sin(stationT * Mathf.PI) * 7f + (layer == 0 ? 2f : -2f);
                float maxLength = restLength + 31f;
                float reach = Mathf.Lerp(-38f, 38f, stationT) + (layer == 0 ? 14f : -10f);

                SupportLegs[leg] = new MossySpiderLeg(
                    leg,
                    station,
                    bodyU,
                    baseChunk,
                    restLength,
                    maxLength,
                    reach);
            }
        }

        airFriction = 0.995f;
        gravity = 0.90f;
        bounce = 0.03f;
        surfaceFriction = 0.88f;
        collisionLayer = 1;
        waterFriction = 0.96f;
        buoyancy = 0.62f;
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);

        Vector2 center = placeRoom.MiddleOfTile(abstractCreature.pos.Tile);
        float half = (SegmentCount - 1) * 0.5f;
        for (int i = 0; i < SegmentCount; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            Vector2 pos = center + new Vector2((i - half) * SegmentSpacing, 0f);
            chunk.pos = pos;
            chunk.lastPos = pos;
            chunk.lastLastPos = pos;
            chunk.vel = Vector2.zero;
        }

        ResetSupportLegs();
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        ResetSupportLegs();
    }

    public override void InitiateGraphicsModule()
    {
        graphicsModule ??= new MossySpiderGraphics(this);
    }

    public override Color ShortCutColor() => new(0.48f, 0.52f, 0.22f);

    public override void Update(bool eu)
    {
        base.Update(eu);

        LastIdleMotion = IdleMotion;
        IdleMotion += 0.013f;
        if (IdleMotion > 10000f)
        {
            IdleMotion -= 10000f;
            LastIdleMotion -= 10000f;
        }

        if (room != null)
        {
            Vector2 bodyAxis = PhysicalBodyAxis();

            // Contact search and support are creature physics, not graphics. This is
            // the missing layer that previously made the torso simply lie on the floor
            // while the rendered legs appeared underneath it.
            for (int i = 0; i < SupportLegs.Length; i++)
            {
                SupportLegs[i].UpdateContact(this, bodyAxis);
            }

            if (Consious && !dead)
            {
                for (int i = 0; i < SupportLegs.Length; i++)
                {
                    SupportLegs[i].ApplySupport(this);
                }
            }
        }

        // AI/locomotion is not authored yet. Keep the test body calm and broadly
        // horizontal. Leg support now owns the vertical standing height; this block
        // only prevents the braced torso from turning into a vertical worm.
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].vel.x *= 0.988f;
        }

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            Vector2 target = (bodyChunks[i - 1].pos + bodyChunks[i + 1].pos) * 0.5f;
            bodyChunks[i].vel += (target - bodyChunks[i].pos) * 0.016f;
        }

        float endHeightDifference = Mathf.Clamp(
            bodyChunks[SegmentCount - 1].pos.y - bodyChunks[0].pos.y,
            -100f,
            100f);

        float bodyHalf = (SegmentCount - 1) * 0.5f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            float normalized = (i - bodyHalf) / bodyHalf;
            bodyChunks[i].vel.y -= endHeightDifference * normalized * 0.0034f;
        }
    }

    private void ResetSupportLegs()
    {
        if (SupportLegs == null || bodyChunks == null || bodyChunks.Length == 0)
        {
            return;
        }

        Vector2 axis = PhysicalBodyAxis();
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            SupportLegs[i].Reset(this, axis);
        }
    }

    private Vector2 PhysicalBodyAxis()
    {
        if (bodyChunks == null || bodyChunks.Length < 2)
        {
            return Vector2.right;
        }

        Vector2 axis = bodyChunks[SegmentCount - 1].pos - bodyChunks[0].pos;
        if (axis.sqrMagnitude < 0.001f)
        {
            return Vector2.right;
        }

        // Standing/foothold search is primarily horizontal. Preserve a little slope
        // information without letting a temporarily vertical torso make the feet search
        // sideways into walls.
        axis.y *= 0.22f;
        if (axis.sqrMagnitude < 0.001f)
        {
            return Vector2.right;
        }

        return axis.normalized;
    }
}
