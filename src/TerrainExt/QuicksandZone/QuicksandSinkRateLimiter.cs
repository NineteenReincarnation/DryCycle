using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Baseline quicksand motion model.
/// Keep the normal behaviour deliberately simple until the contact physics are
/// stable: one fixed sink speed after surface contact, plus one fixed outward
/// struggle impulse when the player jumps.
/// </summary>
internal static class QuicksandSinkRateLimiter
{
    // Rain World physics runs at about 40 ticks/s.
    // Player: 0.10 px/tick = ~4 px/s.
    // Loose object: 0.065 px/tick = ~2.6 px/s.
    private const float PlayerSinkSpeed = 0.10f;
    private const float ObjectSinkSpeed = 0.065f;

    // Jumping in quicksand is a short struggle, not a normal jump.
    private const float PlayerStruggleOutwardSpeed = 1.15f;
    private const float DetectionMarginRadii = 2.0f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.BodyChunk.Update += BodyChunk_Update;
        On.Player.Jump += Player_Jump;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.BodyChunk.Update -= BodyChunk_Update;
        On.Player.Jump -= Player_Jump;
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanLimit(owner) ||
            !TryFindContactFrame(
                self,
                out QuicksandZone zone,
                out Vector2 inward,
                out float startDepth))
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

        Vector2 startPos = self.pos;
        float radius = Mathf.Max(1f, self.rad);
        float fixedSinkSpeed = isPlayer ? PlayerSinkSpeed : ObjectSinkSpeed;

        orig(self);

        if (!IsUsableZone(zone) ||
            !TrySampleAtChunk(self, zone, out Vector2 currentInward, out float currentDepth))
        {
            return;
        }

        inward = currentInward;
        Vector2 displacement = self.pos - startPos;
        float inwardDisplacement = Vector2.Dot(displacement, inward);

        // Before the body actually touches the surface, leave its approach alone.
        // The moment a radius reaches the sand, normal motion becomes exactly the
        // authored fixed sink speed. If a fast impact crossed the surface this frame,
        // correcting the displacement here also removes the free-fall overshoot.
        bool touchingSurface = startDepth >= -radius || currentDepth >= -radius;
        if (!touchingSurface)
        {
            return;
        }

        // Negative means the body is intentionally moving out of the sand (jump /
        // climb). Do not fight that here. Any non-outward motion sinks at one speed.
        if (inwardDisplacement >= 0f)
        {
            self.pos += inward * (fixedSinkSpeed - inwardDisplacement);

            float inwardVelocity = Vector2.Dot(self.vel, inward);
            self.vel += inward * (fixedSinkSpeed - inwardVelocity);
        }
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (self == null || self.bodyChunks == null || self.bodyChunks.Length == 0)
        {
            orig(self);
            return;
        }

        bool inQuicksand = TryFindPlayerFrame(self, out Vector2 inward);
        orig(self);

        if (!inQuicksand)
        {
            return;
        }

        // Replace whatever normal jump the base game produced with one fixed
        // outward struggle impulse. Tangential velocity is left intact.
        Vector2 outward = -inward;
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            float outwardSpeed = Vector2.Dot(chunk.vel, outward);
            chunk.vel += outward * (PlayerStruggleOutwardSpeed - outwardSpeed);
        }

        // Prevent Rain World's held-jump boost from turning the fixed struggle into
        // a normal jump over the following ticks.
        self.jumpBoost = 0f;
    }

    private static bool TryFindPlayerFrame(Player player, out Vector2 inward)
    {
        inward = Vector2.down;
        if (player?.bodyChunks == null)
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null &&
                TryFindContactFrame(chunk, out _, out inward, out float depth) &&
                depth >= -Mathf.Max(1f, chunk.rad))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindContactFrame(
        BodyChunk chunk,
        out QuicksandZone bestZone,
        out Vector2 bestInward,
        out float bestDepth)
    {
        bestZone = null;
        bestInward = Vector2.down;
        bestDepth = float.NegativeInfinity;

        PhysicalObject owner = chunk?.owner;
        Room room = owner?.room;
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

            if (!TrySampleAtChunk(chunk, zone, out Vector2 inward, out float depth))
            {
                continue;
            }

            if (depth < -radius * DetectionMarginRadii)
            {
                continue;
            }

            if (depth > bestDepth)
            {
                bestDepth = depth;
                bestZone = zone;
                bestInward = inward;
            }
        }

        return bestZone != null;
    }

    private static bool TrySampleAtChunk(
        BodyChunk chunk,
        QuicksandZone zone,
        out Vector2 inward,
        out float signedDepth)
    {
        inward = Vector2.down;
        signedDepth = 0f;

        float radius = Mathf.Max(1f, chunk.rad);
        if (chunk.pos.x < zone.startX - radius * 1.15f ||
            chunk.pos.x > zone.endX + radius * 1.15f)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(chunk.pos.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surfacePoint,
                out _,
                out inward,
                out float depthLength))
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

        signedDepth = Vector2.Dot(chunk.pos - surfacePoint, inward);
        return signedDepth >= -radius * DetectionMarginRadii &&
               signedDepth <= depthLength + radius * 0.50f;
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
