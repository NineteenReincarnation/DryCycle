using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandSurface
{
    internal readonly struct Contact
    {
        internal readonly float U;
        internal readonly Vector2 SurfacePoint;
        internal readonly Vector2 BottomPoint;
        internal readonly Vector2 Tangent;
        internal readonly Vector2 Inward;
        internal readonly float DepthLength;
        internal readonly float SignedDepth;

        internal Contact(
            float u,
            Vector2 surfacePoint,
            Vector2 bottomPoint,
            Vector2 tangent,
            Vector2 inward,
            float depthLength,
            float signedDepth)
        {
            U = u;
            SurfacePoint = surfacePoint;
            BottomPoint = bottomPoint;
            Tangent = tangent;
            Inward = inward;
            DepthLength = depthLength;
            SignedDepth = signedDepth;
        }
    }

    internal static void SampleZone(
        PlacedObject placedObject,
        QuicksandZoneData data,
        Vector2[] surface,
        Vector2[] bottom)
    {
        if (placedObject == null || data == null || surface == null || bottom == null)
        {
            return;
        }

        int count = Mathf.Min(surface.Length, bottom.Length);
        if (count <= 0)
        {
            return;
        }

        float bottomY = placedObject.pos.y - data.BottomDepth;
        for (int i = 0; i < count; i++)
        {
            float u = count <= 1 ? 0f : (float)i / (count - 1);
            surface[i] = placedObject.pos + EvaluateByApproximateLength(data.SurfaceSpline, u);
            bottom[i] = new Vector2(surface[i].x, bottomY);
        }
    }

    internal static bool TryGetContact(
        Vector2 point,
        float radius,
        Vector2[] surface,
        Vector2[] bottom,
        out Contact contact)
    {
        contact = default;
        if (surface == null || bottom == null)
        {
            return false;
        }

        int count = Mathf.Min(surface.Length, bottom.Length);
        if (count < 2)
        {
            return false;
        }

        float bestDistanceSq = float.MaxValue;
        int bestSegment = -1;
        float bestT = 0f;
        Vector2 bestSurface = Vector2.zero;

        for (int i = 0; i < count - 1; i++)
        {
            Vector2 a = surface[i];
            Vector2 b = surface[i + 1];
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 0.001f)
            {
                continue;
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
            Vector2 closest = a + ab * t;
            float distanceSq = (point - closest).sqrMagnitude;
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestSegment = i;
                bestT = t;
                bestSurface = closest;
            }
        }

        if (bestSegment < 0)
        {
            return false;
        }

        Vector2 segment = surface[bestSegment + 1] - surface[bestSegment];
        Vector2 tangent = SafeNormal(segment, Vector2.right);

        if (bestSegment == 0 && bestT <= 0.0001f &&
            Vector2.Dot(point - surface[0], tangent) < -radius)
        {
            return false;
        }

        if (bestSegment == count - 2 && bestT >= 0.9999f &&
            Vector2.Dot(point - surface[count - 1], tangent) > radius)
        {
            return false;
        }

        Vector2 bottomPoint = Vector2.Lerp(
            bottom[bestSegment],
            bottom[bestSegment + 1],
            bestT);
        Vector2 depthVector = bottomPoint - bestSurface;

        Vector2 inward = SafeNormal(new Vector2(tangent.y, -tangent.x), Vector2.down);
        if (Vector2.Dot(inward, depthVector) < 0f)
        {
            inward = -inward;
        }

        float depthLength = Mathf.Max(4f, Vector2.Dot(depthVector, inward));
        float signedDepth = Vector2.Dot(point - bestSurface, inward);

        if (signedDepth < -radius || signedDepth > depthLength + radius * 0.65f)
        {
            return false;
        }

        float u = (bestSegment + bestT) / (count - 1f);
        contact = new Contact(
            u,
            bestSurface,
            bottomPoint,
            tangent,
            inward,
            depthLength,
            signedDepth);
        return true;
    }

    internal static Vector2 EvaluateByApproximateLength(BezierSpline spline, float u)
    {
        if (spline == null || spline.Segments <= 0)
        {
            return Vector2.zero;
        }

        u = Mathf.Clamp01(u);
        float totalLength = Mathf.Max(0.001f, spline.GetFullLength);
        float target = totalLength * u;

        for (int segment = 0; segment < spline.Segments; segment++)
        {
            float segmentLength = Mathf.Max(0.001f, spline.GetSegmentLength(segment));
            if (target <= segmentLength || segment == spline.Segments - 1)
            {
                return spline.GetBezier(segment).GetPoint(Mathf.Clamp01(target / segmentLength));
            }

            target -= segmentLength;
        }

        return spline.posB;
    }

    internal static float FindNearestU(BezierSpline spline, Vector2 localPoint, out float distance)
    {
        const int coarseSamples = 96;
        float bestU = 0f;
        float bestDistanceSq = float.MaxValue;
        Vector2 previous = EvaluateByApproximateLength(spline, 0f);

        for (int i = 1; i <= coarseSamples; i++)
        {
            float nextU = (float)i / coarseSamples;
            Vector2 next = EvaluateByApproximateLength(spline, nextU);
            Vector2 segment = next - previous;
            float segmentLengthSq = segment.sqrMagnitude;
            float t = segmentLengthSq > 0.0001f
                ? Mathf.Clamp01(Vector2.Dot(localPoint - previous, segment) / segmentLengthSq)
                : 0f;
            Vector2 closest = previous + segment * t;
            float candidate = (localPoint - closest).sqrMagnitude;
            if (candidate < bestDistanceSq)
            {
                bestDistanceSq = candidate;
                bestU = Mathf.Lerp((float)(i - 1) / coarseSamples, nextU, t);
            }

            previous = next;
        }

        distance = Mathf.Sqrt(bestDistanceSq);
        return Mathf.Clamp01(bestU);
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
