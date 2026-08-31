using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Makes the moss-covered dorsal surface a one-way moving floor for players.
///
/// The collision surface is one analytic straight segment from
/// MossySpiderDorsalPlane. It is not assembled from body-chunk samples, so there are no
/// internal seams between torso sections for a player to fall through.
/// </summary>
internal static class MossySpiderBackPlatform
{
    private const float SurfaceClearance = 1.5f;
    private const float ContactTolerance = 6f;
    private const float PreviousSideTolerance = 5f;
    private const float MinimumStandableNormalY = 0.35f;
    private const float RideRetentionAbove = 22f;
    private const float RideRetentionBelow = 14f;
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

        if (rider.Active &&
            Vector2.Dot(relativeVelocity, contact.Normal) > JumpReleaseRelativeSpeed &&
            intendedBottom >= contact.CurrentPoint.y - 1f)
        {
            Deactivate(rider);
            return;
        }

        float targetCenterY = contact.CurrentPoint.y + terrainRadius + SurfaceClearance;

        // Respect a real room floor if vanilla collision found one clearly above the
        // MossySpider plane.
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

            // From-above crossing against one continuous line. A fast downward frame is
            // accepted because currentRelative may be far below zero; only a chunk that
            // was already below the platform on the previous frame is rejected.
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
        if (!ValidSpider(spider, room) ||
            !TrySurfaceAtX(
                spider,
                worldX,
                out float u,
                out Vector2 currentPoint,
                out Vector2 previousPoint,
                out Vector2 normal))
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
        out float u,
        out Vector2 currentPoint,
        out Vector2 previousPoint,
        out Vector2 normal)
    {
        if (!MossySpiderDorsalPlane.TrySurfaceAtWorldX(
                spider,
                worldX,
                out u,
                out currentPoint,
                out previousPoint,
                out normal))
        {
            return false;
        }

        return normal.y >= MinimumStandableNormalY;
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
