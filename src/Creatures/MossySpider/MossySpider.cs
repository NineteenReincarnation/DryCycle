using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 7;
    internal const float SegmentSpacing = 44f;

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

        // AI/locomotion is not authored yet. Keep the test body calm and broadly
        // horizontal so collisions do not immediately fold the temporary core into
        // the vertical worm shape seen in the first visual prototype. The future
        // leg-support controller will replace this stabilizer.
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].vel.x *= 0.985f;
        }

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            Vector2 target = (bodyChunks[i - 1].pos + bodyChunks[i + 1].pos) * 0.5f;
            bodyChunks[i].vel += (target - bodyChunks[i].pos) * 0.018f;
        }

        float endHeightDifference = Mathf.Clamp(
            bodyChunks[SegmentCount - 1].pos.y - bodyChunks[0].pos.y,
            -100f,
            100f);

        float half = (SegmentCount - 1) * 0.5f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            float normalized = (i - half) / half;
            bodyChunks[i].vel.y -= endHeightDifference * normalized * 0.0045f;
        }
    }
}
