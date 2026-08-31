using System;
using Watcher;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Watcher DrillCrabs treat QuicksandZone as ordinary curved terrain.
///
/// DrillCrab uses two different terrain systems:
/// - its legs query TerrainManager.ITerrain;
/// - its BodyChunks still use Rain World's normal BodyChunk collision path.
///
/// QuicksandZone deliberately reports no solid TerrainManager coverage to ordinary
/// creatures. During DrillCrab.Update we temporarily expose it as solid so the native
/// DrillCrab leg controller can acquire footholds. After the native update we also
/// resolve the two torso BodyChunks against the same curve. Without that second pass,
/// supporting legs can reduce DrillCrab gravity to zero only after the torso has already
/// fallen through the non-tile quicksand surface, leaving the whole animal permanently
/// embedded even though its feet are technically planted.
///
/// DrillCrab leg tips are clamped to the actual quicksand surface, not merely corrected
/// after Supporting becomes true. Vanilla landing detects penetration first and only then
/// calls LandOnGround(), so without this post-pass a seeking/planted foot can visibly sit
/// below the curve. The target is clamped as well so the native IK never keeps pulling a
/// planted foot back into the material on the following frame.
///
/// DrillCrabs are excluded visually from DryCycle's generic quicksand clipping: their
/// graphics stay in their native containers because this creature walks on top of
/// quicksand rather than being rendered as an immersed creature.
/// </summary>
internal static class QuicksandDrillCrabCompatibility
{
    private const float SurfaceCorrectionTolerance = 0.05f;
    private const float SurfaceSearchMargin = 36f;
    private const float BodyRecoveryExtraDepth = 80f;
    private const float FootSurfaceClearance = 2.5f;

    [ThreadStatic]
    private static int _terrainQueryDepth;

    private static bool _enabled;

    internal static bool TreatQuicksandAsSolidTerrain => _terrainQueryDepth > 0;

    internal static bool IsDrillCrab(Creature creature)
    {
        return creature is DrillCrab;
    }

    internal static void EnsureEnabled()
    {
        if (_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update += DrillCrab_Update;
        On.RoomCamera.SpriteLeaser.Update += SpriteLeaser_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Watcher.DrillCrab.Update -= DrillCrab_Update;
        On.RoomCamera.SpriteLeaser.Update -= SpriteLeaser_Update;
        _terrainQueryDepth = 0;
        _enabled = false;
    }

    private static void DrillCrab_Update(
        On.Watcher.DrillCrab.orig_Update orig,
        DrillCrab self,
        bool eu)
    {
        _terrainQueryDepth++;
        try
        {
            orig(self, eu);

            // Legs see QuicksandZone through TerrainManager while this scope is active,
            // but BodyChunk collision never consults TerrainManager. Resolve the torso
            // explicitly and then clamp every active leg tip/target to the same surface.
            CorrectBodyChunks(self);
            CorrectLegFeet(self);
        }
        finally
        {
            _terrainQueryDepth = Math.Max(0, _terrainQueryDepth - 1);
        }
    }

    private static void CorrectBodyChunks(DrillCrab crab)
    {
        if (crab?.room == null || crab.bodyChunks == null)
        {
            return;
        }

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            if (chunk == null ||
                !TryGetNearbyQuicksand(crab.room, chunk.pos, out QuicksandZone zone))
            {
                continue;
            }

            float u = zone.MaterialUAtWorldX(chunk.pos.x);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surface,
                    out _,
                    out _,
                    out float depthLength))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            float bottomPenetration = surface.y - (chunk.pos.y - radius);

            if (bottomPenetration <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            float centerDepth = surface.y - chunk.pos.y;
            if (centerDepth > depthLength + radius + BodyRecoveryExtraDepth)
            {
                continue;
            }

            Vector2 normal;
            Vector2 snapped = ((TerrainManager.ITerrain)zone).SnapToTerrain(
                chunk.pos,
                radius,
                out normal,
                chunk.lastPos);

            Vector2 correction = snapped - chunk.pos;
            if (correction.y <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            chunk.pos += correction;
            chunk.lastPos += correction;
            chunk.lastLastPos += correction;

            if (normal.sqrMagnitude > 0.0001f)
            {
                normal.Normalize();
                if (normal.y < 0f)
                {
                    normal = -normal;
                }

                float intoSurface = Vector2.Dot(chunk.vel, normal);
                if (intoSurface < 0f)
                {
                    chunk.vel -= normal * intoSurface;
                }
            }
            else if (chunk.vel.y < 0f)
            {
                chunk.vel.y = 0f;
            }
        }
    }

    private static void CorrectLegFeet(DrillCrab crab)
    {
        if (crab?.room == null || crab.legs == null)
        {
            return;
        }

        for (int i = 0; i < crab.legs.Length; i++)
        {
            DrillCrab.Leg leg = crab.legs[i];
            if (leg == null ||
                leg.Tip == null ||
                leg.mode == DrillCrab.Leg.Mode.Retracting)
            {
                continue;
            }

            // Clamp the destination first. This matters while Seeking: otherwise the
            // native IK can keep aiming several pixels inside the curve even after the
            // current Tip has been corrected.
            if (leg.mode == DrillCrab.Leg.Mode.Seeking)
            {
                ClampLegTargetToSurface(crab.room, leg);
            }

            if (!TryGetNearbyQuicksand(crab.room, leg.Tip.pos, out QuicksandZone zone) ||
                !TryGetFootSurface(zone, leg.Tip.pos, out Vector2 desiredFoot, out Vector2 inward))
            {
                continue;
            }

            // Positive means the tip is on the material side of the desired contact
            // plane. Keep a tiny clearance because DrillCrab's rendered foot has width;
            // an exact center-on-curve contact still looks visually buried.
            float penetration = Vector2.Dot(leg.Tip.pos - desiredFoot, inward);
            if (penetration <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            Vector2 correction = -inward * penetration;
            leg.Tip.pos += correction;
            leg.Tip.lastPos += correction;

            float inwardVelocity = Vector2.Dot(leg.Tip.vel, inward);
            if (inwardVelocity > 0f)
            {
                leg.Tip.vel -= inward * inwardVelocity;
            }

            if (leg.Supporting)
            {
                leg.Tip.vel = Vector2.zero;
            }

            // A supporting DrillCrab leg normally remains in Seeking mode. Keep the
            // planted target at least as high as the corrected contact so the next
            // AnimateInverseKinematics pass cannot re-introduce penetration.
            if (leg.mode == DrillCrab.Leg.Mode.Seeking)
            {
                ClampLegTargetToSurface(crab.room, leg);
            }
        }
    }

    private static void ClampLegTargetToSurface(Room room, DrillCrab.Leg leg)
    {
        if (room == null || leg == null ||
            !TryGetNearbyQuicksand(room, leg.targetPos, out QuicksandZone zone) ||
            !TryGetFootSurface(zone, leg.targetPos, out Vector2 desiredTarget, out Vector2 inward))
        {
            return;
        }

        float penetration = Vector2.Dot(leg.targetPos - desiredTarget, inward);
        if (penetration > SurfaceCorrectionTolerance)
        {
            leg.targetPos -= inward * penetration;
        }
    }

    private static bool TryGetFootSurface(
        QuicksandZone zone,
        Vector2 point,
        out Vector2 desiredPoint,
        out Vector2 inward)
    {
        desiredPoint = point;
        inward = Vector2.down;

        if (zone == null || zone.Data == null)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(point.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surface,
                out _,
                out inward,
                out _))
        {
            return false;
        }

        if (inward.sqrMagnitude < 0.0001f)
        {
            inward = Vector2.down;
        }
        else
        {
            inward.Normalize();
        }

        if (inward.y > 0f)
        {
            inward = -inward;
        }

        desiredPoint = surface - inward * FootSurfaceClearance;
        return true;
    }

    private static bool TryGetNearbyQuicksand(
        Room room,
        Vector2 point,
        out QuicksandZone bestZone)
    {
        bestZone = null;
        if (room?.updateList == null)
        {
            return false;
        }

        float bestVerticalDistance = float.PositiveInfinity;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone ||
                zone.slatedForDeletetion ||
                zone.PlacedObject == null ||
                !zone.PlacedObject.active ||
                zone.Data == null ||
                point.x < zone.startX ||
                point.x > zone.endX)
            {
                continue;
            }

            float u = zone.MaterialUAtWorldX(point.x);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surface,
                    out _,
                    out _,
                    out float depthLength))
            {
                continue;
            }

            float verticalDistance = surface.y - point.y;
            if (verticalDistance < -SurfaceSearchMargin ||
                verticalDistance > depthLength + SurfaceSearchMargin + BodyRecoveryExtraDepth)
            {
                continue;
            }

            float absDistance = Mathf.Abs(verticalDistance);
            if (absDistance < bestVerticalDistance)
            {
                bestVerticalDistance = absDistance;
                bestZone = zone;
            }
        }

        return bestZone != null;
    }

    private static void SpriteLeaser_Update(
        On.RoomCamera.SpriteLeaser.orig_Update orig,
        RoomCamera.SpriteLeaser self,
        float timeStacker,
        RoomCamera rCam,
        Vector2 camPos)
    {
        orig(self, timeStacker, rCam, camPos);

        if (self?.drawableObject == null || self.sprites == null || rCam?.room == null)
        {
            return;
        }

        DrillCrab crab = null;
        if (self.drawableObject is GraphicsModule graphicsModule)
        {
            crab = graphicsModule.owner as DrillCrab;
        }
        else if (self.drawableObject is DrillCrab directCrab)
        {
            crab = directCrab;
        }

        if (crab == null || crab.room != rCam.room)
        {
            return;
        }

        self.AddSpritesToContainer(null, rCam);
    }
}
