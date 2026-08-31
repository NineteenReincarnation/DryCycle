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
/// Supporting feet are snapped back to the exact curve as well, because vanilla
/// DrillCrab.Leg.LandOnGround() keeps the first penetrating Tip position returned by
/// Contains() instead of the TerrainCurve snap point.
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
            // explicitly before correcting the feet so the creature gets the same
            // one-sided surface support it receives from ordinary authored terrain.
            CorrectBodyChunks(self);
            CorrectSupportingFeet(self);
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

            // Above the curve: ordinary leg suspension owns the body height. Only the
            // part of a BodyChunk that has crossed the quicksand surface is collision.
            if (bottomPenetration <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            // Recover already-embedded crabs from saves/tests produced by the old
            // compatibility code, but do not teleport a chunk that is genuinely below
            // the authored quicksand volume back through the entire zone.
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
                // TerrainCurve.SnapToTerrain is vertically resolved for these left-to-
                // right curves. A malformed/non-contact result should not alter motion.
                continue;
            }

            // Shift the interpolation history together with the current position. This
            // makes the correction a collision response, not a one-frame visual launch.
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

    private static void CorrectSupportingFeet(DrillCrab crab)
    {
        if (crab?.room == null || crab.legs == null)
        {
            return;
        }

        for (int i = 0; i < crab.legs.Length; i++)
        {
            DrillCrab.Leg leg = crab.legs[i];
            if (leg == null || !leg.Supporting || leg.Tip == null)
            {
                continue;
            }

            Vector2 foot = leg.Tip.pos;
            if (!TryGetNearbyQuicksand(crab.room, foot, out QuicksandZone zone))
            {
                continue;
            }

            Vector2 normal;
            Vector2 snapped = ((TerrainManager.ITerrain)zone).SnapToTerrain(
                foot,
                0f,
                out normal,
                leg.Tip.lastPos);

            float correctionY = snapped.y - foot.y;
            if (correctionY <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            // Move current and previous positions together so the correction is not
            // interpreted as an upward foot velocity on the following frame.
            Vector2 correction = Vector2.up * correctionY;
            leg.Tip.pos += correction;
            leg.Tip.lastPos += correction;
            leg.Tip.vel.y = 0f;

            // Seeking legs continue aiming at their planted target. Keep that target
            // on/above the corrected surface instead of immediately pulling the tip
            // back under the curve on the next IK pass.
            if (leg.mode == DrillCrab.Leg.Mode.Seeking && leg.targetPos.y < leg.Tip.pos.y)
            {
                leg.targetPos.y = leg.Tip.pos.y;
            }
        }
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

        // QuicksandZoneHooks moves immersed creatures into the Sand container for
        // curved clipping. DrillCrab is deliberately not an immersed creature, so
        // restore the graphics module's normal layer assignment after the generic
        // SpriteLeaser update chain has completed.
        self.AddSpritesToContainer(null, rCam);
    }
}
