using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal static class MantleCrabRigMath
{
    internal static float SupportAcceleration(float heightError, float velocity, float gravity, int feet)
    {
        if (feet <= 0) return 0f;
        float capacity = feet == 1 ? .55f : 1.8f;
        return Mathf.Clamp(gravity + heightError * .022f - velocity * .24f, 0f, gravity * capacity) / feet;
    }

    /// <summary>Four rigid links with a stable bend seed; no segment is a BodyChunk.</summary>
    internal static void Solve(Vector2 anchor, Vector2 target, float[] lengths, Vector2[] points)
    {
        int count = lengths.Length;
        float reach = 0f;
        for (int i = 0; i < count; i++) reach += lengths[i];
        if (Vector2.Distance(anchor, target) >= reach - .01f)
        {
            Vector2 direction = (target - anchor).normalized;
            for (int i = 0; i < count; i++) { anchor += direction * lengths[i]; points[i] = anchor; }
            return;
        }
        for (int iteration = 0; iteration < 32; iteration++)
        {
            points[count - 1] = target;
            for (int i = count - 2; i >= 0; i--)
                points[i] = points[i + 1] + (points[i] - points[i + 1]).normalized * lengths[i + 1];
            Vector2 previous = anchor;
            for (int i = 0; i < count; i++)
            {
                Vector2 delta = points[i] - previous;
                if (delta.sqrMagnitude < .0001f) delta = Vector2.down;
                points[i] = previous + delta.normalized * lengths[i];
                previous = points[i];
            }
            if ((points[count - 1] - target).sqrMagnitude < .0025f) break;
        }
    }
}
