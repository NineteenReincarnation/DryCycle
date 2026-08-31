using System;
using Watcher;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Watcher DrillCrabs treat QuicksandZone as ordinary curved terrain.
///
/// There are two separate compatibility problems to solve:
/// 1. During DrillCrab.Update, TerrainManager queries made by the body and legs must
///    see the quicksand section as solid TerrainCurve geometry.
/// 2. A DrillCrab leg decides that it has landed from TerrainManager.Contains(), but
///    vanilla LandOnGround() keeps the already-penetrating Tip position instead of the
///    TerrainCurve snap point. On a soft-looking curve this can put a newly planted
///    foot visibly below the surface and make the whole gait read as sinking.
///
/// DrillCrabs are also excluded visually from DryCycle's generic quicksand clipping:
/// their graphics stay in their native containers because this creature is supposed
/// to walk on top of quicksand, not be rendered as an immersed creature.
/// </summary>
internal static class QuicksandDrillCrabCompatibility
{
    private const float SurfaceCorrectionTolerance = 0.05f;
    private const float SurfaceSearchMargin = 36f;

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

            // Contains() only reports that a leg tip crossed into TerrainCurve; it
            // does not return the corrected surface position used by SnapToTerrain().
            // Correct supporting feet immediately while the quicksand terrain is still
            // exposed as solid to TerrainManager. This preserves the native DrillCrab
            // gait but prevents each new step from accumulating visible penetration.
            CorrectSupportingFeet(self);
        }
        finally
        {
            _terrainQueryDepth = Math.Max(0, _terrainQueryDepth - 1);
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
                verticalDistance > depthLength + SurfaceSearchMargin)
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
