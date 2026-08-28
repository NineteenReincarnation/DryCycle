using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Owns baseline quicksand motion for players and loose physical objects.
///
/// Player rules deliberately separate physics from locomotion semantics:
/// - the quicksand curve only supplies a local surface Y / containment test;
/// - player sinking is one world-Y translation applied to the whole body;
/// - relative motion between player body chunks is preserved;
/// - no fake BodyChunk.ContactPoint is written, so hard-ground features such as
///   feetStuckPos are never activated by quicksand;
/// - standing / walking is requested at Player state level instead;
/// - jumping is one fixed world-Y struggle impulse;
/// - no quicksand operation adds X displacement or X velocity.
/// </summary>
internal static class QuicksandSinkRateLimiter
{
    // Rain World normally updates physics at roughly 40 ticks/s.
    private const float PlayerSinkSpeed = 0.10f;
    private const float ObjectSinkSpeed = 0.065f;
    private const float PlayerStruggleUpwardSpeed = 1.15f;
    private const float DetectionMarginRadii = 2.0f;
    private const float UpwardMotionThreshold = 0.015f;
    private const int PlayerDeathConfirmTicks = 10;
    private const float PlayerHeadClearance = 8f;

    private sealed class PlayerSinkState
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal float Immersion;
        internal int FullySubmergedTicks;
    }

    private static readonly ConditionalWeakTable<Player, PlayerSinkState> PlayerStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.Player.Jump += Player_Jump;
        On.BodyChunk.Update += BodyChunk_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        On.Player.Jump -= Player_Jump;
        On.BodyChunk.Update -= BodyChunk_Update;
    }

    /// <summary>
    /// Shared render-side quicksand test. Physics ownership lives here, so render
    /// containment no longer depends on the retired legacy player/object physics.
    /// </summary>
    internal static bool TryGetVisualSink(
        PhysicalObject physicalObject,
        out Vector2 visualOffset,
        out QuicksandZone zone,
        out float progress)
    {
        visualOffset = Vector2.zero;
        zone = null;
        progress = 0f;

        if (physicalObject == null ||
            physicalObject.room == null ||
            physicalObject.room.updateList == null ||
            physicalObject.bodyChunks == null ||
            physicalObject.bodyChunks.Length == 0)
        {
            return false;
        }

        if (physicalObject is Player player &&
            PlayerStates.TryGetValue(player, out PlayerSinkState playerState) &&
            playerState.Active &&
            IsUsableZone(playerState.Zone) &&
            playerState.Zone.room == player.room &&
            playerState.Immersion > 0.005f)
        {
            zone = playerState.Zone;
            progress = Mathf.Clamp01(playerState.Immersion);
            return true;
        }

        float bestProgress = 0f;
        QuicksandZone bestZone = null;

        for (int i = 0; i < physicalObject.room.updateList.Count; i++)
        {
            if (physicalObject.room.updateList[i] is not QuicksandZone candidate ||
                !IsUsableZone(candidate))
            {
                continue;
            }

            float candidateProgress = ComputeObjectImmersion(physicalObject, candidate);
            if (candidateProgress > bestProgress)
            {
                bestProgress = candidateProgress;
                bestZone = candidate;
            }
        }

        if (bestZone == null || bestProgress <= 0.005f)
        {
            return false;
        }

        zone = bestZone;
        progress = Mathf.Clamp01(bestProgress);
        return true;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!CanControlPlayer(self))
        {
            orig(self, eu);
            return;
        }

        PlayerSinkState state = PlayerStates.GetValue(self, _ => new PlayerSinkState());
        if (state.Active && !IsPlayerStateValid(self, state))
        {
            DeactivatePlayer(state);
        }

        bool enteredThisFrame = false;
        float entryDropAllowance = PlayerSinkSpeed;

        if (!state.Active &&
            TryFindPlayerEntry(self, out QuicksandZone entryZone, out entryDropAllowance))
        {
            state.Active = true;
            state.Zone = entryZone;
            state.Immersion = 0f;
            state.FullySubmergedTicks = 0;
            enteredThisFrame = true;
        }

        if (!state.Active)
        {
            orig(self, eu);
            return;
        }

        int chunkCount = self.bodyChunks.Length;
        bool[] originalTerrainCollision = new bool[chunkCount];
        bool[] collisionOverridden = new bool[chunkCount];
        float startAverageY = AverageChunkY(self);
        bool movingUpBeforeUpdate = AverageChunkVelocityY(self) > UpwardMotionThreshold;

        // Never carry a hard-ground foot lock into quicksand.
        self.feetStuckPos = null;
        ApplySupportedLocomotionState(self, movingUpBeforeUpdate);

        for (int i = 0; i < chunkCount; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            originalTerrainCollision[i] = chunk.collideWithTerrain;
            if (ChunkWithinZoneInfluence(chunk, state.Zone, predictive: true))
            {
                collisionOverridden[i] = true;
                chunk.collideWithTerrain = false;
            }
        }

        try
        {
            orig(self, eu);
        }
        finally
        {
            for (int i = 0; i < chunkCount; i++)
            {
                BodyChunk chunk = self.bodyChunks[i];
                if (chunk != null && collisionOverridden[i])
                {
                    chunk.collideWithTerrain = originalTerrainCollision[i];
                }
            }
        }

        // ContactPoint remains whatever the real collision system produced. In the
        // quicksand band collision was disabled, so it stays zero rather than being
        // forged as a floor. This is what prevents feetStuckPos from pinning chunk 1.
        self.feetStuckPos = null;

        if (!IsPlayerStateValid(self, state) ||
            (!enteredThisFrame && !PlayerStillInZone(self, state.Zone)))
        {
            DeactivatePlayer(state);
            return;
        }

        float endAverageY = AverageChunkY(self);
        float rawAverageDisplacement = endAverageY - startAverageY;
        float averageVelocityY = AverageChunkVelocityY(self);
        bool movingUp = rawAverageDisplacement > UpwardMotionThreshold ||
                        averageVelocityY > UpwardMotionThreshold;

        if (!movingUp)
        {
            // On the entry frame, allow exactly enough whole-body travel to reach the
            // surface plus one sink step. Every later frame sinks by one fixed step.
            float targetDisplacement = -(enteredThisFrame
                ? Mathf.Max(PlayerSinkSpeed, entryDropAllowance)
                : PlayerSinkSpeed);

            float positionCorrectionY = targetDisplacement - rawAverageDisplacement;
            TranslatePlayerY(self, positionCorrectionY);

            // Preserve the body's relative vertical velocity (standing posture,
            // animation impulses, connection corrections) while making the player as
            // a whole descend at the fixed sink speed.
            float velocityCorrectionY = -PlayerSinkSpeed - AverageChunkVelocityY(self);
            AddPlayerVelocityY(self, velocityCorrectionY);

            ApplySupportedLocomotionState(self, movingUp: false);
        }
        else
        {
            // A struggle / climb is allowed to move upward. It must not inherit a
            // stale ordinary-ground foot pin.
            self.feetStuckPos = null;
            self.standing = false;
        }

        UpdatePlayerImmersion(self, state);
        CheckPlayerFullySubmerged(self, state);
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (self == null || self.bodyChunks == null || self.bodyChunks.Length == 0)
        {
            orig(self);
            return;
        }

        bool inQuicksand = PlayerStates.TryGetValue(self, out PlayerSinkState state) &&
                           IsPlayerStateValid(self, state) &&
                           PlayerStillInZone(self, state.Zone);

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

            // Quicksand struggle is world-Y only. Undo any horizontal jump impulse
            // produced by normal Player.Jump while preserving pre-jump X motion.
            chunk.vel.x = beforeX[i];
            chunk.vel.y = PlayerStruggleUpwardSpeed;
        }

        self.feetStuckPos = null;
        self.standing = false;
        self.jumpBoost = 0f;
        self.canJump = 0;
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;

        // Player motion is owned at Player.Update level so both chunks receive one
        // common Y translation. Never control player chunks independently here.
        if (owner is Player)
        {
            orig(self);
            return;
        }

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

    private static void ApplySupportedLocomotionState(Player player, bool movingUp)
    {
        player.feetStuckPos = null;

        if (movingUp || player.dead || !player.Consious || !CanUseSupportedBodyMode(player))
        {
            return;
        }

        // This is intentionally Player-level state, not a fake BodyChunk floor.
        // UpdateAnimation can therefore use ordinary standing/walking behaviour while
        // ContactPoint remains zero and hard-ground anchoring stays disabled.
        player.standing = true;
        player.bodyMode = Player.BodyModeIndex.Stand;
        player.canJump = Mathf.Max(player.canJump, 2);
    }

    private static bool CanUseSupportedBodyMode(Player player)
    {
        if (player == null || player.animation != Player.AnimationIndex.None)
        {
            return false;
        }

        return player.bodyMode == Player.BodyModeIndex.Default ||
               player.bodyMode == Player.BodyModeIndex.Stand ||
               player.bodyMode == Player.BodyModeIndex.Crawl;
    }

    private static bool TryFindPlayerEntry(
        Player player,
        out QuicksandZone bestZone,
        out float allowedDrop)
    {
        bestZone = null;
        allowedDrop = PlayerSinkSpeed;

        if (player?.room?.updateList == null ||
            player.bodyChunks == null ||
            player.bodyChunks.Length == 0)
        {
            return false;
        }

        float bestGap = float.PositiveInfinity;
        float bestDepth = float.NegativeInfinity;

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not QuicksandZone zone || !IsUsableZone(zone))
            {
                continue;
            }

            for (int j = 0; j < player.bodyChunks.Length; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk == null ||
                    !TrySampleVerticalAtPositionRaw(
                        chunk,
                        zone,
                        chunk.pos.x,
                        chunk.pos.y,
                        out float surfaceY,
                        out float currentDepth,
                        out float depthLength))
                {
                    continue;
                }

                float radius = Mathf.Max(1f, chunk.rad);
                if (currentDepth > depthLength + radius * 0.50f)
                {
                    continue;
                }

                float currentGap = Mathf.Max(0f, chunk.pos.y - radius - surfaceY);
                bool currentlyTouching = currentDepth >= -radius;

                float predictedX = chunk.pos.x + chunk.vel.x;
                float predictedY = chunk.pos.y + chunk.vel.y - player.gravity;
                bool willTouch = false;

                if (TrySampleVerticalAtPositionRaw(
                        chunk,
                        zone,
                        predictedX,
                        predictedY,
                        out _,
                        out float predictedDepth,
                        out float predictedDepthLength))
                {
                    willTouch = predictedDepth >= -radius &&
                                predictedDepth <= predictedDepthLength + radius * 0.50f;
                }

                if (!currentlyTouching && !willTouch)
                {
                    continue;
                }

                if (currentGap < bestGap ||
                    (Mathf.Abs(currentGap - bestGap) < 0.001f && currentDepth > bestDepth))
                {
                    bestGap = currentGap;
                    bestDepth = currentDepth;
                    bestZone = zone;
                    allowedDrop = currentGap + PlayerSinkSpeed;
                }
            }
        }

        return bestZone != null;
    }

    private static bool PlayerStillInZone(Player player, QuicksandZone zone)
    {
        if (player?.bodyChunks == null || !IsUsableZone(zone))
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            if (TrySampleVerticalAtChunk(
                    chunk,
                    zone,
                    out _,
                    out float depth,
                    out float depthLength))
            {
                float radius = Mathf.Max(1f, chunk.rad);
                if (depth >= -radius * DetectionMarginRadii &&
                    depth <= depthLength + radius * 0.50f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ChunkWithinZoneInfluence(
        BodyChunk chunk,
        QuicksandZone zone,
        bool predictive)
    {
        if (chunk == null || !IsUsableZone(zone))
        {
            return false;
        }

        if (TrySampleVerticalAtChunk(
                chunk,
                zone,
                out _,
                out float depth,
                out float depthLength))
        {
            float radius = Mathf.Max(1f, chunk.rad);
            if (depth >= -radius * DetectionMarginRadii &&
                depth <= depthLength + radius * 0.50f)
            {
                return true;
            }
        }

        if (!predictive)
        {
            return false;
        }

        float predictedX = chunk.pos.x + chunk.vel.x;
        float predictedY = chunk.pos.y + chunk.vel.y - chunk.owner.gravity;
        if (!TrySampleVerticalAtPosition(
                chunk,
                zone,
                predictedX,
                predictedY,
                out _,
                out float predictedDepth,
                out float predictedDepthLength))
        {
            return false;
        }

        float predictedRadius = Mathf.Max(1f, chunk.rad);
        return predictedDepth >= -predictedRadius * DetectionMarginRadii &&
               predictedDepth <= predictedDepthLength + predictedRadius * 0.50f;
    }

    private static void UpdatePlayerImmersion(Player player, PlayerSinkState state)
    {
        if (!IsPlayerStateValid(player, state))
        {
            state.Immersion = 0f;
            return;
        }

        float total = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TrySampleVerticalAtChunk(
                    chunk,
                    state.Zone,
                    out _,
                    out float depth,
                    out _))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            total += Mathf.Clamp01((depth + radius) / (radius * 2f));
            count++;
        }

        state.Immersion = count > 0 ? Mathf.Clamp01(total / count) : 0f;
    }

    private static void CheckPlayerFullySubmerged(Player player, PlayerSinkState state)
    {
        if (player == null || player.dead || !IsPlayerStateValid(player, state))
        {
            if (state != null)
            {
                state.FullySubmergedTicks = 0;
            }
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TrySampleVerticalAtChunk(
                    chunk,
                    state.Zone,
                    out float surfaceY,
                    out _,
                    out _))
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (chunk.pos.y + radius > surfaceY - 1f)
            {
                state.FullySubmergedTicks = 0;
                return;
            }
        }

        Vector2 headPoint;
        if (player.graphicsModule is PlayerGraphics graphics && graphics.head != null)
        {
            headPoint = graphics.head.pos;
        }
        else
        {
            BodyChunk main = player.bodyChunks[0];
            if (main == null)
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            headPoint = main.pos + Vector2.up * (main.rad + PlayerHeadClearance);
        }

        if (!TrySampleVerticalAtWorldPoint(
                state.Zone,
                headPoint,
                out float headSurfaceY,
                out float headDepthLength))
        {
            state.FullySubmergedTicks = 0;
            return;
        }

        float headDepth = headSurfaceY - headPoint.y;
        if (headDepth < PlayerHeadClearance || headDepth > headDepthLength)
        {
            state.FullySubmergedTicks = 0;
            return;
        }

        state.FullySubmergedTicks++;
        if (state.FullySubmergedTicks >= PlayerDeathConfirmTicks)
        {
            player.Die();
        }
    }

    private static float ComputeObjectImmersion(PhysicalObject physicalObject, QuicksandZone zone)
    {
        if (physicalObject?.bodyChunks == null || !IsUsableZone(zone))
        {
            return 0f;
        }

        float maximum = 0f;
        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null ||
                !TrySampleVerticalAtChunk(
                    chunk,
                    zone,
                    out _,
                    out float depth,
                    out _))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            maximum = Mathf.Max(maximum, Mathf.Clamp01((depth + radius) / (radius * 2f)));
        }

        return maximum;
    }

    private static float AverageChunkY(Player player)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                total += player.bodyChunks[i].pos.y;
                count++;
            }
        }

        return count > 0 ? total / count : 0f;
    }

    private static float AverageChunkVelocityY(Player player)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                total += player.bodyChunks[i].vel.y;
                count++;
            }
        }

        return count > 0 ? total / count : 0f;
    }

    private static void TranslatePlayerY(Player player, float deltaY)
    {
        if (Mathf.Abs(deltaY) < 0.000001f)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].pos.y += deltaY;
            }
        }
    }

    private static void AddPlayerVelocityY(Player player, float deltaY)
    {
        if (Mathf.Abs(deltaY) < 0.000001f)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].vel.y += deltaY;
            }
        }
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
        if (chunk == null)
        {
            surfaceY = 0f;
            signedDepth = 0f;
            depthLength = 0f;
            return false;
        }

        return TrySampleVerticalAtPosition(
            chunk,
            zone,
            chunk.pos.x,
            chunk.pos.y,
            out surfaceY,
            out signedDepth,
            out depthLength);
    }

    private static bool TrySampleVerticalAtPosition(
        BodyChunk chunk,
        QuicksandZone zone,
        float x,
        float y,
        out float surfaceY,
        out float signedDepth,
        out float depthLength)
    {
        if (!TrySampleVerticalAtPositionRaw(
                chunk,
                zone,
                x,
                y,
                out surfaceY,
                out signedDepth,
                out depthLength))
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        return signedDepth >= -radius * DetectionMarginRadii &&
               signedDepth <= depthLength + radius * 0.50f;
    }

    private static bool TrySampleVerticalAtPositionRaw(
        BodyChunk chunk,
        QuicksandZone zone,
        float x,
        float y,
        out float surfaceY,
        out float signedDepth,
        out float depthLength)
    {
        surfaceY = 0f;
        signedDepth = 0f;
        depthLength = 0f;

        if (chunk == null || !IsUsableZone(zone))
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        if (x < zone.startX - radius * 1.15f ||
            x > zone.endX + radius * 1.15f)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(x);
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
        signedDepth = surfaceY - y;
        return true;
    }

    private static bool TrySampleVerticalAtWorldPoint(
        QuicksandZone zone,
        Vector2 point,
        out float surfaceY,
        out float depthLength)
    {
        surfaceY = 0f;
        depthLength = 0f;

        if (!IsUsableZone(zone) || point.x < zone.startX || point.x > zone.endX)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(point.x);
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
        return true;
    }

    private static bool CanControlPlayer(Player player)
    {
        return player != null &&
               player.room != null &&
               player.room.updateList != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0;
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

    private static bool IsPlayerStateValid(Player player, PlayerSinkState state)
    {
        return player != null &&
               player.room != null &&
               state != null &&
               state.Active &&
               IsUsableZone(state.Zone) &&
               state.Zone.room == player.room;
    }

    private static void DeactivatePlayer(PlayerSinkState state)
    {
        if (state == null)
        {
            return;
        }

        state.Active = false;
        state.Zone = null;
        state.Immersion = 0f;
        state.FullySubmergedTicks = 0;
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
