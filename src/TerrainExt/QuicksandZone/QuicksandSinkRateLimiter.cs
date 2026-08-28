using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Baseline quicksand motion model.
///
/// Physics rules are intentionally simple:
/// - quicksand surface geometry is used only to decide whether a chunk is inside;
/// - sinking is always straight down on the world Y axis;
/// - jumping/struggling is always straight up on the world Y axis;
/// - no quicksand physics operation is allowed to add an X displacement;
/// - while a chunk is in the quicksand band, native terrain collision is disabled.
/// </summary>
internal static class QuicksandSinkRateLimiter
{
    // Rain World physics runs at about 40 ticks/s.
    private const float PlayerSinkSpeed = 0.10f;
    private const float ObjectSinkSpeed = 0.065f;
    private const float PlayerStruggleUpwardSpeed = 1.15f;
    private const float DetectionMarginRadii = 2.0f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
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
        On.Player.Update -= Player_Update;
        On.BodyChunk.Update -= BodyChunk_Update;
        On.Player.Jump -= Player_Jump;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (self == null ||
            self.room == null ||
            self.bodyChunks == null ||
            self.bodyChunks.Length == 0)
        {
            orig(self, eu);
            return;
        }

        int count = self.bodyChunks.Length;
        Vector2[] startPositions = new Vector2[count];
        bool[] startedTouching = new bool[count];
        bool[] collisionOverridden = new bool[count];
        bool[] originalTerrainCollision = new bool[count];

        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            startPositions[i] = chunk.pos;
            originalTerrainCollision[i] = chunk.collideWithTerrain;

            if (!TryFindVerticalContact(
                    chunk,
                    out _,
                    out _,
                    out float startDepth,
                    out _))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            startedTouching[i] = startDepth >= -radius;

            // Once a body chunk is at or immediately approaching quicksand, do not
            // let Rain World's normal tile/terrain collision decide its motion.
            collisionOverridden[i] = true;
            chunk.collideWithTerrain = false;
        }

        try
        {
            orig(self, eu);
        }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                BodyChunk chunk = self.bodyChunks[i];
                if (chunk != null && collisionOverridden[i])
                {
                    chunk.collideWithTerrain = originalTerrainCollision[i];
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null ||
                !TryFindVerticalContact(
                    chunk,
                    out _,
                    out float currentSurfaceY,
                    out float currentDepth,
                    out _))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            bool touchingSurface = startedTouching[i] || currentDepth >= -radius;
            if (!touchingSurface)
            {
                continue;
            }

            float verticalDisplacement = chunk.pos.y - startPositions[i].y;

            // Positive Y movement is an intentional upward movement (jump, climb,
            // pole movement). Do not turn it into a sink step here.
            if (verticalDisplacement > 0f)
            {
                continue;
            }

            // A chunk that crossed the surface this frame is placed one fixed sink
            // step below the contact height. A chunk already in sand simply loses
            // exactly one fixed Y step. X is never touched here.
            float targetY = startedTouching[i]
                ? startPositions[i].y - PlayerSinkSpeed
                : currentSurfaceY + radius - PlayerSinkSpeed;

            chunk.pos.y = targetY;
            chunk.vel.y = -PlayerSinkSpeed;
        }
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanLimitLooseObject(owner) ||
            !TryFindVerticalContact(
                self,
                out _,
                out _,
                out float startDepth,
                out _))
        {
            orig(self);
            return;
        }

        if (owner.grabbedBy != null && owner.grabbedBy.Count > 0)
        {
            orig(self);
            return;
        }

        Vector2 startPos = self.pos;
        float radius = Mathf.Max(1f, self.rad);
        bool startedTouching = startDepth >= -radius;

        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;

        try
        {
            orig(self);
        }
        finally
        {
            self.collideWithTerrain = originalTerrainCollision;
        }

        if (!TryFindVerticalContact(
                self,
                out _,
                out float currentSurfaceY,
                out float currentDepth,
                out _))
        {
            return;
        }

        bool touchingSurface = startedTouching || currentDepth >= -radius;
        if (!touchingSurface)
        {
            return;
        }

        float verticalDisplacement = self.pos.y - startPos.y;
        if (verticalDisplacement > 0f)
        {
            return;
        }

        float targetY = startedTouching
            ? startPos.y - ObjectSinkSpeed
            : currentSurfaceY + radius - ObjectSinkSpeed;

        self.pos.y = targetY;
        self.vel.y = -ObjectSinkSpeed;
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (self == null || self.bodyChunks == null || self.bodyChunks.Length == 0)
        {
            orig(self);
            return;
        }

        bool inQuicksand = IsPlayerTouchingQuicksand(self);
        float[] beforeX = null;

        if (inQuicksand)
        {
            beforeX = new float[self.bodyChunks.Length];
            for (int i = 0; i < self.bodyChunks.Length; i++)
            {
                if (self.bodyChunks[i] != null)
                {
                    beforeX[i] = self.bodyChunks[i].vel.x;
                }
            }
        }

        orig(self);

        if (!inQuicksand)
        {
            return;
        }

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            // The struggle impulse is Y-only. Restore the X velocity that existed
            // before the normal jump code ran so quicksand does not create sideways
            // jump movement of its own.
            chunk.vel.x = beforeX[i];
            chunk.vel.y = PlayerStruggleUpwardSpeed;
        }

        self.jumpBoost = 0f;
    }

    private static bool IsPlayerTouchingQuicksand(Player player)
    {
        if (player?.bodyChunks == null)
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TryFindVerticalContact(
                    chunk,
                    out _,
                    out _,
                    out float depth,
                    out float depthLength))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (depth >= -radius && depth <= depthLength + radius * 0.50f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindVerticalContact(
        BodyChunk chunk,
        out QuicksandZone bestZone,
        out float bestSurfaceY,
        out float bestDepth,
        out float bestDepthLength)
    {
        bestZone = null;
        bestSurfaceY = 0f;
        bestDepth = float.NegativeInfinity;
        bestDepthLength = 0f;

        PhysicalObject owner = chunk?.owner;
        Room room = owner?.room;
        if (room?.updateList == null)
        {
            return false;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone || !IsUsableZone(zone))
            {
                continue;
            }

            if (!TrySampleVerticalAtChunk(
                    chunk,
                    zone,
                    out float surfaceY,
                    out float depth,
                    out float depthLength))
            {
                continue;
            }

            if (depth > bestDepth)
            {
                bestDepth = depth;
                bestZone = zone;
                bestSurfaceY = surfaceY;
                bestDepthLength = depthLength;
            }
        }

        return bestZone != null;
    }

    private static bool TrySampleVerticalAtChunk(
        BodyChunk chunk,
        QuicksandZone zone,
        out float surfaceY,
        out float signedDepth,
        out float depthLength)
    {
        surfaceY = 0f;
        signedDepth = 0f;
        depthLength = 0f;

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
                out _,
                out _))
        {
            return false;
        }

        surfaceY = surfacePoint.y;
        float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        depthLength = Mathf.Max(4f, surfaceY - bottomY);

        // Positive depth means below the surface. This is deliberately world-Y
        // depth, not distance along the curved surface normal.
        signedDepth = surfaceY - chunk.pos.y;

        return signedDepth >= -radius * DetectionMarginRadii &&
               signedDepth <= depthLength + radius * 0.50f;
    }

    private static bool CanLimitLooseObject(PhysicalObject owner)
    {
        return owner != null &&
               owner is not Player &&
               owner is not Creature &&
               owner.room != null &&
               owner.bodyChunks != null &&
               owner.bodyChunks.Length > 0;
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
