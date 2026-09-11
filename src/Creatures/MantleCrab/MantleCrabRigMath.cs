using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal static class MantleCrabRigMath
{
    /// <summary>
    /// 旧的无约束 FABRIK 仅保留给兼容路径；承重步足必须使用 SolveConstrained。
    ///
    /// The unconstrained FABRIK solver remains only for compatibility paths. Load-bearing legs use SolveConstrained.
    /// </summary>
    internal static void Solve(Vector2 anchor, Vector2 target, float[] lengths, Vector2[] points)
    {
        int count = lengths.Length;
        float reach = 0f;
        for (int i = 0; i < count; i++) reach += lengths[i];
        if (Vector2.Distance(anchor, target) >= reach - .01f)
        {
            Vector2 direction = target - anchor;
            direction = direction.sqrMagnitude > .0001f ? direction.normalized : Vector2.down;
            for (int i = 0; i < count; i++)
            {
                anchor += direction * lengths[i];
                points[i] = anchor;
            }
            return;
        }

        for (int iteration = 0; iteration < 32; iteration++)
        {
            points[count - 1] = target;
            for (int i = count - 2; i >= 0; i--)
            {
                Vector2 direction = points[i] - points[i + 1];
                if (direction.sqrMagnitude < .0001f) direction = Vector2.down;
                points[i] = points[i + 1] + direction.normalized * lengths[i + 1];
            }

            Vector2 previous = anchor;
            for (int i = 0; i < count; i++)
            {
                Vector2 delta = points[i] - previous;
                if (delta.sqrMagnitude < .0001f) delta = Vector2.down;
                points[i] = previous + delta.normalized * lengths[i];
                previous = points[i];
            }

            if ((points[count - 1] - target).sqrMagnitude < .0025f)
                break;
        }
    }

    /// <summary>
    /// 带关节角约束的 FABRIK。目标点仍然参与迭代，但每一次从髋部向外重建链条时都会重新限制：
    /// 第一节只能在参考方向附近摆动，后续关节只能在各自解剖静止夹角附近弯曲。
    /// 这样目标不可达时允许足端“够不到”，而不是把膝盖翻面或把整条腿拉成绳子。
    ///
    /// FABRIK with anatomical angle limits. The target remains part of the iteration, but every
    /// root-to-tip reconstruction re-applies a root cone and per-joint relative-angle limits.
    /// If the target is mechanically impossible the foot is allowed to miss instead of inverting a joint.
    /// </summary>
    internal static void SolveConstrained(
        Vector2 anchor,
        Vector2 target,
        float[] lengths,
        Vector2[] points,
        Vector2[] preferredDirections,
        float rootLimitDegrees,
        float jointLimitDegrees)
    {
        int count = lengths.Length;
        if (count <= 0 || points.Length < count || preferredDirections.Length < count)
            return;

        float rootLimit = Mathf.Abs(rootLimitDegrees) * Mathf.Deg2Rad;
        float jointLimit = Mathf.Abs(jointLimitDegrees) * Mathf.Deg2Rad;

        // 参考方向本身就是一个始终合法的种子，避免初始零向量把 FABRIK 推进错误弯曲分支。
        // The authored directions provide an always-valid seed and keep zero-length initialization off the wrong branch.
        Vector2 previous = anchor;
        for (int i = 0; i < count; i++)
        {
            Vector2 preferred = SafeDirection(preferredDirections[i], i == 0 ? Vector2.down : preferredDirections[i - 1]);
            if ((points[i] - previous).sqrMagnitude < .0001f)
                points[i] = previous + preferred * lengths[i];
            previous = points[i];
        }

        for (int iteration = 0; iteration < 24; iteration++)
        {
            // 先从足端把链条拉向目标。
            // First pull the chain backward from the desired foot target.
            points[count - 1] = target;
            for (int i = count - 2; i >= 0; i--)
            {
                Vector2 direction = SafeDirection(points[i] - points[i + 1], -preferredDirections[i + 1]);
                points[i] = points[i + 1] + direction * lengths[i + 1];
            }

            // 再从髋部向外重建，并在这一遍真正执行解剖关节限制。
            // Rebuild from the hip and enforce anatomical limits on this pass.
            previous = anchor;
            Vector2 previousDirection = Vector2.zero;
            for (int i = 0; i < count; i++)
            {
                Vector2 rawDirection = SafeDirection(points[i] - previous, preferredDirections[i]);
                Vector2 direction;

                if (i == 0)
                {
                    Vector2 reference = SafeDirection(preferredDirections[0], Vector2.down);
                    float offset = Mathf.Clamp(SignedAngle(reference, rawDirection), -rootLimit, rootLimit);
                    direction = Rotate(reference, offset);
                }
                else
                {
                    Vector2 preferredPrevious = SafeDirection(preferredDirections[i - 1], previousDirection);
                    Vector2 preferredCurrent = SafeDirection(preferredDirections[i], preferredPrevious);
                    float restRelative = SignedAngle(preferredPrevious, preferredCurrent);
                    float actualRelative = SignedAngle(previousDirection, rawDirection);
                    float clampedRelative = Mathf.Clamp(
                        actualRelative,
                        restRelative - jointLimit,
                        restRelative + jointLimit);
                    direction = Rotate(previousDirection, clampedRelative);
                }

                points[i] = previous + direction * lengths[i];
                previous = points[i];
                previousDirection = direction;
            }

            if ((points[count - 1] - target).sqrMagnitude < .25f)
                break;
        }
    }

    private static Vector2 SafeDirection(Vector2 value, Vector2 fallback)
    {
        if (value.sqrMagnitude > .0001f)
            return value.normalized;
        if (fallback.sqrMagnitude > .0001f)
            return fallback.normalized;
        return Vector2.down;
    }

    private static float SignedAngle(Vector2 from, Vector2 to)
    {
        from = SafeDirection(from, Vector2.right);
        to = SafeDirection(to, from);
        return Mathf.Atan2(Cross(from, to), Vector2.Dot(from, to));
    }

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(
            vector.x * cos - vector.y * sin,
            vector.x * sin + vector.y * cos);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
