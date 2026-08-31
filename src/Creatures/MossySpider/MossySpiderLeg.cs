using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Physical support state for one MossySpider leg.
///
/// Legs are anchored to real torso BodyChunks, while the leg segments themselves stay
/// procedural. Grounded legs act as spring supports; in deep water the same procedural
/// foot target transitions into a paddling stroke instead of searching endlessly for
/// unreachable terrain.
/// </summary>
internal sealed class MossySpiderLeg
{
    private const float MinGroundDistance = 12f;
    private const float ReplantDistance = 28f;

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

    internal BodyChunk BaseChunk(MossySpider spider) => spider.bodyChunks[BaseChunkIndex];

    internal Vector2 PhysicalHip(MossySpider spider)
    {
        BodyChunk chunk = BaseChunk(spider);
        return chunk.pos + Vector2.down * (chunk.rad * 0.34f);
    }

    internal void Reset(MossySpider spider, Vector2 bodyAxis)
    {
        Vector2 hip = PhysicalHip(spider);
        DesiredFootPos = hip + bodyAxis * ReachOffset + Vector2.down * RestLength;
        FootPos = DesiredFootPos;
        LastFootPos = FootPos;
        Planted = false;
        Support = 0f;
    }

    internal void UpdateContact(MossySpider spider, Vector2 bodyAxis)
    {
        LastFootPos = FootPos;

        if (spider.room == null || spider.dead)
        {
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.2f);
            return;
        }

        Vector2 hip = PhysicalHip(spider);

        // Once the body is clearly in deep-water mode, stop treating the leg as a
        // bottom-seeking strut. The alternating phase produces the visual/physical
        // paddling posture while torso propulsion remains owned by MossySpider.cs.
        if (spider.SwimFactor > 0.58f)
        {
            float phase = spider.IdleMotion * 1.55f +
                          Index * 1.37f +
                          (Index % 2 == 0 ? 0f : Mathf.PI * 0.72f);

            float stroke = Mathf.Sin(phase);
            float recovery = Mathf.Cos(phase);
            Vector2 forward = spider.MoveDirection.sqrMagnitude > 0.01f
                ? spider.MoveDirection.normalized
                : bodyAxis;

            DesiredFootPos = hip +
                             forward * (-stroke * 30f + ReachOffset * 0.28f) +
                             Vector2.down * (RestLength * 0.48f + recovery * 12f);

            FootPos = Vector2.Lerp(FootPos, DesiredFootPos, 0.20f);
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.22f);
            return;
        }

        Vector2 searchStart = hip + Vector2.up * 5f + bodyAxis * (ReachOffset * 0.12f);
        DesiredFootPos = hip + bodyAxis * ReachOffset + Vector2.down * MaxLength;

        Vector2? terrainHit = SharedPhysics.ExactTerrainRayTracePos(
            spider.room,
            searchStart,
            DesiredFootPos);

        bool validHit = terrainHit.HasValue &&
                        terrainHit.Value.y < hip.y - MinGroundDistance &&
                        Vector2.Distance(hip, terrainHit.Value) <= MaxLength + 18f;

        if (validHit)
        {
            Vector2 hit = terrainHit.Value;
            if (!Planted || Vector2.Distance(FootPos, hit) > ReplantDistance)
            {
                FootPos = hit;
            }
            else
            {
                FootPos = Vector2.Lerp(FootPos, hit, 0.35f);
            }

            Planted = true;
            Support = Mathf.MoveTowards(Support, 1f, 0.22f);
        }
        else
        {
            Planted = false;
            Support = Mathf.MoveTowards(Support, 0f, 0.18f);
            FootPos = Vector2.Lerp(FootPos, DesiredFootPos, 0.18f);
        }
    }

    internal void ApplySupport(MossySpider spider, float supportFactor = 1f)
    {
        if (Support <= 0.001f || !Planted || supportFactor <= 0.001f)
        {
            return;
        }

        BodyChunk chunk = BaseChunk(spider);
        Vector2 hip = PhysicalHip(spider);

        float height = Mathf.Max(0f, hip.y - FootPos.y);
        float compression = RestLength - height;
        float verticalForce = 0.46f + compression * 0.045f - chunk.vel.y * 0.15f;
        verticalForce = Mathf.Clamp(verticalForce, -0.22f, 1.18f) * Support * supportFactor;
        chunk.vel.y += verticalForce;

        float horizontalError = FootPos.x - (hip.x + ReachOffset * 0.45f);
        float horizontalForce = Mathf.Clamp(
            horizontalError * 0.006f - chunk.vel.x * 0.035f,
            -0.22f,
            0.22f);
        chunk.vel.x += horizontalForce * Support * supportFactor;
    }

    internal Vector2 DrawFoot(float timeStacker) =>
        Vector2.Lerp(LastFootPos, FootPos, timeStacker);
}
