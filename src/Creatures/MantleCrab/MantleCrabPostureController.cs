using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal enum MantleCrabRecoveryPhase
{
    None,
    Retract,
    Brace,
    Roll,
    Deploy
}

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
    private const float MovementStopDegrees = 32f;
    private const float RecoveryEnterDegrees = 58f;
    private const float RecoveryExitDegrees = 12f;
    private const float RecoveryDeployDegrees = 46f;
    private const float SupportHeightGain = .0065f;
    private const float SupportVelocityDamping = .34f;
    private const float MaximumSupportFactor = 1.18f;
    private const float OneSidedSupportFactor = .62f;
    private const float PostureGain = .00115f;
    private const float PostureDamping = .18f;
    private const float MaximumAngularAcceleration = .0012f;
    private const float RecoveryAngularAcceleration = .00055f;
    private const float RecoveryMaximumAngularSpeed = .0145f;
    private const float RecoveryAngularDamping = .12f;
    private const int RecoveryRetractFrames = 26;
    private const int RecoveryBraceFrames = 28;
    private const int RecoveryBraceTimeoutFrames = 72;
    private const int RecoveryDeployFrames = 30;
    private const float RecoveryPushCycleFrames = 52f;

    private readonly MantleCrab crab;
    private readonly float[] supportQuality = new float[4];
    private Vector2 supportNormal = Vector2.up;
    private Vector2 walkAxis = Vector2.right;
    private bool recovering;
    private float recoveryDirection = 1f;
    private MantleCrabRecoveryPhase recoveryPhase;
    private int recoveryPhaseFrame;

    internal MantleCrabPostureController(MantleCrab crab)
    {
        this.crab = crab;
    }

    internal Vector2 SupportNormal => supportNormal;
    internal Vector2 WalkAxis => walkAxis;
    internal bool Recovering => recovering;
    internal bool SeverelyUnstable => recovering ||
                                      Mathf.Abs(ShellAngleRadians()) >= MovementStopDegrees * Mathf.Deg2Rad;
    internal MantleCrabRecoveryPhase RecoveryPhase => recoveryPhase;
    internal float RecoveryDirection => recoveryDirection;

    internal float RecoveryPhaseProgress
    {
        get
        {
            return recoveryPhase switch
            {
                MantleCrabRecoveryPhase.Retract => Mathf.Clamp01(recoveryPhaseFrame / (float)RecoveryRetractFrames),
                MantleCrabRecoveryPhase.Brace => Mathf.Clamp01(recoveryPhaseFrame / (float)RecoveryBraceFrames),
                MantleCrabRecoveryPhase.Deploy => Mathf.Clamp01(recoveryPhaseFrame / (float)RecoveryDeployFrames),
                MantleCrabRecoveryPhase.Roll => Mathf.Repeat(recoveryPhaseFrame / RecoveryPushCycleFrames, 1f),
                _ => 0f
            };
        }
    }

    internal float RecoveryPushAmount
    {
        get
        {
            if (recoveryPhase == MantleCrabRecoveryPhase.Brace)
            {
                float t = RecoveryPhaseProgress;
                return t * t * (3f - 2f * t);
            }

            if (recoveryPhase == MantleCrabRecoveryPhase.Roll)
            {
                float pulse = .5f - .5f * Mathf.Cos(RecoveryPhaseProgress * Mathf.PI * 2f);
                return .68f + pulse * .32f;
            }

            return 0f;
        }
    }

    internal void Reset()
    {
        supportNormal = Vector2.up;
        walkAxis = Vector2.right;
        recovering = false;
        recoveryDirection = 1f;
        recoveryPhase = MantleCrabRecoveryPhase.None;
        recoveryPhaseFrame = 0;
        for (int i = 0; i < supportQuality.Length; i++)
            supportQuality[i] = 0f;
    }

    internal void UpdateFrame()
    {
        float shellAngle = ShellAngleRadians();
        float absoluteShellAngle = Mathf.Abs(shellAngle) * Mathf.Rad2Deg;
        if (!recovering && absoluteShellAngle >= RecoveryEnterDegrees)
            BeginRecovery(shellAngle);

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

        // 倒地自救只有在甲壳真实接触地形时才能产生翻身力矩。
        // Self-righting torque is only generated against real shell-terrain contact.
        if (recovering && weight < .08f)
            ApplyShellContactRecovery(shellAngle);
    }

    /// <summary>
    /// 每个 Creature 帧只推进一次恢复动画状态机。UpdateFrame 会在支撑求解前后调用多次，
    /// 所以阶段计时不能放在 UpdateFrame 里，否则动画会一帧走两次。
    ///
    /// Advances the self-righting animation exactly once per creature frame.
    /// </summary>
    internal void AdvanceRecoveryAnimation()
    {
        if (!recovering)
            return;

        float absoluteAngle = Mathf.Abs(ShellAngleRadians()) * Mathf.Rad2Deg;
        bool shellContact = HasShellTerrainContact();

        switch (recoveryPhase)
        {
            case MantleCrabRecoveryPhase.Retract:
                recoveryPhaseFrame++;
                if (absoluteAngle <= RecoveryExitDegrees && recoveryPhaseFrame >= 8)
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Deploy);
                else if (recoveryPhaseFrame >= RecoveryRetractFrames)
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Brace);
                break;

            case MantleCrabRecoveryPhase.Brace:
                // 空中只保持收腿。落地以后才伸出翻身侧步足，并且优先等待真实撑地点建立。
                // Stay tucked in the air. Once grounded, wait for a real brace before committing to the roll.
                if (!shellContact)
                    return;
                recoveryPhaseFrame++;
                if (absoluteAngle <= RecoveryDeployDegrees)
                {
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Deploy);
                }
                else if ((recoveryPhaseFrame >= RecoveryBraceFrames && CountRecoveryBracedLegs() > 0) ||
                         recoveryPhaseFrame >= RecoveryBraceTimeoutFrames)
                {
                    // 超时仍然允许进入 Roll，但没有撑腿时物理权限会很低，只会缓慢摇壳寻找新的接地点。
                    // Timeout avoids a permanent deadlock, but an unbraced roll receives only weak rocking authority.
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Roll);
                }
                break;

            case MantleCrabRecoveryPhase.Roll:
                if (shellContact)
                    recoveryPhaseFrame++;
                if (absoluteAngle <= RecoveryDeployDegrees)
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Deploy);
                break;

            case MantleCrabRecoveryPhase.Deploy:
                recoveryPhaseFrame++;
                if (absoluteAngle > 76f)
                {
                    EnterRecoveryPhase(MantleCrabRecoveryPhase.Roll);
                }
                else if (absoluteAngle <= RecoveryExitDegrees && recoveryPhaseFrame >= RecoveryDeployFrames)
                {
                    recovering = false;
                    recoveryPhase = MantleCrabRecoveryPhase.None;
                    recoveryPhaseFrame = 0;
                }
                break;
        }
    }

    internal bool ShouldBraceLeg(MantleCrabLimb leg)
    {
        if (!recovering || leg == null ||
            (recoveryPhase != MantleCrabRecoveryPhase.Brace && recoveryPhase != MantleCrabRecoveryPhase.Roll))
            return false;

        Vector2 center = BodyCenter();
        float worldOffset = crab.Anchor(leg).x - center.x;
        if (Mathf.Abs(worldOffset) < 1f)
            return leg.Side == recoveryDirection;

        // 正角加速度需要重心右侧的支点，负角加速度需要左侧支点。
        // Positive angular acceleration is visually paired with a brace on the world-right side, and vice versa.
        return Mathf.Sign(worldOffset) == Mathf.Sign(recoveryDirection);
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
        float torqueAuthority = recovering ? .60f : straddlesCenter ? 1f : .52f;
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

    private void BeginRecovery(float shellAngle)
    {
        recovering = true;
        recoveryDirection = ChooseRecoveryDirection(shellAngle);
        EnterRecoveryPhase(MantleCrabRecoveryPhase.Retract);
    }

    private void EnterRecoveryPhase(MantleCrabRecoveryPhase phase)
    {
        recoveryPhase = phase;
        recoveryPhaseFrame = 0;
    }

    private void ApplyShellContactRecovery(float shellAngle)
    {
        if (!HasShellTerrainContact())
            return;

        int bracedLegs = CountRecoveryBracedLegs();
        float braceFactor = Mathf.Clamp01(bracedLegs * .5f);
        float phaseAuthority = recoveryPhase switch
        {
            MantleCrabRecoveryPhase.Retract => 0f,
            MantleCrabRecoveryPhase.Brace => bracedLegs > 0
                ? Mathf.Lerp(.08f, .20f, RecoveryPushAmount) * Mathf.Lerp(.82f, 1f, braceFactor)
                : .018f * RecoveryPushAmount,
            MantleCrabRecoveryPhase.Roll => bracedLegs > 0
                ? Mathf.Lerp(.28f, .58f, RecoveryPushAmount) * Mathf.Lerp(.82f, 1f, braceFactor)
                : Mathf.Lerp(.035f, .09f, RecoveryPushAmount),
            MantleCrabRecoveryPhase.Deploy => .08f,
            _ => 0f
        };
        if (phaseAuthority <= .0001f)
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
        float direction = recoveryPhase == MantleCrabRecoveryPhase.Deploy && absoluteAngle < RecoveryDeployDegrees
            ? Mathf.Sign(DeltaRadians(0f, shellAngle))
            : recoveryDirection;
        if (Mathf.Abs(direction) < .001f)
            direction = recoveryDirection;

        // 不是直接给一个固定翻转力矩，而是追一个很低的目标角速度。
        // 没有撑腿时只允许轻微摇壳；撑腿真正接地以后才允许逐渐建立翻身速度。
        // Self-righting tracks a deliberately low angular speed instead of injecting a fixed spin impulse.
        // Without a real brace the shell may only rock gently; confirmed bracing unlocks the main roll.
        float targetAngularSpeed = direction * Mathf.Lerp(.0035f, RecoveryMaximumAngularSpeed, phaseAuthority);
        float angularAcceleration = (targetAngularSpeed - angularVelocity) * .075f;
        float limit = RecoveryAngularAcceleration * Mathf.Max(.10f, phaseAuthority);
        angularAcceleration = Mathf.Clamp(angularAcceleration, -limit, limit);
        ApplyPureAngularAcceleration(center, angularAcceleration);
    }

    private int CountRecoveryBracedLegs()
    {
        int count = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
            if (crab.Legs[i].RecoveryBraced) count++;
        return count;
    }

    private bool HasShellTerrainContact()
    {
        if (crab.bodyChunks == null)
            return false;

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            IntVector2 contact = crab.bodyChunks[i].ContactPoint;
            if (contact.x != 0 || contact.y != 0)
                return true;
        }

        return false;
    }

    private float ChooseRecoveryDirection(float shellAngle)
    {
        float error = DeltaRadians(0f, shellAngle);
        if (Mathf.Abs(error) > 8f * Mathf.Deg2Rad && Mathf.Abs(Mathf.Abs(shellAngle) - Mathf.PI) > 10f * Mathf.Deg2Rad)
            return Mathf.Sign(error);

        int seed = crab.abstractCreature?.ID.RandomSeed ?? 0;
        return (seed & 1) == 0 ? 1f : -1f;
    }

    private Vector2 BodyCenter()
    {
        float mass = 0f;
        Vector2 center = Vector2.zero;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            mass += chunk.mass;
            center += chunk.pos * chunk.mass;
        }

        return mass > .0001f ? center / mass : crab.bodyChunks[2].pos;
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
