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
    private static readonly float[] Follow = [.48f, .39f, .31f, .24f];
    private static readonly float[] MaxLag = [4.5f, 8f, 13f, 19f];
    private static readonly float[] DistalResponse = [.12f, .34f, .67f, 1f];

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

    internal Vector2 Wrist => Pos[Pos.Length - 2];
    internal Vector2 RestWristOffset => Rest[Rest.Length - 2] - Rest[0];

    private Vector2 lastBodyVelocity;
    private Vector2 bodyAcceleration;

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

        lastBodyVelocity = crab == null ? Vector2.zero : BodyVelocity(crab);
        bodyAcceleration = Vector2.zero;
        Open = LastOpen = TargetOpen = MantleCrabPincerAnatomy.IdleOpen[Index];
    }

    internal void Update(MantleCrab crab, Vector2 anchor)
    {
        LastAnchor = Anchor;
        Anchor = anchor;
        LastOpen = Open;
        Open = Mathf.Lerp(Open, Mathf.Clamp01(TargetOpen), .14f);

        Vector2 currentBodyVelocity = crab == null ? Vector2.zero : BodyVelocity(crab);
        Vector2 rawAcceleration = currentBodyVelocity - lastBodyVelocity;
        bodyAcceleration = Vector2.Lerp(bodyAcceleration, rawAcceleration, .22f);
        lastBodyVelocity = currentBodyVelocity;

        Vector2 previous = anchor;
        for (int i = 0; i < Pos.Length; i++)
        {
            LastPos[i] = Pos[i];
            Vector2 desired = WorldRestPoint(crab, anchor, i + 1);
            desired += SecondaryMotion(crab, i, currentBodyVelocity);

            // 越靠近钳尖越有明显惯性。根部仍牢固跟随甲壳，远端允许逐节滞后，形成真实的关节波传递。
            // Distal joints carry more inertia while the proximal joint remains strongly attached to the mantle.
            float response = DistalResponse[i];
            Vector2 predicted = Pos[i] + Velocity[i] * Mathf.Lerp(.34f, .72f, response);
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
            Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], Mathf.Lerp(9f, 16f, DistalResponse[i]));
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
        if (axis.sqrMagnitude < .0001f) axis = Vector2.down;
        else axis.Normalize();

        // A real chela has a carpal articulation: the manus is allowed to sit at an authored
        // angle to the terminal arm shaft instead of reading as the same rod continued farther.
        return Rotate(axis, MantleCrabPincerAnatomy.PalmRestAngleDegrees[Index] * Mathf.Deg2Rad);
    }

    private Vector2 WorldRestPoint(MantleCrab crab, Vector2 anchor, int restIndex)
    {
        Vector2 shellAxis = crab?.Axis ?? Vector2.right;
        if (shellAxis.sqrMagnitude <= .0001f)
            shellAxis = Vector2.right;
        else
            shellAxis.Normalize();
        Vector2 shellUp = new(-shellAxis.y, shellAxis.x);

        Vector2 local = Rest[restIndex] - Rest[0];
        Vector2 shellPose = anchor + shellAxis * local.x + shellUp * local.y;
        if (crab?.Locomotion == null)
            return shellPose;

        Vector2 walkAxis = crab.Locomotion.WalkAxis;
        Vector2 walkingUp = crab.Locomotion.SupportNormal;
        if (walkAxis.sqrMagnitude <= .0001f) walkAxis = Vector2.right;
        else walkAxis.Normalize();
        if (walkingUp.sqrMagnitude <= .0001f) walkingUp = Vector2.up;
        else walkingUp.Normalize();

        Vector2 gravityPose = anchor + walkAxis * local.x + walkingUp * local.y;
        float distal = Mathf.Clamp01(restIndex / (float)MantleCrabPincerAnatomy.SegmentCount);
        float frameMismatch = 1f - Mathf.Clamp01(Vector2.Dot(shellUp, walkingUp));

        // 正常站立时几乎完全保持设计轮廓；身体倾斜/翻倒后，越远端越倾向继续向世界下方悬垂。
        // In ordinary stance the authored silhouette dominates. During shell tilt the distal chain remains gravity-biased.
        float gravityBias = frameMismatch * Mathf.Lerp(.10f, .72f, distal * distal);
        return Vector2.Lerp(shellPose, gravityPose, gravityBias);
    }

    private Vector2 SecondaryMotion(MantleCrab crab, int segment, Vector2 bodyVelocity)
    {
        if (crab?.Locomotion == null)
            return Vector2.zero;

        float distal = DistalResponse[segment];
        float motion = crab.Locomotion.MotionAmount;
        Vector2 axis = crab.Locomotion.WalkAxis;
        Vector2 up = crab.Locomotion.SupportNormal;
        if (axis.sqrMagnitude <= .0001f) axis = Vector2.right;
        else axis.Normalize();
        if (up.sqrMagnitude <= .0001f) up = Vector2.up;
        else up.Normalize();

        // 两只长钳使用相反相位。摆动不是整体旋转：幅度沿关节链逐级增加，所以能看到节肢逐节响应。
        // Pincers use opposite phases and increasing distal amplitude, producing a visible articulated wave rather than a rigid swing.
        float phase = crab.Locomotion.StridePhase + (Index == 0 ? 0f : Mathf.PI);
        float travelSign = Mathf.Abs(crab.Locomotion.SmoothedMoveIntent) > .04f
            ? Mathf.Sign(crab.Locomotion.SmoothedMoveIntent)
            : 0f;
        float gaitSwing = Mathf.Sin(phase) * travelSign * (2.2f + 7.0f * distal) * motion * crab.ShellScale;
        float gaitLift = Mathf.Max(0f, Mathf.Cos(phase)) * (1.0f + 2.8f * distal) * motion * crab.ShellScale;

        // 身体加减速时远端略向反方向滞后。这里只取很小的量，防止重新变成橡皮绳。
        // Distal sections lag slightly against chassis acceleration, but the offset is tightly capped.
        float alongVelocity = Vector2.Dot(bodyVelocity, axis);
        float alongAcceleration = Vector2.Dot(bodyAcceleration, axis);
        float inertialLag = Mathf.Clamp(-alongAcceleration * 12f - alongVelocity * .32f, -6f, 6f) * distal * crab.ShellScale;

        return axis * (gaitSwing + inertialLag) + up * gaitLift;
    }

    private static Vector2 BodyVelocity(MantleCrab crab)
    {
        if (crab?.bodyChunks == null || crab.bodyChunks.Length == 0)
            return Vector2.zero;

        float mass = 0f;
        Vector2 velocity = Vector2.zero;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            mass += chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }

        return mass > .0001f ? velocity / mass : Vector2.zero;
    }

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
    }
}
