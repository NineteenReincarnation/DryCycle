using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Player quicksand is force-based. We do not replace the player's velocity;
    // native movement and Jump() remain authoritative. The sand only adds a downward
    // pull plus viscous resistance while the normal gravity term is neutralized on
    // downward motion so immersion stays slow and readable.
    private const float PlayerEntryDownForce = 0.004f;
    private const float PlayerSurfaceDownForce = 0.012f;
    private const float PlayerDeepDownForce = 0.035f;
    private const float PlayerSurfaceDownDrag = 0.55f;
    private const float PlayerDeepDownDrag = 0.78f;
    private const float PlayerEntryLookAheadTicks = 2.5f;
    private const float PlayerInfluenceMargin = 2.35f;
    private const int PlayerForceRampTicks = 20;
    private const int PlayerContactGraceTicks = 8;

    // Jump distance reduction is 30% at first contact and 80% when fully immersed.
    // Jump impulse uses sqrt(remaining distance), since ballistic distance/height is
    // approximately proportional to velocity squared.
    private const float PlayerMinJumpDistanceReduction = 0.30f;
    private const float PlayerMaxJumpDistanceReduction = 0.80f;

    // Loose items keep their separate slow terminal-speed model so rocks and similar
    // objects visibly settle into the surface instead of punching through it.
    private const float ObjectEntrySinkSpeed = 0.0025f;
    private const float ObjectSurfaceSinkSpeed = 0.010f;
    private const float ObjectDeepSinkSpeed = 0.020f;
    private const float ObjectTangentialRetention = 0.04f;
    private const float ObjectSinkPull = 0.30f;
    private const float ObjectEntryLookAheadTicks = 2.0f;
    private const int ObjectEntryRampTicks = 40;
    private const int ObjectContactGraceTicks = 10;

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
        internal float Immersion;
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
        On.Player.Jump += Player_Jump;
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
        On.Player.Jump -= Player_Jump;
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
            state.Zone == null)
        {
            return false;
        }

        if (physicalObject is Player)
        {
            // Predictive entry may activate slightly before contact to avoid the old
            // terrain bounce. Do not move the player behind Sand until real immersion
            // has begun.
            if (state.Immersion <= 0.001f)
            {
                return false;
            }

            zone = state.Zone;
            progress = state.Immersion;
            return true;
        }

        if (physicalObject.bodyChunks == null)
        {
            return false;
        }

        bool penetrated = false;
        float deepestProgress = 0f;
        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null ||
                !TryGetContactInZone(
                    chunk,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact contact) ||
                !state.Zone.Data.IsQuicksand(contact.U))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (contact.SignedDepth < -radius * 0.90f)
            {
                continue;
            }

            penetrated = true;
            deepestProgress = Mathf.Max(
                deepestProgress,
                Mathf.Clamp01((contact.SignedDepth + radius) / (radius * 2f)));
        }

        if (!penetrated)
        {
            return false;
        }

        zone = state.Zone;
        progress = deepestProgress;
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

        // Catch the approach before either BodyChunk can collide with the solid room
        // terrain underneath the quicksand surface.
        if (!state.Active &&
            state.ReentryCooldown <= 0 &&
            TryFindPlayerEntry(self, out QuicksandZone entryZone))
        {
            Activate(state, entryZone, PlayerContactGraceTicks);
        }

        if (!state.Active)
        {
            orig(self, eu);
            return;
        }

        // Jump() is called from inside Player.Update. Keep an up-to-date physical
        // immersion value before orig so Player_Jump can scale this exact jump.
        state.Immersion = ComputePlayerImmersion(self, state.Zone);

        // The quicksand volume replaces the solid room terrain while the player is
        // inside it. This only removes collision response; it does not replace or set
        // player velocity, so native walking/jumping code remains free to run.
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
                    out _) ||
                TryGetPlayerInfluenceContact(
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

        state.Immersion = ComputePlayerImmersion(self, state.Zone);
        ApplyPlayerSandForces(self, state);
        state.EntryTicks++;
        CheckPlayerFullySubmerged(self, state);
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (self == null ||
            !SinkStates.TryGetValue(self, out SinkState state) ||
            !IsStateValid(self, state))
        {
            orig(self);
            return;
        }

        float immersion = Mathf.Clamp01(state.Immersion);
        float distanceReduction = Mathf.Lerp(
            PlayerMinJumpDistanceReduction,
            PlayerMaxJumpDistanceReduction,
            immersion);
        float remainingDistance = Mathf.Clamp01(1f - distanceReduction);
        float impulseScale = Mathf.Sqrt(remainingDistance);

        Vector2[] beforeVelocity = new Vector2[self.bodyChunks.Length];
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            if (self.bodyChunks[i] != null)
            {
                beforeVelocity[i] = self.bodyChunks[i].vel;
            }
        }

        orig(self);

        // Scale only the impulse produced by Jump(), not the velocity the player had
        // before pressing jump. This preserves ordinary movement while reducing the
        // resulting jump distance according to immersion depth.
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            Vector2 jumpImpulse = chunk.vel - beforeVelocity[i];
            if (jumpImpulse.y > 0f)
            {
                chunk.vel.y = beforeVelocity[i].y + jumpImpulse.y * impulseScale;
            }

            if (Mathf.Abs(jumpImpulse.x) > 0.0001f)
            {
                chunk.vel.x = beforeVelocity[i].x + jumpImpulse.x * impulseScale;
            }
        }

        // Held-jump boost is part of the same jump and otherwise would restore much
        // of the height removed above on the following frames.
        if (self.jumpBoost > 0f)
        {
            self.jumpBoost *= impulseScale;
        }

        state.ContactGraceTicks = PlayerContactGraceTicks;
    }

    private static void ApplyPlayerSandForces(Player player, SinkState state)
    {
        if (player?.bodyChunks == null || state?.Zone == null)
        {
            return;
        }

        float immersion = Mathf.Clamp01(state.Immersion);
        float entryFactor = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01((float)state.EntryTicks / PlayerForceRampTicks));
        float targetDownForce = Mathf.Lerp(
            PlayerSurfaceDownForce,
            PlayerDeepDownForce,
            immersion);
        float downForce = Mathf.Lerp(
            PlayerEntryDownForce,
            targetDownForce,
            entryFactor);
        float downwardDrag = Mathf.Lerp(
            PlayerSurfaceDownDrag,
            PlayerDeepDownDrag,
            immersion);

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null ||
                !TryGetPlayerInfluenceContact(
                    chunk,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact contact))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            float chunkImmersion = Mathf.Clamp01(
                (contact.SignedDepth + radius) / (radius * 2f));
            if (chunkImmersion <= 0f)
            {
                continue;
            }

            float localDownForce = Mathf.Lerp(
                PlayerEntryDownForce,
                downForce,
                chunkImmersion);

            if (chunk.vel.y <= 0f)
            {
                // BodyChunk.Update already applied normal gravity. Add an opposing
                // buoyancy force, then a viscous force against downward motion. This
                // produces a slow terminal sink without ever assigning a target speed.
                chunk.vel.y += player.gravity;
                chunk.vel.y += -chunk.vel.y * downwardDrag;
            }

            // This is the actual sinking force. During a jump it simply tugs against
            // ascent; it never zeroes or replaces the jump velocity.
            chunk.vel.y -= localDownForce;
        }
    }

    private static float ComputePlayerImmersion(Player player, QuicksandZone zone)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0 || zone?.Data == null)
        {
            return 0f;
        }

        float total = 0f;
        int denominator = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            denominator++;
            float u = zone.MaterialUAtWorldX(chunk.pos.x);
            if (!zone.Data.IsQuicksand(u) ||
                !zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surfacePoint,
                    out _,
                    out Vector2 inward,
                    out _))
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            float signedDepth = Vector2.Dot(chunk.pos - surfacePoint, inward);
            total += Mathf.Clamp01((signedDepth + radius) / (radius * 2f));
        }

        return denominator > 0 ? Mathf.Clamp01(total / denominator) : 0f;
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanSink(owner) || owner is Player)
        {
            // Player physics are handled atomically by Player_Update. This hook is
            // only for loose non-creature PhysicalObjects.
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
            Activate(state, entryZone, ObjectContactGraceTicks);
        }

        if (!state.Active ||
            !TryGetObjectInfluenceContact(
                self,
                state.Zone,
                predictive: true,
                out QuicksandSurface.Contact contact))
        {
            orig(self);
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;
        ApplyObjectSandVelocity(self, owner, state, contact, preGravity: true);

        try
        {
            orig(self);

            if (state.Active &&
                TryGetObjectInfluenceContact(
                    self,
                    state.Zone,
                    predictive: false,
                    out QuicksandSurface.Contact currentContact))
            {
                ApplyObjectSandVelocity(
                    self,
                    owner,
                    state,
                    currentContact,
                    preGravity: false);
            }
            else
            {
                self.vel *= 0.08f;
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

                if (!state.Active || physicalObject is Player)
                {
                    continue;
                }

                if (!IsStateValid(physicalObject, state))
                {
                    Deactivate(state, ExitReentryCooldown);
                    continue;
                }

                if (HasQuicksandContact(physicalObject, state.Zone, predictive: false) ||
                    HasObjectInfluence(physicalObject, state.Zone, predictive: true))
                {
                    state.ContactGraceTicks = ObjectContactGraceTicks;
                    state.EntryTicks++;
                }
                else
                {
                    state.ContactGraceTicks--;
                    if (state.ContactGraceTicks <= 0)
                    {
                        Deactivate(state, ExitReentryCooldown);
                    }
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

    private static void Activate(SinkState state, QuicksandZone zone, int contactGraceTicks)
    {
        state.Active = true;
        state.Zone = zone;
        state.FullySubmergedTicks = 0;
        state.EntryTicks = 0;
        state.ContactGraceTicks = contactGraceTicks;
        state.Immersion = 0f;
    }

    private static void ApplyObjectSandVelocity(
        BodyChunk chunk,
        PhysicalObject owner,
        SinkState state,
        QuicksandSurface.Contact contact,
        bool preGravity)
    {
        float deepness = Mathf.Clamp01(
            contact.SignedDepth / Mathf.Max(1f, contact.DepthLength));
        float targetSinkSpeed = Mathf.Lerp(
            ObjectSurfaceSinkSpeed,
            ObjectDeepSinkSpeed,
            Mathf.SmoothStep(0f, 1f, deepness));
        float entryFactor = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01((float)state.EntryTicks / ObjectEntryRampTicks));
        float sinkSpeed = Mathf.Lerp(
            ObjectEntrySinkSpeed,
            targetSinkSpeed,
            entryFactor);

        float horizontal = chunk.vel.x * ObjectTangentialRetention;
        float dampedVertical = Mathf.Min(0f, chunk.vel.y) * 0.015f - ObjectSinkPull;
        float vertical = Mathf.Max(dampedVertical, -sinkSpeed);
        if (preGravity)
        {
            vertical += owner.gravity;
        }

        chunk.vel = new Vector2(horizontal, vertical);
    }

    private static bool TryFindPlayerEntry(Player player, out QuicksandZone zone)
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
                    !TryGetObjectApproachContact(
                        chunk,
                        candidateZone,
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

    private static bool TryGetObjectApproachContact(
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

        float lookAhead = ObjectEntryLookAheadTicks;
        Vector2 predicted = chunk.pos +
                            chunk.vel * lookAhead +
                            Vector2.down * chunk.owner.gravity * 0.5f * lookAhead * lookAhead;
        float radius = Mathf.Max(1f, chunk.rad);
        float predictiveRadius = radius * 1.35f +
                                 Mathf.Min(14f, chunk.vel.magnitude * 0.48f + 4f);

        if (!QuicksandSurface.TryGetContact(
                predicted,
                predictiveRadius,
                cache.Surface,
                cache.Bottom,
                out QuicksandSurface.Contact predictedContact) ||
            !IsInsideEntryBand(predictedContact, radius, 1.65f) ||
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

    private static bool TryGetObjectInfluenceContact(
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
        if (signedDepth < -radius * 1.70f ||
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

    private static bool HasObjectInfluence(
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
                TryGetObjectInfluenceContact(
                    chunk,
                    zone,
                    predictive,
                    out _))
            {
                return true;
            }
        }

        return false;
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
        state.Immersion = 0f;
        state.ReentryCooldown = Mathf.Max(state.ReentryCooldown, cooldown);
    }
}
