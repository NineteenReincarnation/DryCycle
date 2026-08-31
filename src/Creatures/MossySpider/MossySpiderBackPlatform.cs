using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Turns the moss-covered dorsal surface into a one-way moving floor for players.
///
/// This deliberately hooks BodyChunk.CheckVerticalCollision instead of applying a
/// Player.Update position lock. Rain World's ordinary floor semantics therefore stay
/// authoritative: a player only lands while approaching the surface from above, the
/// contacting BodyChunk receives ContactPoint.y == -1, upward jumps are untouched, and
/// goThroughFloors still lets the player drop through.
///
/// Horizontal carrying is handled separately from collision. When the MossySpider has
/// already updated this frame, the material-point displacement of the back is added to
/// the player's intended X position. When the spider updates later, BodyChunk's native
/// MoveWithOtherObject / Room.chunkGlue path carries the player after the carrier moves.
/// This keeps an idle player riding with the creature without welding player velocity to
/// the MossySpider or preventing ordinary walking on top of it.
/// </summary>
internal static class MossySpiderBackPlatform
{
    private const float WalkableStartU = 0.08f;
    private const float WalkableEndU = 0.92f;
    private const int SurfaceSearchSamples = 32;
    private const float SurfaceClearance = 1.5f;
    private const float ContactTolerance = 4f;
    private const float PreviousSideTolerance = 3f;
    private const float MinimumStandableNormalY = 0.55f;

    private static bool _enabled;

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

        // CheckVerticalCollision is called after BodyChunk has already integrated vel
        // into pos. Keep that uncorrected position: it is the player's intended motion
        // this frame and is what we should preserve while adding carrier displacement.
        Vector2 intendedPos = self.pos;
        Vector2 previousPos = self.lastPos;
        float terrainRadius = self.TerrainRad;

        orig(self);

        // Treat the dorsal surface as a one-way Floor tile, not as solid creature
        // collision. Player's ordinary drop-through state therefore remains valid.
        if (self.goThroughFloors || player.room == null)
        {
            return;
        }

        float previousBottom = previousPos.y - terrainRadius;
        float intendedBottom = intendedPos.y - terrainRadius;

        if (!TryFindBackContact(
                player.room,
                intendedPos.x,
                previousBottom,
                intendedBottom,
                out BackContact contact))
        {
            return;
        }

        float targetCenterY = contact.CurrentPoint.y + terrainRadius + SurfaceClearance;

        // Native tile/TerrainCurve collision may already have landed this BodyChunk on
        // something above the MossySpider. Never pull a player down through real terrain
        // merely because a back surface also exists below the same X coordinate.
        if (self.contactPoint.y == -1 && self.pos.y > targetCenterY + ContactTolerance)
        {
            return;
        }

        Vector2 platformVelocity = contact.CurrentPoint - contact.PreviousPoint;
        Vector2 relativeVelocity = self.vel - platformVelocity;
        float intoBack = Vector2.Dot(relativeVelocity, contact.Normal);
        if (intoBack < 0f)
        {
            // Equivalent to the normal-removal part of BodyChunk's TerrainCurve floor
            // response, but in the platform's moving reference frame.
            self.vel -= contact.Normal * intoBack;
        }

        self.pos.y = targetCenterY;
        self.contactPoint.y = -1;

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

        // PhysicalObject calls base.Update(eu) only after its BodyChunks have updated,
        // so playerChunk.owner.evenUpdate still contains the PREVIOUS frame here. The
        // game's current evenUpdate is the authoritative update-order marker.
        bool currentEu = playerChunk.owner.room?.game != null
            ? playerChunk.owner.room.game.evenUpdate
            : !playerChunk.owner.evenUpdate;
        bool carrierAlreadyUpdated = carrier.owner.evenUpdate == currentEu;

        if (carrierAlreadyUpdated)
        {
            // The precise dorsal material point includes torso bending, not merely the
            // nearest BodyChunk translation. Use that exact displacement when available.
            float carryX = contact.CurrentPoint.x - contact.PreviousPoint.x;
            playerChunk.pos.x = intendedPos.x + carryX;
            playerChunk.pos.y = targetCenterY;
            return;
        }

        // The carrier has not updated yet. BodyChunk.MoveWithOtherObject is designed for
        // exactly this update-order problem: Room.chunkGlue applies this stored relative
        // position after the MossySpider has moved later in the room update.
        Vector2 relativePosition = new(
            intendedPos.x - carrier.pos.x,
            targetCenterY - carrier.pos.y);

        playerChunk.MoveWithOtherObject(
            currentEu,
            carrier,
            relativePosition);
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
            if (room.updateList[i] is not MossySpider spider ||
                spider.slatedForDeletetion ||
                spider.room != room ||
                spider.bodyChunks == null ||
                spider.bodyChunks.Length < 2)
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

            if (normal.y < MinimumStandableNormalY)
            {
                continue;
            }

            // One-way vertical collision: last frame the player's bottom must not have
            // been meaningfully below this same material point, and this frame the
            // integrated bottom must have reached/crossed it. This mirrors the essential
            // from-above test inside BodyChunk.CheckVerticalCollision.
            float previousRelative = previousBottom - previousPoint.y;
            float currentRelative = intendedBottom - currentPoint.y;
            if (previousRelative < -PreviousSideTolerance ||
                currentRelative > ContactTolerance)
            {
                continue;
            }

            // If several folded pieces overlap the same world X, stand on the highest
            // dorsal surface rather than being snapped through it to a lower segment.
            if (found && currentPoint.y <= bestSurfaceY)
            {
                continue;
            }

            int carrierIndex = Mathf.Clamp(
                Mathf.RoundToInt(u * (spider.bodyChunks.Length - 1)),
                0,
                spider.bodyChunks.Length - 1);

            bestSurfaceY = currentPoint.y;
            bestContact = new BackContact(
                spider,
                currentPoint,
                previousPoint,
                normal,
                u,
                spider.bodyChunks[carrierIndex]);
            found = true;
        }

        return found;
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

        float previousU = WalkableStartU;
        Vector2 segmentA = BackPoint(spider, previousU, previousFrame: false);
        bool found = false;
        float highestY = float.NegativeInfinity;

        for (int i = 1; i <= SurfaceSearchSamples; i++)
        {
            float u = Mathf.Lerp(
                WalkableStartU,
                WalkableEndU,
                i / (float)SurfaceSearchSamples);
            Vector2 segmentB = BackPoint(spider, u, previousFrame: false);

            float minX = Mathf.Min(segmentA.x, segmentB.x);
            float maxX = Mathf.Max(segmentA.x, segmentB.x);
            if (worldX >= minX - 0.01f && worldX <= maxX + 0.01f)
            {
                float dx = segmentB.x - segmentA.x;
                float t = Mathf.Abs(dx) > 0.001f
                    ? Mathf.Clamp01((worldX - segmentA.x) / dx)
                    : 0.5f;

                float candidateU = Mathf.Lerp(previousU, u, t);
                Vector2 candidatePoint = Vector2.Lerp(segmentA, segmentB, t);
                Vector2 candidateNormal = BackNormal(spider, candidateU, previousFrame: false);

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

    private static Vector2 BackPoint(MossySpider spider, float u, bool previousFrame)
    {
        Vector2 body = SmoothBodyPoint(spider, u, previousFrame);
        Vector2 tangent = BackTangent(spider, u, previousFrame);
        Vector2 normal = PerpendicularUp(tangent);
        float idle = previousFrame ? spider.LastIdleMotion : spider.IdleMotion;

        float towardCenter = u < 0.5f ? 1f : -1f;
        Vector2 mossPosition = body + tangent * (MossInset(u) * towardCenter);
        float height = ShellTop(u, idle) + Cap(u, idle) - 1.5f;
        return mossPosition + normal * height;
    }

    private static Vector2 BackNormal(MossySpider spider, float u, bool previousFrame)
    {
        return PerpendicularUp(BackTangent(spider, u, previousFrame));
    }

    private static Vector2 BackTangent(MossySpider spider, float u, bool previousFrame)
    {
        const float delta = 0.0125f;
        Vector2 before = SmoothBodyPoint(
            spider,
            Mathf.Max(WalkableStartU, u - delta),
            previousFrame);
        Vector2 after = SmoothBodyPoint(
            spider,
            Mathf.Min(WalkableEndU, u + delta),
            previousFrame);

        Vector2 tangent = after - before;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            BodyChunk first = spider.bodyChunks[0];
            BodyChunk last = spider.bodyChunks[spider.bodyChunks.Length - 1];
            tangent = ChunkPoint(last, previousFrame) - ChunkPoint(first, previousFrame);
        }

        if (tangent.sqrMagnitude < 0.0001f)
        {
            tangent = Vector2.right;
        }

        return tangent.normalized;
    }

    private static Vector2 SmoothBodyPoint(MossySpider spider, float u, bool previousFrame)
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

        if (normal.sqrMagnitude < 0.0001f)
        {
            return Vector2.up;
        }

        return normal.normalized;
    }

    // Keep the collision surface in sync with MossySpiderGraphics' green cap profile.
    private static float MossInset(float u)
    {
        float end = 1f - Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI);
        return 4f + 16f * end * end;
    }

    private static float Profile(float u)
    {
        float a = Mathf.Max(0f, Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI));
        return 0.38f + 0.62f * Mathf.Pow(a, 0.58f);
    }

    private static float ShellTop(float u, float idle)
    {
        return Mathf.Lerp(7f, 13f, Profile(u)) +
               Mathf.Sin(idle * 0.43f + u * 3.1f) * 0.35f;
    }

    private static float Cap(float u, float idle)
    {
        return Mathf.Lerp(13f, 25f, Profile(u)) +
               Mathf.Sin(u * 19.1f + 0.7f) * 1.25f +
               Mathf.Sin(u * 31.7f + 2.1f) * 0.65f +
               Mathf.Sin(idle * 0.31f + u * 5.3f) * 0.35f;
    }
}
