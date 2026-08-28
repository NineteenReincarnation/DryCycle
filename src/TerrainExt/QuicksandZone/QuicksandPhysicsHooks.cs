using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Real physical sinking: the body is never pinned to SurfaceU and there is no
    // synthetic render offset. Strong sand pull plus a very low terminal speed makes
    // entry immediate while immersion remains slow.
    private const float PlayerSurfaceSinkSpeed = 0.028f;
    private const float PlayerDeepSinkSpeed = 0.045f;
    private const float PlayerEntrySinkSpeed = 0.014f;
    private const float PlayerHorizontalSpeed = 0.018f;
    private const float ObjectSinkSpeed = 0.035f;
    private const float ObjectTangentialRetention = 0.08f;
    private const float PlayerSinkPull = 0.35f;
    private const float PlayerEntryLookAheadTicks = 2.5f;
    private const float PlayerInfluenceMargin = 2.35f;
    private const int PlayerEntryRampTicks = 12;
    private const int PlayerContactGraceTicks = 8;
    private const float PlayerHeadVisualClearance = 8f;
    private const int PlayerDeathConfirmTicks = 8;
    private const int ExitReentryCooldown = 6;

    private sealed class ZoneCache
    {
        internal readonly Vector2[] Surface = new Vector2[SampleCount];
        internal readonly Vector2[] Bottom = new Vector2[SampleCount];
    }

    private sealed class SinkState
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal int FullySubmergedTicks;
        internal int ReentryCooldown;
        internal int EntryTicks;
        internal int ContactGraceTicks;
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
        On.Player.Update += Player_Update;
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
        On.Player.Update -= Player_Update;
        On.Room.Update -= Room_Update;
    }

    // Rendering only needs to know whether the object belongs behind the terrain.
    // visualOffset is intentionally zero because BodyChunks themselves really sink.
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
            state.Zone == null)
        {
            return false;
        }

        zone = state.Zone;
        return true;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!CanSink(self))
        {
            orig(self, eu);
            return;
        }

        SinkState state = SinkStates.GetValue(self, _ => new SinkState());

        if (state.Active && !IsStateValid(self, state))
        {
            Deactivate(state, ExitReentryCooldown);
        }

        // Detect the approach before either BodyChunk updates. The old per-chunk
        // hook let one chunk hit solid terrain and bounce before the other chunk
        // discovered quicksand.
        if (!state.Active &&
            state.ReentryCooldown <= 0 &&
            TryFindPlayerEntry(self, out QuicksandZone entryZone))
        {
            Activate(state, entryZone);
        }

        if (!state.Active)
        {
            orig(self, eu);
            return;
        }

        bool[] collisionFlags = new bool[self.bodyChunks.Length];
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            collisionFlags[i] = chunk.collideWithTerrain;
            chunk.collideWithTerrain = false;

            if (TryGetPlayerInfluenceContact(
                    chunk,
                    state.Zone,
                    predictive: true,
                    out QuicksandSurface.Contact contact))
            {
                ApplyPlayerSandVelocity(chunk, self, state, contact, preGravity: true);
            }
        }

        try
        {
            orig(self, eu);
        }
        finally
        {
            for (int i = 0; i < self.bodyChunks.Length; i++)
            {
                if (self.bodyChunks[i] != null)
                {
                    self.bodyChunks[i].collideWithTerrain = collisionFlags[i];
                }
            }
        }

        bool hasInfluence = false;
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            if (TryGetPlayerInfluenceContact(
                    chunk,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact currentContact))
            {
                hasInfluence = true;
                ApplyPlayerSandVelocity(
                    chunk,
                    self,
                    state,
                    currentContact,
                    preGravity: false);
            }
            else if (TryGetPlayerInfluenceContact(
                         chunk,
                         state.Zone,
                         predictive: true,
                         out _))
            {
                hasInfluence = true;
            }
        }

        if (hasInfluence)
        {
            state.ContactGraceTicks = PlayerContactGraceTicks;
        }
        else
        {
            state.ContactGraceTicks--;
            if (state.ContactGraceTicks <= 0)
            {
                Deactivate(state, ExitReentryCooldown);
                return;
            }
        }

        state.EntryTicks++;
        CheckPlayerFullySubmerged(self, state);
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanSink(owner) || owner is Player)
        {
            // Player BodyChunks are controlled atomically by Player_Update. This
            // hook remains for loose items.
            orig(self);
            return;
        }

        SinkState state = SinkStates.GetValue(owner, _ => new SinkState());

        if (owner.grabbedBy != null && owner.grabbedBy.Count > 0)
        {
            Deactivate(state, 0);
            orig(self);
            return;
        }

        if (state.Active && !IsStateValid(owner, state))
        {
            Deactivate(state, ExitReentryCooldown);
        }

        if (!state.Active &&
            state.ReentryCooldown <= 0 &&
            TryFindEntry(owner, out QuicksandZone entryZone, out _))
        {
            Activate(state, entryZone);
        }

        if (!state.Active ||
            !TryGetContactInZone(
                self,
                state.Zone,
                predictive: true,
                out QuicksandSurface.Contact contact) ||
            !state.Zone.Data.IsQuicksand(contact.U))
        {
            orig(self);
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;
        ApplyObjectSandVelocity(self, owner, preGravity: true);

        try
        {
            orig(self);

            if (state.Active &&
                TryGetContactInZone(
                    self,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact currentContact) &&
                state.Zone.Data.IsQuicksand(currentContact.U))
            {
                ApplyObjectSandVelocity(self, owner, preGravity: false);
            }
            else
            {
                self.vel *= 0.30f;
            }
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

                // Player contact and lifetime are handled atomically by Player_Update.
                if (!state.Active || physicalObject is Player)
                {
                    continue;
                }

                if (!IsStateValid(physicalObject, state) ||
                    !HasQuicksandContact(physicalObject, state.Zone, predictive: false))
                {
                    Deactivate(state, ExitReentryCooldown);
                }
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

    private static bool IsStateValid(PhysicalObject physicalObject, SinkState state)
    {
        return physicalObject != null &&
               physicalObject.room != null &&
               state != null &&
               state.Active &&
               state.Zone != null &&
               state.Zone.room == physicalObject.room &&
               !state.Zone.slatedForDeletetion &&
               state.Zone.PlacedObject != null &&
               state.Zone.PlacedObject.active &&
               state.Zone.Data != null;
    }

    private static void Activate(SinkState state, QuicksandZone zone)
    {
        state.Active = true;
        state.Zone = zone;
        state.FullySubmergedTicks = 0;
        state.EntryTicks = 0;
        state.ContactGraceTicks = PlayerContactGraceTicks;
    }

    private static void ApplyPlayerSandVelocity(
        BodyChunk chunk,
        Player player,
        SinkState state,
        QuicksandSurface.Contact contact,
        bool preGravity)
    {
        float deepness = Mathf.Clamp01(
            contact.SignedDepth / Mathf.Max(1f, contact.DepthLength));
        float targetSinkSpeed = Mathf.Lerp(
            PlayerSurfaceSinkSpeed,
            PlayerDeepSinkSpeed,
            Mathf.SmoothStep(0f, 1f, deepness));
        float entryFactor = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01((float)state.EntryTicks / PlayerEntryRampTicks));
        float sinkSpeed = Mathf.Lerp(
            PlayerEntrySinkSpeed,
            targetSinkSpeed,
            entryFactor);

        float inputX = player.input != null && player.input.Length > 0
            ? player.input[0].x
            : 0f;

        // World-down is deliberate: a sloped curve must not generate unsolicited
        // horizontal drift. PlayerSinkPull is far larger than the terminal speed;
        // the sand's viscosity clamps it to sinkSpeed, giving strong pull but slow
        // visible immersion.
        float dampedVertical = Mathf.Min(0f, chunk.vel.y) * 0.02f - PlayerSinkPull;
        float vertical = Mathf.Max(dampedVertical, -sinkSpeed);
        if (preGravity)
        {
            vertical += player.gravity;
        }

        chunk.vel = new Vector2(
            inputX * PlayerHorizontalSpeed,
            vertical);
    }

    private static void ApplyObjectSandVelocity(
        BodyChunk chunk,
        PhysicalObject owner,
        bool preGravity)
    {
        float horizontal = chunk.vel.x * ObjectTangentialRetention;
        float vertical = -ObjectSinkSpeed;
        if (preGravity)
        {
            vertical += owner.gravity;
        }

        chunk.vel = new Vector2(horizontal, vertical);
    }

    private static bool TryFindPlayerEntry(
        Player player,
        out QuicksandZone zone)
    {
        zone = null;
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
                candidateZone.Data == null)
            {
                continue;
            }

            for (int chunkIndex = 0; chunkIndex < player.bodyChunks.Length; chunkIndex++)
            {
                BodyChunk chunk = player.bodyChunks[chunkIndex];
                if (chunk == null ||
                    !TryGetPlayerApproachContact(
                        chunk,
                        candidateZone,
                        out QuicksandSurface.Contact contact) ||
                    !candidateZone.Data.IsQuicksand(contact.U))
                {
                    continue;
                }

                if (contact.SignedDepth > bestDepth)
                {
                    bestDepth = contact.SignedDepth;
                    zone = candidateZone;
                }
            }
        }

        return zone != null;
    }

    private static bool TryGetPlayerApproachContact(
        BodyChunk chunk,
        QuicksandZone zone,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (chunk == null || zone?.Data == null)
        {
            return false;
        }

        if (TryGetContactInZone(
                chunk,
                zone,
                predictive: false,
                out QuicksandSurface.Contact currentContact) &&
            zone.Data.IsQuicksand(currentContact.U))
        {
            contact = currentContact;
            return true;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, zone.Data, cache.Surface, cache.Bottom);

        float lookAhead = PlayerEntryLookAheadTicks;
        Vector2 predicted = chunk.pos +
                            chunk.vel * lookAhead +
                            Vector2.down * chunk.owner.gravity * 0.5f * lookAhead * lookAhead;
        float radius = Mathf.Max(1f, chunk.rad);
        float predictiveRadius = radius * 1.55f +
                                 Mathf.Min(16f, chunk.vel.magnitude * 0.55f + 5f);

        if (!QuicksandSurface.TryGetContact(
                predicted,
                predictiveRadius,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact predictedContact) ||
            !IsInsideEntryBand(predictedContact, radius, 1.85f) ||
            !zone.Data.IsQuicksand(predictedContact.U))
        {
            return false;
        }

        Vector2 travel = predicted - chunk.pos;
        if (Vector2.Dot(travel, predictedContact.Inward) < -0.05f)
        {
            return false;
        }

        contact = predictedContact;
        return true;
    }

    private static bool TryGetPlayerInfluenceContact(
        BodyChunk chunk,
        QuicksandZone zone,
        bool predictive,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (chunk == null || zone?.Data == null)
        {
            return false;
        }

        Vector2 point = chunk.pos;
        if (predictive)
        {
            point += chunk.vel + Vector2.down * chunk.owner.gravity;
        }

        float u = zone.MaterialUAtWorldX(point.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surfacePoint,
                out Vector2 tangent,
                out Vector2 inward,
                out float depthLength))
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        float signedDepth = Vector2.Dot(point - surfacePoint, inward);
        if (signedDepth < -radius * PlayerInfluenceMargin ||
            signedDepth > depthLength + radius * 0.55f)
        {
            return false;
        }

        Vector2 bottomPoint = surfacePoint + inward * depthLength;
        contact = new QuicksandSurface.Contact(
            u,
            surfacePoint,
            bottomPoint,
            tangent,
            inward,
            depthLength,
            signedDepth);
        return true;
    }

    private static bool TryFindEntry(
        PhysicalObject physicalObject,
        out QuicksandZone zone,
        out QuicksandSurface.Contact contact)
    {
        zone = null;
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
                    contact = candidateContact;
                }
            }
        }

        return zone != null;
    }

    private static bool HasQuicksandContact(
        PhysicalObject physicalObject,
        QuicksandZone zone,
        bool predictive)
    {
        if (physicalObject?.bodyChunks == null || zone?.Data == null)
        {
            return false;
        }

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk != null &&
                TryGetContactInZone(
                    chunk,
                    zone,
                    predictive,
                    out QuicksandSurface.Contact contact) &&
                zone.Data.IsQuicksand(contact.U))
            {
                return true;
            }
        }

        return false;
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

    private static bool TryGetPointContactInZone(
        Vector2 point,
        QuicksandZone zone,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (zone == null ||
            zone.slatedForDeletetion ||
            zone.PlacedObject == null ||
            !zone.PlacedObject.active ||
            zone.Data == null)
        {
            return false;
        }

        ZoneCache cache = ZoneCaches.GetValue(zone, _ => new ZoneCache());
        QuicksandSurface.SampleZone(zone.PlacedObject, zone.Data, cache.Surface, cache.Bottom);
        return QuicksandSurface.TryGetContact(
                   point,
                   1f,
                   cache.Surface,
                   cache.Bottom,
                   out contact) &&
               zone.Data.IsQuicksand(contact.U);
    }

    private static bool IsInsideEntryBand(
        QuicksandSurface.Contact contact,
        float radius,
        float entryMargin)
    {
        return contact.SignedDepth >= -radius * entryMargin &&
               contact.SignedDepth <= contact.DepthLength + radius * 0.12f;
    }

    private static void CheckPlayerFullySubmerged(Player player, SinkState state)
    {
        if (player == null || player.dead || state?.Zone == null)
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
                !TryGetContactInZone(
                    chunk,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact contact) ||
                !state.Zone.Data.IsQuicksand(contact.U) ||
                contact.SignedDepth < chunk.rad * 0.95f)
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

            headPoint = main.pos + Vector2.up * (main.rad + PlayerHeadVisualClearance);
        }

        if (!TryGetPointContactInZone(
                headPoint,
                state.Zone,
                out QuicksandSurface.Contact headContact) ||
            headContact.SignedDepth < PlayerHeadVisualClearance)
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

    private static void Deactivate(SinkState state, int cooldown)
    {
        state.Active = false;
        state.Zone = null;
        state.FullySubmergedTicks = 0;
        state.EntryTicks = 0;
        state.ContactGraceTicks = 0;
        state.ReentryCooldown = Mathf.Max(state.ReentryCooldown, cooldown);
    }
}
