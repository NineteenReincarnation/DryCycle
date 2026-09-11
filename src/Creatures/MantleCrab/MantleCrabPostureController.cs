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
/// MantleCrab 的地面站立与翻身控制。
///
/// 普通站立刻意采用 Rain World 原版大型生物的宽容思路：
/// 脚已经踩住地面，就把它当成有效支点；身体整体负责抵消重力和维持站高。
/// 不再因为某条腿伸展比例、IK 小误差或接触质量的轻微波动而减少基础承重。
///
/// Ground stance and self-righting controller. Normal stance deliberately follows the forgiving
/// vanilla large-creature pattern: grounded feet establish support, while the whole body receives
/// gravity compensation. Minor IK or pose errors are handled by stepping, not by collapsing the body.
/// </summary>
internal sealed class MantleCrabPostureController
{
    private const float MaximumTerrainFollowDegrees = 8f;
    private const float MaximumManualLeanDegrees = 4f;
    private const float MovementStopDegrees = 34f;
    private const float RecoveryEnterDegrees = 60f;
    private const float RecoveryExitDegrees = 12f;
    private const float RecoveryDeployDegrees = 46f;

    // 站高只做小修正。基础承重首先完整抵消 Rain World 已经施加的重力。
    // Height control is a small correction on top of full gravity compensation.
    private const float SupportHeightGain = .0022f;
    private const float SupportVelocityDamping = .11f;
    private const float MaximumSupportFactor = 1.14f;
    private const float SingleFootSupportFactor = .62f;

    // 普通姿态回正必须很弱；它只防止甲壳慢慢漂歪，不负责“托住身体”。
    // Ordinary attitude correction is deliberately weak and separate from standing support.
    private const float PostureGain = .00072f;
    private const float PostureDamping = .14f;
    private const float MaximumAngularAcceleration = .00062f;

    private const float RecoveryAngularAcceleration = .00055f;
    private const float RecoveryMaximumAngularSpeed = .0145f;
    private const float RecoveryAngularDamping = .12f;
    private const int RecoveryRetractFrames = 26;
    private const int RecoveryBraceFrames = 28;
    private const int RecoveryBraceTimeoutFrames = 72;
    private const int RecoveryDeployFrames = 30;
    private const float RecoveryPushCycleFrames = 52f;

    private readonly MantleCrab crab;
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
    }

    internal void UpdateFrame()
    {
        float shellAngle = ShellAngleRadians();
        float absoluteShellAngle = Mathf.Abs(shellAngle) * Mathf.Rad2Deg;
        if (!recovering && absoluteShellAngle >= RecoveryEnterDegrees)
            BeginRecovery(shellAngle);

        Vector2 summedNormal = Vector2.zero;
        int groundedFeet = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging || leg.GroundNormal.y <= .15f)
                continue;

            Vector2 normal = leg.GroundNormal;
            if (normal.sqrMagnitude <= .0001f)
                normal = Vector2.up;
            else
                normal.Normalize();
            if (normal.y < 0f)
                normal = -normal;

            summedNormal += normal;
            groundedFeet++;
        }

        Vector2 targetNormal = groundedFeet > 0 ? summedNormal / groundedFeet : Vector2.up;
        if (targetNormal.sqrMagnitude <= .0001f)
            targetNormal = Vector2.up;
        else
            targetNormal.Normalize();

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

        if (recovering && groundedFeet == 0)
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
    /// 普通站立只看“脚有没有真正踩住”。两条及以上脚着地时，完整抵消身体重力；
    /// 腿姿势不舒服由换步逻辑处理，不能通过减少重力补偿把整只生物搞塌。
    /// </summary>
    internal void ApplySupportAndPosture(float ignoredGravity, float turnIntent)
    {
        UpdateFrame();
        BodyState(
            out Vector2 center,
            out Vector2 velocity,
            out _,
            out float inertia,
            out float angularVelocity);

        int groundedFeet = 0;
        float heightErrorSum = 0f;
        bool leftSupport = false;
        bool rightSupport = false;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            groundedFeet++;
            float actualHeight = crab.Anchor(leg).y - leg.Contact.y;
            heightErrorSum += leg.StandHeight - actualHeight;

            float offset = Vector2.Dot(leg.Contact - center, walkAxis);
            if (offset < -2f) leftSupport = true;
            if (offset > 2f) rightSupport = true;
        }

        // PhysicalObject.gravity 已经包含 room.gravity。不要再乘第二次房间重力。
        // PhysicalObject.gravity already includes room.gravity; use it directly just like vanilla creatures do.
        float effectiveGravity = Mathf.Max(0f, crab.gravity);

        if (groundedFeet > 0 && effectiveGravity > 0f)
        {
            float meanHeightError = heightErrorSum / groundedFeet;
            float supportFactor = groundedFeet >= 2 ? 1f : SingleFootSupportFactor;

            float supportAcceleration = effectiveGravity * supportFactor;
            supportAcceleration += meanHeightError * SupportHeightGain;
            supportAcceleration -= velocity.y * SupportVelocityDamping;

            float maxSupport = effectiveGravity * (groundedFeet >= 2
                ? MaximumSupportFactor
                : SingleFootSupportFactor * 1.05f);
            supportAcceleration = Mathf.Clamp(supportAcceleration, 0f, maxSupport);

            // 和 MirosBird 一样：支撑作用到整个身体，而不是某一条腿根。
            // Equal acceleration on every shell chunk cannot create artificial standing torque.
            for (int i = 0; i < crab.bodyChunks.Length; i++)
                crab.bodyChunks[i].vel.y += supportAcceleration;
        }

        // 普通站姿只做很弱的回正。只要有两条脚着地，就不要因为脚点细微差异自己摔倒。
        float terrainLean = Mathf.Atan2(walkAxis.y, walkAxis.x) * .22f;
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
        float postureAuthority = recovering
            ? .55f
            : groundedFeet >= 2 ? 1f : groundedFeet == 1 ? .28f : 0f;

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
