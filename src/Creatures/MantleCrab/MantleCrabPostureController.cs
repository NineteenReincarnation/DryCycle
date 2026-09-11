using RWCustom;
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
    private const float RecoveryEnterDegrees = 28f;
    private const float RecoveryExitDegrees = 12f;
    private const float UnsupportedRecoveryAngle = 22f;
    private const float SupportHeightGain = .0065f;
    private const float SupportVelocityDamping = .34f;
    private const float MaximumSupportFactor = 1.18f;
    private const float OneSidedSupportFactor = .62f;
    private const float PostureGain = .00115f;
    private const float PostureDamping = .18f;
    private const float MaximumAngularAcceleration = .0012f;
    private const float RecoveryAngularAcceleration = .0026f;
    private const float RecoveryAngularDamping = .24f;

    private readonly MantleCrab crab;
    private readonly float[] supportQuality = new float[4];
    private Vector2 supportNormal = Vector2.up;
    private Vector2 walkAxis = Vector2.right;
    private bool recovering;
    private float recoveryDirection = 1f;

    internal MantleCrabPostureController(MantleCrab crab)
    {
        this.crab = crab;
    }

    internal Vector2 SupportNormal => supportNormal;
    internal Vector2 WalkAxis => walkAxis;
    internal bool Recovering => recovering;
    internal bool SeverelyUnstable => recovering;

    internal void Reset()
    {
        supportNormal = Vector2.up;
        walkAxis = Vector2.right;
        recovering = false;
        recoveryDirection = 1f;
        for (int i = 0; i < supportQuality.Length; i++)
            supportQuality[i] = 0f;
    }

    internal void UpdateFrame()
    {
        float shellAngle = ShellAngleRadians();
        float absoluteShellAngle = Mathf.Abs(shellAngle) * Mathf.Rad2Deg;
        if (!recovering && absoluteShellAngle >= RecoveryEnterDegrees)
        {
            recovering = true;
            recoveryDirection = ChooseRecoveryDirection(shellAngle);
        }
        else if (recovering && absoluteShellAngle <= RecoveryExitDegrees)
        {
            recovering = false;
        }

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

        // 失衡恢复时更快把参考系拉回世界竖直，避免地面法线和翻倒甲壳共同形成新的错误姿态。
        // During recovery, converge toward the gravity frame faster so a tipped shell cannot redefine its own "up".
        float frameBlend = recovering ? .30f : .18f;
        supportNormal = Vector2.Lerp(supportNormal, limited, frameBlend).normalized;
        walkAxis = new Vector2(supportNormal.y, -supportNormal.x).normalized;
        if (walkAxis.x < 0f)
            walkAxis = -walkAxis;

        // 原来的恢复只在脚已经 Plant 时才有力矩。视频里一旦翻到壳上，四脚全失去有效支撑，
        // MantleCrab.Update 又会提前退出，于是恢复控制器永久失去执行机会。
        // Recovery must therefore have a shell-contact path: while the shell itself is touching terrain,
        // use that collision as the reaction point and rock the rigid body until a walking leg can reacquire ground.
        if (recovering && weight < .08f)
            ApplyShellContactRecovery(shellAngle);
    }

    internal void ApplySupportAndPosture(float effectiveGravity, float turnIntent)
    {
        UpdateFrame();
        BodyState(
            out Vector2 center,
            out Vector2 velocity,
            out float totalMass,
            out float inertia,
            out float angularVelocity);

        float qualitySum = 0f;
        float weightedHeightError = 0f;
        float leftQuality = 0f;
        float rightQuality = 0f;
        float leftX = 0f;
        float rightX = 0f;
        int validSupports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = leg.Planted ? leg.SupportQuality(crab) : 0f;
            supportQuality[i] = quality;
            if (quality <= .001f)
                continue;

            validSupports++;
            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float heightError = leg.StandHeight - (crab.Anchor(leg).y - leg.Contact.y);
            weightedHeightError += heightError * quality;
            qualitySum += quality;

            float offsetX = anchor.pos.x - center.x;
            if (offsetX < -1f)
            {
                leftQuality += quality;
                leftX += anchor.pos.x * quality;
            }
            else if (offsetX > 1f)
            {
                rightQuality += quality;
                rightX += anchor.pos.x * quality;
            }
        }

        if (qualitySum <= .001f)
            return;

        bool straddlesCenter = leftQuality > .001f && rightQuality > .001f;
        if (leftQuality > .001f)
            leftX /= leftQuality;
        if (rightQuality > .001f)
            rightX /= rightQuality;

        float meanHeightError = weightedHeightError / qualitySum;
        float rhythm = recovering ? 0f : crab.Locomotion.BodyHeightRhythm;
        float supportAcceleration = effectiveGravity +
                                    (meanHeightError + rhythm) * SupportHeightGain -
                                    velocity.y * SupportVelocityDamping;

        // 两侧都有可靠支撑时才允许完整托住身体；只有单侧支撑时必须允许身体缓慢下沉/倾斜去寻找另一侧脚点。
        // Full weight support requires contacts on both sides of the COM. One-sided contacts may brace the body,
        // but they must not become an invisible jack capable of suspending and launching the whole shell.
        float supportCap = Mathf.Max(0f, effectiveGravity) *
                           (straddlesCenter && validSupports >= 2 ? MaximumSupportFactor : OneSidedSupportFactor);
        supportAcceleration = Mathf.Clamp(supportAcceleration, 0f, supportCap);

        float desiredLeftShare = 0f;
        float desiredRightShare = 0f;
        if (straddlesCenter && rightX - leftX > 1f)
        {
            desiredRightShare = Mathf.Clamp01((center.x - leftX) / (rightX - leftX));
            desiredLeftShare = 1f - desiredRightShare;
        }

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            float quality = supportQuality[i];
            if (quality <= .001f)
                continue;

            MantleCrabLimb leg = crab.Legs[i];
            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float share;

            if (straddlesCenter && rightX - leftX > 1f)
            {
                share = anchor.pos.x < center.x
                    ? desiredLeftShare * (quality / Mathf.Max(.001f, leftQuality))
                    : desiredRightShare * (quality / Mathf.Max(.001f, rightQuality));
            }
            else
            {
                share = quality / qualitySum;
            }

            anchor.vel.y += supportAcceleration * totalMass * share / Mathf.Max(.01f, anchor.mass);
        }

        // 甲壳倾斜是姿态误差，不是新的移动方向。进入恢复状态后关闭人工倾斜，只回正。
        // Shell tilt is a posture error, never a new travel direction. Recovery disables manual lean and only rights the shell.
        float terrainLean = Mathf.Atan2(walkAxis.y, walkAxis.x) * .35f;
        float manualLean = recovering
            ? 0f
            : Mathf.Clamp(turnIntent, -1f, 1f) * MaximumManualLeanDegrees * Mathf.Deg2Rad;
        float desiredAngle = recovering
            ? 0f
            : Mathf.Clamp(
                terrainLean + manualLean,
                -MaximumTerrainFollowDegrees * Mathf.Deg2Rad,
                MaximumTerrainFollowDegrees * Mathf.Deg2Rad);

        float angleError = DeltaRadians(desiredAngle, ShellAngleRadians());
        float torqueAuthority = recovering ? 1.25f : straddlesCenter ? 1f : .52f;
        float maxAngularAcceleration = recovering ? RecoveryAngularAcceleration : MaximumAngularAcceleration;
        float damping = recovering ? RecoveryAngularDamping : PostureDamping;
        float angularAcceleration = Mathf.Clamp(
            angleError * PostureGain - angularVelocity * damping,
            -maxAngularAcceleration,
            maxAngularAcceleration) * torqueAuthority;

        if (Mathf.Abs(angularAcceleration) <= .000001f || inertia <= .001f)
            return;

        ApplyPureAngularAcceleration(center, angularAcceleration);
    }

    private void ApplyShellContactRecovery(float shellAngle)
    {
        if (crab.bodyChunks == null || crab.bodyChunks.Length == 0)
            return;

        bool terrainContact = false;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            IntVector2 contact = crab.bodyChunks[i].ContactPoint;
            if (contact.x != 0 || contact.y != 0)
            {
                terrainContact = true;
                break;
            }
        }

        // 空中不允许凭空自转。只有甲壳真正压在地形上时才使用壳-地接触提供反作用力。
        // Never self-right in free fall. The emergency roll is only available while the shell contacts terrain.
        if (!terrainContact)
            return;

        BodyState(
            out Vector2 center,
            out _,
            out _,
            out float inertia,
            out float angularVelocity);
        if (inertia <= .001f)
            return;

        float absoluteAngle = Mathf.Abs(shellAngle) * Mathf.Rad2Deg;
        float direction;
        if (absoluteAngle >= 150f)
        {
            // 接近完全倒扣时左右两条最短回正路径等价，锁定一个方向避免每帧在 ±PI 附近翻号。
            // Near a perfect inversion both routes are equivalent; keep a latched side to avoid sign chatter at ±PI.
            direction = recoveryDirection;
        }
        else
        {
            float error = DeltaRadians(0f, shellAngle);
            direction = Mathf.Abs(error) > .0001f ? Mathf.Sign(error) : recoveryDirection;
            recoveryDirection = direction;
        }

        float authority = Mathf.InverseLerp(UnsupportedRecoveryAngle, 90f, absoluteAngle);
        authority = Mathf.Lerp(.45f, 1f, authority);
        float angularAcceleration = direction * RecoveryAngularAcceleration * authority -
                                    angularVelocity * RecoveryAngularDamping;
        angularAcceleration = Mathf.Clamp(
            angularAcceleration,
            -RecoveryAngularAcceleration,
            RecoveryAngularAcceleration);

        ApplyPureAngularAcceleration(center, angularAcceleration);
    }

    private float ChooseRecoveryDirection(float shellAngle)
    {
        float error = DeltaRadians(0f, shellAngle);
        if (Mathf.Abs(error) > 8f * Mathf.Deg2Rad && Mathf.Abs(Mathf.Abs(shellAngle) - Mathf.PI) > 10f * Mathf.Deg2Rad)
            return Mathf.Sign(error);

        int seed = crab.abstractCreature?.ID.RandomSeed ?? 0;
        return (seed & 1) == 0 ? 1f : -1f;
    }

    private void ApplyPureAngularAcceleration(Vector2 center, float angularAcceleration)
    {
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
