using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 7;
    internal const float SegmentSpacing = 44f;

    private static readonly float[] SegmentRadii =
    [
        19f,
        23f,
        27f,
        29f,
        27f,
        23f,
        19f
    ];

    public float IdleMotion;
    public float LastIdleMotion;

    public BodyChunk MiddleChunk => bodyChunks[SegmentCount / 2];

    public MossySpider(AbstractCreature abstractCreature, World world) : base(abstractCreature, world)
    {
        bodyChunks = new BodyChunk[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            float edge = Mathf.Abs(i - (SegmentCount - 1) * 0.5f) / ((SegmentCount - 1) * 0.5f);
            float mass = Mathf.Lerp(4.8f, 3.2f, edge);
            bodyChunks[i] = new BodyChunk(this, i, Vector2.zero, SegmentRadii[i], mass);
        }

        // Adjacent links define the long body. Second-neighbour braces keep the
        // creature broad and low instead of letting the chain fold into a worm.
        bodyChunkConnections = new BodyChunkConnection[(SegmentCount - 1) + (SegmentCount - 2)];
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
                0.65f,
                -1f);
        }

        airFriction = 0.995f;
        gravity = 0.9f;
        bounce = 0.05f;
        surfaceFriction = 0.82f;
        collisionLayer = 1;
        waterFriction = 0.96f;
        buoyancy = 0.55f;
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

        // Until locomotion/AI is authored, keep the large body deliberately calm.
        // The braces above do the structural work; this only damps residual jitter.
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].vel.x *= 0.985f;
        }

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            float targetY = (bodyChunks[i - 1].pos.y + bodyChunks[i + 1].pos.y) * 0.5f;
            bodyChunks[i].vel.y += (targetY - bodyChunks[i].pos.y) * 0.012f;
        }
    }
}
