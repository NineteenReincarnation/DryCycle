using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// A MossySpider locomotor leg.
///
/// This follows the same division used by Rain Deer and DrillCrab: the torso owns only
/// a small set of BodyChunks, while a leg owns an independent tip target plus a chain
/// of visual/physical segment points. The tip searches for terrain, seeks a fixed
/// world-space support point, stays attached while supporting, and releases when the
/// body has travelled past it. Intermediate joints are solved from the moving anchor
/// and the real tip instead of being decorative lines recalculated under the body.
/// </summary>
internal sealed class MossySpiderLeg
{
    internal sealed class Segment
    {
        internal Vector2 pos;
        internal Vector2 lastPos;
        internal Vector2 vel;

        internal Segment(Vector2 pos)
        {
            this.pos = pos;
            lastPos = pos;
            vel = Vector2.zero;
        }
    }

    private enum Mode
    {
        Scanning,
        Seeking,
        Supporting,
        Swimming,
        Limp
    }

    private const float MinimumGroundDrop = 28f;
    private const float StepLead = 46f;
    private const float TipMaxSpeed = 18f;
    private const float TipAcceleration = 0.24f;
    private const float SupportValidationLift = 18f;
    private const float SupportValidationDrop = 34f;

    private static readonly float[] SearchOffsets =
    [
        0f,
        -18f,
        18f,
        -38f,
        38f,
        -58f,
        58f
    ];

    private readonly Segment[] segments;
    private Mode mode;
    private Vector2 anchor;
    private Vector2 lastAnchor;
    private Vector2 targetPos;
    private Vector2 tipVelocity;
    private int scanCounter;

    internal MossySpiderLeg(
        int index,
        int station,
        int layer,
        float bodyU,
        float standHeight,
        float maxLength,
        float reachOffset)
    {
        Index = index;
        Station = station;
        Layer = layer;
        BodyU = bodyU;
        StandHeight = standHeight;
        MaxLength = maxLength;
        ReachOffset = reachOffset;

        segments = new Segment[3]
        {
            new(Vector2.zero),
            new(Vector2.zero),
            new(Vector2.zero)
        };

        mode = Mode.Scanning;
    }

    internal int Index { get; }
    internal int Station { get; }
    internal int Layer { get; }
    internal float BodyU { get; }
    internal float StandHeight { get; }
    internal float RestLength => StandHeight;
    internal float MaxLength { get; }
    internal float ReachOffset { get; }

    internal bool Supporting => mode == Mode.Supporting;
    internal bool Planted => Supporting;
    internal bool Stepping => mode == Mode.Scanning || mode == Mode.Seeking;
    internal float Support { get; private set; }

    internal Vector2 FootPos => segments[segments.Length - 1].pos;
    internal Vector2 LastFootPos => segments[segments.Length - 1].lastPos;
    internal Vector2 DesiredFootPos => targetPos;

    internal void Reset(MossySpider spider, Vector2 bodyAxis)
    {
        anchor = PhysicalHip(spider, bodyAxis);
        lastAnchor = anchor;
        targetPos = DesiredGroundPoint(spider, bodyAxis, 0f);
        tipVelocity = Vector2.zero;
        Support = 0f;
        scanCounter = 0;
        mode = Mode.Scanning;

        for (int i = 0; i < segments.Length; i++)
        {
            float t = (i + 1f) / segments.Length;
            Vector2 pos = Vector2.Lerp(anchor, targetPos, t);
            segments[i].pos = pos;
            segments[i].lastPos = pos;
            segments[i].vel = Vector2.zero;
        }
    }

    internal void Update(MossySpider spider, Vector2 bodyAxis)
    {
        lastAnchor = anchor;
        anchor = PhysicalHip(spider, bodyAxis);

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i].lastPos = segments[i].pos;
        }

        if (spider.room == null)
        {
            return;
        }

        if (spider.dead || !spider.Consious)
        {
            mode = Mode.Limp;
            Support = Mathf.MoveTowards(Support, 0f, 0.12f);
            AnimateLimp(spider);
            return;
        }

        if (spider.SwimFactor > 0.58f)
        {
            mode = Mode.Swimming;
            Support = Mathf.MoveTowards(Support, 0f, 0.16f);
            AnimateSwimming(spider, bodyAxis);
            return;
        }

        if (mode == Mode.Swimming || mode == Mode.Limp)
        {
            mode = Mode.Scanning;
            scanCounter = 0;
        }

        switch (mode)
        {
            case Mode.Supporting:
                UpdateSupporting(spider, bodyAxis);
                break;

            case Mode.Seeking:
                UpdateSeeking(spider, bodyAxis);
                break;

            default:
                UpdateScanning(spider, bodyAxis);
                break;
        }
    }

    internal bool WantsToStep(MossySpider spider, Vector2 bodyAxis)
    {
        if (!Supporting || spider.SwimFactor > 0.45f)
        {
            return false;
        }

        Vector2 fromAnchor = FootPos - anchor;
        float extension = fromAnchor.magnitude;
        float move = Mathf.Clamp(Vector2.Dot(spider.MoveDirection, bodyAxis), -1f, 1f);
        float desiredReach = ReachOffset + move * StepLead;
        float currentReach = Vector2.Dot(fromAnchor, bodyAxis);

        if (extension > MaxLength * 0.88f)
        {
            return true;
        }

        if (Mathf.Abs(currentReach - desiredReach) > 48f)
        {
            return true;
        }

        float phase = Mathf.Repeat(spider.GaitCycle + Index * 0.173f, 1f);
        return Mathf.Abs(move) > 0.2f && phase > 0.86f;
    }

    internal bool StartStep(MossySpider spider, Vector2 bodyAxis)
    {
        if (!Supporting || spider.room == null)
        {
            return false;
        }

        mode = Mode.Scanning;
        scanCounter = 0;
        Support = 0f;

        // DrillCrab kicks the free tip away from its old support before scanning. A
        // small lift here keeps the MossySpider foot visibly clear of the terrain and
        // prevents an immediate reattach to the same pixel.
        tipVelocity += Vector2.up * 5.5f + bodyAxis * (Mathf.Sign(Vector2.Dot(spider.MoveDirection, bodyAxis)) * 3f);
        return true;
    }

    internal void ApplySupportMomentum(MossySpider spider, float supportFactor)
    {
        if (!Supporting || supportFactor <= 0.001f)
        {
            return;
        }

        // The foot is fixed in world space. Supporting legs reduce global gravity in
        // MossySpider.cs (DrillCrab style), while this local momentum restores the
        // preferred body height (Deer tentacle style). It is deliberately almost
        // vertical so planted feet do not cancel the AI's horizontal migration drive.
        float currentHeight = Mathf.Max(0f, anchor.y - FootPos.y);
        float error = StandHeight - currentHeight;
        Vector2 localVelocity = spider.BodyVelocityAt(BodyU);

        float upward = 0.24f + error * 0.055f - localVelocity.y * 0.34f;
        upward = Mathf.Clamp(upward, -0.45f, 2.65f) * Support * supportFactor;
        spider.ApplyMomentumAt(BodyU, Vector2.up * upward);
    }

    internal Vector2 DrawSegment(int segment, float timeStacker)
    {
        int index = Mathf.Clamp(segment, 0, segments.Length - 1);
        return Vector2.Lerp(segments[index].lastPos, segments[index].pos, timeStacker);
    }

    internal Vector2 DrawFoot(float timeStacker) => DrawSegment(segments.Length - 1, timeStacker);

    internal Vector2 DrawAnchor(float timeStacker) => Vector2.Lerp(lastAnchor, anchor, timeStacker);

    private Vector2 PhysicalHip(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 point = spider.BodyPointAt(BodyU);
        float pairOffset = Layer == 0 ? -6f : 6f;
        return point + Vector2.down * 18f + bodyAxis * pairOffset;
    }

    private void UpdateSupporting(MossySpider spider, Vector2 bodyAxis)
    {
        float distance = Vector2.Distance(anchor, FootPos);
        if (distance > MaxLength * 1.03f || FootPos.y > anchor.y - MinimumGroundDrop * 0.45f)
        {
            Release();
            AnimateInverseKinematics(FootPos, bodyAxis);
            return;
        }

        Vector2? ground = SharedPhysics.ExactTerrainRayTracePos(
            spider.room,
            FootPos + Vector2.up * SupportValidationLift,
            FootPos + Vector2.down * SupportValidationDrop);

        if (!ground.HasValue || Mathf.Abs(ground.Value.y - FootPos.y) > SupportValidationDrop)
        {
            Release();
            AnimateInverseKinematics(FootPos, bodyAxis);
            return;
        }

        // Keep X locked as a true world-space foothold. Only allow tiny Y correction
        // so moving/curved terrain does not detach a leg every frame.
        Vector2 tip = FootPos;
        tip.y = Mathf.Lerp(tip.y, ground.Value.y, 0.18f);
        segments[segments.Length - 1].pos = tip;
        tipVelocity = Vector2.zero;
        Support = Mathf.MoveTowards(Support, 1f, 0.16f);
        AnimateInverseKinematics(tip, bodyAxis);
    }

    private void UpdateScanning(MossySpider spider, Vector2 bodyAxis)
    {
        scanCounter++;
        Support = Mathf.MoveTowards(Support, 0f, 0.18f);

        if (TryFindGroundTarget(spider, bodyAxis, out Vector2 target))
        {
            targetPos = target;
            mode = Mode.Seeking;
            return;
        }

        // No support in reach: keep a free appendage moving under physics rather than
        // teleporting the foot under the body. It will keep scanning on later frames.
        AnimateFreeTip(spider, bodyAxis);
    }

    private void UpdateSeeking(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 tip = FootPos;
        Vector2 toTarget = targetPos - tip;
        float distance = toTarget.magnitude;

        if (distance < 7f)
        {
            LandOnGround(targetPos, bodyAxis);
            return;
        }

        Vector2 desiredVelocity = distance > 0.001f
            ? toTarget / distance * Mathf.Min(TipMaxSpeed, 4f + distance * 0.18f)
            : Vector2.zero;

        // Early in the seek, retain an upward component so the foot follows a real
        // swing instead of scraping straight across the floor.
        if (distance > 35f)
        {
            desiredVelocity.y += 2.2f;
        }

        tipVelocity = Vector2.Lerp(tipVelocity, desiredVelocity, TipAcceleration);
        tipVelocity *= 0.94f;
        Vector2 next = tip + tipVelocity;

        // If the moving tip hits authored terrain close to its intended target, attach
        // there immediately, like DrillCrab.Leg.LandOnGround().
        Vector2? hit = SharedPhysics.ExactTerrainRayTracePos(spider.room, tip, next);
        if (hit.HasValue && Vector2.Distance(hit.Value, targetPos) < 30f)
        {
            LandOnGround(hit.Value, bodyAxis);
            return;
        }

        segments[segments.Length - 1].pos = next;
        AnimateInverseKinematics(next, bodyAxis);

        if (Vector2.Distance(anchor, next) > MaxLength * 1.08f || scanCounter > 90)
        {
            mode = Mode.Scanning;
            scanCounter = 0;
        }
    }

    private void LandOnGround(Vector2 point, Vector2 bodyAxis)
    {
        segments[segments.Length - 1].pos = point;
        segments[segments.Length - 1].vel = Vector2.zero;
        targetPos = point;
        tipVelocity = Vector2.zero;
        mode = Mode.Supporting;
        Support = Mathf.Max(Support, 0.30f);
        scanCounter = 0;
        AnimateInverseKinematics(point, bodyAxis);
    }

    private void Release()
    {
        mode = Mode.Scanning;
        Support = 0f;
        scanCounter = 0;
        tipVelocity += Vector2.up * 2f;
    }

    private bool TryFindGroundTarget(MossySpider spider, Vector2 bodyAxis, out Vector2 target)
    {
        target = Vector2.zero;
        Vector2 desired = DesiredGroundPoint(spider, bodyAxis, StepLead);
        float bestScore = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < SearchOffsets.Length; i++)
        {
            Vector2 sample = desired + bodyAxis * SearchOffsets[i];
            Vector2 start = new(sample.x, anchor.y + 26f);
            Vector2 end = new(sample.x, anchor.y - MaxLength - 26f);
            Vector2? hit = SharedPhysics.ExactTerrainRayTracePos(spider.room, start, end);

            if (!hit.HasValue)
            {
                continue;
            }

            float drop = anchor.y - hit.Value.y;
            float extension = Vector2.Distance(anchor, hit.Value);
            if (drop < MinimumGroundDrop || extension > MaxLength)
            {
                continue;
            }

            float score = Mathf.Abs(SearchOffsets[i]) * 0.55f +
                          Mathf.Abs(drop - StandHeight) * 0.24f +
                          Mathf.Abs(Vector2.Dot(hit.Value - desired, bodyAxis)) * 0.18f;

            if (score < bestScore)
            {
                bestScore = score;
                target = hit.Value;
                found = true;
            }
        }

        return found;
    }

    private Vector2 DesiredGroundPoint(MossySpider spider, Vector2 bodyAxis, float movementLead)
    {
        float move = Mathf.Clamp(Vector2.Dot(spider.MoveDirection, bodyAxis), -1f, 1f);
        return anchor +
               bodyAxis * (ReachOffset + move * movementLead) +
               Vector2.down * StandHeight;
    }

    private void AnimateSwimming(MossySpider spider, Vector2 bodyAxis)
    {
        float phase = spider.IdleMotion * 1.65f + Index * 1.17f + Layer * Mathf.PI * 0.58f;
        float stroke = Mathf.Sin(phase);
        float recovery = Mathf.Cos(phase);
        Vector2 forward = spider.MoveDirection.sqrMagnitude > 0.01f
            ? spider.MoveDirection.normalized
            : bodyAxis;

        Vector2 tip = anchor +
                      forward * (-stroke * 42f + ReachOffset * 0.18f) +
                      Vector2.down * (StandHeight * 0.52f + recovery * 16f);

        targetPos = tip;
        segments[segments.Length - 1].pos = Vector2.Lerp(FootPos, tip, 0.23f);
        AnimateInverseKinematics(segments[segments.Length - 1].pos, bodyAxis);
    }

    private void AnimateFreeTip(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 freeTarget = DesiredGroundPoint(spider, bodyAxis, StepLead * 0.35f);
        Vector2 tip = FootPos;
        Vector2 force = Vector2.ClampMagnitude((freeTarget - tip) * 0.035f, 1.8f);
        tipVelocity += force;
        tipVelocity.y -= spider.room.gravity * 0.16f;
        tipVelocity *= 0.92f;
        tip += tipVelocity;

        if (Vector2.Distance(anchor, tip) > MaxLength)
        {
            tip = anchor + (tip - anchor).normalized * MaxLength;
            tipVelocity *= 0.55f;
        }

        segments[segments.Length - 1].pos = tip;
        AnimateInverseKinematics(tip, bodyAxis);
    }

    private void AnimateInverseKinematics(Vector2 tip, Vector2 bodyAxis)
    {
        Vector2 legVector = tip - anchor;
        float length = Mathf.Max(1f, legVector.magnitude);
        Vector2 direction = legVector / length;
        Vector2 bendNormal = Custom.PerpendicularVector(direction);

        // Near/far legs bend to opposite silhouettes. Alternate stations reverse the
        // secondary bend so ten legs do not overlap into one repeated zig-zag.
        float bendSign = ((Station + Layer) % 2 == 0) ? 1f : -1f;
        bendNormal *= bendSign;

        float extension = Mathf.Clamp01(length / MaxLength);
        float upperBend = Mathf.Lerp(34f, 14f, extension);
        float lowerBend = Mathf.Lerp(24f, 9f, extension);

        Vector2 jointA = anchor + legVector * 0.30f + bendNormal * upperBend;
        Vector2 jointB = anchor + legVector * 0.64f - bendNormal * lowerBend;

        SolveJoint(0, jointA, 0.58f);
        SolveJoint(1, jointB, 0.62f);
        segments[2].pos = tip;

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i].vel = segments[i].pos - segments[i].lastPos;
        }
    }

    private void SolveJoint(int index, Vector2 target, float stiffness)
    {
        Segment segment = segments[index];
        segment.pos = Vector2.Lerp(segment.pos, target, stiffness);
    }

    private void AnimateLimp(MossySpider spider)
    {
        Vector2 previous = anchor;
        float segmentLength = MaxLength / segments.Length * 0.72f;

        for (int i = 0; i < segments.Length; i++)
        {
            Segment segment = segments[i];
            segment.vel.y -= spider.room.gravity * 0.45f;
            segment.vel *= 0.96f;
            segment.pos += segment.vel;

            Vector2 delta = segment.pos - previous;
            if (delta.magnitude > segmentLength)
            {
                segment.pos = previous + delta.normalized * segmentLength;
                segment.vel *= 0.75f;
            }

            previous = segment.pos;
        }
    }
}
