using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    private const float PlayerSurfaceRestRadius = 1.22f;
    private const float ObjectSurfaceRestRadius = 1.10f;
    private const float PlayerCrawlDistancePerTick = 0.022f;
    private const float PlayerEdgeCrawlDistancePerTick = 0.095f;
    private const float PlayerEdgeAssistDistance = 24f;
    private const float PlayerIdleVisualSinkPerTick = 0.055f;
    private const float PlayerStruggleVisualSinkPerTick = 0.008f;
    private const float PlayerEdgeRecoveryPerTick = 0.11f;
    private const float ObjectVisualSinkPerTick = 0.045f;
    private const float PlayerVisualSinkLimit = 64f;
    private const float PlayerHeadVisualClearance = 8f;
    private const int PlayerDeathConfirmTicks = 8;
    private const int ExitReentryCooldown = 24;

    // The quicksand pin must never override an external warp/teleport. Normal
    // quicksand motion is sub-pixel to a few pixels per frame, while Dev Console
    // ov, shortcuts and scripted warps move the anchor much farther in one step.
    private const float ExternalDisplacementReleaseDistance = 32f;

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
        internal int ReentryCooldown;
        internal bool HasPinnedAnchor;
        internal Vector2 LastPinnedAnchor;
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
            !state.Zone.TrySampleSurfaceFrame(
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
            Deactivate(state, 0);
            orig(self);
            return;
        }

        // Detect an external teleport before suppressing terrain collision or
        // damping velocity. Otherwise the next Room.Update would snap the object
        // straight back to its old SurfaceU, making commands such as ov appear to
        // have failed.
        if (state.Active && ShouldReleaseForExternalMove(owner, state))
        {
            Deactivate(state, ExitReentryCooldown);
        }

        if (!state.Active &&
            state.ReentryCooldown <= 0 &&
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

                if (state.ReentryCooldown > 0)
                {
                    state.ReentryCooldown--;
                }

                if (!state.Active)
                {
                    continue;
                }

                if (ShouldReleaseForExternalMove(physicalObject, state))
                {
                    Deactivate(state, ExitReentryCooldown);
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

        return physicalObject is Player || physicalObject is not Creature;
    }

    private static bool ShouldReleaseForExternalMove(
        PhysicalObject physicalObject,
        SinkState state)
    {
        if (physicalObject == null ||
            state == null ||
            !state.Active ||
            state.Zone == null)
        {
            return false;
        }

        // A room transfer must always win over the old quicksand state.
        if (state.Zone.room != physicalObject.room)
        {
            return true;
        }

        if (!state.HasPinnedAnchor ||
            state.AnchorChunkIndex < 0 ||
            state.AnchorChunkIndex >= physicalObject.bodyChunks.Length)
        {
            return false;
        }

        BodyChunk anchor = physicalObject.bodyChunks[state.AnchorChunkIndex];
        if (anchor == null)
        {
            return true;
        }

        float releaseDistance = Mathf.Max(
            ExternalDisplacementReleaseDistance,
            Mathf.Max(1f, anchor.rad) * 4f);
        return Vector2.Distance(anchor.pos, state.LastPinnedAnchor) > releaseDistance;
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

        BodyChunk anchor = physicalObject.bodyChunks[state.AnchorChunkIndex];
        state.HasPinnedAnchor = anchor != null;
        state.LastPinnedAnchor = anchor?.pos ?? Vector2.zero;

        if (zone.TrySampleSurfaceFrame(
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
            state.Zone.room != physicalObject.room ||
            state.Zone.slatedForDeletetion ||
            state.Zone.PlacedObject == null ||
            !state.Zone.PlacedObject.active ||
            state.Zone.Data == null ||
            state.AnchorChunkIndex < 0 ||
            state.AnchorChunkIndex >= physicalObject.bodyChunks.Length)
        {
            Deactivate(state, 0);
            return;
        }

        if (physicalObject is not Player &&
            physicalObject.grabbedBy != null &&
            physicalObject.grabbedBy.Count > 0)
        {
            Deactivate(state, 0);
            return;
        }

        if (!state.Zone.Data.IsQuicksand(state.SurfaceU))
        {
            Deactivate(state, ExitReentryCooldown);
            return;
        }

        BodyChunk anchor = physicalObject.bodyChunks[state.AnchorChunkIndex];
        if (anchor == null ||
            !state.Zone.TrySampleSurfaceFrame(
                state.SurfaceU,
                out Vector2 surfacePoint,
                out Vector2 tangent,
                out Vector2 inward,
                out _))
        {
            Deactivate(state, 0);
            return;
        }

        float playerInputX = 0f;
        float edgeRecovery = 0f;

        if (physicalObject is Player player)
        {
            playerInputX = player.input != null && player.input.Length > 0
                ? player.input[0].x
                : 0f;

            if (playerInputX != 0f &&
                state.Zone.Data.TryGetQuicksandInterval(
                    state.SurfaceU,
                    out float intervalStart,
                    out float intervalEnd))
            {
                float tangentWorldSign = Mathf.Abs(tangent.x) > 0.05f
                    ? Mathf.Sign(tangent.x)
                    : 1f;
                float directionU = Mathf.Sign(playerInputX * tangentWorldSign);
                float exitU = directionU > 0f ? intervalEnd : intervalStart;
                float surfaceLength = state.Zone.EstimateSurfaceLength();
                float distanceToExit = Mathf.Abs(exitU - state.SurfaceU) * surfaceLength;
                float edgeFactor = 1f - Mathf.InverseLerp(
                    4f,
                    PlayerEdgeAssistDistance,
                    distanceToExit);
                edgeFactor = Mathf.Clamp01(edgeFactor);

                float crawlDistance = Mathf.Lerp(
                    PlayerCrawlDistancePerTick,
                    PlayerEdgeCrawlDistancePerTick,
                    edgeFactor);
                float nextU = state.SurfaceU +
                              directionU * crawlDistance / surfaceLength;

                bool crossedBoundary = directionU > 0f
                    ? nextU >= exitU - 0.00001f
                    : nextU <= exitU + 0.00001f;

                if (crossedBoundary || !state.Zone.Data.IsQuicksand(Mathf.Clamp01(nextU)))
                {
                    ExitToTerrain(
                        physicalObject,
                        state,
                        anchor,
                        exitU,
                        directionU);
                    return;
                }

                state.SurfaceU = Mathf.Clamp01(nextU);
                edgeRecovery = PlayerEdgeRecoveryPerTick * edgeFactor;

                if (!state.Zone.TrySampleSurfaceFrame(
                        state.SurfaceU,
                        out surfacePoint,
                        out tangent,
                        out inward,
                        out _))
                {
                    Deactivate(state, 0);
                    return;
                }
            }
        }

        float restRadius = physicalObject is Player
            ? PlayerSurfaceRestRadius
            : ObjectSurfaceRestRadius;
        Vector2 targetAnchor = surfacePoint -
                               inward * Mathf.Max(1f, anchor.rad) * restRadius;
        TranslatePhysicalObject(physicalObject, targetAnchor - anchor.pos);
        KillMomentum(physicalObject);

        state.LastPinnedAnchor = targetAnchor;
        state.HasPinnedAnchor = true;

        float sinkDelta;
        if (physicalObject is Player)
        {
            sinkDelta = playerInputX == 0f
                ? PlayerIdleVisualSinkPerTick
                : PlayerStruggleVisualSinkPerTick;
            sinkDelta -= edgeRecovery;
        }
        else
        {
            sinkDelta = ObjectVisualSinkPerTick;
        }

        state.VisualSink = Mathf.Clamp(
            state.VisualSink + sinkDelta,
            0f,
            state.VisualSinkLimit);

        if (physicalObject is Player sinkingPlayer)
        {
            CheckPlayerVisualSubmersion(sinkingPlayer, state, surfacePoint, inward);
        }
    }

    private static void ExitToTerrain(
        PhysicalObject physicalObject,
        SinkState state,
        BodyChunk anchor,
        float boundaryU,
        float directionU)
    {
        float surfaceLength = state.Zone.EstimateSurfaceLength();
        float epsilonU = Mathf.Min(0.012f, 3f / surfaceLength);
        float terrainU = Mathf.Clamp01(boundaryU + directionU * epsilonU);

        if (!state.Zone.TrySampleSurfaceFrame(
                terrainU,
                out Vector2 surfacePoint,
                out Vector2 tangent,
                out Vector2 inward,
                out _))
        {
            Deactivate(state, ExitReentryCooldown);
            return;
        }

        float restRadius = physicalObject is Player
            ? PlayerSurfaceRestRadius
            : ObjectSurfaceRestRadius;
        Vector2 targetAnchor = surfacePoint -
                               inward * Mathf.Max(1f, anchor.rad) * restRadius +
                               tangent * directionU * 2f;
        TranslatePhysicalObject(physicalObject, targetAnchor - anchor.pos);
        KillMomentum(physicalObject);
        Deactivate(state, ExitReentryCooldown);
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
                candidateZone.Data == null)
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
                        out QuicksandSurface.Contact candidateContact) ||
                    !candidateZone.Data.IsQuicksand(candidateContact.U))
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
            zone.Data == null)
        {
            return false;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, zone.Data, cache.Surface, cache.Bottom);

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

    private static void KillMomentum(PhysicalObject physicalObject)
    {
        if (physicalObject?.bodyChunks == null)
        {
            return;
        }

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            if (physicalObject.bodyChunks[i] != null)
            {
                physicalObject.bodyChunks[i].vel = Vector2.zero;
            }
        }
    }

    private static void Deactivate(SinkState state, int cooldown)
    {
        state.Active = false;
        state.Zone = null;
        state.AnchorChunkIndex = -1;
        state.SurfaceU = 0f;
        state.VisualSink = 0f;
        state.VisualSinkLimit = 0f;
        state.FullySubmergedTicks = 0;
        state.ReentryCooldown = Mathf.Max(state.ReentryCooldown, cooldown);
        state.HasPinnedAnchor = false;
        state.LastPinnedAnchor = Vector2.zero;
    }
}
