using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Physical support state for one MossySpider leg.
///
/// The leg is anchored to a real torso BodyChunk, but the leg segments themselves are
/// intentionally not BodyChunks. A ten-leg creature built from multi-chunk leg chains
/// would be extremely unstable and expensive. Instead, each leg owns a terrain foot
/// contact and applies spring support back into its base torso chunk, similar in spirit
/// to Rain World's large procedural-legged creatures.
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
                // Ground is normally static, so this mostly removes one-pixel raycast
                // jitter while still following moving/edited terrain reasonably fast.
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

    internal void ApplySupport(MossySpider spider)
    {
        if (Support <= 0.001f || !Planted)
        {
            return;
        }

        BodyChunk chunk = BaseChunk(spider);
        Vector2 hip = PhysicalHip(spider);

        // A pair of legs is attached to each of five torso stations. At neutral
        // extension each leg contributes roughly half of the creature's local gravity;
        // compression adds extra lift and vertical velocity supplies damping.
        float height = Mathf.Max(0f, hip.y - FootPos.y);
        float compression = RestLength - height;
        float verticalForce = 0.46f + compression * 0.045f - chunk.vel.y * 0.15f;
        verticalForce = Mathf.Clamp(verticalForce, -0.22f, 1.18f) * Support;
        chunk.vel.y += verticalForce;

        // Keep a planted leg from behaving like a frictionless telescoping strut.
        // This is deliberately weak; actual walking propulsion belongs to locomotion.
        float horizontalError = FootPos.x - (hip.x + ReachOffset * 0.45f);
        float horizontalForce = Mathf.Clamp(
            horizontalError * 0.006f - chunk.vel.x * 0.035f,
            -0.22f,
            0.22f);
        chunk.vel.x += horizontalForce * Support;
    }

    internal Vector2 DrawFoot(float timeStacker) =>
        Vector2.Lerp(LastFootPos, FootPos, timeStacker);
}
