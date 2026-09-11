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
/// MantleCrab 的地面姿态与承重控制。
///
/// V3 的核心原则来自 Rain World 原版 Deer / MirosBird：步足决定“身体现在有多少可靠支撑”，
/// 但维持体重的基础支撑作用在整个身体上，而不是分别从左右腿根向几个 BodyChunk 注入力。
/// 这样正常站立不会因为左右脚质量的一点差异就自己制造旋转；甲壳角度只由单独、很弱的姿态控制处理。
///
/// Ground posture and support controller. Following the vanilla large-creature pattern, limbs determine
/// support authority while gravity compensation is applied to the whole body. Standing support therefore
/// does not create torque by itself; shell orientation is handled separately by a deliberately soft controller.
/// </summary>
internal sealed class MantleCrabPostureController
{
    private const float MaximumTerrainFollowDegrees = 9f;
    private const float MaximumManualLeanDegrees = 4f;
    private const float MovementStopDegrees = 34f;
    private const float RecoveryEnterDegrees = 60f;
    private const float RecoveryExitDegrees = 12f;
    private const float RecoveryDeployDegrees = 46f;

    // 站立支撑故意比较“软”。生成时已经处在接近正确高度，所以这里主要负责抵消重力、
    // 吸收小误差，而不是像千斤顶一样把身体迅速顶到目标高度。
    // Standing suspension is intentionally soft: it mostly cancels gravity and removes small height error.
    private const float SupportHeightGain = .0034f;
    private const float SupportVelocityDamping = .20f;
    private const float FullSupportQuality = 1.55f;
    private const float OneSidedSupportAuthority = .44f;
    private const float MaximumSupportFactor = 1.10f;

    // 甲壳姿态控制只修正缓慢漂移，不承担“站立”本身。
    // Shell attitude correction is separate from support and intentionally low-authority.
    private const float PostureGain = .00082f;
    private const float PostureDamping = .15f;
    private const float MaximumAngularAcceleration = .00072f;

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

        Vector2 summedNormal = Vector2.zero;
        float summedQuality = 0f;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.GroundNormal.y <= .15f)
                continue;

            float quality = crab.Locomotion.EffectiveSupportQuality(leg);
            if (quality <= .001f)
                continue;

            Vector2 normal = leg.GroundNormal;
            if (normal.sqrMagnitude <= .0001f)
                normal = Vector2.up;
            else
                normal.Normalize();
            if (normal.y < 0f)
                normal = -normal;

            summedNormal += normal * quality;
            summedQuality += quality;
        }

        Vector2 targetNormal = summedQuality > .001f ? summedNormal / summedQuality : Vector2.up;
        if (targetNormal.sqrMagnitude <= .0001f)
            targetNormal = Vector2.up;
        else
            targetNormal.Normalize();

        // 长腿吸收大部分碎石和小坡度，甲壳只缓慢跟随一小部分地形角度。
        // Long legs absorb local terrain; the shell follows only a limited averaged slope.
        float terrainAngle = Mathf.Clamp(
            Mathf.Atan2(-targetNormal.x, targetNormal.y),
            -MaximumTerrainFollowDegrees * Mathf.Deg2Rad,
            MaximumTerrainFollowDegrees * Mathf.Deg2Rad);
        Vector2 limitedNormal = new(-Mathf.Sin(terrainAngle), Mathf.Cos(terrainAngle));

        float frameBlend = recovering ? .28f : .12f;
        supportNormal = Vector2.Lerp(supportNormal, limitedNormal, frameBlend);
        if (supportNormal.sqrMagnitude <= .0001f)
            supportNormal = Vector2.up;
        else
            supportNormal.Normalize();

        walkAxis = new Vector2(supportNormal.y, -supportNormal.x);
        if (walkAxis.sqrMagnitude <= .0001f)
            walkAxis = Vector2.right;
        else
            walkAxis.Normalize();
        if (walkAxis.x < 0f)
            walkAxis = -walkAxis;

        // 翻身阶段只有真实接触地形时才允许壳体产生恢复力矩。
        // Recovery torque exists only against real terrain contact.
        if (recovering && summedQuality < .08f)
            ApplyShellContactRecovery(shellAngle);
    }

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
                // 空中保持收腿。只有壳体落地以后才伸撑腿找真实支点。
                // Stay tucked in the air; bracing starts only after shell-terrain contact.
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

        return Mathf.Sign(worldOffset) == Mathf.Sign(recoveryDirection);
    }

    /// <summary>
    /// 站立 V3：步足只决定支撑权限，维持体重的加速度统一施加到整个甲壳。
    /// 这是本轮最关键的改动——正常站立本身不再产生任何人为角动量。
    ///
    /// Standing V3: feet determine support authority, while gravity compensation acts on the whole shell.
    /// Basic standing therefore injects no artificial angular momentum.
    /// </summary>
    internal void ApplySupportAndPosture(float effectiveGravity, float turnIntent)
    {
        UpdateFrame();
        BodyState(
            out Vector2 center,
            out Vector2 velocity,
            out _,
            out float inertia,
            out float angularVelocity);

        float qualitySum = 0f;
        float weightedHeightError = 0f;
        float leftQuality = 0f;
        float rightQuality = 0f;
        int validSupports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = crab.Locomotion.EffectiveSupportQuality(leg);
            supportQuality[i] = quality;
            if (quality <= .001f)
                continue;

            validSupports++;
            qualitySum += quality;

            // 用真实脚点而不是腿根判断支撑位于重心哪一侧。
            // Support side is defined by the real foot contact, not the shell anchor.
            float offset = Vector2.Dot(leg.Contact - center, walkAxis);
            if (offset < -2f)
                leftQuality += quality;
            else if (offset > 2f)
                rightQuality += quality;

            // 站高误差沿世界竖直计算，与 Rain World 原版重力补偿保持一致。
            // Height correction is world-vertical, matching vanilla gravity compensation.
            float actualHeight = crab.Anchor(leg).y - leg.Contact.y;
            float heightError = leg.StandHeight - actualHeight;
            weightedHeightError += heightError * quality;
        }

        bool straddlesCenter = leftQuality > .08f && rightQuality > .08f;
        float supportCoverage = Mathf.Clamp01(qualitySum / FullSupportQuality);
        float sideAuthority = straddlesCenter
            ? 1f
            : validSupports >= 2 ? OneSidedSupportAuthority : OneSidedSupportAuthority * .65f;
        float supportAuthority = supportCoverage * sideAuthority;

        if (qualitySum > .001f && supportAuthority > .001f)
        {
            float meanHeightError = weightedHeightError / qualitySum;
            float heightCorrection = meanHeightError * SupportHeightGain -
                                     velocity.y * SupportVelocityDamping;

            // 支撑首先抵消重力，站高误差只做小修正。最大权限略高于 1g，足够慢慢站回正确高度，
            // 但不会再出现把整个大型甲壳弹飞的千斤顶效果。
            // Support first cancels gravity; height error adds only a small correction.
            float supportAcceleration = effectiveGravity * supportAuthority + heightCorrection * supportAuthority;
            float maximumSupport = Mathf.Max(0f, effectiveGravity) * MaximumSupportFactor;
            supportAcceleration = Mathf.Clamp(supportAcceleration, 0f, maximumSupport);

            // 原版 Deer / MirosBird 的关键思路：身体级支撑。
            // 每个 BodyChunk 得到同样的竖直加速度，因此这里不会制造旋转。
            // Vanilla-style body-level support: equal acceleration on all shell chunks, hence no support torque.
            for (int i = 0; i < crab.bodyChunks.Length; i++)
                crab.bodyChunks[i].vel.y += supportAcceleration;
        }

        // 翻身由专门的恢复流程负责；普通站姿只做很弱的角度阻尼。
        // Recovery owns large rotations. Ordinary stance only damps small shell-angle drift.
        float terrainLean = Mathf.Atan2(walkAxis.y, walkAxis.x) * .25f;
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
        float postureAuthority;
        if (recovering)
            postureAuthority = .55f;
        else if (straddlesCenter)
            postureAuthority = Mathf.Clamp01(qualitySum / 1.35f);
        else
            postureAuthority = .12f * supportCoverage;

        float maxAngularAcceleration = recovering ? RecoveryAngularAcceleration : MaximumAngularAcceleration;
        float damping = recovering ? RecoveryAngularDamping : PostureDamping;
        float angularAcceleration = Mathf.Clamp(
            angleError * PostureGain - angularVelocity * damping,
            -maxAngularAcceleration,
            maxAngularAcceleration) * postureAuthority;

        if (Mathf.Abs(angularAcceleration) > .000001f && inertia > .001f)
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
        if (Mathf.Abs(error) > 8f * Mathf.Deg2Rad &&
            Mathf.Abs(Mathf.Abs(shellAngle) - Mathf.PI) > 10f * Mathf.Deg2Rad)
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
