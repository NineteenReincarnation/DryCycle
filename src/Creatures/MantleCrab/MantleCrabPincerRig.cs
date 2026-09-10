using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Dedicated articulated rig for one capture appendage. The authored rest silhouette is the
/// primary constraint; secondary inertia is allowed at the joints, but segment lengths remain
/// rigid. This keeps the long appendage articulated instead of letting endpoint IK straighten it
/// into a hanging spear.
/// </summary>
internal sealed class MantleCrabPincerRig
{
    private static readonly float[] Follow = [.46f, .39f, .33f, .27f];
    private static readonly float[] MaxLag = [5f, 8f, 12f, 16f];

    internal readonly int Index;
    internal readonly float Side;
    internal readonly Vector2[] Rest;
    internal readonly float[] Lengths = new float[MantleCrabPincerAnatomy.SegmentCount];
    internal readonly Vector2[] Pos = new Vector2[MantleCrabPincerAnatomy.SegmentCount];
    internal readonly Vector2[] LastPos = new Vector2[MantleCrabPincerAnatomy.SegmentCount];
    internal readonly Vector2[] Velocity = new Vector2[MantleCrabPincerAnatomy.SegmentCount];

    internal Vector2 Anchor;
    internal Vector2 LastAnchor;
    internal float Open;
    internal float LastOpen;
    internal float TargetOpen;

    internal Vector2 Wrist => Pos[Pos.Length - 1];
    internal Vector2 RestWristOffset => Rest[Rest.Length - 1] - Rest[0];

    internal MantleCrabPincerRig(int index)
    {
        Index = index;
        Side = index == 0 ? -1f : 1f;
        Rest = MantleCrabPincerAnatomy.Landmarks(index);
        for (int i = 0; i < Lengths.Length; i++)
            Lengths[i] = Vector2.Distance(Rest[i], Rest[i + 1]);

        Open = LastOpen = TargetOpen = MantleCrabPincerAnatomy.IdleOpen[index];
    }

    internal void Reset(MantleCrab crab, Vector2 anchor)
    {
        Anchor = LastAnchor = anchor;
        for (int i = 0; i < Pos.Length; i++)
        {
            Vector2 point = WorldRestPoint(crab, anchor, i + 1);
            Pos[i] = LastPos[i] = point;
            Velocity[i] = Vector2.zero;
        }

        Open = LastOpen = TargetOpen = MantleCrabPincerAnatomy.IdleOpen[Index];
    }

    internal void Update(MantleCrab crab, Vector2 anchor)
    {
        LastAnchor = Anchor;
        Anchor = anchor;
        LastOpen = Open;
        Open = Mathf.Lerp(Open, Mathf.Clamp01(TargetOpen), .14f);

        Vector2 previous = anchor;
        for (int i = 0; i < Pos.Length; i++)
        {
            LastPos[i] = Pos[i];
            Vector2 desired = WorldRestPoint(crab, anchor, i + 1);

            // Predict from the previous-frame joint velocity, then pull toward the authored pose.
            // Distal joints are intentionally slower and may lag farther behind, creating weight
            // without turning a rigid chitin segment into a rope.
            Vector2 predicted = Pos[i] + Velocity[i] * .58f;
            Vector2 candidate = Vector2.Lerp(predicted, desired, Follow[i]);
            candidate = desired + Vector2.ClampMagnitude(candidate - desired, MaxLag[i]);

            Vector2 direction = candidate - previous;
            if (direction.sqrMagnitude < .0001f)
                direction = desired - previous;
            if (direction.sqrMagnitude < .0001f)
                direction = Vector2.down;

            Pos[i] = previous + direction.normalized * Lengths[i];
            previous = Pos[i];
        }

        for (int i = 0; i < Pos.Length; i++)
            Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], 18f);
    }

    internal Vector2 Point(int index, float timeStacker) =>
        Vector2.Lerp(LastPos[index], Pos[index], timeStacker);

    internal Vector2 InterpolatedAnchor(float timeStacker) =>
        Vector2.Lerp(LastAnchor, Anchor, timeStacker);

    internal float InterpolatedOpen(float timeStacker) =>
        Mathf.Lerp(LastOpen, Open, timeStacker);

    internal Vector2 PalmAxis(float timeStacker)
    {
        Vector2 beforeWrist = Point(Pos.Length - 2, timeStacker);
        Vector2 wrist = Point(Pos.Length - 1, timeStacker);
        Vector2 axis = wrist - beforeWrist;
        return axis.sqrMagnitude > .0001f ? axis.normalized : Vector2.down;
    }

    private Vector2 WorldRestPoint(MantleCrab crab, Vector2 anchor, int restIndex)
    {
        Vector2 axis = crab?.Axis ?? Vector2.right;
        Vector2 up = new(-axis.y, axis.x);
        Vector2 local = Rest[restIndex] - Rest[0];
        return anchor + axis * local.x + up * local.y;
    }
}
