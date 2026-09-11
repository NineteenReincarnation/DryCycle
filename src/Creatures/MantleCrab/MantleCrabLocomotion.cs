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
    private const float StepUrgencyThreshold = .78f;
    private const float MinimumSupportMargin = 12f;
    private const float SecondStepProgress = .86f;
    private const float MaxGroundSpeed = 1.25f;
    private const float MaxGroundAcceleration = .045f;
    private const float IntentResponse = .025f;
    private const float DriveJerk = .0045f;

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

    // 这一轮先取消明显的正弦上下弹跳。大型身体的起伏应主要来自真实负载转移，
    // 后续再把“压腿/卸载”做成支撑驱动的细微动画，而不是用周期函数主动抬身体。
    // Remove the obvious sinusoidal bob for now. A large body should derive vertical motion from
    // real load transfer; later polish can reintroduce subtle compression from support state instead of a forced sine wave.
    internal float BodyHeightRhythm => 0f;

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

        // 大型生物的运动指令要有明显建立负载的过程，不能一帧进入完整巡航。
        // A large creature needs time to build and unload drive force instead of snapping into full travel.
        float targetMove = posture.SeverelyUnstable ? 0f : MoveIntent;
        smoothedMoveIntent = Mathf.MoveTowards(smoothedMoveIntent, targetMove, IntentResponse);
        motionAmount = Mathf.MoveTowards(
            motionAmount,
            Mathf.Abs(smoothedMoveIntent),
            posture.SeverelyUnstable ? .08f : .018f);

        Vector2 bodyVelocity = BodyVelocity();
        float groundSpeed = Mathf.Abs(Vector2.Dot(bodyVelocity, WalkAxis));
        if (motionAmount > .001f)
        {
            // 节律现在只是一条很慢的“机会窗口”，不应该把四足变成固定拍子。
            // Rhythm is deliberately slow and remains only an opportunity window; stability still owns the actual step choice.
            float phaseRate = (.018f + Mathf.Clamp(groundSpeed, 0f, MaxGroundSpeed) * .010f) * motionAmount;
            stridePhase = Mathf.Repeat(stridePhase + phaseRate, Mathf.PI * 2f);
        }

        traversal.Update(smoothedMoveIntent);

        for (int i = 0; i < stepCooldown.Length; i++)
            if (stepCooldown[i] > 0) stepCooldown[i]--;
        if (startCooldown > 0)
            startCooldown--;

        if (!crab.Consious || crab.room == null || posture.SeverelyUnstable)
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

            // stance 脚现在不允许滑，所以必须更早安排换步，不能等到接近极限长度才处理。
            // Stance contacts are now locked, so schedule the next step earlier instead of relying on late contact sliding.
            bool emergency = stretch > .85f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));
            float urgency = alongError / Mathf.Max(18f, 30f * crab.ShellScale);
            urgency += Mathf.InverseLerp(.70f, .86f, stretch) * 1.45f;
            urgency += (1f - leg.SupportQuality(crab)) * .30f;
            urgency *= traversal.UrgencyMultiplier(leg);

            // 对角腿只保留很弱的相位偏好。慢速大型生物允许地形和承重需要轻易打破节拍。
            // Diagonal timing is only a weak bias. Terrain and load requirements may freely break the rhythm.
            float phaseOffset = i == 0 || i == 3 ? 0f : Mathf.PI;
            float rhythm = .5f + .5f * Mathf.Cos(stridePhase - phaseOffset);
            urgency += (rhythm - .5f) * .10f * motionAmount;

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal)
                    urgency += .08f;
                else if (leg.Side == crab.Legs[lastStepIndex].Side)
                    urgency -= .06f;
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
        startCooldown = 7;
    }

    internal void ApplyGroundForces(float effectiveGravity)
    {
        // 姿态控制先负责站稳和负载转移；推进只负责沿地面切线产生速度。
        // Posture owns support and load transfer. Propulsion only owns grounded tangent speed.
        posture.ApplySupportAndPosture(effectiveGravity, TurnIntent);

        // 本轮先加一层保守的“大体型垂直稳定器”，专门压掉录像里的 pogo 起跳。
        // 它不负责把身体抬到目标高度，只限制已经由支撑系统产生的过大向上 COM 速度。
        // This conservative large-body vertical stabilizer suppresses the pogo launches seen in the recording.
        // It never lifts the shell by itself; it only limits excessive upward COM velocity already produced by support.
        StabilizeVerticalMotion();

        if (!crab.Consious || crab.room == null || crab.SupportingFeet < 2 || posture.SeverelyUnstable)
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

        // 支撑越差越应该慢，而不是让剩下的两条腿拖着整个甲壳维持巡航速度。
        // Weak support reduces speed instead of asking the remaining legs to drag the shell at full cruise.
        float targetSpeed = effectiveMove * MaxGroundSpeed * Mathf.Lerp(.42f, .92f, supportFactor);
        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .060f,
            -MaxGroundAcceleration,
            MaxGroundAcceleration);

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

    private void StabilizeVerticalMotion()
    {
        if (crab.bodyChunks == null || crab.SupportingFeet < 2 || posture.SeverelyUnstable)
            return;

        Vector2 velocity = BodyVelocity();
        if (velocity.y <= 0f)
            return;

        // 上台阶允许略多一点垂直速度，其余情况下大型甲壳不应该被腿连续弹离地面。
        // Step-up traversal gets a little more vertical freedom; otherwise the shell should not be launched by support legs.
        float maximumUpwardSpeed = traversal.Mode == MantleCrabTraversalMode.StepUp ? .78f : .42f;
        float targetUpwardSpeed = Mathf.Min(velocity.y * .82f, maximumUpwardSpeed);
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

        // 步幅跟随新的慢速巡航一起缩小。更短的步长能让脚有时间完成抬起和落地，而不是大跨度追赶身体。
        // Shorter stride projection matches the slower cruise and gives the limb time to visibly lift and settle.
        float inputLead = smoothedMoveIntent *
                          Mathf.Lerp(12f, 26f, Mathf.Abs(smoothedMoveIntent)) *
                          crab.ShellScale;
        float velocityLead = Mathf.Clamp(Vector2.Dot(bodyVelocity, axis) * 3.4f, -10f, 10f) * crab.ShellScale;
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
