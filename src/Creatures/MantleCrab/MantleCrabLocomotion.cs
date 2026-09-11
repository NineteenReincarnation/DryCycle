using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// MantleCrab 的低层步行驱动。高层只提供 move/turn 意图；这里负责换步、落脚预测和地面推进。
/// 行走框架由 PostureController 根据世界重力和已接触地面的平均法线建立，绝不再使用甲壳当前旋转角当作移动方向。
///
/// Low-level walking motor. Higher layers only provide move/turn intent. The grounded frame is owned
/// by PostureController and is never derived from the shell's current physical rotation.
/// </summary>
internal sealed class MantleCrabLocomotion
{
    private const float StepUrgencyThreshold = .92f;
    private const float MinimumSupportMargin = 12f;
    private const float SecondStepProgress = .78f;
    private const float MaxGroundSpeed = 2.2f;
    private const float MaxGroundAcceleration = .10f;
    private const float IntentResponse = .045f;
    private const float DriveJerk = .0105f;

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
    internal float BodyHeightRhythm => Mathf.Sin(stridePhase * 2f) * 1.15f * motionAmount * crab.ShellScale;
    internal MantleCrabTraversalPlanner Traversal => traversal;
    internal MantleCrabPostureController Posture => posture;
    internal Vector2 WalkAxis => posture.WalkAxis;
    internal Vector2 SupportNormal => posture.SupportNormal;

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

    internal void UpdateStepPlanning()
    {
        // 先建立独立于甲壳旋转的地面坐标系，再让 Traversal 和步态使用同一套方向。
        // Build the gravity/terrain frame first so traversal and gait consume one stable direction basis.
        posture.UpdateFrame();
        posture.AdvanceRecoveryAnimation();

        // 高层输入不直接变成腿的瞬时动作。大型身体需要先建立/卸掉推进负载。
        // High-level intent is filtered before it reaches gait planning so a large body never snaps between full directions.
        float targetMove = posture.Recovering ? 0f : MoveIntent;
        smoothedMoveIntent = Mathf.MoveTowards(smoothedMoveIntent, targetMove, IntentResponse);
        motionAmount = Mathf.MoveTowards(
            motionAmount,
            Mathf.Abs(smoothedMoveIntent),
            posture.Recovering ? .10f : .032f);

        Vector2 bodyVelocity = BodyVelocity();
        float groundSpeed = Mathf.Abs(Vector2.Dot(bodyVelocity, WalkAxis));
        if (motionAmount > .001f)
        {
            // 节律只做很弱的换步偏好，稳定性判断永远拥有更高优先级。
            // Rhythm provides only a weak preferred timing window; stability always has authority to override it.
            float phaseRate = (.034f + Mathf.Clamp(groundSpeed, 0f, MaxGroundSpeed) * .018f) * motionAmount;
            stridePhase = Mathf.Repeat(stridePhase + phaseRate, Mathf.PI * 2f);
        }

        traversal.Update(smoothedMoveIntent);

        for (int i = 0; i < stepCooldown.Length; i++)
            if (stepCooldown[i] > 0) stepCooldown[i]--;
        if (startCooldown > 0)
            startCooldown--;

        if (!crab.Consious || crab.room == null || posture.Recovering)
            return;

        int swinging = 0;
        float furthestSwingProgress = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (!crab.Legs[i].Swinging) continue;
            swinging++;
            furthestSwingProgress = Mathf.Max(furthestSwingProgress, crab.Legs[i].SwingProgress);
        }

        if (swinging >= 2 || startCooldown > 0)
            return;

        if (swinging == 1 && furthestSwingProgress < SecondStepProgress)
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
            bool emergency = stretch > .89f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));
            float urgency = alongError / Mathf.Max(18f, 28f * crab.ShellScale);
            urgency += Mathf.InverseLerp(.78f, .90f, stretch) * 1.25f;
            urgency += (1f - leg.SupportQuality(crab)) * .35f;
            urgency *= traversal.UrgencyMultiplier(leg);

            // 对角腿有相反的自然节律窗口，但只给很小的权重；粗糙地形仍可打破这个节奏。
            // Diagonal pairs have opposite preferred windows, but the bias is intentionally small and terrain may break it.
            float phaseOffset = i == 0 || i == 3 ? 0f : Mathf.PI;
            float rhythm = .5f + .5f * Mathf.Cos(stridePhase - phaseOffset);
            urgency += (rhythm - .5f) * .16f * motionAmount;

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal)
                    urgency += .12f;
                else if (leg.Side == crab.Legs[lastStepIndex].Side)
                    urgency -= .08f;
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
        startCooldown = 4;
    }

    internal void ApplyGroundForces(float effectiveGravity)
    {
        // 姿态控制先负责站稳、负载转移和翻倒恢复；推进只负责沿地面切线产生速度。
        // Posture owns support, load transfer and self-righting. Propulsion only owns grounded tangent speed.
        posture.ApplySupportAndPosture(effectiveGravity, TurnIntent);

        if (!crab.Consious || crab.room == null || crab.SupportingFeet < 2 || posture.Recovering)
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
            qualitySum += crab.Legs[i].SupportQuality(crab);
        if (qualitySum <= .05f)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        float supportFactor = Mathf.Clamp01(qualitySum / 3.2f);
        float targetSpeed = effectiveMove * MaxGroundSpeed * Mathf.Lerp(.62f, 1f, supportFactor);
        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .095f,
            -MaxGroundAcceleration,
            MaxGroundAcceleration);

        // 推进力增加加加速度限制，换向时不会一帧从满制动跳到满加速。
        // Propulsion is jerk-limited so reversals cannot jump from full braking to full drive in one frame.
        driveAcceleration = Mathf.MoveTowards(driveAcceleration, requestedAcceleration, DriveJerk);

        float totalMass = crab.TotalMass;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = leg.SupportQuality(crab);
            if (quality <= .001f)
                continue;

            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float share = quality / qualitySum;
            anchor.vel += axis * (driveAcceleration * totalMass * share / Mathf.Max(.01f, anchor.mass));
        }
    }

    private Vector2 DesiredLanding(
        MantleCrabLimb leg,
        Vector2 anchor,
        Vector2 axis,
        Vector2 bodyVelocity)
    {
        Vector2 rest = TransformWalkingLocal(leg.RestTipOffset);
        float inputLead = smoothedMoveIntent *
                          Mathf.Lerp(18f, 36f, Mathf.Abs(smoothedMoveIntent)) *
                          crab.ShellScale;
        float velocityLead = Mathf.Clamp(Vector2.Dot(bodyVelocity, axis) * 4.6f, -15f, 15f) * crab.ShellScale;
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
            if (i == candidate) continue;
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging || leg.SupportQuality(crab) < .18f)
                continue;

            float coordinate = Vector2.Dot(leg.Contact - center, axis);
            min = Mathf.Min(min, coordinate);
            max = Mathf.Max(max, coordinate);
            supports++;
        }

        if (supports < 2)
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
        if (right.sqrMagnitude <= .0001f) right = Vector2.right;
        else right.Normalize();
        if (up.sqrMagnitude <= .0001f) up = Vector2.up;
        else up.Normalize();
        return right * local.x + up * local.y;
    }
}
