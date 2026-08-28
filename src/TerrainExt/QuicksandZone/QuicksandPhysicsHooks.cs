using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandPhysicsHooks
{
    private const int SampleCount = 64;

    private sealed class ZoneCache
    {
        internal readonly Vector2[] Surface = new Vector2[SampleCount];
        internal readonly Vector2[] Bottom = new Vector2[SampleCount];
    }

    private static readonly ConditionalWeakTable<QuicksandZone, ZoneCache> ZoneCaches = new();
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

        if (quicksandOverridesTerrain)
        {
            // Quicksand owns collision inside its volume. This intentionally disables
            // both vanilla tile collision and Watcher's TerrainManager snap for this
            // BodyChunk update, so buried solid terrain cannot be stood on through sand.
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

        // BodyChunk.Update has already applied gravity and integrated position here.
        // Clamp the remaining velocity again so the next frame starts from a viscous
        // quicksand velocity instead of immediately accelerating back to free fall.
        if (TryGetQuicksandContact(self, predictive: false, out QuicksandSurface.Contact postContact))
        {
            ApplyPostStepResistance(self, postContact);
        }
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

            ZoneCache cache = ZoneCaches.GetOrCreateValue(zone);
            QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);

            if (QuicksandSurface.TryGetContact(
                    current,
                    radius + 1.5f,
                    cache.Surface,
                    cache.Bottom,
                    out QuicksandSurface.Contact currentContact) &&
                IsInsideOverrideBand(currentContact, radius, entryMargin: 0.32f))
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
                IsInsideOverrideBand(predictedContact, radius, entryMargin: 0.12f) &&
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

        // Surface support counters most of the next gravity step. At the top the
        // chunk sinks slowly; deeper down this support tapers off so the sand still
        // feels dangerous rather than behaving like a solid floor.
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

        // Stronger surface drag than the runtime zone's normal drag. This is applied
        // before integration specifically to stop a fast-running/falling body from
        // travelling a large distance on the first frame of contact.
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
            // Moving out of quicksand is still resisted, but noticeably less than
            // sinking so jumps and crawling toward an edge remain useful.
            chunk.vel -= inward * inwardSpeed * (1f - Mathf.Lerp(0.82f, 0.66f, viscosity));
        }
    }
}
