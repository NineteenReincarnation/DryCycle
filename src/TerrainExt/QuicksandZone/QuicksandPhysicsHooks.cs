using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Player interaction is deliberately much more viscous than the generic
    // BodyChunk behaviour. The player lands on the surface, then sinks at a
    // controlled rate instead of falling through the zone under normal gravity.
    private const float PlayerSurfaceRestRadius = 0.86f;
    private const float PlayerSurfaceSinkPerTick = 0.045f;
    private const float PlayerDeepSinkPerTick = 0.075f;
    private const float PlayerHorizontalSpeed = 0.28f;
    private const float PlayerDeathSubmergeRadius = 0.94f;

    private sealed class ZoneCache
    {
        internal readonly Vector2[] Surface = new Vector2[SampleCount];
        internal readonly Vector2[] Bottom = new Vector2[SampleCount];
    }

    private sealed class PlayerChunkState
    {
        internal bool WasInSand;
        internal float LastSignedDepth;
    }

    private static readonly ConditionalWeakTable<QuicksandZone, ZoneCache> ZoneCaches = new();
    private static readonly ConditionalWeakTable<BodyChunk, PlayerChunkState> PlayerChunkStates = new();
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
        if (self?.owner?.room == null)
        {
            orig(self);
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        bool quicksandOverridesTerrain =
            originalTerrainCollision &&
            TryGetQuicksandContact(self, predictive: true, out QuicksandSurface.Contact contact);

        Player player = self.owner as Player;
        PlayerChunkState playerState = player != null
            ? PlayerChunkStates.GetValue(self, _ => new PlayerChunkState())
            : null;

        if (quicksandOverridesTerrain)
        {
            // Quicksand owns collision inside its volume. This disables both vanilla
            // tile collision and Watcher's TerrainManager snap for this BodyChunk
            // update, so solid terrain hidden beneath the zone cannot support it.
            if (player != null)
            {
                PreparePlayerForSandStep(self, player, contact, playerState);
            }
            else
            {
                ApplyEntryResistance(self, contact);
            }

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
            if (player != null)
            {
                FinishPlayerSandStep(self, player, postContact, playerState);
                CheckPlayerFullySubmerged(player);
            }
            else
            {
                ApplyPostStepResistance(self, postContact);
            }
        }
        else if (playerState != null)
        {
            playerState.WasInSand = false;
        }
    }

    private static void PreparePlayerForSandStep(
        BodyChunk chunk,
        Player player,
        QuicksandSurface.Contact contact,
        PlayerChunkState state)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        Vector2 inward = contact.Inward;
        Vector2 tangent = contact.Tangent;

        float currentDepth = Vector2.Dot(chunk.pos - contact.SurfacePoint, inward);

        // First impact: catch the body on top of the quicksand instead of allowing
        // its previous fall velocity to carry it a large distance into the volume.
        if (!state.WasInSand && currentDepth < -radius * 0.48f)
        {
            chunk.pos = contact.SurfacePoint - inward * radius * PlayerSurfaceRestRadius;
            currentDepth = -radius * PlayerSurfaceRestRadius;
        }

        float deepness = Mathf.Clamp01(
            Mathf.Max(0f, currentDepth) /
            Mathf.Max(1f, contact.DepthLength));
        float sinkSpeed = Mathf.Lerp(
            PlayerSurfaceSinkPerTick,
            PlayerDeepSinkPerTick,
            Mathf.SmoothStep(0f, 1f, deepness));

        // Make left/right movement possible but extremely slow. Use the surface
        // tangent so sloped quicksand still follows its edited top curve, while the
        // sign is chosen so input.x continues to mean world left/right.
        float inputX = player.input != null && player.input.Length > 0
            ? player.input[0].x
            : 0f;
        float tangentRightSign = Mathf.Abs(tangent.x) > 0.05f
            ? Mathf.Sign(tangent.x)
            : 1f;
        float targetTangentSpeed = inputX * PlayerHorizontalSpeed * tangentRightSign;

        // BodyChunk.Update will apply gravity after this hook. Pre-compensate its
        // component into the sand so the integrated speed remains at the tiny sink
        // speed above rather than normal free fall.
        float gravityIntoSand = Vector2.Dot(
            Vector2.down * chunk.owner.gravity,
            inward);
        float desiredPreGravityInward = sinkSpeed - gravityIntoSand;

        float currentInwardSpeed = Vector2.Dot(chunk.vel, inward);
        float currentTangentSpeed = Vector2.Dot(chunk.vel, tangent);
        chunk.vel += inward * (desiredPreGravityInward - currentInwardSpeed);
        chunk.vel += tangent * (targetTangentSpeed - currentTangentSpeed);

        state.WasInSand = true;
        state.LastSignedDepth = currentDepth;
    }

    private static void FinishPlayerSandStep(
        BodyChunk chunk,
        Player player,
        QuicksandSurface.Contact contact,
        PlayerChunkState state)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        Vector2 inward = contact.Inward;
        Vector2 tangent = contact.Tangent;
        float currentDepth = Vector2.Dot(chunk.pos - contact.SurfacePoint, inward);
        float deepness = Mathf.Clamp01(
            Mathf.Max(0f, currentDepth) /
            Mathf.Max(1f, contact.DepthLength));
        float sinkSpeed = Mathf.Lerp(
            PlayerSurfaceSinkPerTick,
            PlayerDeepSinkPerTick,
            Mathf.SmoothStep(0f, 1f, deepness));

        // Hard-limit actual penetration per physics tick. This is intentionally a
        // position constraint as well as velocity drag so body connections, impacts
        // and gravity cannot cause a sudden one-frame plunge.
        if (state.WasInSand)
        {
            float maximumDepth = state.LastSignedDepth + sinkSpeed;
            if (currentDepth > maximumDepth)
            {
                float excess = currentDepth - maximumDepth;
                chunk.pos -= inward * excess;
                currentDepth = maximumDepth;
            }
        }

        float inputX = player.input != null && player.input.Length > 0
            ? player.input[0].x
            : 0f;
        float tangentRightSign = Mathf.Abs(tangent.x) > 0.05f
            ? Mathf.Sign(tangent.x)
            : 1f;
        float targetTangentSpeed = inputX * PlayerHorizontalSpeed * tangentRightSign;

        float currentTangentSpeed = Vector2.Dot(chunk.vel, tangent);
        chunk.vel += tangent * (targetTangentSpeed - currentTangentSpeed);

        // Vertical escape is not a jump mechanic here: the sand continuously pulls
        // the player down at the controlled rate. Escaping is done by slowly moving
        // left/right until the body leaves the zone boundary.
        float currentInwardSpeed = Vector2.Dot(chunk.vel, inward);
        chunk.vel += inward * (sinkSpeed - currentInwardSpeed);

        state.WasInSand = true;
        state.LastSignedDepth = currentDepth;
    }

    private static void CheckPlayerFullySubmerged(Player player)
    {
        if (player == null || player.dead || player.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return;
        }

        // "Fully submerged" means the top of every physical body chunk has passed
        // below a quicksand surface. This waits until the whole slugcat body is under
        // rather than killing as soon as the lower chunk disappears.
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TryGetQuicksandContact(
                    chunk,
                    predictive: false,
                    out QuicksandSurface.Contact contact))
            {
                return;
            }

            float signedDepth = Vector2.Dot(
                chunk.pos - contact.SurfacePoint,
                contact.Inward);
            if (signedDepth < chunk.rad * PlayerDeathSubmergeRadius)
            {
                return;
            }
        }

        player.Die();
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

        Vector2 current = chunk.pos;
        Vector2 predicted = current + chunk.vel + Vector2.down * chunk.owner.gravity;
        float radius = Mathf.Max(1f, chunk.rad);
        bool playerChunk = chunk.owner is Player;
        float currentEntryMargin = playerChunk ? 1.02f : 0.32f;
        float predictiveEntryMargin = playerChunk ? 1.02f : 0.12f;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone ||
                zone.slatedForDeletetion ||
                zone.PlacedObject == null ||
                !zone.PlacedObject.active ||
                zone.PlacedObject.data is not QuicksandZoneData data)
            {
                continue;
            }

            ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
            QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);

            if (QuicksandSurface.TryGetContact(
                    current,
                    radius + 1.5f,
                    cache.Surface,
                    cache.Bottom,
                    out QuicksandSurface.Contact currentContact) &&
                IsInsideOverrideBand(currentContact, radius, currentEntryMargin))
            {
                contact = currentContact;
                return true;
            }

            if (!predictive)
            {
                continue;
            }

            float predictiveRadius = radius + Mathf.Min(10f, chunk.vel.magnitude * 0.32f + 2f);
            if (QuicksandSurface.TryGetContact(
                    predicted,
                    predictiveRadius,
                    cache.Surface,
                    cache.Bottom,
                    out QuicksandSurface.Contact predictedContact) &&
                IsInsideOverrideBand(predictedContact, radius, predictiveEntryMargin) &&
                Vector2.Dot(predicted - current, predictedContact.Inward) > -0.05f)
            {
                contact = predictedContact;
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
