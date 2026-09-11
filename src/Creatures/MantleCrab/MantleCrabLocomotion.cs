using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// MantleCrab 的低层地面移动。
///
/// 这一版主动做减法：参考 Rain World 原版 Deer / MirosBird，步足负责提供“是否可靠支撑”的信息，
/// 身体的基础承重和地面推进都尽量作用在整个甲壳上，而不是从单个腿根向 BodyChunk 注入不对称力矩。
/// 正常步态暂时只允许一条腿处于 Swing，先保证站立和三足支撑稳定，再逐步恢复更复杂节律。
///
/// Simplified grounded locomotion. Feet describe support state; body-level support and propulsion move the shell
/// without injecting artificial torque through individual leg anchors. Only one walking leg may swing at a time.
/// </summary>
internal sealed class MantleCrabLocomotion
{
    private const float StepUrgencyThreshold = .78f;
    private const float MinimumSupportMargin = 12f;

    // 大型生物先慢下来。先把站立、落脚、三足支撑做稳定，再谈更快的节律。
    // Keep cruise deliberately slow until standing and three-leg support are proven stable in game.
    private const float MaxGroundSpeed = 1.10f;
    private const float MaxGroundAcceleration = .035f;
    private const float IntentResponse = .022f;
    private const float DriveJerk = .0035f;

    private const float ReliableSupportQuality = .18f;
    private const int MinimumReliableSupportsForDrive = 3;

    private readonly MantleCrab crab;
    private readonly int[] stepCooldown = new int[4];
    private readonly MantleCrabTraversalPlanner traversal;
    private readonly MantleCrabPostureController posture;

    private int lastStepIndex = -1;
    private int startCooldown;
    private float smoothedMoveIntent;
    private float driveAcceleration;
    private float stridePhase;
    private float motionAmount;

    internal float MoveIntent { get; private set; }
    internal float TurnIntent { get; private set; }
    internal float SmoothedMoveIntent => smoothedMoveIntent;
    internal float StridePhase => stridePhase;
    internal float MotionAmount => motionAmount;
    internal float BodyHeightRhythm => 0f;

    internal MantleCrabTraversalPlanner Traversal => traversal;
    internal MantleCrabPostureController Posture => posture;
    internal Vector2 WalkAxis => posture.WalkAxis;
    internal Vector2 SupportNormal => posture.SupportNormal;

    // 保留给现有代码/调试接口。V3 暂时取消预卸载状态机。
    // Compatibility surface: V3 intentionally has no pending unload state.
    internal int PendingStepIndex => -1;

    internal MantleCrabLocomotion(MantleCrab crab)
    {
        this.crab = crab;
        posture = new MantleCrabPostureController(crab);
        traversal = new MantleCrabTraversalPlanner(crab);
    }

    internal void Reset()
    {
        MoveIntent = 0f;
        TurnIntent = 0f;
        smoothedMoveIntent = 0f;
        driveAcceleration = 0f;
        stridePhase = 0f;
        motionAmount = 0f;
        lastStepIndex = -1;
        startCooldown = 0;

        posture.Reset();
        traversal.Reset();

        for (int i = 0; i < stepCooldown.Length; i++)
            stepCooldown[i] = 0;
    }

    internal void SetIntent(float move, float turn)
    {
        MoveIntent = Mathf.Clamp(move, -1f, 1f);
        TurnIntent = Mathf.Clamp(turn, -1f, 1f);
    }

    internal float DesiredStandHeight(MantleCrabLimb leg) => traversal.DesiredStandHeight(leg);

    /// <summary>
    /// V3 暂时取消预卸载/重新吃重控制器。Plant 的正常步足就是完整支撑，Swing 或失去接触就是 0。
    /// 后续只有在基础移动重新通过实机测试后，才考虑把渐进承重以更简单的方式加回来。
    /// </summary>
    internal float SupportLoad(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || !leg.Planted || leg.Swinging || posture.Recovering)
            return 0f;
        return 1f;
    }

    // 保留接口；复杂 Stress Manager 已从当前稳定性基线中移除。
    // Compatibility method; the layered stress manager is deliberately absent from this baseline.
    internal float StepStress(MantleCrabLimb leg) => 0f;

    internal float EffectiveSupportQuality(MantleCrabLimb leg)
    {
        if (leg == null || !leg.Planted || leg.Swinging)
            return 0f;
        return leg.SupportQuality(crab) * SupportLoad(leg);
    }

    internal void UpdateStepPlanning()
    {
        posture.UpdateFrame();
        posture.AdvanceRecoveryAnimation();

        float targetMove = posture.SeverelyUnstable ? 0f : MoveIntent;
        smoothedMoveIntent = Mathf.MoveTowards(smoothedMoveIntent, targetMove, IntentResponse);
        motionAmount = Mathf.MoveTowards(
            motionAmount,
            Mathf.Abs(smoothedMoveIntent),
            posture.SeverelyUnstable ? .08f : .014f);

        Vector2 bodyVelocity = BodyVelocity();
        float groundSpeed = Mathf.Abs(Vector2.Dot(bodyVelocity, WalkAxis));
        if (motionAmount > .001f)
        {
            float phaseRate = (.014f + Mathf.Clamp(groundSpeed, 0f, MaxGroundSpeed) * .008f) * motionAmount;
            stridePhase = Mathf.Repeat(stridePhase + phaseRate, Mathf.PI * 2f);
        }

        traversal.Update(smoothedMoveIntent);

        for (int i = 0; i < stepCooldown.Length; i++)
            if (stepCooldown[i] > 0) stepCooldown[i]--;
        if (startCooldown > 0)
            startCooldown--;

        if (!crab.Consious || crab.room == null || posture.SeverelyUnstable)
            return;

        // 先回到最保守、最容易验证的大型四足基线：任何时刻最多只有一条腿离地。
        // Return to the conservative vanilla-like baseline: at most one walking leg may be airborne.
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (crab.Legs[i].Swinging)
                return;
        }

        if (startCooldown > 0)
            return;

        // 四脚没有全部重新建立可靠接触时，不主动发起下一步。
        // Do not start another step until all four feet have recovered a reliable planted contact.
        int reliableSupports = CountReliableSupports();
        if (reliableSupports < crab.Legs.Length)
            return;

        Vector2 axis = WalkAxis;
        int candidate = -1;
        float bestUrgency = StepUrgencyThreshold;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;
            if (!traversal.AllowLift(leg))
                continue;

            Vector2 anchor = crab.Anchor(leg);
            float stretch = Vector2.Distance(anchor, leg.Contact) / Mathf.Max(1f, leg.Reach);
            bool emergency = stretch > .85f;

            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));

            float urgency = alongError / Mathf.Max(18f, 30f * crab.ShellScale);
            urgency += Mathf.InverseLerp(.70f, .86f, stretch) * 1.45f;
            urgency += (1f - leg.SupportQuality(crab)) * .25f;
            urgency *= traversal.UrgencyMultiplier(leg);

            // 只保留极弱节律偏好；稳定性和实际脚位拥有绝对优先级。
            // Rhythm is only a tiny tie-breaker. Stability and real foot geometry own the step decision.
            float phaseOffset = i == 0 || i == 3 ? 0f : Mathf.PI;
            float rhythm = .5f + .5f * Mathf.Cos(stridePhase - phaseOffset);
            urgency += (rhythm - .5f) * .05f * motionAmount;

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal)
                    urgency += .035f;
                else if (leg.Side == crab.Legs[lastStepIndex].Side)
                    urgency -= .025f;
            }

            if (urgency > bestUrgency)
            {
                bestUrgency = urgency;
                candidate = i;
            }
        }

        if (candidate < 0)
            return;

        MantleCrabLimb steppingLeg = crab.Legs[candidate];
        Vector2 steppingAnchor = crab.Anchor(steppingLeg);
        Vector2 landing = DesiredLanding(steppingLeg, steppingAnchor, axis, bodyVelocity);
        if (!steppingLeg.TryBeginStep(crab, steppingAnchor, landing))
            return;

        lastStepIndex = candidate;
        stepCooldown[candidate] = traversal.StepCooldown(steppingLeg);
        startCooldown = 10;
    }

    internal void ApplyGroundForces(float effectiveGravity)
    {
        // 先由 V3 PostureController 做身体级重力补偿和弱姿态阻尼。
        // V3 posture applies body-level gravity support and low-authority angle damping first.
        posture.ApplySupportAndPosture(effectiveGravity, TurnIntent);
        StabilizeVerticalMotion();

        if (!crab.Consious || crab.room == null || posture.SeverelyUnstable)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        int reliableSupports = CountReliableSupports();
        if (reliableSupports < 2)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        Vector2 axis = WalkAxis;
        Vector2 velocity = BodyVelocity();
        float speed = Vector2.Dot(velocity, axis);
        float effectiveMove = traversal.EffectiveMoveIntent(smoothedMoveIntent);

        float qualitySum = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
            qualitySum += EffectiveSupportQuality(crab.Legs[i]);

        if (qualitySum <= .05f)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        float supportFactor = Mathf.Clamp01(qualitySum / 3.2f);

        // 三条可靠支撑是正常移动最低条件。只剩两脚时不继续拖着壳走，只允许速度自然衰减。
        // Three reliable stance feet are the normal drive baseline; with only two, stop driving and settle.
        float driveAuthority = reliableSupports >= MinimumReliableSupportsForDrive ? 1f : 0f;
        float targetSpeed = effectiveMove *
                            MaxGroundSpeed *
                            Mathf.Lerp(.36f, .86f, supportFactor) *
                            driveAuthority;

        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .055f,
            -MaxGroundAcceleration,
            MaxGroundAcceleration);
        driveAcceleration = Mathf.MoveTowards(driveAcceleration, requestedAcceleration, DriveJerk);

        // 关键：推进作为整个刚性甲壳的 COM 加速度施加到全部 BodyChunk。
        // 不再根据哪条腿承重把水平冲量打进某个腿根，因此普通行走推进本身不会制造旋转。
        // Critical change: propulsion is a whole-shell COM acceleration, so walking drive itself injects no torque.
        Vector2 correction = axis * driveAcceleration;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
            crab.bodyChunks[i].vel += correction;
    }

    private int CountReliableSupports()
    {
        int count = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (leg.Planted && !leg.Swinging && EffectiveSupportQuality(leg) >= ReliableSupportQuality)
                count++;
        }
        return count;
    }

    private void StabilizeVerticalMotion()
    {
        if (crab.bodyChunks == null || CountReliableSupports() < 2 || posture.SeverelyUnstable)
            return;

        Vector2 velocity = BodyVelocity();
        if (velocity.y <= 0f)
            return;

        float maximumUpwardSpeed = traversal.Mode == MantleCrabTraversalMode.StepUp ? .66f : .30f;
        float targetUpwardSpeed = Mathf.Min(velocity.y * .78f, maximumUpwardSpeed);
        float remove = velocity.y - targetUpwardSpeed;
        if (remove <= .001f)
            return;

        for (int i = 0; i < crab.bodyChunks.Length; i++)
            crab.bodyChunks[i].vel.y -= remove;
    }

    private Vector2 DesiredLanding(
        MantleCrabLimb leg,
        Vector2 anchor,
        Vector2 axis,
        Vector2 bodyVelocity)
    {
        Vector2 rest = TransformWalkingLocal(leg.RestTipOffset);

        float inputLead = smoothedMoveIntent *
                          Mathf.Lerp(10f, 22f, Mathf.Abs(smoothedMoveIntent)) *
                          crab.ShellScale;
        float velocityLead = Mathf.Clamp(Vector2.Dot(bodyVelocity, axis) * 3.0f, -8f, 8f) * crab.ShellScale;
        Vector2 nominal = anchor + rest + axis * (inputLead + velocityLead);
        return traversal.AdjustLanding(leg, nominal, axis);
    }

    private bool CanLift(int candidate)
    {
        Vector2 axis = WalkAxis;
        Vector2 center = BodyCenter(out _);
        float min = float.MaxValue;
        float max = float.MinValue;
        int supports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (i == candidate)
                continue;

            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging || EffectiveSupportQuality(leg) < ReliableSupportQuality)
                continue;

            float coordinate = Vector2.Dot(leg.Contact - center, axis);
            min = Mathf.Min(min, coordinate);
            max = Mathf.Max(max, coordinate);
            supports++;
        }

        // 四足生物抬一条腿以后必须确实剩下三条可靠支撑。
        // A four-legged body may lift only when all three remaining contacts are reliable.
        if (supports < 3)
            return false;

        float margin = MinimumSupportMargin * crab.ShellScale;
        return min < -margin && max > margin;
    }

    private Vector2 BodyVelocity()
    {
        float totalMass = 0f;
        Vector2 velocity = Vector2.zero;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            totalMass += chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }
        return totalMass > .0001f ? velocity / totalMass : Vector2.zero;
    }

    private Vector2 BodyCenter(out float totalMass)
    {
        totalMass = 0f;
        Vector2 center = Vector2.zero;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            totalMass += chunk.mass;
            center += chunk.pos * chunk.mass;
        }
        return totalMass > .0001f ? center / totalMass : crab.bodyChunks[2].pos;
    }

    private Vector2 TransformWalkingLocal(Vector2 local)
    {
        Vector2 right = WalkAxis;
        Vector2 up = SupportNormal;
        if (right.sqrMagnitude <= .0001f)
            right = Vector2.right;
        else
            right.Normalize();
        if (up.sqrMagnitude <= .0001f)
            up = Vector2.up;
        else
            up.Normalize();
        return right * local.x + up * local.y;
    }
}
