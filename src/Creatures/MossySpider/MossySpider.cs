using RWCustom;
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

    internal MossySpiderAI AI => abstractCreature?.abstractAI?.RealAI as MossySpiderAI;

    internal Vector2 MoveDirection { get; private set; }

    internal float SwimFactor { get; private set; }

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

        MoveDirection = Vector2.zero;
        SwimFactor = 0f;
        ResetSupportLegs();
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        MoveDirection = Vector2.zero;
        SwimFactor = 0f;
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

        // MossySpider does not seek shelter from lethal rain. Resetting the rain-death
        // accumulator each creature tick prevents death-rain lethality from building up,
        // while the physical rain forces and stun effects remain intact.
        rainDeath = 0f;

        LastIdleMotion = IdleMotion;
        IdleMotion += 0.013f;
        if (IdleMotion > 10000f)
        {
            IdleMotion -= 10000f;
            LastIdleMotion -= 10000f;
        }

        AI?.Update();

        if (room != null)
        {
            Vector2 bodyAxis = PhysicalBodyAxis();

            for (int i = 0; i < SupportLegs.Length; i++)
            {
                SupportLegs[i].UpdateContact(this, bodyAxis);
            }

            UpdateSwimFactor();

            if (Consious && !dead)
            {
                // Shallow water remains ordinary leg-supported walking. Once most feet
                // cannot reach the bottom, support fades and the dorsal float controller
                // takes over continuously rather than through a hard state switch.
                float groundSupportFactor = 1f - Mathf.SmoothStep(0.55f, 0.92f, SwimFactor);
                if (groundSupportFactor > 0.001f)
                {
                    for (int i = 0; i < SupportLegs.Length; i++)
                    {
                        if (SupportLegs[i].Planted)
                        {
                            SupportLegs[i].ApplySupport(this, groundSupportFactor);
                        }
                    }
                }

                UpdateLocomotion();
            }
            else
            {
                MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.15f);
            }
        }

        StabilizeTorso();
        rainDeath = 0f;
    }

    internal void AccessSideSpace(WorldCoordinate start, WorldCoordinate destination)
    {
        if (room?.game?.shortcuts == null)
        {
            return;
        }

        room.game.shortcuts.CreatureTakeFlight(
            this,
            AbstractRoomNode.Type.SideExit,
            start,
            destination);
    }

    private void UpdateLocomotion()
    {
        if (room == null || AI?.Pather == null)
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.12f);
            return;
        }

        MovementConnection move = AI.Pather.FollowPath(
            room.GetWorldCoordinate(MiddleChunk.pos),
            actuallyFollowingThisPath: true);

        if (room == null)
        {
            return;
        }

        if (move == default)
        {
            // A large body can momentarily put its center outside an accessible tile;
            // try two inner torso chunks before declaring that the path is unavailable.
            move = AI.Pather.FollowPath(
                room.GetWorldCoordinate(bodyChunks[1].pos),
                actuallyFollowingThisPath: true);

            if (move == default)
            {
                move = AI.Pather.FollowPath(
                    room.GetWorldCoordinate(bodyChunks[SegmentCount - 2].pos),
                    actuallyFollowingThisPath: true);
            }
        }

        if (room == null || move == default || !move.destinationCoord.TileDefined)
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.10f);
            return;
        }

        Vector2 target = room.MiddleOfTile(move.destinationCoord);
        Vector2 desired = target - MiddleChunk.pos;
        if (desired.sqrMagnitude > 0.001f)
        {
            desired.Normalize();
        }

        if (SwimFactor < 0.55f)
        {
            // Ground locomotion is driven mostly horizontally. Legs and terrain contact
            // determine body height; the AI is not allowed to turn Air pathing into flight.
            desired.y *= 0.18f;
            if (desired.sqrMagnitude > 0.001f)
            {
                desired.Normalize();
            }
        }

        MoveDirection = Vector2.Lerp(MoveDirection, desired, 0.09f);

        float drive = Mathf.Lerp(0.030f, 0.047f, SwimFactor);
        float maxHorizontalSpeed = Mathf.Lerp(1.20f, 1.55f, SwimFactor);
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            chunk.vel.x += MoveDirection.x * drive;
            chunk.vel.x = Mathf.Clamp(chunk.vel.x, -maxHorizontalSpeed, maxHorizontalSpeed);

            if (SwimFactor > 0.05f)
            {
                chunk.vel.y += MoveDirection.y * drive * 0.52f * SwimFactor;
            }
        }

        ApplyDorsalFloat();
    }

    private void UpdateSwimFactor()
    {
        if (room == null || !room.water)
        {
            SwimFactor = Mathf.MoveTowards(SwimFactor, 0f, 0.04f);
            return;
        }

        int planted = 0;
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Planted)
            {
                planted++;
            }
        }

        float averageSubmersion = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            averageSubmersion += bodyChunks[i].submersion;
        }
        averageSubmersion /= bodyChunks.Length;

        // Deep water is defined by loss of bottom support, not by a hard water-depth
        // number. A partially submerged body with several planted feet is still walking.
        float waterFactor = Mathf.InverseLerp(0.12f, 0.55f, averageSubmersion);
        float supportLoss = Mathf.InverseLerp(5f, 1f, planted);
        float target = Mathf.Clamp01(waterFactor * supportLoss);
        SwimFactor = Mathf.MoveTowards(SwimFactor, target, 0.025f);
    }

    private void ApplyDorsalFloat()
    {
        if (room == null || !room.water || SwimFactor <= 0.001f)
        {
            return;
        }

        float surface = room.FloatWaterLevel(MiddleChunk.pos);
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            float dorsalTop = chunk.pos.y + chunk.rad * 0.55f + 22f;
            float error = surface + 2f - dorsalTop;
            float correction = Mathf.Clamp(
                error * 0.012f - chunk.vel.y * 0.070f,
                -0.34f,
                0.52f);
            chunk.vel.y += correction * SwimFactor;
        }
    }

    private void StabilizeTorso()
    {
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].vel.x *= SwimFactor > 0.5f ? 0.996f : 0.991f;
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

        axis.y *= 0.22f;
        if (axis.sqrMagnitude < 0.001f)
        {
            return Vector2.right;
        }

        return axis.normalized;
    }
}
