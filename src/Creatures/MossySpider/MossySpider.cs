using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 4;
    internal const float SegmentSpacing = 78f;
    internal const int LegCount = 10;

    private const float UnsupportedGravity = 0.90f;
    private const float SupportedGravity = 0.075f;
    private const int MinimumStableFeet = 6;

    private static readonly float[] SegmentRadii =
    [
        28f,
        31f,
        31f,
        28f
    ];

    private static readonly float[] SegmentMasses =
    [
        6.2f,
        7.0f,
        7.0f,
        6.2f
    ];

    public float IdleMotion;
    public float LastIdleMotion;

    internal MossySpiderLeg[] SupportLegs { get; }

    internal MossySpiderAI AI => abstractCreature?.abstractAI?.RealAI as MossySpiderAI;

    internal Vector2 MoveDirection { get; private set; }

    internal float SwimFactor { get; private set; }

    internal float GaitCycle { get; private set; }

    internal float GroundSupport { get; private set; }

    private int nextStepStation;
    private int stepCounter;

    public BodyChunk MiddleChunk => bodyChunks[1];

    internal Vector2 BodyCenter
    {
        get
        {
            Vector2 center = Vector2.zero;
            for (int i = 0; i < bodyChunks.Length; i++)
            {
                center += bodyChunks[i].pos;
            }
            return center / bodyChunks.Length;
        }
    }

    public MossySpider(AbstractCreature abstractCreature, World world) : base(abstractCreature, world)
    {
        // Keep the actual collision skeleton sparse. The broad moss-covered animal is
        // mostly silhouette; it is not a row of overlapping collision balls. Deer and
        // DrillCrab both keep their real torso much simpler than their visible body.
        bodyChunks = new BodyChunk[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            bodyChunks[i] = new BodyChunk(
                this,
                i,
                Vector2.zero,
                SegmentRadii[i],
                SegmentMasses[i]);
        }

        // Only local length constraints form the skeleton. There are deliberately no
        // second-neighbour triangles and no fixed-angle braces.
        bodyChunkConnections = new BodyChunkConnection[SegmentCount - 1];
        for (int i = 0; i < SegmentCount - 1; i++)
        {
            bodyChunkConnections[i] = new BodyChunkConnection(
                bodyChunks[i],
                bodyChunks[i + 1],
                SegmentSpacing,
                BodyChunkConnection.Type.Normal,
                1f,
                -1f);
        }

        bodyChunks[0].rotationChunk = bodyChunks[1];
        bodyChunks[1].rotationChunk = bodyChunks[2];
        bodyChunks[2].rotationChunk = bodyChunks[1];
        bodyChunks[3].rotationChunk = bodyChunks[2];

        SupportLegs = new MossySpiderLeg[LegCount];
        for (int station = 0; station < 5; station++)
        {
            float stationT = station / 4f;
            float bodyU = Mathf.Lerp(0.055f, 0.945f, stationT);
            float standHeight = 102f + Mathf.Sin(stationT * Mathf.PI) * 10f;
            float maxLength = 171f + Mathf.Sin(stationT * Mathf.PI) * 9f;
            float stationFan = Mathf.Lerp(-72f, 72f, stationT);

            for (int layer = 0; layer < 2; layer++)
            {
                int legIndex = station * 2 + layer;
                float pairFan = layer == 0 ? -19f : 19f;

                SupportLegs[legIndex] = new MossySpiderLeg(
                    legIndex,
                    station,
                    layer,
                    bodyU,
                    standHeight,
                    maxLength,
                    stationFan + pairFan);
            }
        }

        airFriction = 0.997f;
        gravity = UnsupportedGravity;
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
            Vector2 pos = center + Vector2.right * ((i - half) * SegmentSpacing);
            chunk.pos = pos;
            chunk.lastPos = pos;
            chunk.lastLastPos = pos;
            chunk.vel = Vector2.zero;
        }

        ResetLocomotionState();
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        ResetLocomotionState();
    }

    public override void InitiateGraphicsModule()
    {
        graphicsModule ??= new MossySpiderGraphics(this);
    }

    public override Color ShortCutColor() => new(0.48f, 0.52f, 0.22f);

    public override void Update(bool eu)
    {
        base.Update(eu);

        rainDeath = 0f;
        LastIdleMotion = IdleMotion;
        IdleMotion = Mathf.Repeat(IdleMotion + 0.013f, 10000f);

        AI?.Update();

        if (room == null)
        {
            gravity = UnsupportedGravity;
            rainDeath = 0f;
            return;
        }

        if (Consious && !dead)
        {
            UpdateLocomotionIntent();
        }
        else
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.15f);
        }

        Vector2 bodyAxis = PhysicalBodyAxis();
        UpdateGaitClock(bodyAxis);

        for (int i = 0; i < SupportLegs.Length; i++)
        {
            SupportLegs[i].Update(this, bodyAxis);
        }

        UpdateSwimFactor();

        if (Consious && !dead)
        {
            if (SwimFactor < 0.58f)
            {
                CoordinateGroundGait(bodyAxis);
            }

            ApplyGroundSupport();
            ApplyDorsalFloat();
        }
        else
        {
            gravity = Mathf.MoveTowards(gravity, UnsupportedGravity, 0.05f);
            GroundSupport = Mathf.MoveTowards(GroundSupport, 0f, 0.08f);
        }

        PreventCatastrophicFolding();
        rainDeath = 0f;
    }

    internal Vector2 BodyPointAt(float bodyU)
    {
        float x = Mathf.Clamp01(bodyU) * (bodyChunks.Length - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(x), 0, bodyChunks.Length - 2);
        float t = x - a;
        return Vector2.Lerp(bodyChunks[a].pos, bodyChunks[a + 1].pos, t);
    }

    internal Vector2 BodyVelocityAt(float bodyU)
    {
        float x = Mathf.Clamp01(bodyU) * (bodyChunks.Length - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(x), 0, bodyChunks.Length - 2);
        float t = x - a;
        return Vector2.Lerp(bodyChunks[a].vel, bodyChunks[a + 1].vel, t);
    }

    internal void ApplyMomentumAt(float bodyU, Vector2 momentum)
    {
        float x = Mathf.Clamp01(bodyU) * (bodyChunks.Length - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(x), 0, bodyChunks.Length - 2);
        int b = a + 1;
        float t = x - a;

        bodyChunks[a].vel += momentum * ((1f - t) / Mathf.Max(0.01f, bodyChunks[a].mass));
        bodyChunks[b].vel += momentum * (t / Mathf.Max(0.01f, bodyChunks[b].mass));
    }

    internal int SupportingLegCount()
    {
        int count = 0;
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Supporting)
            {
                count++;
            }
        }
        return count;
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

    private void ResetLocomotionState()
    {
        MoveDirection = Vector2.zero;
        SwimFactor = 0f;
        GroundSupport = 0f;
        GaitCycle = 0f;
        nextStepStation = 0;
        stepCounter = 0;
        gravity = UnsupportedGravity;

        Vector2 axis = PhysicalBodyAxis();
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            SupportLegs[i].Reset(this, axis);
        }
    }

    private void UpdateLocomotionIntent()
    {
        if (AI?.Pather == null)
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.12f);
            return;
        }

        WorldCoordinate origin = room.GetWorldCoordinate(BodyCenter);
        MovementConnection move = AI.Pather.FollowPath(origin, actuallyFollowingThisPath: true);

        if (move == default)
        {
            move = AI.Pather.FollowPath(
                room.GetWorldCoordinate(bodyChunks[1].pos),
                actuallyFollowingThisPath: true);
        }

        if (move == default)
        {
            move = AI.Pather.FollowPath(
                room.GetWorldCoordinate(bodyChunks[2].pos),
                actuallyFollowingThisPath: true);
        }

        if (move == default || !move.destinationCoord.TileDefined)
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.10f);
            return;
        }

        Vector2 desired = room.MiddleOfTile(move.destinationCoord) - BodyCenter;
        if (desired.sqrMagnitude > 0.001f)
        {
            desired.Normalize();
        }

        if (SwimFactor < 0.55f)
        {
            desired.y *= 0.15f;
            if (desired.sqrMagnitude > 0.001f)
            {
                desired.Normalize();
            }
        }

        MoveDirection = Vector2.Lerp(MoveDirection, desired, 0.10f);

        float supportDrive = Mathf.Lerp(0.18f, 1f, GroundSupport);
        float drive = Mathf.Lerp(0.052f * supportDrive, 0.064f, SwimFactor);
        float maxHorizontalSpeed = Mathf.Lerp(1.35f, 1.65f, SwimFactor);

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            chunk.vel.x += MoveDirection.x * drive;
            chunk.vel.x = Mathf.Clamp(chunk.vel.x, -maxHorizontalSpeed, maxHorizontalSpeed);

            if (SwimFactor > 0.05f)
            {
                chunk.vel.y += MoveDirection.y * drive * 0.55f * SwimFactor;
            }
        }
    }

    private void UpdateGaitClock(Vector2 bodyAxis)
    {
        float movement = Mathf.Clamp01(Mathf.Abs(Vector2.Dot(MoveDirection, bodyAxis)));
        GaitCycle = Mathf.Repeat(GaitCycle + Mathf.Lerp(0.001f, 0.016f, movement), 1f);
    }

    private void CoordinateGroundGait(Vector2 bodyAxis)
    {
        float motion = Mathf.Abs(Vector2.Dot(MoveDirection, bodyAxis));
        if (motion < 0.08f)
        {
            stepCounter = Mathf.Max(0, stepCounter - 1);
            return;
        }

        stepCounter++;
        if (stepCounter < 18)
        {
            return;
        }
        stepCounter = 0;

        int supporting = SupportingLegCount();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int station = nextStepStation;
            nextStepStation = (nextStepStation + 1) % 5;

            MossySpiderLeg a = SupportLegs[station * 2];
            MossySpiderLeg b = SupportLegs[station * 2 + 1];

            int remaining = supporting;
            if (a.Supporting) remaining--;
            if (b.Supporting) remaining--;

            bool wants = a.WantsToStep(this, bodyAxis) || b.WantsToStep(this, bodyAxis);
            if (!wants || remaining < MinimumStableFeet)
            {
                continue;
            }

            bool movedA = a.StartStep(this, bodyAxis);
            bool movedB = b.StartStep(this, bodyAxis);
            if (movedA || movedB)
            {
                return;
            }
        }
    }

    private void ApplyGroundSupport()
    {
        int supporting = SupportingLegCount();
        float supportTarget = Mathf.InverseLerp(1f, MinimumStableFeet, supporting);
        float groundFactor = 1f - Mathf.SmoothStep(0.48f, 0.90f, SwimFactor);
        supportTarget *= groundFactor;

        GroundSupport = Mathf.Lerp(GroundSupport, supportTarget, 0.16f);
        gravity = Mathf.Lerp(UnsupportedGravity, SupportedGravity, GroundSupport);
        airFriction = Mathf.Lerp(0.997f, 0.965f, GroundSupport);

        if (groundFactor <= 0.001f)
        {
            return;
        }

        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Supporting)
            {
                SupportLegs[i].ApplySupportMomentum(this, groundFactor);
            }
        }
    }

    private void UpdateSwimFactor()
    {
        if (!room.water)
        {
            SwimFactor = Mathf.MoveTowards(SwimFactor, 0f, 0.04f);
            return;
        }

        float averageSubmersion = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            averageSubmersion += bodyChunks[i].submersion;
        }
        averageSubmersion /= bodyChunks.Length;

        int support = SupportingLegCount();
        float waterFactor = Mathf.InverseLerp(0.12f, 0.55f, averageSubmersion);
        float bottomLost = Mathf.InverseLerp(5f, 1f, support);
        float target = Mathf.Clamp01(waterFactor * bottomLost);
        SwimFactor = Mathf.MoveTowards(SwimFactor, target, 0.025f);
    }

    private void ApplyDorsalFloat()
    {
        if (!room.water || SwimFactor <= 0.001f)
        {
            return;
        }

        gravity = Mathf.Lerp(gravity, 0.04f, SwimFactor);
        float surface = room.FloatWaterLevel(BodyCenter);

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

    private void PreventCatastrophicFolding()
    {
        // This is not an angle rest constraint. It activates only after two outer
        // chunks have folded implausibly close together, then gently separates them.
        // Normal bends and player dragging remain free.
        float minimumOuterDistance = SegmentSpacing * 1.18f;

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            BodyChunk left = bodyChunks[i - 1];
            BodyChunk right = bodyChunks[i + 1];
            Vector2 delta = right.pos - left.pos;
            float distance = delta.magnitude;

            if (distance >= minimumOuterDistance || distance < 0.001f)
            {
                continue;
            }

            Vector2 dir = delta / distance;
            float push = Mathf.InverseLerp(minimumOuterDistance, SegmentSpacing * 0.55f, distance) * 0.18f;
            left.vel -= dir * push;
            right.vel += dir * push;
        }
    }

    private Vector2 PhysicalBodyAxis()
    {
        Vector2 axis = bodyChunks[bodyChunks.Length - 1].pos - bodyChunks[0].pos;
        return axis.sqrMagnitude > 0.001f ? axis.normalized : Vector2.right;
    }
}
