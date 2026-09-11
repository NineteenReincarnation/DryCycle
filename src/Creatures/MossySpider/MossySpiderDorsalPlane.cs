using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// One rigid dorsal plane shared by rendering and the shared dynamic-walkable-surface runtime.
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

    /// <summary>
    /// 为共享动态地形采样器提供背部直线的两个端点。
    /// 点序会保证当前帧曲线的左侧法线指向 MossySpider 背部外侧；前后各保留原来的 8 px 碰撞延伸。
    ///
    /// Supplies the two endpoints of the dorsal line to the shared dynamic-surface sampler.
    /// Point order guarantees that the curve's left-hand normal faces the dorsal outside, while preserving the existing 8 px collision overhang at both ends.
    /// </summary>
    internal static bool TryGetWalkableCurvePoint(
        MossySpider spider,
        int index,
        bool previous,
        out Vector2 point)
    {
        point = default;
        if (index < 0 || index > 1 ||
            !TryGetFrame(spider, previous ? 0f : 1f, out Frame frame))
        {
            return false;
        }

        Vector2 start = frame.Start - frame.Tangent * CollisionOverhang;
        Vector2 end = frame.End + frame.Tangent * CollisionOverhang;

        // DynamicWalkableCurveSampler derives its contact normal from point order.
        // MossySpiderDorsalPlane deliberately keeps the visual dorsal normal world-up,
        // so reverse the geometry only when needed to make the sampler derive that same side.
        Vector2 leftNormal = new(-frame.Tangent.y, frame.Tangent.x);
        if (Vector2.Dot(leftNormal, frame.Normal) < 0f)
        {
            Vector2 swap = start;
            start = end;
            end = swap;
        }

        point = index == 0 ? start : end;
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
