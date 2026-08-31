using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// One procedural MossySpider leg.
///
/// A foot is a real world-space support point: while planted it stays where it was
/// placed instead of being re-raycast underneath the moving hip every frame. When the
/// torso travels far enough past that foot, the leg releases, swings to a new terrain
/// contact and plants again. This makes locomotion emerge from alternating supports
/// instead of ten decorative telescoping struts sliding with the body.
/// </summary>
internal sealed class MossySpiderLeg
{
    private const float MinGroundDistance = 12f;
    private const float GroundValidationDepth = 28f;
    private const float GroundValidationLift = 16f;
    private const float StepLead = 36f;
    private const float StepLift = 27f;

    private static readonly float[] SearchOffsets =
    [
        0f,
        -18f,
        18f,
        -36f,
        36f
    ];

    internal MossySpiderLeg(
        int index,
        int station,
        float bodyU,
        int baseChunkIndex,
        float restLength,
        float maxLength,
        float reachOffset)
    {
        Index = index;
        Station = station;
        BodyU = bodyU;
        BaseChunkIndex = baseChunkIndex;
        RestLength = restLength;
        MaxLength = maxLength;
        ReachOffset = reachOffset;
    }

    internal int Index { get; }
    internal int Station { get; }
    internal float BodyU { get; }
    internal int BaseChunkIndex { get; }
    internal float RestLength { get; }
    internal float MaxLength { get; }
    internal float ReachOffset { get; }

    internal Vector2 FootPos;
    internal Vector2 LastFootPos;
    internal Vector2 DesiredFootPos;
    internal bool Planted;
    internal float Support;

    internal bool Stepping { get; private set; }
    internal float StepProgress { get; private set; }

    private Vector2 stepStart;
    private Vector2 stepTarget;

    internal BodyChunk BaseChunk(MossySpider spider) => spider.bodyChunks[BaseChunkIndex];

    internal Vector2 PhysicalHip(MossySpider spider)
    {
        BodyChunk chunk = BaseChunk(spider);
        return chunk.pos + Vector2.down * (chunk.rad * 0.30f);
    }

    internal void Reset(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 hip = PhysicalHip(spider);
        DesiredFootPos = hip + bodyAxis * ReachOffset + Vector2.down * RestLength;
        FootPos = DesiredFootPos;
        LastFootPos = FootPos;
        stepStart = FootPos;
        stepTarget = FootPos;
        StepProgress = 0f;
        Stepping = false;
        Planted = false;
        Support = 0f;
    }

    internal void UpdateContact(MossySpider spider, Vector2 bodyAxis)
    {
        LastFootPos = FootPos;

        if (spider.room == null || spider.dead)
        {
            Stepping = false;
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.2f);
            return;
        }

        Vector2 hip = PhysicalHip(spider);

        // Deep water is a separate locomotion mode. Feet stop seeking terrain and
        // become paddles, with staggered phases so all ten legs never sweep together.
        if (spider.SwimFactor > 0.58f)
        {
            Stepping = false;
            StepProgress = 0f;

            float phase = spider.IdleMotion * 1.55f +
                          Index * 1.37f +
                          (Index % 2 == 0 ? 0f : Mathf.PI * 0.72f);

            float stroke = Mathf.Sin(phase);
            float recovery = Mathf.Cos(phase);
            Vector2 forward = spider.MoveDirection.sqrMagnitude > 0.01f
                ? spider.MoveDirection.normalized
                : bodyAxis;

            DesiredFootPos = hip +
                             forward * (-stroke * 34f + ReachOffset * 0.22f) +
                             Vector2.down * (RestLength * 0.48f + recovery * 13f);

            FootPos = Vector2.Lerp(FootPos, DesiredFootPos, 0.20f);
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.22f);
            return;
        }

        if (Stepping)
        {
            UpdateStep();
            return;
        }

        if (Planted)
        {
            ValidatePlantedFoot(spider, hip);
            return;
        }

        // Initial placement and recovery after losing a ledge are allowed to acquire
        // support immediately. Deliberate locomotion steps use BeginStep() below.
        if (TryFindGroundTarget(spider, bodyAxis, out Vector2 ground))
        {
            FootPos = ground;
            DesiredFootPos = ground;
            Planted = true;
            Support = Mathf.MoveTowards(Support, 1f, 0.24f);
        }
        else
        {
            DesiredFootPos = FreeHangingTarget(spider, bodyAxis);
            FootPos = Vector2.Lerp(FootPos, DesiredFootPos, 0.15f);
            Support = Mathf.MoveTowards(Support, 0f, 0.18f);
        }
    }

    internal float StepUrgency(MossySpider spider, Vector2 bodyAxis)
    {
        if (!Planted || Stepping || spider.SwimFactor > 0.45f)
        {
            return 0f;
        }

        Vector2 hip = PhysicalHip(spider);
        float moveAlongBody = Mathf.Clamp(Vector2.Dot(spider.MoveDirection, bodyAxis), -1f, 1f);
        float desiredReach = ReachOffset + moveAlongBody * StepLead;
        float currentReach = Vector2.Dot(FootPos - hip, bodyAxis);

        float reachError = Mathf.Abs(currentReach - desiredReach);
        float reachUrgency = Mathf.InverseLerp(22f, 52f, reachError);
        float extensionUrgency = Mathf.InverseLerp(
            MaxLength - 30f,
            MaxLength + 4f,
            Vector2.Distance(hip, FootPos));

        float movement = Mathf.Clamp01(Mathf.Abs(moveAlongBody) * 1.4f);
        float phase = Mathf.Repeat(spider.GaitCycle + Index * 0.371f, 1f);
        float gaitDue = Mathf.InverseLerp(0.76f, 1f, phase) * movement * 0.68f;

        return Mathf.Max(reachUrgency, extensionUrgency, gaitDue);
    }

    internal bool BeginStep(MossySpider spider, Vector2 bodyAxis)
    {
        if (!Planted || Stepping || spider.room == null)
        {
            return false;
        }

        if (!TryFindGroundTarget(spider, bodyAxis, out Vector2 target))
        {
            return false;
        }

        stepStart = FootPos;
        stepTarget = target;
        DesiredFootPos = target;
        StepProgress = 0f;
        Stepping = true;
        Planted = false;
        return true;
    }

    internal void ApplySupport(MossySpider spider, float supportFactor = 1f)
    {
        if (Support <= 0.001f || !Planted || supportFactor <= 0.001f)
        {
            return;
        }

        BodyChunk chunk = BaseChunk(spider);
        Vector2 hip = PhysicalHip(spider);

        // The leg's primary physical job is vertical support. Horizontal locomotion is
        // deliberately not spring-locked to ReachOffset; otherwise ten planted legs
        // cancel the AI drive and make the creature tread in place.
        float height = Mathf.Max(0f, hip.y - FootPos.y);
        float compression = RestLength - height;
        float verticalForce = 0.45f + compression * 0.041f - chunk.vel.y * 0.14f;
        verticalForce = Mathf.Clamp(verticalForce, -0.20f, 1.12f) * Support * supportFactor;
        chunk.vel.y += verticalForce;
    }

    internal Vector2 DrawFoot(float timeStacker) =>
        Vector2.Lerp(LastFootPos, FootPos, timeStacker);

    private void UpdateStep()
    {
        float frames = 14f + (Index % 3) * 2f;
        StepProgress = Mathf.Min(1f, StepProgress + 1f / frames);

        float t = StepProgress;
        float eased = t * t * (3f - 2f * t);
        Vector2 foot = Vector2.Lerp(stepStart, stepTarget, eased);
        foot.y += Mathf.Sin(t * Mathf.PI) * StepLift;
        FootPos = foot;
        Support = Mathf.MoveTowards(Support, 0f, 0.20f);

        if (StepProgress < 1f)
        {
            return;
        }

        FootPos = stepTarget;
        DesiredFootPos = stepTarget;
        Stepping = false;
        Planted = true;
        Support = Mathf.Max(Support, 0.22f);
    }

    private void ValidatePlantedFoot(MossySpider spider, Vector2 hip)
    {
        float distance = Vector2.Distance(hip, FootPos);
        if (FootPos.y >= hip.y - MinGroundDistance || distance > MaxLength + 24f)
        {
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.20f);
            return;
        }

        // Keep world X locked while allowing a few pixels of vertical correction for
        // moving/curved terrain. The old code recomputed X from the hip each frame,
        // which made every foot slide along with the torso.
        Vector2? hit = SharedPhysics.ExactTerrainRayTracePos(
            spider.room,
            FootPos + Vector2.up * GroundValidationLift,
            FootPos + Vector2.down * GroundValidationDepth);

        if (hit.HasValue && Mathf.Abs(hit.Value.y - FootPos.y) <= GroundValidationDepth)
        {
            FootPos.y = Mathf.Lerp(FootPos.y, hit.Value.y, 0.18f);
            DesiredFootPos = FootPos;
            Support = Mathf.MoveTowards(Support, 1f, 0.16f);
        }
        else
        {
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.20f);
        }
    }

    private bool TryFindGroundTarget(
        MossySpider spider,
        Vector2 bodyAxis,
        out Vector2 target)
    {
        target = Vector2.zero;
        Vector2 hip = PhysicalHip(spider);
        float moveAlongBody = Mathf.Clamp(Vector2.Dot(spider.MoveDirection, bodyAxis), -1f, 1f);
        float desiredReach = ReachOffset + moveAlongBody * StepLead;
        Vector2 desired = hip + bodyAxis * desiredReach;

        float bestScore = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < SearchOffsets.Length; i++)
        {
            float x = desired.x + SearchOffsets[i];
            Vector2 start = new(x, hip.y + 10f);
            Vector2 end = new(x, hip.y - MaxLength - 20f);
            Vector2? hit = SharedPhysics.ExactTerrainRayTracePos(spider.room, start, end);

            if (!hit.HasValue ||
                hit.Value.y >= hip.y - MinGroundDistance ||
                Vector2.Distance(hip, hit.Value) > MaxLength + 24f)
            {
                continue;
            }

            float score = Mathf.Abs(SearchOffsets[i]) * 0.7f +
                          Mathf.Abs((hip.y - hit.Value.y) - RestLength) * 0.20f;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            target = hit.Value;
            found = true;
        }

        return found;
    }

    private Vector2 FreeHangingTarget(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 hip = PhysicalHip(spider);
        float moveAlongBody = Mathf.Clamp(Vector2.Dot(spider.MoveDirection, bodyAxis), -1f, 1f);
        return hip +
               bodyAxis * (ReachOffset + moveAlongBody * StepLead * 0.45f) +
               Vector2.down * RestLength;
    }
}
