using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Physics stay pinned to the quicksand surface. Sinking is a visual-only offset.
    private const float PlayerSurfaceRestRadius = 1.22f;
    private const float ObjectSurfaceRestRadius = 1.10f;

    // Normal movement is intentionally almost nonexistent. Close to an edge, however,
    // sustained outward input is allowed to turn into a slow crawl so escape is possible.
    private const float PlayerHorizontalDistancePerTick = 0.008f;
    private const float PlayerEdgeHorizontalDistancePerTick = 0.050f;
    private const float PlayerEdgeEscapeBand = 24f;
    private const float PlayerExitNudge = 10f;
    private const int PlayerReentryCooldownTicks = 24;

    private const float PlayerVisualSinkPerTick = 0.055f;
    private const float PlayerEscapeRisePerTick = 0.090f;
    private const float ObjectVisualSinkPerTick = 0.045f;
    private const float PlayerVisualSinkLimit = 64f;
    private const float PlayerHeadVisualClearance = 8f;
    private const int PlayerDeathConfirmTicks = 8;

    private sealed class ZoneCache
    {
        internal readonly Vector2[] Surface = new Vector2[SampleCount];
        internal readonly Vector2[] Bottom = new Vector2[SampleCount];
    }

    private sealed class SinkState
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal int AnchorChunkIndex = -1;
        internal float SurfaceU;
        internal float VisualSink;
        internal float VisualSinkLimit;
        internal int FullySubmergedTicks;
        internal int ReentryCooldownTicks;
    }

    private static readonly ConditionalWeakTable<QuicksandZone, ZoneCache> ZoneCaches = new();
    private static readonly ConditionalWeakTable<PhysicalObject, SinkState> SinkStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.BodyChunk.Update += BodyChunk_Update;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.BodyChunk.Update -= BodyChunk_Update;
        On.Room.Update -= Room_Update;
    }

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
            !SinkStates.TryGetValue(physicalObject, out SinkState state) ||
            !state.Active ||
            state.Zone == null ||
            !TrySampleSurfaceFrame(
                state.Zone,
                state.SurfaceU,
                out _,
                out _,
                out Vector2 inward,
                out _))
        {
            return false;
        }

        visualOffset = inward * state.VisualSink;
        zone = state.Zone;
        progress = state.VisualSinkLimit > 0.001f
            ? Mathf.Clamp01(state.VisualSink / state.VisualSinkLimit)
            : 0f;
        return true;
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanSink(owner))
        {
            orig(self);
            return;
        }

        SinkState state = SinkStates.GetValue(owner, _ => new SinkState());

        if (owner is not Player && owner.grabbedBy != null && owner.grabbedBy.Count > 0)
        {
            ResetState(state, keepCooldown: false);
            orig(self);
            return;
        }

        if (!state.Active && state.ReentryCooldownTicks <= 0 &&
            TryFindEntry(
                owner,
                out QuicksandZone entryZone,
                out int anchorIndex,
                out QuicksandSurface.Contact entryContact))
        {
            Activate(owner, state, entryZone, anchorIndex, entryContact);
        }

        if (!state.Active)
        {
            orig(self);
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;

        // Vanilla physics can animate internally, but none of that momentum is allowed
        // to move the trapped physical layer. Room_Update pins it back to the surface.
        self.vel *= owner is Player ? 0.015f : 0.04f;

        try
        {
            orig(self);
        }
        finally
        {
            self.collideWithTerrain = originalTerrainCollision;
        }
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);

        if (self?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < self.physicalObjects.Length; layer++)
        {
            var objects = self.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                PhysicalObject physicalObject = objects[i];
                if (!CanSink(physicalObject) ||
                    !SinkStates.TryGetValue(physicalObject, out SinkState state))
                {
                    continue;
                }

                if (!state.Active)
                {
                    if (state.ReentryCooldownTicks > 0)
                    {
                        state.ReentryCooldownTicks--;
                    }
                    continue;
                }

                UpdatePinnedObject(physicalObject, state);
            }
        }
    }

    private static bool CanSink(PhysicalObject physicalObject)
    {
        if (physicalObject?.room == null ||
            physicalObject.bodyChunks == null ||
            physicalObject.bodyChunks.Length == 0)
        {
            return false;
        }

        // Shared sinking presentation is currently for the player and loose items.
        return physicalObject is Player || physicalObject is not Creature;
    }

    private static void Activate(
        PhysicalObject physicalObject,
        SinkState state,
        QuicksandZone zone,
        int anchorIndex,
        QuicksandSurface.Contact contact)
    {
        state.Active = true;
        state.Zone = zone;
        state.AnchorChunkIndex = Mathf.Clamp(anchorIndex, 0, physicalObject.bodyChunks.Length - 1);
        state.SurfaceU = Mathf.Clamp01(contact.U);
        state.VisualSink = 0f;
        state.FullySubmergedTicks = 0;
        state.ReentryCooldownTicks = 0;

        if (TrySampleSurfaceFrame(
                zone,
                state.SurfaceU,
                out _,
                out _,
                out Vector2 inward,
                out _))
        {
            state.VisualSinkLimit = physicalObject is Player
                ? PlayerVisualSinkLimit
                : ComputeObjectVisualSinkLimit(physicalObject, inward);
        }
        else
        {
            state.VisualSinkLimit = physicalObject is Player ? PlayerVisualSinkLimit : 32f;
        }
    }

    private static void UpdatePinnedObject(PhysicalObject physicalObject, SinkState state)
    {
        if (physicalObject.room == null ||
            state.Zone == null ||
            state.Zone.slatedForDeletetion ||
            state.Zone.PlacedObject == null ||
            !state.Zone.PlacedObject.active ||
            state.AnchorChunkIndex < 0 ||
            state.AnchorChunkIndex >= physicalObject.bodyChunks.Length)
        {
            ResetState(state, keepCooldown: false);
            return;
        }

        if (physicalObject is not Player &&
            physicalObject.grabbedBy != null &&
            physicalObject.grabbedBy.Count > 0)
        {
            ResetState(state, keepCooldown: false);
            return;
        }

        BodyChunk anchor = physicalObject.bodyChunks[state.AnchorChunkIndex];
        if (anchor == null)
        {
            ResetState(state, keepCooldown: false);
            return;
        }

        if (!TrySampleSurfaceFrame(
                state.Zone,
                state.SurfaceU,
                out Vector2 surfacePoint,
                out Vector2 tangent,
                out Vector2 inward,
                out _))
        {
            ResetState(state, keepCooldown: false);
            return;
        }

        bool playerTryingToEscape = false;

        if (physicalObject is Player player)
        {
            float inputX = player.input != null && player.input.Length > 0
                ? player.input[0].x
                : 0f;

            if (inputX != 0f)
            {
                float surfaceLength = Mathf.Max(1f, EstimateSurfaceLength(state.Zone));
                float tangentWorldSign = Mathf.Abs(tangent.x) > 0.05f
                    ? Mathf.Sign(tangent.x)
                    : 1f;

                // Positive moveAlongU means toward u=1; negative means toward u=0.
                float moveAlongU = inputX * tangentWorldSign;
                float distanceToRequestedEdge = moveAlongU < 0f
                    ? state.SurfaceU * surfaceLength
                    : (1f - state.SurfaceU) * surfaceLength;

                float edgeFactor = 1f - Mathf.Clamp01(distanceToRequestedEdge / PlayerEdgeEscapeBand);
                float moveDistance = Mathf.Lerp(
                    PlayerHorizontalDistancePerTick,
                    PlayerEdgeHorizontalDistancePerTick,
                    Mathf.SmoothStep(0f, 1f, edgeFactor));

                playerTryingToEscape = distanceToRequestedEdge <= PlayerEdgeEscapeBand;

                float deltaU = moveAlongU * moveDistance / surfaceLength;
                float nextU = state.SurfaceU + deltaU;

                if (nextU <= 0f || nextU >= 1f)
                {
                    // Actually leave the edited band instead of merely clearing the
                    // state while still touching it. Cooldown prevents immediate
                    // recapture by the predictive entry test on the next frame.
                    Vector2 outward = SafeNormal(tangent, Vector2.right) * Mathf.Sign(deltaU);
                    float nudgeDistance = Mathf.Max(
                        PlayerExitNudge,
                        anchor.rad * 1.8f + 4f);
                    TranslatePhysicalObject(physicalObject, outward * nudgeDistance);
                    SetObjectVelocity(physicalObject, outward * 0.35f);
                    ReleaseFromSand(state, PlayerReentryCooldownTicks);
                    return;
                }

                state.SurfaceU = Mathf.Clamp01(nextU);

                if (!TrySampleSurfaceFrame(
                        state.Zone,
                        state.SurfaceU,
                        out surfacePoint,
                        out tangent,
                        out inward,
                        out _))
                {
                    ResetState(state, keepCooldown: false);
                    return;
                }
            }
        }

        float restRadius = physicalObject is Player
            ? PlayerSurfaceRestRadius
            : ObjectSurfaceRestRadius;
        Vector2 targetAnchor = surfacePoint - inward * Mathf.Max(1f, anchor.rad) * restRadius;

        // Physical placement never sinks. Only the renderer moves inward.
        Vector2 correction = targetAnchor - anchor.pos;
        TranslatePhysicalObject(physicalObject, correction);
        KillMomentum(physicalObject);

        if (physicalObject is Player)
        {
            if (playerTryingToEscape)
            {
                // Near an edge, sustained outward struggle slowly pulls the visible
                // body back out of the sand so the crawl can finish before drowning.
                state.VisualSink = Mathf.Max(
                    0f,
                    state.VisualSink - PlayerEscapeRisePerTick);
                state.FullySubmergedTicks = 0;
            }
            else
            {
                state.VisualSink = Mathf.Min(
                    state.VisualSinkLimit,
                    state.VisualSink + PlayerVisualSinkPerTick);
            }
        }
        else
        {
            state.VisualSink = Mathf.Min(
                state.VisualSinkLimit,
                state.VisualSink + ObjectVisualSinkPerTick);
        }

        if (physicalObject is Player sinkingPlayer)
        {
            CheckPlayerVisualSubmersion(sinkingPlayer, state, surfacePoint, inward);
        }
    }

    private static void CheckPlayerVisualSubmersion(
        Player player,
        SinkState state,
        Vector2 surfacePoint,
        Vector2 inward)
    {
        if (player == null || player.dead)
        {
            state.FullySubmergedTicks = 0;
            return;
        }

        Vector2 visualHead;
        if (player.graphicsModule is PlayerGraphics graphics && graphics.head != null)
        {
            visualHead = graphics.head.pos + inward * state.VisualSink;
        }
        else
        {
            BodyChunk main = player.bodyChunks[0];
            if (main == null)
            {
                state.FullySubmergedTicks = 0;
                return;
            }

            visualHead = main.pos + Vector2.up * (main.rad + PlayerHeadVisualClearance) +
                         inward * state.VisualSink;
        }

        float visualHeadDepth = Vector2.Dot(visualHead - surfacePoint, inward);
        if (visualHeadDepth < PlayerHeadVisualClearance)
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

    private static bool TryFindEntry(
        PhysicalObject physicalObject,
        out QuicksandZone zone,
        out int anchorChunkIndex,
        out QuicksandSurface.Contact contact)
    {
        zone = null;
        anchorChunkIndex = -1;
        contact = default;

        Room room = physicalObject?.room;
        if (room?.updateList == null || physicalObject.bodyChunks == null)
        {
            return false;
        }

        float bestDepth = float.NegativeInfinity;

        for (int zoneIndex = 0; zoneIndex < room.updateList.Count; zoneIndex++)
        {
            if (room.updateList[zoneIndex] is not QuicksandZone candidateZone ||
                candidateZone.slatedForDeletetion ||
                candidateZone.PlacedObject == null ||
                !candidateZone.PlacedObject.active ||
                candidateZone.PlacedObject.data is not QuicksandZoneData)
            {
                continue;
            }

            for (int chunkIndex = 0; chunkIndex < physicalObject.bodyChunks.Length; chunkIndex++)
            {
                BodyChunk chunk = physicalObject.bodyChunks[chunkIndex];
                if (chunk == null ||
                    !TryGetContactInZone(
                        chunk,
                        candidateZone,
                        predictive: true,
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

    private static bool TryGetContactInZone(
        BodyChunk chunk,
        QuicksandZone zone,
        bool predictive,
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
        if (QuicksandSurface.TryGetContact(
                chunk.pos,
                radius + 2f,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact currentContact) &&
            IsInsideEntryBand(currentContact, radius, 1.10f))
        {
            contact = currentContact;
            return true;
        }

        if (!predictive)
        {
            return false;
        }

        Vector2 predicted = chunk.pos + chunk.vel + Vector2.down * chunk.owner.gravity;
        float predictiveRadius = radius + Mathf.Min(12f, chunk.vel.magnitude * 0.36f + 3f);
        if (QuicksandSurface.TryGetContact(
                predicted,
                predictiveRadius,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact predictedContact) &&
            IsInsideEntryBand(predictedContact, radius, 1.12f) &&
            Vector2.Dot(predicted - chunk.pos, predictedContact.Inward) > -0.05f)
        {
            contact = predictedContact;
            return true;
        }

        return false;
    }

    private static bool IsInsideEntryBand(
        QuicksandSurface.Contact contact,
        float radius,
        float entryMargin)
    {
        return contact.SignedDepth >= -radius * entryMargin &&
               contact.SignedDepth <= contact.DepthLength + radius * 0.12f;
    }

    private static bool TrySampleSurfaceFrame(
        QuicksandZone zone,
        float u,
        out Vector2 surfacePoint,
        out Vector2 tangent,
        out Vector2 inward,
        out float depthLength)
    {
        surfacePoint = Vector2.zero;
        tangent = Vector2.right;
        inward = Vector2.down;
        depthLength = 0f;

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

        float scaled = Mathf.Clamp01(u) * (SampleCount - 1);
        int segment = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, SampleCount - 2);
        float t = Mathf.Clamp01(scaled - segment);

        Vector2 surfaceA = cache.Surface[segment];
        Vector2 surfaceB = cache.Surface[segment + 1];
        Vector2 bottomA = cache.Bottom[segment];
        Vector2 bottomB = cache.Bottom[segment + 1];

        surfacePoint = Vector2.Lerp(surfaceA, surfaceB, t);
        Vector2 bottomPoint = Vector2.Lerp(bottomA, bottomB, t);
        tangent = SafeNormal(surfaceB - surfaceA, Vector2.right);

        Vector2 depthVector = bottomPoint - surfacePoint;
        depthLength = depthVector.magnitude;
        if (depthLength < 4f)
        {
            return false;
        }

        Vector2 geometricNormal = SafeNormal(
            new Vector2(tangent.y, -tangent.x),
            Vector2.down);
        if (Vector2.Dot(geometricNormal, depthVector) < 0f)
        {
            geometricNormal = -geometricNormal;
        }

        inward = geometricNormal;
        return true;
    }

    private static float EstimateSurfaceLength(QuicksandZone zone)
    {
        if (zone == null ||
            zone.PlacedObject == null ||
            zone.PlacedObject.data is not QuicksandZoneData data)
        {
            return 1f;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);

        float total = 0f;
        for (int i = 0; i < SampleCount - 1; i++)
        {
            total += Vector2.Distance(cache.Surface[i], cache.Surface[i + 1]);
        }

        return Mathf.Max(1f, total);
    }

    private static float ComputeObjectVisualSinkLimit(
        PhysicalObject physicalObject,
        Vector2 inward)
    {
        if (physicalObject?.bodyChunks == null || physicalObject.bodyChunks.Length == 0)
        {
            return 32f;
        }

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        float largestRadius = 1f;

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            float projection = Vector2.Dot(chunk.pos, inward);
            min = Mathf.Min(min, projection - chunk.rad);
            max = Mathf.Max(max, projection + chunk.rad);
            largestRadius = Mathf.Max(largestRadius, chunk.rad);
        }

        if (float.IsInfinity(min) || float.IsInfinity(max))
        {
            return 32f;
        }

        return Mathf.Max(28f, max - min + largestRadius + 8f);
    }

    private static void TranslatePhysicalObject(PhysicalObject physicalObject, Vector2 delta)
    {
        if (physicalObject == null || delta.sqrMagnitude < 0.0000001f)
        {
            return;
        }

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            chunk.pos += delta;
            chunk.lastPos += delta;
            chunk.lastLastPos += delta;
        }

        if (physicalObject.graphicsModule?.bodyParts != null)
        {
            for (int i = 0; i < physicalObject.graphicsModule.bodyParts.Length; i++)
            {
                BodyPart part = physicalObject.graphicsModule.bodyParts[i];
                if (part == null)
                {
                    continue;
                }

                part.pos += delta;
                part.lastPos += delta;
            }
        }
    }

    private static void SetObjectVelocity(PhysicalObject physicalObject, Vector2 velocity)
    {
        if (physicalObject?.bodyChunks == null)
        {
            return;
        }

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            if (physicalObject.bodyChunks[i] != null)
            {
                physicalObject.bodyChunks[i].vel = velocity;
            }
        }
    }

    private static void KillMomentum(PhysicalObject physicalObject)
    {
        SetObjectVelocity(physicalObject, Vector2.zero);
    }

    private static void ReleaseFromSand(SinkState state, int cooldownTicks)
    {
        state.Active = false;
        state.Zone = null;
        state.AnchorChunkIndex = -1;
        state.SurfaceU = 0f;
        state.VisualSink = 0f;
        state.VisualSinkLimit = 0f;
        state.FullySubmergedTicks = 0;
        state.ReentryCooldownTicks = Mathf.Max(0, cooldownTicks);
    }

    private static void ResetState(SinkState state, bool keepCooldown)
    {
        int cooldown = keepCooldown ? state.ReentryCooldownTicks : 0;
        ReleaseFromSand(state, cooldown);
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
