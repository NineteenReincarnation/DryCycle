using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Quicksand model based on a dense, saturated granular medium rather than water.
///
/// Design goals:
/// - impact is absorbed instead of bounced;
/// - resistance grows with immersion and movement speed;
/// - resistance never reverses a downward velocity into an upward launch;
/// - there is no conveyor-belt horizontal force from the visual flow setting;
/// - loose weapons keep the pose they had when they entered the surface and then
///   settle slowly instead of spinning forever.
/// </summary>
internal static class QuicksandRealisticPhysics
{
    private const float PlayerPredictionTicks = 1.35f;
    private const float ObjectPredictionTicks = 1.55f;
    private const float PlayerInfluenceMargin = 1.45f;
    private const float ObjectInfluenceMargin = 1.30f;
    private const int PlayerContactGraceTicks = 7;
    private const int ObjectContactGraceTicks = 9;
    private const int ExitReentryCooldownTicks = 5;
    private const int PlayerEntryRampTicks = 18;
    private const int ObjectEntryRampTicks = 28;
    private const int PlayerDeathConfirmTicks = 10;
    private const float PlayerHeadClearance = 8f;

    private const float MinJumpDistanceReduction = 0.30f;
    private const float MaxJumpDistanceReduction = 0.80f;

    private sealed class SinkState
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal int EntryTicks;
        internal int ContactGraceTicks;
        internal int ReentryCooldownTicks;
        internal int FullySubmergedTicks;
        internal float Immersion;
        internal bool CapturedWeaponPose;
        internal Vector2 WeaponPose;
    }

    private static readonly ConditionalWeakTable<PhysicalObject, SinkState> States = new();
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
        On.Weapon.Update += Weapon_Update;
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
        On.Weapon.Update -= Weapon_Update;
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
            !States.TryGetValue(physicalObject, out SinkState state) ||
            !IsStateValid(physicalObject, state) ||
            state.Immersion <= 0.005f)
        {
            return false;
        }

        zone = state.Zone;
        progress = Mathf.Clamp01(state.Immersion);
        return true;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!CanSinkPlayer(self))
        {
            orig(self, eu);
            return;
        }

        SinkState state = States.GetValue(self, _ => new SinkState());
        if (state.ReentryCooldownTicks > 0)
        {
            state.ReentryCooldownTicks--;
        }

        if (state.Active && !IsStateValid(self, state))
        {
            Deactivate(state, ExitReentryCooldownTicks);
        }

        if (!state.Active &&
            state.ReentryCooldownTicks <= 0 &&
            TryFindPlayerEntry(self, out QuicksandZone entryZone))
        {
            Activate(state, entryZone, PlayerContactGraceTicks);
        }

        if (!state.Active)
        {
            orig(self, eu);
            return;
        }

        bool[] originalTerrainCollision = new bool[self.bodyChunks.Length];
        bool[] collisionOverridden = new bool[self.bodyChunks.Length];

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            originalTerrainCollision[i] = chunk.collideWithTerrain;
            if (TryGetChunkContact(
                    chunk,
                    state.Zone,
                    predictive: true,
                    PlayerPredictionTicks,
                    PlayerInfluenceMargin,
                    out _))
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
            for (int i = 0; i < self.bodyChunks.Length; i++)
            {
                if (collisionOverridden[i] && self.bodyChunks[i] != null)
                {
                    self.bodyChunks[i].collideWithTerrain = originalTerrainCollision[i];
                }
            }
        }

        bool hasInfluence = false;
        float immersionTotal = 0f;
        int immersionCount = 0;

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            if (TryGetChunkContact(
                    chunk,
                    state.Zone,
                    predictive: false,
                    0f,
                    PlayerInfluenceMargin,
                    out QuicksandSurface.Contact contact))
            {
                hasInfluence = true;
                float chunkImmersion = ComputeImmersion(chunk, contact);
                immersionTotal += chunkImmersion;
                immersionCount++;
                ApplyPlayerResistance(chunk, self, state, chunkImmersion);
            }
            else if (TryGetChunkContact(
                         chunk,
                         state.Zone,
                         predictive: true,
                         PlayerPredictionTicks,
                         PlayerInfluenceMargin,
                         out _))
            {
                hasInfluence = true;
            }
        }

        state.Immersion = immersionCount > 0
            ? Mathf.Clamp01(immersionTotal / immersionCount)
            : 0f;

        if (hasInfluence)
        {
            state.ContactGraceTicks = PlayerContactGraceTicks;
            state.EntryTicks++;
        }
        else
        {
            state.ContactGraceTicks--;
            if (state.ContactGraceTicks <= 0)
            {
                Deactivate(state, ExitReentryCooldownTicks);
                return;
            }
        }

        CheckPlayerFullySubmerged(self, state);
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (self == null ||
            !States.TryGetValue(self, out SinkState state) ||
            !IsStateValid(self, state) ||
            state.Immersion <= 0.005f)
        {
            orig(self);
            return;
        }

        float immersion = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(state.Immersion));
        float distanceReduction = Mathf.Lerp(
            MinJumpDistanceReduction,
            MaxJumpDistanceReduction,
            immersion);
        float impulseScale = Mathf.Sqrt(Mathf.Clamp01(1f - distanceReduction));

        Vector2[] beforeVelocity = new Vector2[self.bodyChunks.Length];
        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            if (self.bodyChunks[i] != null)
            {
                beforeVelocity[i] = self.bodyChunks[i].vel;
            }
        }

        orig(self);

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            Vector2 impulse = chunk.vel - beforeVelocity[i];
            if (impulse.y > 0f)
            {
                chunk.vel.y = beforeVelocity[i].y + impulse.y * impulseScale;
            }

            if (Mathf.Abs(impulse.x) > 0.0001f)
            {
                chunk.vel.x = beforeVelocity[i].x + impulse.x * impulseScale;
            }
        }

        if (self.jumpBoost > 0f)
        {
            self.jumpBoost *= impulseScale;
        }

        state.ContactGraceTicks = PlayerContactGraceTicks;
    }

    private static void ApplyPlayerResistance(
        BodyChunk chunk,
        Player player,
        SinkState state,
        float immersion)
    {
        if (immersion <= 0f)
        {
            return;
        }

        QuicksandZoneData data = state.Zone.Data;
        float packing = Mathf.SmoothStep(0f, 1f, immersion);
        float motion = Mathf.Clamp01(chunk.vel.magnitude / 9f);
        float struggleMultiplier = 1f + motion * 0.55f;

        float horizontalTuning = data != null
            ? Mathf.Lerp(0.72f, 1.28f, Mathf.Clamp01(data.HorizontalDrag))
            : 1f;
        float horizontalDrag = Mathf.Clamp01(
            Mathf.Lerp(0.10f, 0.70f, packing) *
            horizontalTuning *
            struggleMultiplier);
        chunk.vel.x *= 1f - horizontalDrag;

        float entryFactor = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01((float)state.EntryTicks / PlayerEntryRampTicks));
        float sinkTuning = data != null
            ? Mathf.Clamp(0.65f + data.SinkStrength * 1.45f, 0.45f, 2.25f)
            : 1f;
        float creep = Mathf.Lerp(0.0025f, 0.014f, packing) *
                      entryFactor *
                      sinkTuning;

        if (chunk.vel.y < 0f)
        {
            float downwardDrag = Mathf.Clamp01(
                Mathf.Lerp(0.79f, 0.965f, packing) * struggleMultiplier);
            chunk.vel.y *= 1f - downwardDrag;
            chunk.vel.y -= creep;
        }
        else
        {
            float upwardDrag = Mathf.Clamp01(
                Mathf.Lerp(0.10f, 0.61f, packing) * struggleMultiplier);
            chunk.vel.y *= 1f - upwardDrag;
            chunk.vel.y -= creep * 0.55f;
        }

        // FlowSpeed/FlowStrength remain visual. No horizontal conveyor-belt force is
        // generated by the quicksand physics itself.
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        PhysicalObject owner = self?.owner;
        if (!CanSinkLooseObject(owner))
        {
            orig(self);
            return;
        }

        SinkState state = States.GetValue(owner, _ => new SinkState());
        bool firstChunk = owner.bodyChunks.Length > 0 && owner.bodyChunks[0] == self;

        if (firstChunk)
        {
            if (state.ReentryCooldownTicks > 0)
            {
                state.ReentryCooldownTicks--;
            }

            state.Immersion = 0f;
        }

        if (owner.grabbedBy != null && owner.grabbedBy.Count > 0)
        {
            Deactivate(state, 0);
            orig(self);
            return;
        }

        if (state.Active && !IsStateValid(owner, state))
        {
            Deactivate(state, ExitReentryCooldownTicks);
        }

        if (!state.Active &&
            state.ReentryCooldownTicks <= 0 &&
            TryFindObjectEntry(owner, out QuicksandZone entryZone))
        {
            Activate(state, entryZone, ObjectContactGraceTicks);
        }

        if (!state.Active)
        {
            orig(self);
            return;
        }

        bool predictiveContact = TryGetChunkContact(
            self,
            state.Zone,
            predictive: true,
            ObjectPredictionTicks,
            ObjectInfluenceMargin,
            out _);

        if (!predictiveContact)
        {
            orig(self);
            if (firstChunk)
            {
                state.ContactGraceTicks--;
                if (state.ContactGraceTicks <= 0)
                {
                    Deactivate(state, ExitReentryCooldownTicks);
                }
            }
            return;
        }

        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;

        if (self.vel.y < 0f)
        {
            self.vel.y *= 0.28f;
        }
        self.vel.x *= 0.58f;

        try
        {
            orig(self);
        }
        finally
        {
            self.collideWithTerrain = originalTerrainCollision;
        }

        if (TryGetChunkContact(
                self,
                state.Zone,
                predictive: false,
                0f,
                ObjectInfluenceMargin,
                out QuicksandSurface.Contact contact))
        {
            float immersion = ComputeImmersion(self, contact);
            state.Immersion = Mathf.Max(state.Immersion, immersion);
            state.ContactGraceTicks = ObjectContactGraceTicks;
            ApplyObjectResistance(self, state, immersion);
        }

        if (firstChunk)
        {
            state.EntryTicks++;
        }
    }

    private static void ApplyObjectResistance(
        BodyChunk chunk,
        SinkState state,
        float immersion)
    {
        if (immersion <= 0f)
        {
            return;
        }

        QuicksandZoneData data = state.Zone.Data;
        float packing = Mathf.SmoothStep(0f, 1f, immersion);
        float motion = Mathf.Clamp01(chunk.vel.magnitude / 12f);
        float struggleMultiplier = 1f + motion * 0.65f;

        float horizontalTuning = data != null
            ? Mathf.Lerp(0.78f, 1.25f, Mathf.Clamp01(data.HorizontalDrag))
            : 1f;
        float horizontalDrag = Mathf.Clamp01(
            Mathf.Lerp(0.72f, 0.95f, packing) *
            horizontalTuning *
            struggleMultiplier);
        chunk.vel.x *= 1f - horizontalDrag;

        float verticalDrag = Mathf.Clamp01(
            Mathf.Lerp(0.91f, 0.985f, packing) * struggleMultiplier);
        chunk.vel.y *= 1f - verticalDrag;

        float entryFactor = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.Clamp01((float)state.EntryTicks / ObjectEntryRampTicks));
        float sinkTuning = data != null
            ? Mathf.Clamp(0.60f + data.SinkStrength * 1.55f, 0.40f, 2.35f)
            : 1f;
        float creep = Mathf.Lerp(0.0015f, 0.011f, packing) *
                      Mathf.Lerp(0.35f, 1f, entryFactor) *
                      sinkTuning;
        chunk.vel.y -= creep;
    }

    private static void Weapon_Update(On.Weapon.orig_Update orig, Weapon self, bool eu)
    {
        orig(self, eu);

        if (self == null ||
            self.room == null ||
            self.grabbedBy == null ||
            self.grabbedBy.Count > 0 ||
            !States.TryGetValue(self, out SinkState state) ||
            !IsStateValid(self, state) ||
            self.firstChunk == null)
        {
            return;
        }

        if (!TryGetChunkContact(
                self.firstChunk,
                state.Zone,
                predictive: false,
                0f,
                ObjectInfluenceMargin,
                out QuicksandSurface.Contact contact))
        {
            return;
        }

        float immersion = ComputeImmersion(self.firstChunk, contact);
        if (immersion <= 0.015f)
        {
            return;
        }

        if (!state.CapturedWeaponPose)
        {
            state.WeaponPose = self.rotation;
            state.CapturedWeaponPose = true;

            if (self.mode == Weapon.Mode.Thrown)
            {
                self.ChangeMode(Weapon.Mode.Free);
            }
        }

        self.rotationSpeed = 0f;
        self.rotation = state.WeaponPose;
        self.lastRotation = state.WeaponPose;
        self.setRotation = state.WeaponPose;
        self.vibrate = 0;

        if (self is Spear spear)
        {
            spear.spinning = false;
        }
    }

    private static bool CanSinkPlayer(Player player)
    {
        return player != null &&
               player.room != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0;
    }

    private static bool CanSinkLooseObject(PhysicalObject physicalObject)
    {
        return physicalObject != null &&
               physicalObject is not Player &&
               physicalObject is not Creature &&
               physicalObject.room != null &&
               physicalObject.bodyChunks != null &&
               physicalObject.bodyChunks.Length > 0;
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

    private static void Activate(SinkState state, QuicksandZone zone, int graceTicks)
    {
        state.Active = true;
        state.Zone = zone;
        state.EntryTicks = 0;
        state.ContactGraceTicks = graceTicks;
        state.FullySubmergedTicks = 0;
        state.Immersion = 0f;
        state.CapturedWeaponPose = false;
        state.WeaponPose = Vector2.up;
    }

    private static void Deactivate(SinkState state, int cooldownTicks)
    {
        if (state == null)
        {
            return;
        }

        state.Active = false;
        state.Zone = null;
        state.EntryTicks = 0;
        state.ContactGraceTicks = 0;
        state.FullySubmergedTicks = 0;
        state.Immersion = 0f;
        state.CapturedWeaponPose = false;
        state.ReentryCooldownTicks = Mathf.Max(state.ReentryCooldownTicks, cooldownTicks);
    }

    private static bool TryFindPlayerEntry(Player player, out QuicksandZone zone)
    {
        zone = null;
        if (player?.room?.updateList == null || player.bodyChunks == null)
        {
            return false;
        }

        float bestDepth = float.NegativeInfinity;

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not QuicksandZone candidate ||
                !IsUsableZone(candidate))
            {
                continue;
            }

            for (int j = 0; j < player.bodyChunks.Length; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk == null ||
                    !TryGetChunkContact(
                        chunk,
                        candidate,
                        predictive: true,
                        PlayerPredictionTicks,
                        PlayerInfluenceMargin,
                        out QuicksandSurface.Contact contact))
                {
                    continue;
                }

                if (contact.SignedDepth > bestDepth)
                {
                    bestDepth = contact.SignedDepth;
                    zone = candidate;
                }
            }
        }

        return zone != null;
    }

    private static bool TryFindObjectEntry(PhysicalObject physicalObject, out QuicksandZone zone)
    {
        zone = null;
        if (physicalObject?.room?.updateList == null || physicalObject.bodyChunks == null)
        {
            return false;
        }

        float bestDepth = float.NegativeInfinity;

        for (int i = 0; i < physicalObject.room.updateList.Count; i++)
        {
            if (physicalObject.room.updateList[i] is not QuicksandZone candidate ||
                !IsUsableZone(candidate))
            {
                continue;
            }

            for (int j = 0; j < physicalObject.bodyChunks.Length; j++)
            {
                BodyChunk chunk = physicalObject.bodyChunks[j];
                if (chunk == null ||
                    !TryGetChunkContact(
                        chunk,
                        candidate,
                        predictive: true,
                        ObjectPredictionTicks,
                        ObjectInfluenceMargin,
                        out QuicksandSurface.Contact contact))
                {
                    continue;
                }

                if (contact.SignedDepth > bestDepth)
                {
                    bestDepth = contact.SignedDepth;
                    zone = candidate;
                }
            }
        }

        return zone != null;
    }

    private static bool TryGetChunkContact(
        BodyChunk chunk,
        QuicksandZone zone,
        bool predictive,
        float lookAheadTicks,
        float influenceMargin,
        out QuicksandSurface.Contact contact)
    {
        contact = default;
        if (chunk == null || !IsUsableZone(zone))
        {
            return false;
        }

        Vector2 point = chunk.pos;
        if (predictive)
        {
            float lookAhead = Mathf.Max(0f, lookAheadTicks);
            point += chunk.vel * lookAhead +
                     Vector2.down * chunk.owner.gravity * 0.5f * lookAhead * lookAhead;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        if (point.x < zone.startX - radius * 1.15f ||
            point.x > zone.endX + radius * 1.15f)
        {
            return false;
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

        float signedDepth = Vector2.Dot(point - surfacePoint, inward);
        if (signedDepth < -radius * influenceMargin ||
            signedDepth > depthLength + radius * 0.50f)
        {
            return false;
        }

        if (predictive)
        {
            Vector2 travel = point - chunk.pos;
            if (Vector2.Dot(travel, inward) < -0.05f && signedDepth < radius * 0.15f)
            {
                return false;
            }
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

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }

    private static float ComputeImmersion(BodyChunk chunk, QuicksandSurface.Contact contact)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        return Mathf.Clamp01((contact.SignedDepth + radius) / (radius * 2f));
    }

    private static void CheckPlayerFullySubmerged(Player player, SinkState state)
    {
        if (player == null || player.dead || !IsStateValid(player, state))
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
                !TryGetChunkContact(
                    chunk,
                    state.Zone,
                    predictive: false,
                    0f,
                    PlayerInfluenceMargin,
                    out QuicksandSurface.Contact contact) ||
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

            headPoint = main.pos + Vector2.up * (main.rad + PlayerHeadClearance);
        }

        float headU = state.Zone.MaterialUAtWorldX(headPoint.x);
        if (!state.Zone.Data.IsQuicksand(headU) ||
            !state.Zone.TrySampleSurfaceFrame(
                headU,
                out Vector2 surfacePoint,
                out _,
                out Vector2 inward,
                out float depthLength))
        {
            state.FullySubmergedTicks = 0;
            return;
        }

        float headDepth = Vector2.Dot(headPoint - surfacePoint, inward);
        if (headDepth < PlayerHeadClearance || headDepth > depthLength)
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
}
