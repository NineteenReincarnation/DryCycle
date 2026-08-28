using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Limits the actual BodyChunk integration speed while a chunk is entering quicksand.
/// Rain World's BodyChunk.Update subtracts gravity immediately before moving the chunk,
/// so post-update drag alone still allows roughly one gravity-step of free fall every
/// tick. This hook compensates that integration step and caps only motion into the
/// quicksand surface normal; tangential movement and intentional movement out of the
/// sand remain untouched.
/// </summary>
internal static class QuicksandSinkRateLimiter
{
    // Rain World normally updates physics at 40 Hz. These values therefore produce
    // visibly slow sinking instead of an almost-free fall through the zone.
    private const float PlayerSurfaceSinkSpeed = 0.18f;
    private const float PlayerDeepSinkSpeed = 0.055f;
    private const float ObjectSurfaceSinkSpeed = 0.085f;
    private const float ObjectDeepSinkSpeed = 0.022f;

    private const float DetectionMarginRadii = 2.4f;
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

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanLimit(owner))
        {
            orig(self);
            return;
        }

        bool isPlayer = owner is Player;
        if (!isPlayer && owner.grabbedBy != null && owner.grabbedBy.Count > 0)
        {
            orig(self);
            return;
        }

        // BodyChunk.Update begins with: vel.y -= owner.gravity. Predict what the game
        // is about to integrate, clamp that velocity in the quicksand frame, then add
        // gravity back once so the native subtraction lands on the clamped value.
        Vector2 integratedVelocity = self.vel + Vector2.down * owner.gravity;
        if (TryLimitInwardVelocity(
                self,
                owner,
                isPlayer,
                integratedVelocity,
                out Vector2 limitedVelocity))
        {
            self.vel = limitedVelocity + Vector2.up * owner.gravity;
        }

        orig(self);
    }

    private static bool TryLimitInwardVelocity(
        BodyChunk chunk,
        PhysicalObject owner,
        bool isPlayer,
        Vector2 integratedVelocity,
        out Vector2 limitedVelocity)
    {
        limitedVelocity = integratedVelocity;
        Room room = owner.room;
        if (room?.updateList == null)
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        Vector2 predictedPoint = chunk.pos + integratedVelocity;
        float bestPredictedDepth = float.NegativeInfinity;
        QuicksandZone bestZone = null;
        Vector2 bestSurface = Vector2.zero;
        Vector2 bestInward = Vector2.down;
        float bestCurrentDepth = 0f;
        float bestDepthLength = 0f;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone || !IsUsableZone(zone))
            {
                continue;
            }

            float sampleX = predictedPoint.x;
            if (sampleX < zone.startX - radius * 1.15f ||
                sampleX > zone.endX + radius * 1.15f)
            {
                continue;
            }

            float u = zone.MaterialUAtWorldX(sampleX);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surfacePoint,
                    out _,
                    out Vector2 inward,
                    out float depthLength))
            {
                continue;
            }

            if (inward.sqrMagnitude < 0.0001f)
            {
                continue;
            }

            inward.Normalize();
            float currentDepth = Vector2.Dot(chunk.pos - surfacePoint, inward);
            float predictedDepth = Vector2.Dot(predictedPoint - surfacePoint, inward);
            float inwardSpeed = Vector2.Dot(integratedVelocity, inward);

            // Not moving into the material, still too far above it, or already below
            // the authored quicksand band: leave native motion alone.
            if (inwardSpeed <= 0f ||
                predictedDepth < -radius ||
                currentDepth < -radius * DetectionMarginRadii ||
                currentDepth > depthLength + radius * 0.50f)
            {
                continue;
            }

            if (predictedDepth > bestPredictedDepth)
            {
                bestPredictedDepth = predictedDepth;
                bestZone = zone;
                bestSurface = surfacePoint;
                bestInward = inward;
                bestCurrentDepth = currentDepth;
                bestDepthLength = depthLength;
            }
        }

        if (bestZone == null)
        {
            return false;
        }

        float immersion = Mathf.Clamp01((bestCurrentDepth + radius) / (radius * 2f));
        float packing = Mathf.SmoothStep(0f, 1f, immersion);

        float surfaceSpeed = isPlayer ? PlayerSurfaceSinkSpeed : ObjectSurfaceSinkSpeed;
        float deepSpeed = isPlayer ? PlayerDeepSinkSpeed : ObjectDeepSinkSpeed;
        float sinkSpeed = Mathf.Lerp(surfaceSpeed, deepSpeed, packing);

        // Preserve the editor SinkStrength control, but keep it within a range that
        // cannot turn the material back into near-free fall.
        float sinkTuning = Mathf.Lerp(
            0.72f,
            1.28f,
            Mathf.Clamp01(bestZone.Data.SinkStrength));
        sinkSpeed *= sinkTuning;

        // If the body is still just above the surface, allow exactly enough normal
        // travel to reach it plus one slow sinking step. This absorbs high-speed
        // impacts without making objects hover above the sand.
        float surfaceGap = Mathf.Max(0f, -radius - bestCurrentDepth);
        float allowedInwardSpeed = surfaceGap + sinkSpeed;

        float inwardComponent = Vector2.Dot(integratedVelocity, bestInward);
        if (inwardComponent <= allowedInwardSpeed)
        {
            return false;
        }

        limitedVelocity = integratedVelocity -
                          bestInward * (inwardComponent - allowedInwardSpeed);
        return true;
    }

    private static bool CanLimit(PhysicalObject owner)
    {
        return owner != null &&
               owner.room != null &&
               owner.bodyChunks != null &&
               owner.bodyChunks.Length > 0 &&
               (owner is Player || owner is not Creature);
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }
}
