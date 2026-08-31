using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 7;
    internal const float SegmentSpacing = 44f;
    internal const int LegCount = 10;

    private const int MaximumSimultaneousSteps = 3;

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

    internal float GaitCycle { get; private set; }

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

        // Rain World bodies are usually chains whose pose is produced by muscles,
        // terrain and appendages. The previous MossySpider triangulated every chunk
        // with second- and third-neighbour Normal constraints; that mathematically
        // locked the chain into a rigid bar. Keep the real skeleton local instead:
        // adjacent chunks hold length, while weak Push braces only stop catastrophic
        // folding and do not try to preserve a straight angle.
        int adjacentCount = SegmentCount - 1;
        int bendLimiterCount = SegmentCount - 2;
        bodyChunkConnections = new BodyChunkConnection[adjacentCount + bendLimiterCount];

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
                SegmentSpacing * 1.50f,
                BodyChunkConnection.Type.Push,
                0.36f,
                -1f);
        }

        // Push braces assign rotationChunk as a side effect; restore local neighbours
        // so body orientation continues to describe the actual chain rather than a
        // two-segment diagonal brace.
        int middle = SegmentCount / 2;
        for (int i = 0; i < SegmentCount; i++)
        {
            bodyChunks[i].rotationChunk = bodyChunks[
                i < middle
                    ? i + 1
                    : Mathf.Max(0, i - 1)];
        }

        SupportLegs = new MossySpiderLeg[LegCount];
        for (int station = 0; station < 5; station++)
        {
            float stationT = station / 4f;
            float bodyU = Mathf.Lerp(0.10f, 0.90f, stationT);
            int baseChunk = Mathf.Clamp(
                Mathf.RoundToInt(bodyU * (SegmentCount - 1)),
                1,
                SegmentCount - 2);

            for (int layer = 0; layer < 2; layer++)
            {
                int leg = station * 2 + layer;
                float restLength = 88f + Mathf.Sin(stationT * Mathf.PI) * 8f +
                                   (layer == 0 ? 2f : -2f);
                float maxLength = 150f + Mathf.Sin(stationT * Mathf.PI) * 8f;

                // Two legs at each station fan in different fore/aft directions. In
                // side view this produces the broad arthropod stance from the design
                // instead of ten nearly vertical lines stacked under the belly.
                float stationFan = Mathf.Lerp(-46f, 46f, stationT);
                float pairFan = layer == 0 ? -20f : 20f;
                float reach = stationFan + pairFan;

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

        airFriction = 0.997f;
        gravity = 0.90f;
        bounce = 0.03f;
        surfaceFriction = 0.82f;
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
        GaitCycle = 0f;
        ResetSupportLegs();
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        MoveDirection = Vector2.zero;
        SwimFactor = 0f;
        GaitCycle = 0f;
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

        // Lethal rain can still hit, push and stun this animal, but it does not build
        // the rainDeath accumulator that normally kills exposed creatures.
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
            if (Consious && !dead)
            {
                // Resolve this frame's path intent before the feet update. New steps
                // therefore lead the current movement direction rather than reacting
                // one full body-length after the torso has already slid away.
                UpdateLocomotion();
            }
            else
            {
                MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.15f);
            }

            Vector2 bodyAxis = PhysicalBodyAxis();
            UpdateGaitClock(bodyAxis);

            for (int i = 0; i < SupportLegs.Length; i++)
            {
                SupportLegs[i].UpdateContact(this, bodyAxis);
            }

            UpdateSwimFactor();

            if (Consious && !dead)
            {
                if (SwimFactor < 0.58f)
                {
                    ScheduleGroundSteps(bodyAxis);
                }

                // Shallow water is still ordinary leg-supported locomotion. Support
                // fades only after bottom contact is genuinely being lost.
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

                ApplyDorsalFloat();
            }
        }

        ApplyTorsoMuscles();
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
            // A long flexible body can briefly put its middle chunk on an AI tile that
            // is worse than the tiles under either inner end. Try both inner anchors
            // before declaring that the migration path is unavailable.
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
            // Air is legal for the AI center, but ground locomotion never turns that
            // fact into flight. Terrain and leg contacts own vertical body placement.
            desired.y *= 0.18f;
            if (desired.sqrMagnitude > 0.001f)
            {
                desired.Normalize();
            }
        }

        MoveDirection = Vector2.Lerp(MoveDirection, desired, 0.11f);

        // Rain World large creatures generally push their torso toward the pather's
        // desired direction while appendages decide how that motion is supported.
        // Once planted feet are no longer horizontal springs, this small drive is able
        // to translate the body and naturally forces old feet to take another step.
        float drive = Mathf.Lerp(0.043f, 0.055f, SwimFactor);
        float maxHorizontalSpeed = Mathf.Lerp(1.40f, 1.65f, SwimFactor);
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
    }

    private void UpdateGaitClock(Vector2 bodyAxis)
    {
        float movement = Mathf.Clamp01(Mathf.Abs(Vector2.Dot(MoveDirection, bodyAxis)));
        float advance = Mathf.Lerp(0.001f, 0.019f, movement);
        GaitCycle = Mathf.Repeat(GaitCycle + advance, 1f);
    }

    private void ScheduleGroundSteps(Vector2 bodyAxis)
    {
        int stepping = 0;
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Stepping)
            {
                stepping++;
            }
        }

        while (stepping < MaximumSimultaneousSteps)
        {
            int best = -1;
            float bestUrgency = 0.48f;

            for (int i = 0; i < SupportLegs.Length; i++)
            {
                MossySpiderLeg leg = SupportLegs[i];
                if (leg.Stepping || !leg.Planted || StationHasSteppingLeg(leg.Station))
                {
                    continue;
                }

                float urgency = leg.StepUrgency(this, bodyAxis);
                if (urgency > bestUrgency)
                {
                    bestUrgency = urgency;
                    best = i;
                }
            }

            if (best < 0 || !SupportLegs[best].BeginStep(this, bodyAxis))
            {
                break;
            }

            stepping++;
        }
    }

    private bool StationHasSteppingLeg(int station)
    {
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Station == station && SupportLegs[i].Stepping)
            {
                return true;
            }
        }

        return false;
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

    private void ApplyTorsoMuscles()
    {
        if (bodyChunks == null || bodyChunks.Length < 3)
        {
            return;
        }

        // Local, weak muscle tone only. There is deliberately no end-to-end leveling
        // force and no triangulated length constraint: each section may rotate over
        // slopes, impacts and uneven leg supports just like other Rain World bodies.
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].vel.x *= SwimFactor > 0.5f ? 0.998f : 0.996f;
        }

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            Vector2 midpoint = (bodyChunks[i - 1].pos + bodyChunks[i + 1].pos) * 0.5f;
            Vector2 error = midpoint - bodyChunks[i].pos;
            bodyChunks[i].vel += Vector2.ClampMagnitude(error, 24f) * 0.0032f;
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

        // Legs should follow a sloping torso, but not point into the sky because one
        // end was briefly kicked upward. Keep some Y rather than flattening it almost
        // completely as the previous rigid-body workaround did.
        axis.y *= 0.45f;
        if (axis.sqrMagnitude < 0.001f)
        {
            return Vector2.right;
        }

        return axis.normalized;
    }
}
