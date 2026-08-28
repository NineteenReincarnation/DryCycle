using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    // Quicksand no longer pins an object to a synthetic surface position. The body
    // really moves through the sand; these values simply make that movement extremely
    // viscous. At 40 ticks/s the player sinks roughly 1.1 -> 1.8 px/s.
    private const float PlayerSurfaceSinkSpeed = 0.028f;
    private const float PlayerDeepSinkSpeed = 0.045f;
    private const float PlayerHorizontalSpeed = 0.018f;
    private const float ObjectSinkSpeed = 0.035f;
    private const float ObjectTangentialRetention = 0.08f;
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

    // Kept as the rendering hook contract. There is deliberately no visual offset
    // anymore: immersion comes from the real BodyChunk positions moving downward.
    // The renderer only needs to know that the drawable belongs behind the terrain.
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

        // The quicksand section replaces TerrainCurve support for this chunk only.
        // Adjacent normal-material sections keep their ordinary terrain collision,
        // which lets a player slowly crawl out across a material boundary.
        bool originalTerrainCollision = self.collideWithTerrain;
        self.collideWithTerrain = false;

        ApplyViscousVelocity(self, owner, state.Zone, contact, preGravity: true);

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
                ApplyViscousVelocity(
                    self,
                    owner,
                    state.Zone,
                    currentContact,
                    preGravity: false);
            }
            else
            {
                // Do not carry stored quicksand momentum out onto solid terrain.
                self.vel *= owner is Player ? 0.18f : 0.30f;
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

                if (!state.Active)
                {
                    continue;
                }

                if (!IsStateValid(physicalObject, state) ||
                    !HasQuicksandContact(physicalObject, state.Zone, predictive: false))
                {
                    Deactivate(state, ExitReentryCooldown);
                    continue;
                }

                if (physicalObject is Player player)
                {
                    CheckPlayerFullySubmerged(player, state);
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

        // Preserve the existing scope: players and loose items use quicksand. Other
        // creatures keep their native creature physics until explicitly adapted.
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
    }

    private static void ApplyViscousVelocity(
        BodyChunk chunk,
        PhysicalObject owner,
        QuicksandZone zone,
        QuicksandSurface.Contact contact,
        bool preGravity)
    {
        Vector2 tangent = contact.Tangent;
        Vector2 inward = contact.Inward;
        float deepness = Mathf.Clamp01(
            contact.SignedDepth / Mathf.Max(1f, contact.DepthLength));

        float tangentSpeed;
        float inwardSpeed;

        if (owner is Player player)
        {
            float inputX = player.input != null && player.input.Length > 0
                ? player.input[0].x
                : 0f;
            float tangentRightSign = Mathf.Abs(tangent.x) > 0.05f
                ? Mathf.Sign(tangent.x)
                : 1f;

            // No flow-driven player motion. With no input, tangential speed is zero;
            // holding a direction only permits a very small crawl along the surface.
            tangentSpeed = inputX * PlayerHorizontalSpeed * tangentRightSign;
            inwardSpeed = Mathf.Lerp(
                PlayerSurfaceSinkSpeed,
                PlayerDeepSinkSpeed,
                Mathf.SmoothStep(0f, 1f, deepness));
        }
        else
        {
            tangentSpeed = Vector2.Dot(chunk.vel, tangent) * ObjectTangentialRetention;
            inwardSpeed = ObjectSinkSpeed;
        }

        if (preGravity)
        {
            // BodyChunk.Update applies normal gravity. Cancel almost all of that
            // component in advance so gravity cannot turn the intended slow sink
            // into a free fall during the update itself.
            float gravityIntoSand = Vector2.Dot(
                Vector2.down * owner.gravity,
                inward);
            inwardSpeed -= gravityIntoSand;
        }

        chunk.vel = tangent * tangentSpeed + inward * inwardSpeed;
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
        state.ReentryCooldown = Mathf.Max(state.ReentryCooldown, cooldown);
    }
}
