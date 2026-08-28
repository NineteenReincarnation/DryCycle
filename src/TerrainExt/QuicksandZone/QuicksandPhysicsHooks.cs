using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Player quicksand is intentionally almost immobile. The whole slugcat is
    // translated as one body so the two BodyChunks never fight each other through
    // BodyChunkConnection and produce the surface-entry jitter seen in the old code.
    private const float PlayerSurfaceRestRadius = 0.90f;
    private const float PlayerSurfaceSinkPerTick = 0.025f;
    private const float PlayerDeepSinkPerTick = 0.040f;
    private const float PlayerHorizontalSpeed = 0.018f;
    private const float PlayerDeathSubmergeRadius = 1.00f;
    private const float PlayerHeadVisualClearance = 8f;
    private const int PlayerDeathConfirmTicks = 8;

    private sealed class ZoneCache
    {
        internal readonly Vector2[] Surface = new Vector2[SampleCount];
        internal readonly Vector2[] Bottom = new Vector2[SampleCount];
    }

    private sealed class PlayerSandState
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal int AnchorChunkIndex = -1;
        internal float TargetSignedDepth;
        internal bool[] CollisionFlags;
        internal int FullySubmergedTicks;
    }

    private static readonly ConditionalWeakTable<QuicksandZone, ZoneCache> ZoneCaches = new();
    private static readonly ConditionalWeakTable<Player, PlayerSandState> PlayerStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.BodyChunk.Update += BodyChunk_Update;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.BodyChunk.Update -= BodyChunk_Update;
        On.Player.Update -= Player_Update;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (self?.room == null || self.bodyChunks == null || self.bodyChunks.Length == 0)
        {
            orig(self, eu);
            return;
        }

        PlayerSandState state = PlayerStates.GetValue(self, _ => new PlayerSandState());

        if (!state.Active)
        {
            if (!TryFindPlayerEntry(
                    self,
                    out QuicksandZone entryZone,
                    out int anchorIndex,
                    out QuicksandSurface.Contact entryContact))
            {
                orig(self, eu);
                return;
            }

            state.Active = true;
            state.Zone = entryZone;
            state.AnchorChunkIndex = anchorIndex;
            state.FullySubmergedTicks = 0;

            BodyChunk entryAnchor = self.bodyChunks[anchorIndex];
            float radius = Mathf.Max(1f, entryAnchor.rad);
            state.TargetSignedDepth = -radius * PlayerSurfaceRestRadius;

            // Catch the entire player at the surface with one translation. Moving
            // both chunks by the same vector preserves their connection length and
            // removes the old per-chunk correction loop that caused up/down shaking.
            float currentDepth = Vector2.Dot(
                entryAnchor.pos - entryContact.SurfacePoint,
                entryContact.Inward);
            TranslatePlayer(
                self,
                entryContact.Inward * (state.TargetSignedDepth - currentDepth));

            KillPlayerMomentum(self);
        }

        if (!TryGetActiveAnchorContact(self, state, predictive: true, out QuicksandSurface.Contact preContact))
        {
            ResetPlayerState(state);
            orig(self, eu);
            return;
        }

        EnsureCollisionFlagCapacity(state, self.bodyChunks.Length);
        Vector2 beforeCenter = PlayerCenter(self);

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            state.CollisionFlags[i] = chunk.collideWithTerrain;
            chunk.collideWithTerrain = false;

            // Remove almost all motion before vanilla movement code runs. Player
            // movement is reintroduced below only through the tiny controlled crawl.
            chunk.vel *= 0.04f;
        }

        try
        {
            orig(self, eu);
        }
        finally
        {
            for (int i = 0; i < self.bodyChunks.Length; i++)
            {
                BodyChunk chunk = self.bodyChunks[i];
                if (chunk != null)
                {
                    chunk.collideWithTerrain = state.CollisionFlags[i];
                }
            }
        }

        if (!TryGetActiveAnchorContact(self, state, predictive: false, out QuicksandSurface.Contact postContact))
        {
            // Use the pre-step surface once as a fallback. This prevents a single
            // vanilla body-connection correction from dropping the sand state for a
            // frame right at the surface.
            postContact = preContact;
        }

        BodyChunk anchor = self.bodyChunks[state.AnchorChunkIndex];
        float currentSignedDepth = Vector2.Dot(
            anchor.pos - postContact.SurfacePoint,
            postContact.Inward);
        float deepness = Mathf.Clamp01(
            Mathf.Max(0f, state.TargetSignedDepth) /
            Mathf.Max(1f, postContact.DepthLength));
        float sinkPerTick = Mathf.Lerp(
            PlayerSurfaceSinkPerTick,
            PlayerDeepSinkPerTick,
            Mathf.SmoothStep(0f, 1f, deepness));

        state.TargetSignedDepth += sinkPerTick;

        // First constrain the whole player to the one authoritative sinking depth.
        // No individual BodyChunk receives its own vertical correction.
        Vector2 correction = postContact.Inward *
                             (state.TargetSignedDepth - currentSignedDepth);

        // Vanilla Player.Update may have applied running/crawling acceleration before
        // this point. Cancel that displacement and replace it with an almost static
        // crawl: 0.018 px/tick = only 0.72 px/s at 40 ticks/s.
        Vector2 afterCenter = PlayerCenter(self);
        float actualHorizontalDisplacement = afterCenter.x - beforeCenter.x;
        float inputX = self.input != null && self.input.Length > 0
            ? self.input[0].x
            : 0f;
        float allowedHorizontalDisplacement = inputX * PlayerHorizontalSpeed;
        correction.x += allowedHorizontalDisplacement - actualHorizontalDisplacement;

        TranslatePlayer(self, correction);

        Vector2 desiredVelocity = postContact.Inward * sinkPerTick;
        desiredVelocity.x = allowedHorizontalDisplacement;
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            if (self.bodyChunks[i] != null)
            {
                self.bodyChunks[i].vel = desiredVelocity;
            }
        }

        CheckPlayerFullySubmerged(self, state);

        // Once the anchor has actually left the edited quicksand volume, restore
        // normal player physics on the next frame.
        if (!TryGetContactInZone(anchor, state.Zone, predictive: true, playerMargin: true, out _))
        {
            ResetPlayerState(state);
        }
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        if (self?.owner?.room == null || self.owner is Player)
        {
            orig(self);
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        QuicksandSurface.Contact contact = default;
        bool quicksandOverridesTerrain =
            originalTerrainCollision &&
            TryGetQuicksandContact(self, predictive: true, out contact);

        if (quicksandOverridesTerrain)
        {
            ApplyEntryResistance(self, contact);
            self.collideWithTerrain = false;
        }

        try
        {
            orig(self);
        }
        finally
        {
            self.collideWithTerrain = originalTerrainCollision;
        }

        if (TryGetQuicksandContact(
                self,
                predictive: false,
                out QuicksandSurface.Contact postContact))
        {
            ApplyPostStepResistance(self, postContact);
        }
    }

    private static bool TryFindPlayerEntry(
        Player player,
        out QuicksandZone zone,
        out int anchorChunkIndex,
        out QuicksandSurface.Contact contact)
    {
        zone = null;
        anchorChunkIndex = -1;
        contact = default;

        if (player?.room?.updateList == null || player.bodyChunks == null)
        {
            return false;
        }

        float bestDepth = float.NegativeInfinity;

        for (int zoneIndex = 0; zoneIndex < player.room.updateList.Count; zoneIndex++)
        {
            if (player.room.updateList[zoneIndex] is not QuicksandZone candidateZone ||
                candidateZone.slatedForDeletetion ||
                candidateZone.PlacedObject == null ||
                !candidateZone.PlacedObject.active ||
                candidateZone.PlacedObject.data is not QuicksandZoneData)
            {
                continue;
            }

            for (int chunkIndex = 0; chunkIndex < player.bodyChunks.Length; chunkIndex++)
            {
                BodyChunk chunk = player.bodyChunks[chunkIndex];
                if (chunk == null ||
                    !TryGetContactInZone(
                        chunk,
                        candidateZone,
                        predictive: true,
                        playerMargin: true,
                        out QuicksandSurface.Contact candidateContact))
                {
                    continue;
                }

                if (candidateContact.SignedDepth > bestDepth)
                {
                    bestDepth = candidateContact.SignedDepth;
                    zone = candidateZone;
                    anchorChunkIndex = chunkIndex;
                    contact = candidateContact;
                }
            }
        }

        return zone != null && anchorChunkIndex >= 0;
    }

    private static bool TryGetActiveAnchorContact(
        Player player,
        PlayerSandState state,
        bool predictive,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (!state.Active ||
            state.Zone == null ||
            state.AnchorChunkIndex < 0 ||
            state.AnchorChunkIndex >= player.bodyChunks.Length ||
            player.bodyChunks[state.AnchorChunkIndex] == null)
        {
            return false;
        }

        return TryGetContactInZone(
            player.bodyChunks[state.AnchorChunkIndex],
            state.Zone,
            predictive,
            playerMargin: true,
            out contact);
    }

    private static bool TryGetContactInZone(
        BodyChunk chunk,
        QuicksandZone zone,
        bool predictive,
        bool playerMargin,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (chunk == null ||
            zone == null ||
            zone.slatedForDeletetion ||
            zone.PlacedObject == null ||
            !zone.PlacedObject.active ||
            zone.PlacedObject.data is not QuicksandZoneData data)
        {
            return false;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);

        float radius = Mathf.Max(1f, chunk.rad);
        float margin = playerMargin ? 1.08f : 0.32f;
        if (QuicksandSurface.TryGetContact(
                chunk.pos,
                radius + 1.5f,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact currentContact) &&
            IsInsideOverrideBand(currentContact, radius, margin))
        {
            contact = currentContact;
            return true;
        }

        if (!predictive)
        {
            return false;
        }

        Vector2 predicted = chunk.pos + chunk.vel + Vector2.down * chunk.owner.gravity;
        float predictiveRadius = radius + Mathf.Min(10f, chunk.vel.magnitude * 0.32f + 2f);
        float predictiveMargin = playerMargin ? 1.08f : 0.12f;
        if (QuicksandSurface.TryGetContact(
                predicted,
                predictiveRadius,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact predictedContact) &&
            IsInsideOverrideBand(predictedContact, radius, predictiveMargin) &&
            Vector2.Dot(predicted - chunk.pos, predictedContact.Inward) > -0.05f)
        {
            contact = predictedContact;
            return true;
        }

        return false;
    }

    private static bool TryGetPointContactInZone(
        Vector2 point,
        float radius,
        QuicksandZone zone,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (zone == null ||
            zone.slatedForDeletetion ||
            zone.PlacedObject == null ||
            !zone.PlacedObject.active ||
            zone.PlacedObject.data is not QuicksandZoneData data)
        {
            return false;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);
        return QuicksandSurface.TryGetContact(
            point,
            Mathf.Max(1f, radius),
            cache.Surface,
            cache.Bottom,
            out contact);
    }

    private static bool TryGetQuicksandContact(
        BodyChunk chunk,
        bool predictive,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        Room room = chunk?.owner?.room;
        if (room?.updateList == null)
        {
            return false;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone)
            {
                continue;
            }

            if (TryGetContactInZone(
                    chunk,
                    zone,
                    predictive,
                    playerMargin: false,
                    out contact))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideOverrideBand(
        QuicksandSurface.Contact contact,
        float radius,
        float entryMargin)
    {
        return contact.SignedDepth >= -radius * entryMargin &&
               contact.SignedDepth <= contact.DepthLength + radius * 0.12f;
    }

    private static void CheckPlayerFullySubmerged(Player player, PlayerSandState state)
    {
        if (player == null ||
            player.dead ||
            player.bodyChunks == null ||
            state == null ||
            state.Zone == null)
        {
            if (state != null)
            {
                state.FullySubmergedTicks = 0;
            }
            return;
        }

        // First require every physical chunk's entire collision circle to be below
        // the surface. This is stricter than the old 0.94-radius check.
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TryGetContactInZone(
                    chunk,
                    state.Zone,
                    predictive: false,
                    playerMargin: true,
                    out QuicksandSurface.Contact contact))
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            float signedDepth = Vector2.Dot(
                chunk.pos - contact.SurfacePoint,
                contact.Inward);
            if (signedDepth < chunk.rad * PlayerDeathSubmergeRadius)
            {
                state.FullySubmergedTicks = 0;
                return;
            }
        }

        // BodyChunks are not the visual top of a slugcat. PlayerGraphics.head sits
        // several pixels above bodyChunks[0], so the old check could kill while the
        // head sprite was still visibly above the sand. Require the actual graphics
        // head to clear the surface as well.
        if (player.graphicsModule is PlayerGraphics graphics && graphics.head != null)
        {
            if (!TryGetPointContactInZone(
                    graphics.head.pos,
                    PlayerHeadVisualClearance + 2f,
                    state.Zone,
                    out QuicksandSurface.Contact headContact))
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            float headDepth = Vector2.Dot(
                graphics.head.pos - headContact.SurfacePoint,
                headContact.Inward);
            if (headDepth < PlayerHeadVisualClearance)
            {
                state.FullySubmergedTicks = 0;
                return;
            }
        }
        else
        {
            // Conservative fallback for frames before PlayerGraphics exists.
            BodyChunk main = player.bodyChunks[0];
            if (main == null ||
                !TryGetContactInZone(
                    main,
                    state.Zone,
                    predictive: false,
                    playerMargin: true,
                    out QuicksandSurface.Contact mainContact))
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            float mainDepth = Vector2.Dot(
                main.pos - mainContact.SurfacePoint,
                mainContact.Inward);
            if (mainDepth < main.rad + PlayerHeadVisualClearance)
            {
                state.FullySubmergedTicks = 0;
                return;
            }
        }

        state.FullySubmergedTicks++;
        if (state.FullySubmergedTicks >= PlayerDeathConfirmTicks)
        {
            player.Die();
        }
    }

    private static void TranslatePlayer(Player player, Vector2 delta)
    {
        if (delta.sqrMagnitude < 0.0000001f)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].pos += delta;
            }
        }
    }

    private static Vector2 PlayerCenter(Player player)
    {
        Vector2 sum = Vector2.zero;
        int count = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] == null)
            {
                continue;
            }

            sum += player.bodyChunks[i].pos;
            count++;
        }

        return count > 0 ? sum / count : Vector2.zero;
    }

    private static void KillPlayerMomentum(Player player)
    {
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].vel = Vector2.zero;
            }
        }
    }

    private static void EnsureCollisionFlagCapacity(PlayerSandState state, int count)
    {
        if (state.CollisionFlags == null || state.CollisionFlags.Length != count)
        {
            state.CollisionFlags = new bool[count];
        }
    }

    private static void ResetPlayerState(PlayerSandState state)
    {
        state.Active = false;
        state.Zone = null;
        state.AnchorChunkIndex = -1;
        state.TargetSignedDepth = 0f;
        state.FullySubmergedTicks = 0;
    }

    private static void ApplyEntryResistance(BodyChunk chunk, QuicksandSurface.Contact contact)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        float immersion = Mathf.Clamp01(
            (contact.SignedDepth + radius) /
            Mathf.Max(1f, radius * 2f));
        float deepness = Mathf.Clamp01(
            Mathf.Max(0f, contact.SignedDepth) /
            Mathf.Max(1f, contact.DepthLength));

        Vector2 inward = contact.Inward;
        Vector2 tangent = contact.Tangent;

        float gravitySupport = chunk.owner.gravity *
                               Mathf.Lerp(0.86f, 0.46f, deepness) *
                               Mathf.SmoothStep(0.25f, 1f, immersion);
        chunk.vel -= inward * gravitySupport;

        float inwardSpeed = Vector2.Dot(chunk.vel, inward);
        if (inwardSpeed > 0f)
        {
            float maximumEntrySpeed = Mathf.Lerp(0.38f, 0.95f, deepness);
            chunk.vel -= inward * Mathf.Max(0f, inwardSpeed - maximumEntrySpeed);
        }

        float tangentSpeed = Vector2.Dot(chunk.vel, tangent);
        float tangentKeep = Mathf.Lerp(0.78f, 0.58f, immersion);
        chunk.vel -= tangent * tangentSpeed * (1f - tangentKeep);
    }

    private static void ApplyPostStepResistance(BodyChunk chunk, QuicksandSurface.Contact contact)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        float immersion = Mathf.Clamp01(
            (contact.SignedDepth + radius) /
            Mathf.Max(1f, radius * 2f));
        float deepness = Mathf.Clamp01(
            Mathf.Max(0f, contact.SignedDepth) /
            Mathf.Max(1f, contact.DepthLength));
        float viscosity = Mathf.Clamp01(Mathf.Max(
            immersion,
            Mathf.Pow(deepness, 0.72f)));

        Vector2 inward = contact.Inward;
        Vector2 tangent = contact.Tangent;

        float tangentSpeed = Vector2.Dot(chunk.vel, tangent);
        float tangentKeep = Mathf.Lerp(0.76f, 0.44f, viscosity);
        chunk.vel -= tangent * tangentSpeed * (1f - tangentKeep);

        float inwardSpeed = Vector2.Dot(chunk.vel, inward);
        if (inwardSpeed > 0f)
        {
            float sinkCap = Mathf.Lerp(0.30f, 0.82f, deepness);
            chunk.vel -= inward * Mathf.Max(0f, inwardSpeed - sinkCap);
        }
        else
        {
            chunk.vel -= inward * inwardSpeed *
                         (1f - Mathf.Lerp(0.82f, 0.66f, viscosity));
        }
    }
}
