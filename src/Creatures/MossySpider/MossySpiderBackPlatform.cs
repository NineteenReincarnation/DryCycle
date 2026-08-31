using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Makes the moss-covered dorsal surface a one-way moving floor for players.
///
/// The first contact is resolved inside BodyChunk.CheckVerticalCollision, matching Rain
/// World's ordinary floor semantics. A small per-BodyChunk rider state then keeps an
/// already-standing player attached across carrier update order, small downward body
/// motions and the curved transition between dorsal samples. Jumping or dropping through
/// immediately releases that state.
/// </summary>
internal static class MossySpiderBackPlatform
{
    private const int SurfaceSearchSamples = 96;
    private const float SurfaceClearance = 1.5f;
    private const float ContactTolerance = 5f;
    private const float PreviousSideTolerance = 4f;
    private const float MinimumStandableNormalY = 0.48f;
    private const float RideRetentionAbove = 18f;
    private const float RideRetentionBelow = 10f;
    private const float NearUSearchRadius = 0.12f;
    private const float NearUSearchMaxX = 16f;
    private const float JumpReleaseRelativeSpeed = 1.15f;

    private sealed class RiderState
    {
        internal bool Active;
        internal MossySpider Spider;
        internal float U;
    }

    private readonly struct BackContact
    {
        internal readonly MossySpider Spider;
        internal readonly Vector2 CurrentPoint;
        internal readonly Vector2 PreviousPoint;
        internal readonly Vector2 Normal;
        internal readonly float U;
        internal readonly BodyChunk CarrierChunk;

        internal BackContact(
            MossySpider spider,
            Vector2 currentPoint,
            Vector2 previousPoint,
            Vector2 normal,
            float u,
            BodyChunk carrierChunk)
        {
            Spider = spider;
            CurrentPoint = currentPoint;
            PreviousPoint = previousPoint;
            Normal = normal;
            U = u;
            CarrierChunk = carrierChunk;
        }
    }

    private static readonly ConditionalWeakTable<BodyChunk, RiderState> RiderStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.BodyChunk.CheckVerticalCollision += BodyChunk_CheckVerticalCollision;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.BodyChunk.CheckVerticalCollision -= BodyChunk_CheckVerticalCollision;
        _enabled = false;
    }

    private static void BodyChunk_CheckVerticalCollision(
        On.BodyChunk.orig_CheckVerticalCollision orig,
        BodyChunk self)
    {
        if (self?.owner is not Player player ||
            player.room == null ||
            player.bodyChunks == null ||
            player.enteringShortCut.HasValue)
        {
            orig(self);
            return;
        }

        RiderState rider = RiderStates.GetOrCreateValue(self);
        if (rider.Active && !ValidSpider(rider.Spider, player.room))
        {
            Deactivate(rider);
        }

        // BodyChunk.Update has already integrated velocity into pos before this method.
        // Preserve that uncorrected position so walking input is not lost when carrier
        // displacement is added later.
        Vector2 intendedPos = self.pos;
        Vector2 previousPos = self.lastPos;
        float terrainRadius = self.TerrainRad;

        orig(self);

        if (self.goThroughFloors || player.room == null || player.enteringShortCut.HasValue)
        {
            Deactivate(rider);
            return;
        }

        float previousBottom = previousPos.y - terrainRadius;
        float intendedBottom = intendedPos.y - terrainRadius;

        bool found = TryFindBackContact(
            player.room,
            intendedPos.x,
            previousBottom,
            intendedBottom,
            out BackContact contact);

        // CheckVerticalCollision's normal from-above crossing test is ideal for landing,
        // but is not enough for a moving creature. If the back moves downward several
        // pixels between frames, a stationary player can be above it without technically
        // crossing it. Retain the previous ride contact through that small separation.
        if (!found && rider.Active)
        {
            found = TryRetainBackContact(
                rider,
                player.room,
                intendedPos.x,
                previousBottom,
                intendedBottom,
                out contact);
        }

        if (!found)
        {
            Deactivate(rider);
            return;
        }

        Vector2 platformVelocity = contact.CurrentPoint - contact.PreviousPoint;
        Vector2 relativeVelocity = self.vel - platformVelocity;

        // A deliberate upward jump must leave the one-way platform. The retained rider
        // state is only for passive separation caused by the carrier moving under the
        // player, never for pinning an ascending player to the moss.
        if (rider.Active &&
            Vector2.Dot(relativeVelocity, contact.Normal) > JumpReleaseRelativeSpeed &&
            intendedBottom >= contact.CurrentPoint.y - 1f)
        {
            Deactivate(rider);
            return;
        }

        float targetCenterY = contact.CurrentPoint.y + terrainRadius + SurfaceClearance;

        // If native collision already found a real floor clearly above the MossySpider,
        // keep the real floor. This prevents the moving platform from pulling the player
        // down through room terrain where both surfaces overlap in X.
        if (self.contactPoint.y == -1 && self.pos.y > targetCenterY + ContactTolerance)
        {
            Deactivate(rider);
            return;
        }

        float intoBack = Vector2.Dot(relativeVelocity, contact.Normal);
        if (intoBack < 0f)
        {
            self.vel -= contact.Normal * intoBack;
        }

        self.pos.y = targetCenterY;
        self.contactPoint.y = -1;

        rider.Active = true;
        rider.Spider = contact.Spider;
        rider.U = contact.U;

        CarryWithBack(self, intendedPos, targetCenterY, contact);
    }

    private static void CarryWithBack(
        BodyChunk playerChunk,
        Vector2 intendedPos,
        float targetCenterY,
        BackContact contact)
    {
        BodyChunk carrier = contact.CarrierChunk;
        if (carrier == null || carrier.owner == null)
        {
            return;
        }

        bool currentEu = playerChunk.owner.room?.game != null
            ? playerChunk.owner.room.game.evenUpdate
            : !playerChunk.owner.evenUpdate;
        bool carrierAlreadyUpdated = carrier.owner.evenUpdate == currentEu;

        if (carrierAlreadyUpdated)
        {
            float carryX = contact.CurrentPoint.x - contact.PreviousPoint.x;
            playerChunk.pos.x = intendedPos.x + carryX;
            playerChunk.pos.y = targetCenterY;
            return;
        }

        Vector2 relativePosition = new(
            intendedPos.x - carrier.pos.x,
            targetCenterY - carrier.pos.y);

        playerChunk.MoveWithOtherObject(currentEu, carrier, relativePosition);
    }

    private static bool TryFindBackContact(
        Room room,
        float worldX,
        float previousBottom,
        float intendedBottom,
        out BackContact bestContact)
    {
        bestContact = default;
        if (room?.updateList == null)
        {
            return false;
        }

        bool found = false;
        float bestSurfaceY = float.NegativeInfinity;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not MossySpider spider || !ValidSpider(spider, room))
            {
                continue;
            }

            if (!TrySurfaceAtX(
                    spider,
                    worldX,
                    out float u,
                    out Vector2 currentPoint,
                    out Vector2 previousPoint,
                    out Vector2 normal))
            {
                continue;
            }

            float previousRelative = previousBottom - previousPoint.y;
            float currentRelative = intendedBottom - currentPoint.y;
            if (previousRelative < -PreviousSideTolerance ||
                currentRelative > ContactTolerance)
            {
                continue;
            }

            if (found && currentPoint.y <= bestSurfaceY)
            {
                continue;
            }

            bestSurfaceY = currentPoint.y;
            bestContact = MakeContact(spider, u, currentPoint, previousPoint, normal);
            found = true;
        }

        return found;
    }

    private static bool TryRetainBackContact(
        RiderState rider,
        Room room,
        float worldX,
        float previousBottom,
        float intendedBottom,
        out BackContact contact)
    {
        contact = default;
        MossySpider spider = rider.Spider;
        if (!ValidSpider(spider, room))
        {
            return false;
        }

        bool found = TrySurfaceAtX(
            spider,
            worldX,
            out float u,
            out Vector2 currentPoint,
            out Vector2 previousPoint,
            out Vector2 normal);

        // A sharply bent torso can make the sampled dorsal curve locally vertical in X.
        // Preserve contact around the previous material coordinate instead of creating a
        // one-frame hole in the platform at that bend.
        if (!found)
        {
            found = TrySurfaceNearU(
                spider,
                rider.U,
                worldX,
                out u,
                out currentPoint,
                out previousPoint,
                out normal);
        }

        if (!found)
        {
            return false;
        }

        float currentRelative = intendedBottom - currentPoint.y;
        float previousRelative = previousBottom - previousPoint.y;

        if (currentRelative > RideRetentionAbove ||
            currentRelative < -RideRetentionBelow ||
            previousRelative < -RideRetentionBelow)
        {
            return false;
        }

        contact = MakeContact(spider, u, currentPoint, previousPoint, normal);
        return true;
    }

    private static bool TrySurfaceAtX(
        MossySpider spider,
        float worldX,
        out float bestU,
        out Vector2 bestPoint,
        out Vector2 previousPoint,
        out Vector2 bestNormal)
    {
        bestU = 0f;
        bestPoint = Vector2.zero;
        previousPoint = Vector2.zero;
        bestNormal = Vector2.up;

        float previousU = MossySpiderSilhouette.WalkableStartU;
        Vector2 segmentA = BackPoint(spider, previousU, previousFrame: false);
        bool found = false;
        float highestY = float.NegativeInfinity;

        for (int i = 1; i <= SurfaceSearchSamples; i++)
        {
            float u = Mathf.Lerp(
                MossySpiderSilhouette.WalkableStartU,
                MossySpiderSilhouette.WalkableEndU,
                i / (float)SurfaceSearchSamples);
            Vector2 segmentB = BackPoint(spider, u, previousFrame: false);

            float minX = Mathf.Min(segmentA.x, segmentB.x);
            float maxX = Mathf.Max(segmentA.x, segmentB.x);
            if (worldX >= minX - 0.05f && worldX <= maxX + 0.05f)
            {
                float dx = segmentB.x - segmentA.x;
                float t = Mathf.Abs(dx) > 0.001f
                    ? Mathf.Clamp01((worldX - segmentA.x) / dx)
                    : 0.5f;

                float candidateU = Mathf.Lerp(previousU, u, t);
                Vector2 candidatePoint = Vector2.Lerp(segmentA, segmentB, t);
                Vector2 candidateNormal = BackNormal(
                    spider,
                    candidateU,
                    previousFrame: false);

                if (candidateNormal.y >= MinimumStandableNormalY &&
                    (!found || candidatePoint.y > highestY))
                {
                    found = true;
                    highestY = candidatePoint.y;
                    bestU = candidateU;
                    bestPoint = candidatePoint;
                    previousPoint = BackPoint(spider, candidateU, previousFrame: true);
                    bestNormal = candidateNormal;
                }
            }

            previousU = u;
            segmentA = segmentB;
        }

        return found;
    }

    private static bool TrySurfaceNearU(
        MossySpider spider,
        float centerU,
        float worldX,
        out float bestU,
        out Vector2 bestPoint,
        out Vector2 previousPoint,
        out Vector2 bestNormal)
    {
        bestU = 0f;
        bestPoint = Vector2.zero;
        previousPoint = Vector2.zero;
        bestNormal = Vector2.up;

        float start = Mathf.Max(
            MossySpiderSilhouette.WalkableStartU,
            centerU - NearUSearchRadius);
        float end = Mathf.Min(
            MossySpiderSilhouette.WalkableEndU,
            centerU + NearUSearchRadius);

        bool found = false;
        float bestXDistance = float.PositiveInfinity;

        for (int i = 0; i <= 24; i++)
        {
            float u = Mathf.Lerp(start, end, i / 24f);
            Vector2 point = BackPoint(spider, u, previousFrame: false);
            float xDistance = Mathf.Abs(point.x - worldX);
            if (xDistance > NearUSearchMaxX || xDistance >= bestXDistance)
            {
                continue;
            }

            Vector2 normal = BackNormal(spider, u, previousFrame: false);
            if (normal.y < MinimumStandableNormalY)
            {
                continue;
            }

            found = true;
            bestXDistance = xDistance;
            bestU = u;
            bestPoint = point;
            previousPoint = BackPoint(spider, u, previousFrame: true);
            bestNormal = normal;
        }

        return found;
    }

    private static BackContact MakeContact(
        MossySpider spider,
        float u,
        Vector2 currentPoint,
        Vector2 previousPoint,
        Vector2 normal)
    {
        int carrierIndex = Mathf.Clamp(
            Mathf.RoundToInt(u * (spider.bodyChunks.Length - 1)),
            0,
            spider.bodyChunks.Length - 1);

        return new BackContact(
            spider,
            currentPoint,
            previousPoint,
            normal,
            u,
            spider.bodyChunks[carrierIndex]);
    }

    private static Vector2 BackPoint(
        MossySpider spider,
        float u,
        bool previousFrame)
    {
        Vector2 body = SmoothBodyPoint(spider, u, previousFrame);
        Vector2 bodyTangent = BodyTangent(spider, u, previousFrame);
        Vector2 bodyNormal = PerpendicularUp(bodyTangent);
        return body + bodyNormal * MossySpiderSilhouette.MossHigh(u);
    }

    private static Vector2 BackNormal(
        MossySpider spider,
        float u,
        bool previousFrame)
    {
        const float delta = 0.006f;
        float beforeU = Mathf.Max(MossySpiderSilhouette.WalkableStartU, u - delta);
        float afterU = Mathf.Min(MossySpiderSilhouette.WalkableEndU, u + delta);
        Vector2 tangent = BackPoint(spider, afterU, previousFrame) -
                          BackPoint(spider, beforeU, previousFrame);
        return PerpendicularUp(tangent);
    }

    private static Vector2 BodyTangent(
        MossySpider spider,
        float u,
        bool previousFrame)
    {
        const float delta = 0.0125f;
        Vector2 before = SmoothBodyPoint(
            spider,
            Mathf.Max(0f, u - delta),
            previousFrame);
        Vector2 after = SmoothBodyPoint(
            spider,
            Mathf.Min(1f, u + delta),
            previousFrame);

        Vector2 tangent = after - before;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            BodyChunk first = spider.bodyChunks[0];
            BodyChunk last = spider.bodyChunks[spider.bodyChunks.Length - 1];
            tangent = ChunkPoint(last, previousFrame) - ChunkPoint(first, previousFrame);
        }

        return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector2.right;
    }

    private static Vector2 SmoothBodyPoint(
        MossySpider spider,
        float u,
        bool previousFrame)
    {
        int count = spider.bodyChunks.Length;
        float x = Mathf.Clamp01(u) * (count - 1);
        int i1 = Mathf.Clamp(Mathf.FloorToInt(x), 0, count - 1);
        int i2 = Mathf.Min(count - 1, i1 + 1);
        int i0 = Mathf.Max(0, i1 - 1);
        int i3 = Mathf.Min(count - 1, i2 + 1);
        float t = x - Mathf.Floor(x);

        Vector2 p0 = ChunkPoint(spider.bodyChunks[i0], previousFrame);
        Vector2 p1 = ChunkPoint(spider.bodyChunks[i1], previousFrame);
        Vector2 p2 = ChunkPoint(spider.bodyChunks[i2], previousFrame);
        Vector2 p3 = ChunkPoint(spider.bodyChunks[i3], previousFrame);

        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * ((2f * p1) +
                       (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static Vector2 ChunkPoint(BodyChunk chunk, bool previousFrame)
    {
        return previousFrame ? chunk.lastPos : chunk.pos;
    }

    private static Vector2 PerpendicularUp(Vector2 tangent)
    {
        Vector2 normal = new(-tangent.y, tangent.x);
        if (normal.y < 0f)
        {
            normal = -normal;
        }

        return normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector2.up;
    }

    private static bool ValidSpider(MossySpider spider, Room room)
    {
        return spider != null &&
               !spider.slatedForDeletetion &&
               spider.room == room &&
               spider.bodyChunks != null &&
               spider.bodyChunks.Length >= 2;
    }

    private static void Deactivate(RiderState rider)
    {
        rider.Active = false;
        rider.Spider = null;
        rider.U = 0f;
    }
}
