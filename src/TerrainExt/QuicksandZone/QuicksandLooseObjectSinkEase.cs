using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Adds a slow, immersion-dependent settling pass for loose non-creature objects.
///
/// QuicksandSinkRateLimiter still owns contact acquisition, terrain-collision
/// suppression and the hard safety cap. This outer hook replaces only the final
/// downward step: objects barely touching the surface settle very slowly, then gain
/// a little speed as more of the BodyChunk is immersed. This makes round stones and
/// other compact items visibly sink instead of looking as if they simply fall through.
/// </summary>
internal static class QuicksandLooseObjectSinkEase
{
    private const float SurfaceSinkSpeed = 0.012f;
    private const float DeepSinkSpeed = 0.032f;
    private const float SurfaceInfluenceRadii = 1.0f;
    private const float Epsilon = 0.000001f;

    private sealed class ObjectState
    {
        internal bool Touching;
        internal float SmoothedSpeed;
    }

    private static readonly ConditionalWeakTable<PhysicalObject, ObjectState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.BodyChunk.Update += BodyChunk_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.BodyChunk.Update -= BodyChunk_Update;
    }

    private static void BodyChunk_Update(
        On.BodyChunk.orig_Update orig,
        BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanEase(owner, self))
        {
            orig(self);
            return;
        }

        float startY = self.pos.y;
        float radius = Mathf.Max(1f, self.rad);
        bool startedTouching = TryFindContact(
            self,
            self.pos,
            out _,
            out _,
            out float startDepth) &&
            startDepth >= -radius;

        orig(self);

        if (!CanEase(owner, self))
        {
            Reset(owner);
            return;
        }

        if (!TryFindContact(
                self,
                self.pos,
                out QuicksandZone zone,
                out float surfaceY,
                out float currentDepth))
        {
            Reset(owner);
            return;
        }

        bool touchingNow = currentDepth >= -radius;
        if (!startedTouching && !touchingNow)
        {
            Reset(owner);
            return;
        }

        // Do not fight a real upward impulse. The easing pass is only a replacement
        // for downward settling after the normal quicksand hook has run.
        float rawDisplacement = self.pos.y - startY;
        if (rawDisplacement > Epsilon)
        {
            Reset(owner);
            return;
        }

        ObjectState state = States.GetValue(owner, _ => new ObjectState());
        float immersion = Mathf.Clamp01((currentDepth + radius) / (radius * 2f));
        float targetSpeed = ResolveSinkSpeed(immersion);

        if (!state.Touching)
        {
            state.Touching = true;
            state.SmoothedSpeed = SurfaceSinkSpeed;
        }
        else
        {
            // Avoid a visible speed step as a round object passes its widest point.
            state.SmoothedSpeed = Mathf.Lerp(
                state.SmoothedSpeed,
                targetSpeed,
                0.08f);
        }

        float sinkSpeed = Mathf.Clamp(
            state.SmoothedSpeed,
            SurfaceSinkSpeed,
            DeepSinkSpeed);

        float targetY = startedTouching
            ? startY - sinkSpeed
            : surfaceY + radius - sinkSpeed;

        // The inner sink limiter may already have moved the chunk farther down at its
        // legacy 0.065 step. Pull only that excess common downward travel back out.
        // Never move it lower than the inner controller already chose.
        if (self.pos.y < targetY)
        {
            self.pos.y = targetY;
        }

        if (self.vel.y < -sinkSpeed)
        {
            self.vel.y = -sinkSpeed;
        }
    }

    private static float ResolveSinkSpeed(float immersion)
    {
        float t = Mathf.Clamp01(immersion);
        t = t * t * (3f - 2f * t); // smoothstep
        return Mathf.Lerp(SurfaceSinkSpeed, DeepSinkSpeed, t);
    }

    private static bool TryFindContact(
        BodyChunk chunk,
        Vector2 point,
        out QuicksandZone bestZone,
        out float bestSurfaceY,
        out float bestDepth)
    {
        bestZone = null;
        bestSurfaceY = 0f;
        bestDepth = float.NegativeInfinity;

        Room room = chunk?.owner?.room;
        if (room?.updateList == null)
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone || !IsUsableZone(zone))
            {
                continue;
            }

            if (point.x < zone.startX - radius * 0.15f ||
                point.x > zone.endX + radius * 0.15f)
            {
                continue;
            }

            float u = zone.MaterialUAtWorldX(point.x);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surfacePoint,
                    out _,
                    out _,
                    out float depthLength))
            {
                continue;
            }

            float depth = surfacePoint.y - point.y;
            if (depth < -radius * SurfaceInfluenceRadii ||
                depth > depthLength + radius * 0.50f)
            {
                continue;
            }

            if (bestZone == null || depth > bestDepth)
            {
                bestZone = zone;
                bestSurfaceY = surfacePoint.y;
                bestDepth = depth;
            }
        }

        return bestZone != null;
    }

    private static bool CanEase(PhysicalObject owner, BodyChunk chunk)
    {
        return owner != null &&
               chunk != null &&
               owner.room != null &&
               owner is not Player &&
               owner is not Creature &&
               owner.bodyChunks != null &&
               owner.bodyChunks.Length > 0 &&
               (owner.grabbedBy == null || owner.grabbedBy.Count == 0);
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }

    private static void Reset(PhysicalObject owner)
    {
        if (owner != null && States.TryGetValue(owner, out ObjectState state))
        {
            state.Touching = false;
            state.SmoothedSpeed = 0f;
        }
    }
}
