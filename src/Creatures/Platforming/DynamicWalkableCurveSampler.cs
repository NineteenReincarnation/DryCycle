using UnityEngine;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Optional geometry contract for moving surfaces that are best represented as a finite ordered
/// polyline. Points must be ordered so the walkable/outside side is the left-hand normal of the
/// curve direction. Providers expose current and previous-frame points; the shared sampler owns
/// nearest-segment projection, endpoint handling, normals and provider coordinates.
/// </summary>
public interface IDynamicWalkableCurveGeometry
{
    int CurvePointCount { get; }

    /// <summary>
    /// Returns one curve vertex. previous=true must return the matching point from the previous
    /// physics frame so moving-platform velocity can be reconstructed without provider-specific
    /// rider logic.
    /// </summary>
    bool TryGetCurvePoint(int index, bool previous, out Vector2 point);
}

/// <summary>
/// Rain-World-style finite curve sampler for dynamic walkable surfaces. The stored coordinate is
/// normalized to [0,1], independent of tessellation density, so providers may change point count
/// without changing the rider contract.
/// </summary>
public static class DynamicWalkableCurveSampler
{
    private const float Epsilon = 0.000001f;
    private const float EndpointEpsilon = 0.0001f;

    public static bool TrySample(
        IWalkableDynamicSurface surface,
        IDynamicWalkableCurveGeometry geometry,
        Vector2 worldPosition,
        out WalkableSurfaceSample sample)
    {
        sample = default;
        if (surface == null || geometry == null || geometry.CurvePointCount < 2)
            return false;

        int pointCount = geometry.CurvePointCount;
        float bestDistance = float.MaxValue;
        float bestCoordinate = 0f;
        bool found = false;

        if (!geometry.TryGetCurvePoint(0, previous: false, out Vector2 a))
            return false;

        for (int i = 0; i < pointCount - 1; i++)
        {
            if (!geometry.TryGetCurvePoint(i + 1, previous: false, out Vector2 b))
                return false;

            Vector2 segment = b - a;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared > Epsilon)
            {
                float t = Mathf.Clamp01(Vector2.Dot(worldPosition - a, segment) / lengthSquared);
                Vector2 nearest = Vector2.Lerp(a, b, t);
                float distance = (worldPosition - nearest).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCoordinate = (i + t) / (pointCount - 1f);
                    found = true;
                }
            }

            a = b;
        }

        if (!found || !TrySample(surface, geometry, bestCoordinate, out sample))
            return false;

        // Finite curves must not behave like infinitely extended slopes. At an exposed endpoint,
        // use the radial contact normal from the endpoint to the rider. This gives a BodyChunk the
        // same rounded roll-off behaviour it gets when leaving the end of ordinary curved terrain.
        if (bestCoordinate <= EndpointEpsilon || bestCoordinate >= 1f - EndpointEpsilon)
        {
            Vector2 radial = worldPosition - sample.Point;
            if (radial.sqrMagnitude > Epsilon)
            {
                radial.Normalize();
                if (Vector2.Dot(radial, sample.Normal) > 0f)
                {
                    Vector2 tangent = new(radial.y, -radial.x);
                    if (Vector2.Dot(tangent, sample.Tangent) < 0f)
                        tangent = -tangent;
                    sample = new WalkableSurfaceSample(
                        surface,
                        sample.Coordinate,
                        sample.Point,
                        sample.PreviousPoint,
                        radial,
                        tangent);
                }
            }
        }

        return true;
    }

    public static bool TrySample(
        IWalkableDynamicSurface surface,
        IDynamicWalkableCurveGeometry geometry,
        float coordinate,
        out WalkableSurfaceSample sample)
    {
        sample = default;
        if (surface == null || geometry == null || geometry.CurvePointCount < 2 ||
            coordinate < 0f || coordinate > 1f)
            return false;

        if (!TryPointAtCoordinate(geometry, coordinate, previous: false, out Vector2 point) ||
            !TryPointAtCoordinate(geometry, coordinate, previous: true, out Vector2 previousPoint) ||
            !TryTangentAtCoordinate(geometry, coordinate, out Vector2 tangent))
            return false;

        Vector2 normal = new(-tangent.y, tangent.x);
        sample = new WalkableSurfaceSample(
            surface,
            coordinate,
            point,
            previousPoint,
            normal,
            tangent);
        return true;
    }

    private static bool TryPointAtCoordinate(
        IDynamicWalkableCurveGeometry geometry,
        float coordinate,
        bool previous,
        out Vector2 point)
    {
        point = default;
        int pointCount = geometry.CurvePointCount;
        float scaled = Mathf.Clamp01(coordinate) * (pointCount - 1f);
        int index = Mathf.Min(pointCount - 2, Mathf.FloorToInt(scaled));
        float t = scaled - index;

        if (!geometry.TryGetCurvePoint(index, previous, out Vector2 a) ||
            !geometry.TryGetCurvePoint(index + 1, previous, out Vector2 b))
            return false;

        point = Vector2.Lerp(a, b, t);
        return true;
    }

    private static bool TryTangentAtCoordinate(
        IDynamicWalkableCurveGeometry geometry,
        float coordinate,
        out Vector2 tangent)
    {
        tangent = Vector2.right;
        float step = 1f / Mathf.Max(1f, geometry.CurvePointCount - 1f);
        float before = Mathf.Max(0f, coordinate - step * .5f);
        float after = Mathf.Min(1f, coordinate + step * .5f);

        if (after - before <= Epsilon ||
            !TryPointAtCoordinate(geometry, before, previous: false, out Vector2 a) ||
            !TryPointAtCoordinate(geometry, after, previous: false, out Vector2 b))
            return false;

        Vector2 direction = b - a;
        if (direction.sqrMagnitude <= Epsilon)
            return false;

        tangent = direction.normalized;
        return true;
    }
}
