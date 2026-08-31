using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    // The visible animal is a broad carapace, not a worm-like chain. Rain Deer keeps
    // its collision body compact and lets tentacles create the stance; DrillCrab goes
    // even further and uses two BodyChunks plus independent legs. MossySpider now uses
    // the same principle: two physical chassis chunks define position/orientation and
    // ten appendages carry the body. This makes an inverted-U torso physically
    // impossible without imposing any preferred world angle.
    internal const int SegmentCount = 2;
    internal const float SegmentSpacing = 250f;
    internal const int LegCount = 10;

    private const float UnsupportedGravity = 0.90f;
    private const float SupportedGravity = 0.025f;
    private const int MinimumStableFeet = 5;
    private const int MaximumSteppingLegs = 2;

    public float IdleMotion;
    public float LastIdleMotion;

    internal MossySpiderLeg[] SupportLegs { get; }
    internal MossySpiderAI AI => abstractCreature?.abstractAI?.RealAI as MossySpiderAI;

    internal Vector2 MoveDirection { get; private set; }
    internal float SwimFactor { get; private set; }
    internal float GaitCycle { get; private set; }
    internal float GroundSupport { get; private set; }

    private int nextStepLeg;
    private int stepCounter;

    public BodyChunk MiddleChunk => bodyChunks[0];

    internal Vector2 BodyCenter => (bodyChunks[0].pos + bodyChunks[1].pos) * 0.5f;

    public MossySpider(AbstractCreature abstractCreature, World world) : base(abstractCreature, world)
    {
        bodyChunks = new BodyChunk[SegmentCount];
        bodyChunks[0] = new BodyChunk(this, 0, Vector2.zero, 34f, 8.5f);
        bodyChunks[1] = new BodyChunk(this, 1, Vector2.zero, 34f, 8.5f);

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].restrictInRoomRange = 2000f;
            bodyChunks[i].defaultRestrictInRoomRange = 2000f;
        }

        // One ordinary length connection. There are no diagonal braces, no angle
        // springs and no hidden horizontal correction. The chassis can rotate freely
        // when dragged or when one end is lifted by terrain.
        bodyChunkConnections =
        [
            new BodyChunkConnection(
                bodyChunks[0],
                bodyChunks[1],
                SegmentSpacing,
                BodyChunkConnection.Type.Normal,
                1f,
                -1f)
        ];

        bodyChunks[0].rotationChunk = bodyChunks[1];
        bodyChunks[1].rotationChunk = bodyChunks[0];

        // Five stations across the long chassis, with a near/far pair at every station.
        // Their anchors are interpolated along the two real BodyChunks. The visible and
        // physical stance therefore covers the entire body without adding more torso
        // collision balls that can fold into an arch.
        SupportLegs = new MossySpiderLeg[LegCount];
        for (int station = 0; station < 5; station++)
        {
            float stationT = station / 4f;
            float bodyU = Mathf.Lerp(0.055f, 0.945f, stationT);
            float standHeight = 112f + Mathf.Sin(stationT * Mathf.PI) * 10f;
            float maxLength = 178f + Mathf.Sin(stationT * Mathf.PI) * 10f;
            float stationFan = Mathf.Lerp(-72f, 72f, stationT);

            for (int layer = 0; layer < 2; layer++)
            {
                int legIndex = station * 2 + layer;
                float pairFan = layer == 0 ? -22f : 22f;

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

        airFriction = 0.999f;
        gravity = UnsupportedGravity;
        bounce = 0.05f;
        surfaceFriction = 0.35f;
        collisionLayer = 1;
        waterFriction = 0.96f;
        buoyancy = 0.70f;
        GoThroughFloors = true;
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);

        // Start high enough for the appendages to deploy before the carapace reaches
        // the floor. This is only initial placement; afterwards height is entirely the
        // result of gravity plus leg support.
        Vector2 center = placeRoom.MiddleOfTile(abstractCreature.pos.Tile) + Vector2.up * 116f;
        Vector2 halfAxis = Vector2.right * (SegmentSpacing * 0.5f);

        bodyChunks[0].pos = center - halfAxis;
        bodyChunks[1].pos = center + halfAxis;

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            bodyChunks[i].lastPos = bodyChunks[i].pos;
            bodyChunks[i].lastLastPos = bodyChunks[i].pos;
            bodyChunks[i].vel = Vector2.zero;
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

        // DrillCrab-style ordering: appendages establish their contacts first, then
        // those contacts determine how much weight the torso is allowed to carry.
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

        rainDeath = 0f;
    }

    internal Vector2 BodyPointAt(float bodyU)
    {
        return Vector2.Lerp(bodyChunks[0].pos, bodyChunks[1].pos, Mathf.Clamp01(bodyU));
    }

    internal Vector2 BodyVelocityAt(float bodyU)
    {
        return Vector2.Lerp(bodyChunks[0].vel, bodyChunks[1].vel, Mathf.Clamp01(bodyU));
    }

    internal void ApplyMomentumAt(float bodyU, Vector2 momentum)
    {
        float t = Mathf.Clamp01(bodyU);
        bodyChunks[0].vel += momentum * ((1f - t) / Mathf.Max(0.01f, bodyChunks[0].mass));
        bodyChunks[1].vel += momentum * (t / Mathf.Max(0.01f, bodyChunks[1].mass));
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
        nextStepLeg = 0;
        stepCounter = 0;
        gravity = UnsupportedGravity;
        airFriction = 0.999f;

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

        MovementConnection move = AI.Pather.FollowPath(
            room.GetWorldCoordinate(BodyCenter),
            actuallyFollowingThisPath: true);

        if (move == default)
        {
            move = AI.Pather.FollowPath(
                room.GetWorldCoordinate(bodyChunks[0].pos),
                actuallyFollowingThisPath: true);
        }

        if (move == default)
        {
            move = AI.Pather.FollowPath(
                room.GetWorldCoordinate(bodyChunks[1].pos),
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
            // Air is legal to pathing, but terrestrial vertical placement belongs to
            // appendages. Ground AI therefore contributes almost no direct Y drive.
            desired.y *= 0.12f;
            if (desired.sqrMagnitude > 0.001f)
            {
                desired.Normalize();
            }
        }

        MoveDirection = Vector2.Lerp(MoveDirection, desired, 0.10f);

        float supportDrive = Mathf.Lerp(0.22f, 1f, GroundSupport);
        float drive = Mathf.Lerp(0.050f * supportDrive, 0.064f, SwimFactor);
        float maxHorizontalSpeed = Mathf.Lerp(1.28f, 1.65f, SwimFactor);

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
        float actual = Mathf.Clamp01(Mathf.Abs(BodyVelocityAt(0.5f).x) / 1.2f);
        GaitCycle = Mathf.Repeat(
            GaitCycle + Mathf.Lerp(0.001f, 0.017f, Mathf.Max(movement * 0.6f, actual)),
            1f);
    }

    private void CoordinateGroundGait(Vector2 bodyAxis)
    {
        float motion = Mathf.Abs(Vector2.Dot(MoveDirection, bodyAxis));
        if (motion < 0.08f)
        {
            stepCounter = Mathf.Max(0, stepCounter - 1);
            return;
        }

        int stepping = 0;
        for (int i = 0; i < SupportLegs.Length; i++)
        {
            if (SupportLegs[i].Stepping)
            {
                stepping++;
            }
        }

        if (stepping >= MaximumSteppingLegs)
        {
            return;
        }

        stepCounter++;
        if (stepCounter < 8)
        {
            return;
        }
        stepCounter = 0;

        int supporting = SupportingLegCount();
        for (int attempt = 0; attempt < LegCount; attempt++)
        {
            int legIndex = nextStepLeg;
            nextStepLeg = (nextStepLeg + 3) % LegCount;
            MossySpiderLeg leg = SupportLegs[legIndex];

            if (!leg.Supporting || !leg.WantsToStep(this, bodyAxis))
            {
                continue;
            }

            if (supporting - 1 < MinimumStableFeet || StationHasSteppingLeg(leg.Station))
            {
                continue;
            }

            if (leg.StartStep(this, bodyAxis))
            {
                return;
            }
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

    private void ApplyGroundSupport()
    {
        int supporting = SupportingLegCount();

        // DrillCrab already starts shedding gravity as soon as a small number of legs
        // support it. Do the same here instead of waiting for six perfect footholds;
        // otherwise the heavy chassis reaches the floor before the outer feet can lift it.
        float supportTarget = Mathf.InverseLerp(0f, 5f, supporting);
        float groundFactor = 1f - Mathf.SmoothStep(0.48f, 0.90f, SwimFactor);
        supportTarget *= groundFactor;

        GroundSupport = Mathf.Lerp(GroundSupport, supportTarget, 0.22f);
        gravity = Mathf.Lerp(UnsupportedGravity, SupportedGravity, GroundSupport);
        airFriction = Mathf.Lerp(0.999f, 0.965f, GroundSupport);

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

        float averageSubmersion = (bodyChunks[0].submersion + bodyChunks[1].submersion) * 0.5f;
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

    private Vector2 PhysicalBodyAxis()
    {
        Vector2 axis = bodyChunks[1].pos - bodyChunks[0].pos;
        return axis.sqrMagnitude > 0.001f ? axis.normalized : Vector2.right;
    }
}
