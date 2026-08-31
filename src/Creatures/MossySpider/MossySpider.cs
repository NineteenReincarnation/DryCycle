using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpider : Creature
{
    internal const int SegmentCount = 4;
    internal const float SegmentSpacing = 78f;
    internal const int LegCount = 10;

    private const float UnsupportedGravity = 0.90f;
    private const float SupportedGravity = 0.040f;
    private const int MinimumStableFeet = 5;
    private const int MaximumSteppingLegs = 2;

    private static readonly float[] SegmentRadii =
    [
        28f,
        32f,
        32f,
        28f
    ];

    private static readonly float[] SegmentMasses =
    [
        5.6f,
        6.6f,
        6.6f,
        5.6f
    ];

    private static readonly Vector2[] PathProbeDirections =
    [
        Vector2.zero,
        Vector2.left,
        Vector2.right,
        Vector2.up,
        Vector2.down
    ];

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
        bodyChunks = new BodyChunk[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            bodyChunks[i] = new BodyChunk(
                this,
                i,
                Vector2.zero,
                SegmentRadii[i],
                SegmentMasses[i]);
            bodyChunks[i].restrictInRoomRange = 2000f;
            bodyChunks[i].defaultRestrictInRoomRange = 2000f;
        }

        // Four real masses remain in the torso. Only adjacent length constraints are
        // used; posture is produced by appendage support plus weak local muscle tone.
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

        // The concept has very long walking legs. The previous ~108 px stance made the
        // carapace look almost ground-bound. Raise the natural stance to ~150 px and
        // give each leg enough extra reach to cross broken terrain without stretching.
        SupportLegs = new MossySpiderLeg[LegCount];
        for (int station = 0; station < 5; station++)
        {
            float stationT = station / 4f;
            float bodyU = Mathf.Lerp(0.04f, 0.96f, stationT);
            float standHeight = 148f + Mathf.Sin(stationT * Mathf.PI) * 14f;
            float maxLength = 226f + Mathf.Sin(stationT * Mathf.PI) * 16f;
            float stationFan = Mathf.Lerp(-24f, 24f, stationT);

            for (int layer = 0; layer < 2; layer++)
            {
                int legIndex = station * 2 + layer;
                float pairFan = layer == 0 ? -29f : 29f;

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
        surfaceFriction = 0.34f;
        collisionLayer = 1;
        waterFriction = 0.96f;
        buoyancy = 0.70f;
        GoThroughFloors = true;
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);

        // Spawn near the intended long-legged stance so feet can acquire terrain before
        // the broad belly reaches the floor. If it does collapse, MossySpiderLeg can now
        // reacquire terrain even from a very small hip-to-ground distance and stand up.
        Vector2 center = placeRoom.MiddleOfTile(abstractCreature.pos.Tile) + Vector2.up * 156f;
        float half = (SegmentCount - 1) * 0.5f;

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            chunk.pos = center + Vector2.right * ((i - half) * SegmentSpacing);
            chunk.lastPos = chunk.pos;
            chunk.lastLastPos = chunk.pos;
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
            ApplyBodyMuscleTone();
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

        MovementConnection move = FindPathConnection();
        if (move == default || !move.destinationCoord.TileDefined)
        {
            MoveDirection = Vector2.Lerp(MoveDirection, Vector2.zero, 0.08f);
            return;
        }

        // Large Rain World creatures do not steer at a one-tile target. Deer explicitly
        // looks several Standard connections ahead. Do the same here so the migration
        // drive has a stable forward direction instead of alternating around one cell.
        WorldCoordinate steeringDestination = LookAhead(move.destinationCoord);
        Vector2 desired = room.MiddleOfTile(steeringDestination) - BodyCenter;
        if (desired.sqrMagnitude > 0.001f)
        {
            desired.Normalize();
        }

        if (SwimFactor < 0.55f)
        {
            // Air is legal to pathing, but appendages own terrestrial height.
            desired.y *= 0.10f;
            if (desired.sqrMagnitude > 0.001f)
            {
                desired.Normalize();
            }
        }

        MoveDirection = Vector2.Lerp(MoveDirection, desired, 0.14f);

        // The old 0.011-0.05 effective ground drive was weaker than belly/floor friction
        // whenever the animal had not yet achieved full leg support, producing the
        // visible "walking in place" state. Keep locomotion slow by limiting terminal
        // speed, not by making the drive too weak to translate the chassis.
        float supportDrive = Mathf.Lerp(0.58f, 1f, GroundSupport);
        float drive = Mathf.Lerp(0.078f * supportDrive, 0.072f, SwimFactor);
        float maxHorizontalSpeed = Mathf.Lerp(1.55f, 1.80f, SwimFactor);

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

    private MovementConnection FindPathConnection()
    {
        MovementConnection move = AI.Pather.FollowPath(
            room.GetWorldCoordinate(BodyCenter),
            actuallyFollowingThisPath: true);

        if (move != default)
        {
            return move;
        }

        // The center of a huge body often sits on a different AI cell than its feet.
        // Deer solves this by probing around several BodyChunks. Probe all four torso
        // masses at increasing radii before concluding that the pather has no route.
        for (int ring = 0; ring < 3; ring++)
        {
            float radius = 20f * (ring + 1);
            for (int chunkIndex = 0; chunkIndex < bodyChunks.Length; chunkIndex++)
            {
                for (int dirIndex = 0; dirIndex < PathProbeDirections.Length; dirIndex++)
                {
                    Vector2 probe = bodyChunks[chunkIndex].pos +
                                    PathProbeDirections[dirIndex] * radius;
                    move = AI.Pather.FollowPath(
                        room.GetWorldCoordinate(probe),
                        actuallyFollowingThisPath: true);

                    if (move != default)
                    {
                        return move;
                    }
                }
            }
        }

        return default;
    }

    private WorldCoordinate LookAhead(WorldCoordinate start)
    {
        WorldCoordinate destination = start;
        if (!destination.TileDefined || destination.room != room.abstractRoom.index)
        {
            return destination;
        }

        for (int i = 0; i < 10; i++)
        {
            MovementConnection next = AI.Pather.FollowPath(
                destination,
                actuallyFollowingThisPath: false);

            if (next == default ||
                !next.destinationCoord.TileDefined ||
                next.destinationCoord.room != room.abstractRoom.index ||
                (next.type != MovementConnection.MovementType.Standard &&
                 next.type != MovementConnection.MovementType.OpenDiagonal))
            {
                break;
            }

            destination = next.destinationCoord;
        }

        return destination;
    }

    private void UpdateGaitClock(Vector2 bodyAxis)
    {
        float movement = Mathf.Clamp01(Mathf.Abs(Vector2.Dot(MoveDirection, bodyAxis)));
        float actual = Mathf.Clamp01(Mathf.Abs(BodyVelocityAt(0.5f).x) / 1.4f);
        GaitCycle = Mathf.Repeat(
            GaitCycle + Mathf.Lerp(0.001f, 0.018f, Mathf.Max(movement * 0.6f, actual)),
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
        if (stepCounter < 7)
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
        float supportTarget = Mathf.InverseLerp(0f, 5f, supporting);
        float groundFactor = 1f - Mathf.SmoothStep(0.48f, 0.90f, SwimFactor);
        supportTarget *= groundFactor;

        GroundSupport = Mathf.Lerp(GroundSupport, supportTarget, 0.24f);
        gravity = Mathf.Lerp(UnsupportedGravity, SupportedGravity, GroundSupport);
        airFriction = Mathf.Lerp(0.999f, 0.972f, GroundSupport);

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

    private void ApplyBodyMuscleTone()
    {
        // Curvature resistance only; there is no preferred world angle. This keeps a
        // broad living torso from folding sharply while preserving free overall rotation.
        float tone = Mathf.Lerp(0.005f, 0.011f, GroundSupport);

        for (int i = 1; i < bodyChunks.Length - 1; i++)
        {
            BodyChunk left = bodyChunks[i - 1];
            BodyChunk middle = bodyChunks[i];
            BodyChunk right = bodyChunks[i + 1];

            Vector2 midpoint = (left.pos + right.pos) * 0.5f;
            Vector2 error = Vector2.ClampMagnitude(midpoint - middle.pos, 34f);
            Vector2 correction = error * tone;

            middle.vel += correction;
            left.vel -= correction * 0.30f;
            right.vel -= correction * 0.30f;
        }
    }

    private Vector2 PhysicalBodyAxis()
    {
        Vector2 axis = bodyChunks[bodyChunks.Length - 1].pos - bodyChunks[0].pos;
        return axis.sqrMagnitude > 0.001f ? axis.normalized : Vector2.right;
    }
}
