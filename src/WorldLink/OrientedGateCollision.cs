using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.WorldLink;

internal readonly struct GateLeaf
{
    internal readonly Vector2 Center;
    internal readonly Vector2 Tangent;
    internal readonly Vector2 Normal;
    internal readonly float HalfLength;
    internal readonly float HalfThickness;
    internal readonly int Side;
    internal readonly float OuterHalfWidth;
    internal readonly float Open;

    internal GateLeaf(Vector2 center, Vector2 tangent, Vector2 normal, float halfLength, float halfThickness, int side, float outerHalfWidth, float open)
    {
        Center = center;
        Tangent = tangent;
        Normal = normal;
        HalfLength = halfLength;
        HalfThickness = halfThickness;
        Side = side;
        OuterHalfWidth = outerHalfWidth;
        Open = open;
    }
}

internal static class OrientedGateCollision
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.BodyChunk.Update += BodyChunkUpdate;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.BodyChunk.Update -= BodyChunkUpdate;
    }

    private static void BodyChunkUpdate(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        orig(self);
        if (self?.owner?.room == null || !self.collideWithTerrain || self.actAsTrigger) return;

        IReadOnlyList<MultiGatePortRuntime> ports = WorldLinkRoomRegistry.Ports(self.owner.room);
        for (int i = 0; i < ports.Count; i++)
        {
            MultiGatePortRuntime port = ports[i];
            if (port == null || port.slatedForDeletetion || !port.ShouldCollide || !MayCrossPortEnvelope(port, self)) continue;
            ResolvePort(port, self);
        }
    }

    private static bool MayCrossPortEnvelope(MultiGatePortRuntime port, BodyChunk chunk)
    {
        if (port.IsWithinTransitEnvelope(chunk.pos, 1.15f) || port.IsWithinTransitEnvelope(chunk.lastPos, 1.15f)) return true;

        Vector2 a = chunk.lastPos - port.Placed.pos;
        Vector2 b = chunk.pos - port.Placed.pos;
        Vector2 p0 = new(Vector2.Dot(a, port.Data.Tangent), Vector2.Dot(a, port.Data.Normal));
        Vector2 p1 = new(Vector2.Dot(b, port.Data.Tangent), Vector2.Dot(b, port.Data.Normal));
        float halfU = port.Data.PassageWidth * 0.5f * 1.15f + 24f + chunk.TerrainRad;
        float halfV = port.Data.TriggerDepth * 1.15f + 30f + chunk.TerrainRad;
        return SegmentIntersectsAabb(p0, p1, halfU, halfV);
    }

    private static bool SegmentIntersectsAabb(Vector2 from, Vector2 to, float halfX, float halfY)
    {
        Vector2 d = to - from;
        float tMin = 0f;
        float tMax = 1f;
        if (!IntervalSlab(from.x, d.x, -halfX, halfX, ref tMin, ref tMax)) return false;
        if (!IntervalSlab(from.y, d.y, -halfY, halfY, ref tMin, ref tMax)) return false;
        return tMin <= tMax && tMax >= 0f && tMin <= 1f;
    }

    private static bool IntervalSlab(float p, float d, float min, float max, ref float tMin, ref float tMax)
    {
        if (Mathf.Abs(d) < 0.000001f) return p >= min && p <= max;
        float inv = 1f / d;
        float t1 = (min - p) * inv;
        float t2 = (max - p) * inv;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Mathf.Max(tMin, t1);
        tMax = Mathf.Min(tMax, t2);
        return tMin <= tMax;
    }

    private static void ResolvePort(MultiGatePortRuntime port, BodyChunk chunk)
    {
        bool hasLeft = port.TryGetLeaf(-1, previous: false, out GateLeaf left);
        bool hasRight = port.TryGetLeaf(1, previous: false, out GateLeaf right);
        if (!hasLeft && !hasRight) return;

        // Once the visible aperture is narrower than this BodyChunk's terrain diameter,
        // the Minkowski-expanded left/right leaves overlap. Solving two opposing inner
        // edges sequentially is order-dependent and makes a chunk at the center seam
        // oscillate left/right. In that no-fit regime the two leaves form one connected
        // configuration-space barrier, so resolve one deterministic full-width slab.
        float gap = port.Data.PassageWidth * port.OpenFactor;
        if (hasLeft && hasRight && gap < chunk.TerrainRad * 2f - 0.001f)
        {
            float half = port.Data.PassageWidth * 0.5f;
            GateLeaf unified = new(
                port.Placed.pos,
                port.Data.Tangent,
                port.Data.Normal,
                half,
                port.Data.PanelThickness * 0.5f,
                0,
                half,
                port.OpenFactor);
            ResolveLeafGeometry(port, chunk, unified, unifiedNoFitBarrier: true);
            return;
        }

        if (hasLeft) ResolveLeafGeometry(port, chunk, left, unifiedNoFitBarrier: false);
        if (hasRight) ResolveLeafGeometry(port, chunk, right, unifiedNoFitBarrier: false);
    }

    private static void ResolveLeafGeometry(MultiGatePortRuntime port, BodyChunk chunk, GateLeaf leaf, bool unifiedNoFitBarrier)
    {
        Vector2 rel = chunk.pos - leaf.Center;
        float u = Vector2.Dot(rel, leaf.Tangent);
        float v = Vector2.Dot(rel, leaf.Normal);
        float cu = Mathf.Clamp(u, -leaf.HalfLength, leaf.HalfLength);
        float cv = Mathf.Clamp(v, -leaf.HalfThickness, leaf.HalfThickness);
        Vector2 closest = leaf.Center + leaf.Tangent * cu + leaf.Normal * cv;
        Vector2 delta = chunk.pos - closest;
        float distSq = delta.sqrMagnitude;
        float radius = chunk.TerrainRad;

        Vector2 normal;
        float penetration;
        if (distSq > 0.000001f)
        {
            float dist = Mathf.Sqrt(distSq);
            if (dist >= radius)
            {
                if (!SweptExpandedBox(chunk.lastPos, chunk.pos, leaf, radius, out float hitT, out normal)) return;
                chunk.pos = Vector2.Lerp(chunk.lastPos, chunk.pos, Mathf.Max(0f, hitT - 0.001f));
                penetration = 0.01f;

                // Recompute a representative contact point after CCD rewinds the chunk.
                rel = chunk.pos - leaf.Center;
                u = Vector2.Dot(rel, leaf.Tangent);
                v = Vector2.Dot(rel, leaf.Normal);
                cu = Mathf.Clamp(u, -leaf.HalfLength, leaf.HalfLength);
                cv = Mathf.Clamp(v, -leaf.HalfThickness, leaf.HalfThickness);
                closest = leaf.Center + leaf.Tangent * cu + leaf.Normal * cv;
            }
            else
            {
                normal = delta / dist;
                penetration = radius - dist;
            }
        }
        else
        {
            float du = leaf.HalfLength - Mathf.Abs(u);
            float dv = leaf.HalfThickness - Mathf.Abs(v);
            if (dv <= du) normal = leaf.Normal * (v >= 0f ? 1f : -1f);
            else normal = leaf.Tangent * (u >= 0f ? 1f : -1f);
            penetration = radius + Mathf.Min(du, dv);
        }

        // Vanilla terrain has already resolved during orig(BodyChunk.Update). At the
        // finite frame endpoint only, yield when the native contact is effectively the
        // same support plane. This prevents Tile + gate double-resolution jitter while
        // preserving the oriented collider throughout the real passage aperture.
        if (ShouldYieldToNativeTerrainAtSeam(chunk, leaf, u, normal, penetration)) return;

        chunk.pos += normal * penetration;
        Vector2 surfaceVelocity = unifiedNoFitBarrier
            ? UnifiedBarrierSurfaceVelocity(port, closest)
            : port.SurfaceVelocityAt(leaf, closest);
        ResolveRainWorldSurfaceResponse(chunk, normal, surfaceVelocity);
    }

    private static Vector2 UnifiedBarrierSurfaceVelocity(MultiGatePortRuntime port, Vector2 point)
    {
        // Away from the center opening, keep the real supporting leaf's tangential
        // velocity so a creature standing on a moving diagonal panel is still carried.
        // Inside the sub-diameter opening both inner edges are equally constraining and
        // move in opposite directions; zero is the stable symmetric manifold velocity.
        float localU = Vector2.Dot(point - port.Placed.pos, port.Data.Tangent);
        float aperture = port.Data.PassageWidth * 0.5f * port.OpenFactor;
        if (Mathf.Abs(localU) <= aperture + 0.5f) return Vector2.zero;

        int side = localU < 0f ? -1 : 1;
        return port.TryGetLeaf(side, previous: false, out GateLeaf realLeaf)
            ? port.SurfaceVelocityAt(realLeaf, point)
            : Vector2.zero;
    }

    private static bool ShouldYieldToNativeTerrainAtSeam(
        BodyChunk chunk,
        GateLeaf leaf,
        float localU,
        Vector2 normal,
        float penetration)
    {
        float edgeDistance = leaf.HalfLength - Mathf.Abs(localU);
        if (edgeDistance > chunk.TerrainRad * 1.5f + 6f) return false;
        if (penetration > Mathf.Max(2.5f, chunk.TerrainRad * 0.4f)) return false;

        if (normal.y > 0.05f && chunk.contactPoint.y == -1)
        {
            Vector2 native = chunk.terrainCurveNormal;
            if (native.sqrMagnitude > 0.001f)
            {
                if (Vector2.Dot(native.normalized, normal.normalized) > 0.84f) return true;
            }
            else if (normal.y > 0.55f)
            {
                return true;
            }
        }

        if (normal.y < -0.5f && chunk.contactPoint.y == 1) return true;

        if (Mathf.Abs(normal.x) > 0.55f && Mathf.Abs(normal.x) > Mathf.Abs(normal.y))
        {
            int expected = normal.x < 0f ? 1 : -1;
            if (chunk.contactPoint.x == expected) return true;
        }

        return false;
    }

    private static void ResolveRainWorldSurfaceResponse(BodyChunk chunk, Vector2 normal, Vector2 surfaceVelocity)
    {
        Vector2 relativeVelocity = chunk.vel - surfaceVelocity;
        ApplyTerrainImpact(chunk, normal, relativeVelocity);

        if (normal.y > 0.05f)
        {
            chunk.terrainCurveNormal = normal;
            if (normal.y < TerrainCurve.maxSlideNormalY)
            {
                chunk.contactPoint.y = 0;
                relativeVelocity -= normal * Mathf.Min(
                    0f,
                    Vector2.Dot(relativeVelocity, normal) * (1f + chunk.owner.bounce * 0.2f));
                Vector2 tangent = new(-normal.y, normal.x);
                relativeVelocity -= Vector2.Dot(relativeVelocity, tangent) *
                                    Mathf.Clamp01(1f - chunk.owner.surfaceFriction * 2f) * tangent;
            }
            else
            {
                chunk.contactPoint.y = -1;
                float magnitude = relativeVelocity.magnitude;
                float slopeTransfer = relativeVelocity.x * (-normal.x) / Mathf.Max(0.0001f, normal.y);
                relativeVelocity.y -= slopeTransfer;
                relativeVelocity.y = Mathf.Abs(relativeVelocity.y) * chunk.owner.bounce;
                if (relativeVelocity.y < chunk.owner.gravity ||
                    relativeVelocity.y < 1f + 9f * (1f - chunk.owner.bounce))
                {
                    relativeVelocity.y = 0f;
                }
                relativeVelocity.y += slopeTransfer;
                relativeVelocity.x *= Mathf.Clamp(chunk.owner.surfaceFriction * 2f, 0f, 1f);
                relativeVelocity = Vector2.ClampMagnitude(relativeVelocity, magnitude);
            }
        }
        else
        {
            float into = Vector2.Dot(relativeVelocity, normal);
            if (into < 0f)
            {
                relativeVelocity -= normal * into * (1f + chunk.owner.bounce * 0.2f);
                Vector2 tangent = new(-normal.y, normal.x);
                relativeVelocity -= Vector2.Dot(relativeVelocity, tangent) *
                                    Mathf.Clamp01(1f - chunk.owner.surfaceFriction * 2f) * tangent;
            }

            if (normal.y < -0.5f) chunk.contactPoint.y = 1;
        }

        if (Mathf.Abs(normal.x) > 0.55f && Mathf.Abs(normal.x) > Mathf.Abs(normal.y))
        {
            chunk.contactPoint.x = normal.x < 0f ? 1 : -1;
        }

        chunk.vel = relativeVelocity + surfaceVelocity;
    }

    private static void ApplyTerrainImpact(BodyChunk chunk, Vector2 normal, Vector2 relativeVelocity)
    {
        if (normal.y > 0.05f)
        {
            float impact = -relativeVelocity.y * normal.y;
            if (impact > chunk.owner.impactTreshhold)
            {
                chunk.owner.TerrainImpact(chunk.index, new IntVector2(0, -1), impact, chunk.lastContactPoint.y > -1);
            }
            return;
        }

        float intoSpeed = -Vector2.Dot(relativeVelocity, normal);
        if (intoSpeed <= chunk.owner.impactTreshhold) return;

        if (normal.y < -0.5f && Mathf.Abs(normal.y) >= Mathf.Abs(normal.x))
        {
            chunk.owner.TerrainImpact(chunk.index, new IntVector2(0, 1), intoSpeed, chunk.lastContactPoint.y < 1);
        }
        else if (Mathf.Abs(normal.x) > 0.55f)
        {
            int direction = normal.x < 0f ? 1 : -1;
            bool firstContact = direction > 0 ? chunk.lastContactPoint.x < 1 : chunk.lastContactPoint.x > -1;
            chunk.owner.TerrainImpact(chunk.index, new IntVector2(direction, 0), intoSpeed, firstContact);
        }
    }

    private static bool SweptExpandedBox(Vector2 from, Vector2 to, GateLeaf leaf, float radius, out float hitT, out Vector2 hitNormal)
    {
        Vector2 a = from - leaf.Center;
        Vector2 b = to - leaf.Center;
        Vector2 p0 = new(Vector2.Dot(a, leaf.Tangent), Vector2.Dot(a, leaf.Normal));
        Vector2 p1 = new(Vector2.Dot(b, leaf.Tangent), Vector2.Dot(b, leaf.Normal));
        Vector2 d = p1 - p0;
        float ex = leaf.HalfLength + radius;
        float ey = leaf.HalfThickness + radius;
        float tMin = 0f;
        float tMax = 1f;
        hitNormal = Vector2.zero;

        if (!Slab(p0.x, d.x, -ex, ex, ref tMin, ref tMax, leaf.Tangent, ref hitNormal)) { hitT = 0f; return false; }
        if (!Slab(p0.y, d.y, -ey, ey, ref tMin, ref tMax, leaf.Normal, ref hitNormal)) { hitT = 0f; return false; }
        hitT = tMin;
        return tMin >= 0f && tMin <= 1f;
    }

    private static bool Slab(float p, float d, float min, float max, ref float tMin, ref float tMax, Vector2 axis, ref Vector2 normal)
    {
        if (Mathf.Abs(d) < 0.000001f) return p >= min && p <= max;
        float inv = 1f / d;
        float t1 = (min - p) * inv;
        float t2 = (max - p) * inv;
        Vector2 n1 = axis * -Mathf.Sign(d);
        if (t1 > t2) (t1, t2) = (t2, t1);
        if (t1 > tMin) { tMin = t1; normal = n1; }
        tMax = Mathf.Min(tMax, t2);
        return tMin <= tMax;
    }
}
