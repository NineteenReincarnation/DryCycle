using System;
using RWCustom;
using Watcher;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Makes Watcher DrillCrabs treat QuicksandZone as a real one-sided walking surface.
///
/// DrillCrab uses TerrainManager for foot acquisition, but its torso BodyChunks use the
/// normal Rain World collision path and its intermediate leg joints are pure IK points.
/// A tip-only correction is therefore insufficient: the foot target can still be an
/// underlying tile and the IK joints can still bend below the visible quicksand curve.
///
/// During DrillCrab.Update the quicksand curve is exposed to TerrainManager. After the
/// vanilla update we resolve torso penetration, replace any foot target below quicksand
/// with the first quicksand surface at that X, rebuild the native leg IK around the
/// corrected tip, and finally keep every rendered leg joint on the air side of the curve.
/// </summary>
internal static class QuicksandDrillCrabCompatibility
{
    private const float SurfaceCorrectionTolerance = 0.05f;
    private const float SurfaceSearchMargin = 36f;
    private const float BodyRecoveryExtraDepth = 80f;
    private const float FootSurfaceClearance = 4f;
    private const float LegSegmentClearance = 7f;
    private const float FootAttachDistance = 12f;
    private const int SurfaceRaySamples = 64;

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
            CorrectBodyChunks(self);
            CorrectLegs(self);
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

    private static void CorrectLegs(DrillCrab crab)
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
                leg.segments == null ||
                leg.mode == DrillCrab.Leg.Mode.Retracting)
            {
                continue;
            }

            // ExactTerrainRayTracePos only supplies normal room terrain. If the scan is
            // currently over a QuicksandZone with no useful tile underneath, explicitly
            // acquire the first quicksand crossing so the leg does not continue downward.
            if (leg.mode == DrillCrab.Leg.Mode.Scanning)
            {
                TrySeedScanningTarget(crab.room, leg);
            }

            // A vanilla scan may already have found an underlying solid tile. Quicksand
            // is the first walkable surface, so replace that deep target by the surface
            // at the same X regardless of how far below the authored band the tile was.
            if (leg.mode == DrillCrab.Leg.Mode.Seeking)
            {
                ClampLegTargetToSurface(crab.room, leg);
            }

            CorrectLegTip(crab.room, leg);

            if (leg.mode == DrillCrab.Leg.Mode.Seeking &&
                TryGetQuicksandSurfaceAtX(
                    crab.room,
                    leg.targetPos.x,
                    out _,
                    out Vector2 targetSurface,
                    out Vector2 targetInward))
            {
                Vector2 desiredTarget = targetSurface - targetInward * FootSurfaceClearance;
                leg.targetPos = desiredTarget;

                // Because the corrected target is deliberately on the air side of the
                // curve, vanilla Contains() no longer needs to observe penetration to
                // establish support. Attach when the seeking tip reaches that contact.
                if (!leg.Supporting &&
                    Vector2.Distance(leg.Tip.pos, desiredTarget) <= FootAttachDistance)
                {
                    leg.Tip.pos = desiredTarget;
                    leg.Tip.lastPos = desiredTarget;
                    leg.Tip.vel = Vector2.zero;
                    leg.Supporting = true;
                }
            }

            if (leg.Supporting &&
                TryGetQuicksandSurfaceAtX(
                    crab.room,
                    leg.Tip.pos.x,
                    out _,
                    out Vector2 plantedSurface,
                    out Vector2 plantedInward))
            {
                Vector2 planted = plantedSurface - plantedInward * FootSurfaceClearance;
                leg.Tip.pos = planted;
                leg.Tip.lastPos = planted;
                leg.Tip.vel = Vector2.zero;
                if (leg.mode == DrillCrab.Leg.Mode.Seeking)
                {
                    leg.targetPos = planted;
                }
            }

            // Vanilla DoInverseKinematics ran before the post-pass above, so correcting
            // only Tip leaves the intermediate segments at their old below-surface pose.
            // Re-run the creature's own IK using the corrected foot, then constrain every
            // rendered joint to the air side of quicksand.
            RebuildLegInverseKinematics(crab, leg);
            ClampLegSegmentsToSurface(crab.room, leg);
        }
    }

    private static void CorrectLegTip(Room room, DrillCrab.Leg leg)
    {
        if (!TryGetQuicksandSurfaceAtX(
                room,
                leg.Tip.pos.x,
                out _,
                out Vector2 surface,
                out Vector2 inward))
        {
            return;
        }

        Vector2 desired = surface - inward * FootSurfaceClearance;
        float penetration = Vector2.Dot(leg.Tip.pos - desired, inward);
        if (penetration <= SurfaceCorrectionTolerance)
        {
            return;
        }

        Vector2 correction = -inward * penetration;
        leg.Tip.pos += correction;
        leg.Tip.lastPos += correction;

        float inwardVelocity = Vector2.Dot(leg.Tip.vel, inward);
        if (inwardVelocity > 0f)
        {
            leg.Tip.vel -= inward * inwardVelocity;
        }
    }

    private static void ClampLegTargetToSurface(Room room, DrillCrab.Leg leg)
    {
        if (room == null || leg == null ||
            !TryGetQuicksandSurfaceAtX(
                room,
                leg.targetPos.x,
                out _,
                out Vector2 surface,
                out Vector2 inward))
        {
            return;
        }

        leg.targetPos = surface - inward * FootSurfaceClearance;
    }

    private static void RebuildLegInverseKinematics(DrillCrab crab, DrillCrab.Leg leg)
    {
        if (leg.Limp || leg.Tip == null || leg.segments == null || leg.segments.Length == 0)
        {
            return;
        }

        float flattened = Custom.SCurve(leg.flatten, 2f);
        float flip = Mathf.Sin(
            (leg.side * 0.4f + 0.75f * crab.flip) * Mathf.PI / 2f) *
            (1f - flattened * 0.8f);
        float extended = Mathf.Clamp01(Vector2.Distance(leg.Tip.pos, leg.anchor) /
                                        Mathf.Max(1f, leg.maxLength));

        leg.DoInverseKinematics(flip, extended);

        for (int i = 0; i < leg.segments.Length - 1; i++)
        {
            leg.segments[i].vel = leg.segments[i].pos - leg.segments[i].lastPos;
        }
    }

    private static void ClampLegSegmentsToSurface(Room room, DrillCrab.Leg leg)
    {
        for (int i = 0; i < leg.segments.Length; i++)
        {
            DrillCrab.Leg.Segment segment = leg.segments[i];
            if (segment == null ||
                !TryGetQuicksandSurfaceAtX(
                    room,
                    segment.pos.x,
                    out _,
                    out Vector2 surface,
                    out Vector2 inward))
            {
                continue;
            }

            float clearance = i == leg.segments.Length - 1
                ? FootSurfaceClearance
                : LegSegmentClearance;
            Vector2 desired = surface - inward * clearance;
            float penetration = Vector2.Dot(segment.pos - desired, inward);
            if (penetration <= SurfaceCorrectionTolerance)
            {
                continue;
            }

            Vector2 correction = -inward * penetration;
            segment.pos += correction;
            segment.lastPos += correction;

            float inwardVelocity = Vector2.Dot(segment.vel, inward);
            if (inwardVelocity > 0f)
            {
                segment.vel -= inward * inwardVelocity;
            }
        }

        if (leg.Supporting)
        {
            leg.Tip.vel = Vector2.zero;
        }
    }

    private static void TrySeedScanningTarget(Room room, DrillCrab.Leg leg)
    {
        if (room == null || leg == null || leg.scanFrom == Vector2.zero)
        {
            return;
        }

        Vector2 rayEnd = leg.scanFrom +
                         Custom.RotateAroundOrigo(Vector2.down * leg.maxLength, leg.IdealAngle);
        if (TryFindFirstQuicksandIntersection(
                room,
                leg.scanFrom,
                rayEnd,
                out Vector2 surfacePoint,
                out Vector2 inward))
        {
            leg.SetTarget(surfacePoint - inward * FootSurfaceClearance);
        }
    }

    private static bool TryFindFirstQuicksandIntersection(
        Room room,
        Vector2 rayStart,
        Vector2 rayEnd,
        out Vector2 bestPoint,
        out Vector2 bestInward)
    {
        bestPoint = Vector2.zero;
        bestInward = Vector2.down;
        if (room?.updateList == null)
        {
            return false;
        }

        bool found = false;
        float bestRayT = float.PositiveInfinity;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone || !IsUsableZone(zone))
            {
                continue;
            }

            Vector2 previous = Vector2.zero;
            float previousU = 0f;
            bool havePrevious = false;

            for (int sample = 0; sample <= SurfaceRaySamples; sample++)
            {
                float u = sample / (float)SurfaceRaySamples;
                if (!zone.Data.IsQuicksand(u) ||
                    !zone.TrySampleSurfaceFrame(
                        u,
                        out Vector2 surface,
                        out _,
                        out Vector2 inward,
                        out _))
                {
                    havePrevious = false;
                    continue;
                }

                if (havePrevious &&
                    TrySegmentIntersection(
                        rayStart,
                        rayEnd,
                        previous,
                        surface,
                        out float rayT,
                        out Vector2 intersection) &&
                    rayT < bestRayT)
                {
                    float hitU = Mathf.Lerp(previousU, u, 0.5f);
                    zone.TrySampleSurfaceFrame(
                        hitU,
                        out _,
                        out _,
                        out Vector2 hitInward,
                        out _);
                    if (hitInward.sqrMagnitude < 0.0001f)
                    {
                        hitInward = Vector2.down;
                    }
                    else
                    {
                        hitInward.Normalize();
                    }
                    if (hitInward.y > 0f)
                    {
                        hitInward = -hitInward;
                    }

                    bestRayT = rayT;
                    bestPoint = intersection;
                    bestInward = hitInward;
                    found = true;
                }

                previous = surface;
                previousU = u;
                havePrevious = true;
            }
        }

        return found;
    }

    private static bool TrySegmentIntersection(
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Vector2 d,
        out float rayT,
        out Vector2 point)
    {
        Vector2 r = b - a;
        Vector2 s = d - c;
        float denominator = Cross(r, s);
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            rayT = 0f;
            point = Vector2.zero;
            return false;
        }

        Vector2 ca = c - a;
        float t = Cross(ca, s) / denominator;
        float u = Cross(ca, r) / denominator;
        if (t < 0f || t > 1f || u < 0f || u > 1f)
        {
            rayT = 0f;
            point = Vector2.zero;
            return false;
        }

        rayT = t;
        point = a + r * t;
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static bool TryGetQuicksandSurfaceAtX(
        Room room,
        float worldX,
        out QuicksandZone bestZone,
        out Vector2 bestSurface,
        out Vector2 bestInward)
    {
        bestZone = null;
        bestSurface = Vector2.zero;
        bestInward = Vector2.down;
        if (room?.updateList == null)
        {
            return false;
        }

        float highestSurface = float.NegativeInfinity;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone ||
                !IsUsableZone(zone) ||
                worldX < zone.startX ||
                worldX > zone.endX)
            {
                continue;
            }

            float u = zone.MaterialUAtWorldX(worldX);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surface,
                    out _,
                    out Vector2 inward,
                    out _))
            {
                continue;
            }

            if (surface.y <= highestSurface)
            {
                continue;
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

            highestSurface = surface.y;
            bestZone = zone;
            bestSurface = surface;
            bestInward = inward;
        }

        return bestZone != null;
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
                !IsUsableZone(zone) ||
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

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
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
