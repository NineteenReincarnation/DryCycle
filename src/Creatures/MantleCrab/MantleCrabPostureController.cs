using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// 地面姿态控制器。它把“身体朝向”和“行走方向”从甲壳当前旋转角里拆开：
/// 行走方向来自世界重力与已接触地面的平均法线，甲壳自身只允许在这个地面框架附近轻微倾斜。
///
/// Grounded posture controller. Walking is derived from gravity and the averaged support normal,
/// while the shell is only allowed to lean modestly around that frame instead of defining it.
/// </summary>
internal sealed class MantleCrabPostureController
{
    private const float MaximumTerrainFollowDegrees = 10f;
    private const float MaximumManualLeanDegrees = 6f;
    private const float SevereTiltDegrees = 32f;
    private const float SupportHeightGain = .0065f;
    private const float SupportVelocityDamping = .34f;
    private const float MaximumSupportFactor = 1.18f;
    private const float PostureGain = .00115f;
    private const float PostureDamping = .18f;
    private const float MaximumAngularAcceleration = .0012f;

    private readonly MantleCrab crab;
    private Vector2 supportNormal = Vector2.up;
    private Vector2 walkAxis = Vector2.right;

    internal MantleCrabPostureController(MantleCrab crab)
    {
        this.crab = crab;
    }

    internal Vector2 SupportNormal => supportNormal;
    internal Vector2 WalkAxis => walkAxis;
    internal Vector2 WalkingUp => Vector2.up;
    internal bool SeverelyUnstable => Mathf.Abs(ShellAngleRadians()) > SevereTiltDegrees * Mathf.Deg2Rad;

    internal void Reset()
    {
        supportNormal = Vector2.up;
        walkAxis = Vector2.right;
    }

    internal void UpdateFrame()
    {
        Vector2 summed = Vector2.zero;
        float weight = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.GroundNormal.y <= .15f)
                continue;

            float quality = leg.SupportQuality(crab);
            if (quality <= .001f)
                continue;

            Vector2 normal = leg.GroundNormal.normalized;
            if (normal.y < 0f)
                normal = -normal;
            summed += normal * quality;
            weight += quality;
        }

        Vector2 target = weight > .001f ? summed / weight : Vector2.up;
        if (target.sqrMagnitude <= .0001f)
            target = Vector2.up;
        else
            target.Normalize();

        // 大型节肢动物不会让整个甲壳完全贴着每一块碎石倾斜。
        // Follow only part of terrain slope so long legs absorb most local height differences.
        float terrainAngle = Mathf.Clamp(
            Mathf.Atan2(-target.x, target.y),
            -MaximumTerrainFollowDegrees * Mathf.Deg2Rad,
            MaximumTerrainFollowDegrees * Mathf.Deg2Rad);
        Vector2 limited = new(-Mathf.Sin(terrainAngle), Mathf.Cos(terrainAngle));
        supportNormal = Vector2.Lerp(supportNormal, limited, .18f).normalized;
        walkAxis = new Vector2(supportNormal.y, -supportNormal.x).normalized;
        if (walkAxis.x < 0f)
            walkAxis = -walkAxis;
    }

    internal void ApplySupportAndPosture(float effectiveGravity, float turnIntent)
    {
        UpdateFrame();
        if (crab.SupportingFeet < 2)
            return;

        BodyState(out Vector2 center, out Vector2 velocity, out float totalMass,
            out float inertia, out float angularVelocity);

        float qualitySum = 0f;
        float weightedHeightError = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted)
                continue;

            float quality = leg.SupportQuality(crab);
            if (quality <= .001f)
                continue;

            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float heightError = leg.StandHeight - (crab.Anchor(leg).y - leg.Contact.y);
            weightedHeightError += heightError * quality;
            qualitySum += quality;
        }

        if (qualitySum <= .001f)
            return;

        float meanHeightError = weightedHeightError / qualitySum;
        float supportAcceleration = effectiveGravity + meanHeightError * SupportHeightGain -
                                    velocity.y * SupportVelocityDamping;
        supportAcceleration = Mathf.Clamp(
            supportAcceleration,
            0f,
            Mathf.Max(0f, effectiveGravity) * MaximumSupportFactor);

        // 所有脚共同承担同一个身体高度目标；单条腿只决定自己承担多少负荷。
        // All planted feet support one chassis-height target. Individual feet only determine load share.
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted)
                continue;

            float quality = leg.SupportQuality(crab);
            if (quality <= .001f)
                continue;

            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float share = quality / qualitySum;
            anchor.vel.y += supportAcceleration * totalMass * share / Mathf.Max(.01f, anchor.mass);
        }

        // 甲壳倾斜是“姿态误差”，不是新的移动方向。严重翻转时只做回正，不产生额外平移。
        // Shell tilt is a posture error, never a new travel direction. Severe tilt only triggers recovery torque.
        float terrainLean = Mathf.Atan2(walkAxis.y, walkAxis.x) * .35f;
        float manualLean = Mathf.Clamp(turnIntent, -1f, 1f) * MaximumManualLeanDegrees * Mathf.Deg2Rad;
        float desiredAngle = Mathf.Clamp(
            terrainLean + manualLean,
            -MaximumTerrainFollowDegrees * Mathf.Deg2Rad,
            MaximumTerrainFollowDegrees * Mathf.Deg2Rad);
        if (SeverelyUnstable)
            desiredAngle = 0f;

        float angleError = DeltaRadians(desiredAngle, ShellAngleRadians());
        float angularAcceleration = Mathf.Clamp(
            angleError * PostureGain - angularVelocity * PostureDamping,
            -MaximumAngularAcceleration,
            MaximumAngularAcceleration);

        if (Mathf.Abs(angularAcceleration) <= .000001f || inertia <= .001f)
            return;

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            Vector2 offset = chunk.pos - center;
            chunk.vel += new Vector2(-offset.y, offset.x) * angularAcceleration;
        }
    }

    private void BodyState(
        out Vector2 center,
        out Vector2 velocity,
        out float totalMass,
        out float inertia,
        out float angularVelocity)
    {
        center = Vector2.zero;
        velocity = Vector2.zero;
        totalMass = 0f;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            totalMass += chunk.mass;
            center += chunk.pos * chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }

        totalMass = Mathf.Max(.0001f, totalMass);
        center /= totalMass;
        velocity /= totalMass;

        float angularMomentum = 0f;
        inertia = 0f;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            Vector2 offset = chunk.pos - center;
            Vector2 relativeVelocity = chunk.vel - velocity;
            inertia += chunk.mass * offset.sqrMagnitude;
            angularMomentum += chunk.mass * Cross(offset, relativeVelocity);
        }

        angularVelocity = angularMomentum / Mathf.Max(1f, inertia);
    }

    private float ShellAngleRadians()
    {
        Vector2 axis = crab.Axis;
        if (axis.sqrMagnitude <= .0001f)
            return 0f;
        axis.Normalize();
        return NormalizeRadians(Mathf.Atan2(axis.y, axis.x));
    }

    private static float DeltaRadians(float target, float current) =>
        NormalizeRadians(target - current);

    private static float NormalizeRadians(float angle)
    {
        while (angle > Mathf.PI) angle -= Mathf.PI * 2f;
        while (angle < -Mathf.PI) angle += Mathf.PI * 2f;
        return angle;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
