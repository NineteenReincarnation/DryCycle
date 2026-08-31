using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// One rigid dorsal plane shared by rendering and player collision.
///
/// The torso BodyChunks remain flexible, but the moss-covered top is deliberately not a
/// spline. Its walkable section is one straight segment built from the front and rear
/// dorsal anchors, so there are no per-chunk seams for a player to fall through.
/// </summary>
internal static class MossySpiderDorsalPlane
{
    internal const float SurfaceHeight = 39.5f;
    internal const float CollisionOverhang = 8f;

    internal readonly struct Frame
    {
        internal readonly Vector2 Start;
        internal readonly Vector2 End;
        internal readonly Vector2 Tangent;
        internal readonly Vector2 Normal;

        internal Frame(Vector2 start, Vector2 end, Vector2 tangent, Vector2 normal)
        {
            Start = start;
            End = end;
            Tangent = tangent;
            Normal = normal;
        }
    }

    internal static bool TryGetFrame(
        MossySpider spider,
        float timeStacker,
        out Frame frame)
    {
        frame = default;
        if (spider?.bodyChunks == null || spider.bodyChunks.Length < 2)
        {
            return false;
        }

        timeStacker = Mathf.Clamp01(timeStacker);
        Vector2 front = BodyPoint(
            spider,
            MossySpiderSilhouette.WalkableStartU,
            timeStacker);
        Vector2 rear = BodyPoint(
            spider,
            MossySpiderSilhouette.WalkableEndU,
            timeStacker);

        Vector2 tangent = rear - front;
        if (tangent.sqrMagnitude < 0.001f)
        {
            BodyChunk first = spider.bodyChunks[0];
            BodyChunk last = spider.bodyChunks[spider.bodyChunks.Length - 1];
            tangent = Vector2.Lerp(first.lastPos, first.pos, timeStacker) -
                      Vector2.Lerp(last.lastPos, last.pos, timeStacker);
            tangent = -tangent;
        }

        if (tangent.sqrMagnitude < 0.001f)
        {
            tangent = Vector2.right;
        }
        tangent.Normalize();

        Vector2 normal = new(-tangent.y, tangent.x);
        if (normal.y < 0f)
        {
            normal = -normal;
        }
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = Vector2.up;
        }
        normal.Normalize();

        frame = new Frame(
            front + normal * SurfaceHeight,
            rear + normal * SurfaceHeight,
            tangent,
            normal);
        return true;
    }

    internal static bool TrySurfaceAtWorldX(
        MossySpider spider,
        float worldX,
        out float u,
        out Vector2 currentPoint,
        out Vector2 previousPoint,
        out Vector2 normal)
    {
        u = 0f;
        currentPoint = Vector2.zero;
        previousPoint = Vector2.zero;
        normal = Vector2.up;

        if (!TryGetFrame(spider, 1f, out Frame current) ||
            !TryGetFrame(spider, 0f, out Frame previous))
        {
            return false;
        }

        Vector2 currentA = current.Start - current.Tangent * CollisionOverhang;
        Vector2 currentB = current.End + current.Tangent * CollisionOverhang;
        float dx = currentB.x - currentA.x;
        if (Mathf.Abs(dx) < 0.001f)
        {
            return false;
        }

        float t = (worldX - currentA.x) / dx;
        if (t < 0f || t > 1f)
        {
            return false;
        }

        Vector2 previousA = previous.Start - previous.Tangent * CollisionOverhang;
        Vector2 previousB = previous.End + previous.Tangent * CollisionOverhang;
        currentPoint = Vector2.Lerp(currentA, currentB, t);
        previousPoint = Vector2.Lerp(previousA, previousB, t);
        normal = current.Normal;

        float coreLength = Vector2.Distance(current.Start, current.End);
        float extendedLength = coreLength + CollisionOverhang * 2f;
        float coreT = coreLength > 0.001f
            ? (t * extendedLength - CollisionOverhang) / coreLength
            : 0.5f;
        u = Mathf.Lerp(
            MossySpiderSilhouette.WalkableStartU,
            MossySpiderSilhouette.WalkableEndU,
            Mathf.Clamp01(coreT));
        return true;
    }

    private static Vector2 BodyPoint(
        MossySpider spider,
        float u,
        float timeStacker)
    {
        int count = spider.bodyChunks.Length;
        float x = Mathf.Clamp01(u) * (count - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(x), 0, count - 1);
        int b = Mathf.Min(count - 1, a + 1);
        float t = x - Mathf.Floor(x);

        BodyChunk chunkA = spider.bodyChunks[a];
        BodyChunk chunkB = spider.bodyChunks[b];
        Vector2 pointA = Vector2.Lerp(chunkA.lastPos, chunkA.pos, timeStacker);
        Vector2 pointB = Vector2.Lerp(chunkB.lastPos, chunkB.pos, timeStacker);
        return Vector2.Lerp(pointA, pointB, t);
    }
}
