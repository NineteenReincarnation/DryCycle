using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// MantleCrab 的低层步行驱动。高层只提供 move/turn 意图；这里负责换步、落脚预测、承重转移和地面推进。
/// 行走框架由 PostureController 根据世界重力和已接触地面的平均法线建立，绝不再使用甲壳当前旋转角当作移动方向。
///
/// Low-level walking motor. Higher layers only provide move/turn intent. This layer owns step choice,
/// foothold prediction, gradual load transfer and grounded propulsion.
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

    // 大型身体不能把一条腿从“满承重”一帧切成“完全离地”。
    // 卸载慢于紧急脱载，重新吃重则更慢，让落脚以后有一个明确的 settle 阶段。
    // A large body must not switch a limb from full load to airborne in one frame.
    private const float NormalUnloadRate = .050f;
    private const float EmergencyUnloadRate = .105f;
    private const float ReloadRate = .032f;
    private const float LostContactUnloadRate = .14f;
    private const float LiftLoadThreshold = .075f;
    private const float ReliableLoadThreshold = .12f;
    private const float TouchdownLoadGate = .36f;

    private readonly MantleCrab crab;
    private readonly int[] stepCooldown = new int[4];
    private readonly float[] legLoad = new float[4];
    private readonly float[] legStress = new float[4];
    private readonly MantleCrabTraversalPlanner traversal;
    private readonly MantleCrabPostureController posture;
    private int lastStepIndex = -1;
    private int pendingStepIndex = -1;
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

    // 暂时不使用正弦函数主动上下抬身体。甲壳的细微起伏现在应该来自真实承重转移。
    // Do not force a sinusoidal body bob. Subtle shell motion should emerge from real load transfer.
    internal float BodyHeightRhythm => 0f;

    internal MantleCrabTraversalPlanner Traversal => traversal;
    internal MantleCrabPostureController Posture => posture;
    internal Vector2 WalkAxis => posture.WalkAxis;
    internal Vector2 SupportNormal => posture.SupportNormal;
    internal int PendingStepIndex => pendingStepIndex;

    internal MantleCrabLocomotion(MantleCrab crab)
    {
        this.crab = crab;
        posture = new MantleCrabPostureController(crab);
        traversal = new MantleCrabTraversalPlanner(crab);
        for (int i = 0; i < legLoad.Length; i++)
            legLoad[i] = 1f;
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
        pendingStepIndex = -1;
        startCooldown = 0;
        posture.Reset();
        traversal.Reset();
        for (int i = 0; i < stepCooldown.Length; i++)
        {
            stepCooldown[i] = 0;
            legLoad[i] = 1f;
            legStress[i] = 0f;
        }
    }

    internal void SetIntent(float move, float turn)
    {
        MoveIntent = Mathf.Clamp(move, -1f, 1f);
        TurnIntent = Mathf.Clamp(turn, -1f, 1f);
    }

    internal float DesiredStandHeight(MantleCrabLimb leg) => traversal.DesiredStandHeight(leg);

    /// <summary>
    /// 返回一条腿当前允许承担多少比例的体重。0 表示已经卸载，1 表示完全承重。
    /// 这是步态和姿态控制之间的桥：腿虽然还 Plant 在地上，也可以先逐渐卸掉重量再抬起。
    ///
    /// Returns the current normalized load authority of one walking leg.
    /// </summary>
    internal float SupportLoad(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || leg.Index < 0 || leg.Index >= legLoad.Length)
            return 0f;
        return Mathf.Clamp01(legLoad[leg.Index]);
    }

    /// <summary>
    /// 返回当前这条腿“有多想换步”。它不是动画节拍，而是工作区、拖后、关节姿态和接触质量的综合压力。
    ///
    /// Returns the current discomfort/stress score used to decide which support should be released next.
    /// </summary>
    internal float StepStress(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || leg.Index < 0 || leg.Index >= legStress.Length)
            return 0f;
        return Mathf.Clamp01(legStress[leg.Index]);
    }

    /// <summary>
    /// 几何接触质量乘以当前承重比例。姿态、推进和稳定性判断统一使用这个值，
    /// 避免“视觉上正在卸载，但物理上仍然 100% 承重”。
    ///
    /// Combines contact quality with the current load-transfer state.
    /// </summary>
    internal float EffectiveSupportQuality(MantleCrabLimb leg)
    {
        if (leg == null || !leg.Planted)
            return 0f;
        return leg.SupportQuality(crab) * SupportLoad(leg);
    }

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
            // 节律只是一条很慢的机会窗口，稳定性和承重状态始终有更高优先级。
            // Rhythm is only a slow opportunity window; stability and load state always have priority.
            float phaseRate = (.018f + Mathf.Clamp(groundSpeed, 0f, MaxGroundSpeed) * .010f) * motionAmount;
            stridePhase = Mathf.Repeat(stridePhase + phaseRate, Mathf.PI * 2f);
        }

        traversal.Update(smoothedMoveIntent);

        for (int i = 0; i < stepCooldown.Length; i++)
            if (stepCooldown[i] > 0) stepCooldown[i]--;
        if (startCooldown > 0)
            startCooldown--;

        UpdateLegStress(bodyVelocity);
        UpdateLegLoads();

        if (!crab.Consious || crab.room == null || posture.SeverelyUnstable)
        {
            CancelPendingStep();
            return;
        }

        int swinging = 0;
        float furthestSwingProgress = 0f;
        bool anySettling = false;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Swinging) continue;
            swinging++;
            furthestSwingProgress = Mathf.Max(furthestSwingProgress, leg.SwingProgress);
            anySettling |= leg.SwingPhase == MantleCrabSwingPhase.Settle;
        }

        // 大型生物必须先把当前这一步真正踩实。Settle 期间不允许另一条腿开始预卸载，
        // 否则会出现“前脚刚碰地，后脚已经开始抬”的流水线机械感。
        // A large animal must finish planting the current step before another limb starts unloading.
        if (anySettling)
        {
            CancelPendingStep();
            return;
        }

        // Settle 结束后也给新落地腿一点真实接管重量的时间。达到约三分之一承重后，
        // 才允许下一次卸载进入队列；其余稳定性规则仍然继续生效。
        // After visual settle, let the touchdown leg accept a meaningful share of body load before the next release begins.
        if (lastStepIndex >= 0 && lastStepIndex < crab.Legs.Length)
        {
            MantleCrabLimb lastStep = crab.Legs[lastStepIndex];
            if (lastStep.Planted && !lastStep.Swinging && legLoad[lastStepIndex] < TouchdownLoadGate)
                return;
        }

        // 已经决定要迈哪条腿以后，不再每帧重新投票。先把重量真正转走，再进入 Swing。
        // Once a leg is committed, finish unloading it instead of re-electing a candidate every frame.
        if (pendingStepIndex >= 0)
        {
            AdvancePendingStep(bodyVelocity, swinging, furthestSwingProgress);
            return;
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
            float stress = legStress[i];

            // 高压力代表这条腿已经不适合继续承担当前 stance，允许绕过正常 cooldown。
            // High stress means the limb is mechanically uncomfortable enough to bypass normal cooldown.
            bool emergency = stress > .90f || stretch > .86f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));

            // 主要依据腿本身的机械压力选腿；预测落点误差只作为次要提前量。
            // Mechanical discomfort is now the primary release score. Prediction error is only a secondary lead cue.
            float urgency = stress * 1.35f;
            urgency += alongError / Mathf.Max(20f, 34f * crab.ShellScale) * .32f;
            urgency *= traversal.UrgencyMultiplier(leg);

            // 对角腿只保留很弱的相位偏好。慢速大型生物允许地形和承重需要轻易打破节拍。
            // Diagonal timing is only a weak bias. Terrain and load requirements may freely break the rhythm.
            float phaseOffset = i == 0 || i == 3 ? 0f : Mathf.PI;
            float rhythm = .5f + .5f * Mathf.Cos(stridePhase - phaseOffset);
            urgency += (rhythm - .5f) * .08f * motionAmount;

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal)
                    urgency += .06f;
                else if (leg.Side == crab.Legs[lastStepIndex].Side)
                    urgency -= .05f;
            }

            if (urgency > bestUrgency)
            {
                bestUrgency = urgency;
                candidate = i;
            }
        }

        if (candidate < 0)
            return;

        // 只进入“准备迈步”状态。真正抬脚要等该腿的承重权重逐渐降到接近零。
        // Enter pre-lift unloading only; the leg remains planted until its load authority is nearly zero.
        pendingStepIndex = candidate;
    }

    internal void ApplyGroundForces(float effectiveGravity)
    {
        // 姿态控制先负责站稳和负载转移；推进只负责沿地面切线产生速度。
        // Posture owns support and load transfer. Propulsion only owns grounded tangent speed.
        posture.ApplySupportAndPosture(effectiveGravity, TurnIntent);

        // 保守的大体型垂直稳定器只压掉异常的 pogo 起跳，不负责主动抬身体。
        // This conservative vertical stabilizer only suppresses abnormal pogo launches.
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
            qualitySum += EffectiveSupportQuality(crab.Legs[i]);
        if (qualitySum <= .05f)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        float supportFactor = Mathf.Clamp01(qualitySum / 3.2f);

        // 正在卸载/重新吃重时，可用推进能力也会自然降低。
        // Propulsion authority falls naturally while supports are unloading or settling after touchdown.
        float targetSpeed = effectiveMove * MaxGroundSpeed * Mathf.Lerp(.38f, .90f, supportFactor);
        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .060f,
            -MaxGroundAcceleration,
            MaxGroundAcceleration);

        driveAcceleration = Mathf.MoveTowards(driveAcceleration, requestedAcceleration, DriveJerk);

        float totalMass = crab.TotalMass;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = EffectiveSupportQuality(leg);
            if (quality <= .001f)
                continue;

            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            float share = quality / qualitySum;
            anchor.vel += axis * (driveAcceleration * totalMass * share / Mathf.Max(.01f, anchor.mass));
        }
    }

    /// <summary>
    /// 计算每条承重腿当前的机械压力。评分不决定动画，只回答“继续把这只脚留在这里有多不舒服”。
    ///
    /// Computes a mechanical discomfort score for every planted leg.
    /// </summary>
    private void UpdateLegStress(Vector2 bodyVelocity)
    {
        Vector2 axis = WalkAxis;
        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();

        float alongVelocity = Vector2.Dot(bodyVelocity, axis);
        float travelSign = 0f;
        if (Mathf.Abs(smoothedMoveIntent) > .08f)
            travelSign = Mathf.Sign(smoothedMoveIntent);
        else if (Mathf.Abs(alongVelocity) > .18f)
            travelSign = Mathf.Sign(alongVelocity);

        float scale = Mathf.Max(.35f, crab.ShellScale);

        for (int i = 0; i < crab.Legs.Length && i < legStress.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging || posture.Recovering)
            {
                legStress[i] = 0f;
                continue;
            }

            Vector2 anchor = crab.Anchor(leg);
            float stretch = Vector2.Distance(anchor, leg.Contact) / Mathf.Max(1f, leg.Reach);

            // 工作区压力同时关注过伸和过度压缩。过度压缩权重较低，因为大型长腿本来就应允许明显折叠。
            // Workspace stress watches both extension and compression; compression is deliberately softer.
            float extensionStress = Mathf.InverseLerp(.62f, .86f, stretch);
            float compressionStress = 1f - Mathf.InverseLerp(.34f, .50f, stretch);
            float workspaceStress = Mathf.Max(extensionStress, compressionStress * .52f);

            // 拖后压力：比较当前脚相对髋部的位置和这条腿自己的自然落点。
            // A stance foot that has been carried behind its own neutral point becomes progressively eager to step.
            float trailingStress = 0f;
            if (travelSign != 0f)
            {
                float currentAlong = Vector2.Dot(leg.Contact - anchor, axis);
                float restAlong = Vector2.Dot(TransformWalkingLocal(leg.RestTipOffset), axis);
                float trailingDistance = (restAlong - currentAlong) * travelSign;
                trailingStress = Mathf.InverseLerp(6f * scale, 42f * scale, trailingDistance);
            }

            // 关节姿态只占较小权重。它用于识别“长度还够，但整条腿已经拧得很别扭”的情况。
            // Joint-pose stress is intentionally secondary: it catches awkward geometry without fighting terrain adaptation.
            float poseStress = JointPoseStress(leg);

            // 接触开始恶化时提前准备换步，但不会因为坡面本身就立刻抬腿。
            // Poor support quality adds a small release bias without treating every slope as a failure.
            float contactStress = 1f - leg.SupportQuality(crab);

            float stress = workspaceStress * .46f +
                           trailingStress * .30f +
                           poseStress * .14f +
                           contactStress * .10f;

            // 真正接近过伸极限时，必须覆盖其它平滑项，避免锁足以后被身体硬拉。
            // Near extension limits, force the score high enough to escape before the stance becomes a taut strut.
            stress = Mathf.Max(stress, extensionStress * .92f);
            legStress[i] = Mathf.Clamp01(stress);
        }
    }

    private float JointPoseStress(MantleCrabLimb leg)
    {
        Vector2 previous = crab.Anchor(leg);
        float stress = 0f;
        float weight = 0f;

        // 最末端足节需要服从 GroundNormal，所以这里只评估前三段承重骨节。
        // The distal foot segment follows terrain normal, so only the three load-bearing proximal segments are scored.
        for (int i = 0; i < 3; i++)
        {
            Vector2 current = leg.Pos[i] - previous;
            Vector2 rest = TransformWalkingLocal(leg.Rest[i + 1] - leg.Rest[i]);
            previous = leg.Pos[i];

            if (current.sqrMagnitude <= .0001f || rest.sqrMagnitude <= .0001f)
                continue;

            current.Normalize();
            rest.Normalize();
            float alignment = Mathf.Clamp(Vector2.Dot(current, rest), -1f, 1f);
            float segmentStress = 1f - Mathf.InverseLerp(.40f, .94f, alignment);
            float segmentWeight = i == 0 ? 1f : .82f;
            stress += segmentStress * segmentWeight;
            weight += segmentWeight;
        }

        return weight > .001f ? Mathf.Clamp01(stress / weight) : 0f;
    }

    private void UpdateLegLoads()
    {
        for (int i = 0; i < crab.Legs.Length && i < legLoad.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float target;
            float rate;

            if (posture.Recovering || leg.Swinging || !leg.Planted)
            {
                target = 0f;
                rate = LostContactUnloadRate;
            }
            else if (i == pendingStepIndex)
            {
                target = 0f;
                rate = legStress[i] > .82f ? EmergencyUnloadRate : NormalUnloadRate;
            }
            else
            {
                target = 1f;
                rate = ReloadRate;
            }

            legLoad[i] = Mathf.MoveTowards(legLoad[i], target, rate);
        }
    }

    private void AdvancePendingStep(Vector2 bodyVelocity, int swinging, float furthestSwingProgress)
    {
        if (pendingStepIndex < 0 || pendingStepIndex >= crab.Legs.Length)
        {
            pendingStepIndex = -1;
            return;
        }

        MantleCrabLimb leg = crab.Legs[pendingStepIndex];
        if (!leg.Planted || leg.Swinging || !traversal.AllowLift(leg))
        {
            CancelPendingStep();
            return;
        }

        // 如果其它脚在卸载期间已经失去稳定支撑，立刻放弃这次迈步并把重量重新压回来。
        // Abort the pre-lift if the remaining support polygon stops being safe during unloading.
        if (!CanLift(pendingStepIndex))
        {
            CancelPendingStep();
            return;
        }

        if (swinging >= 2 || (swinging == 1 && furthestSwingProgress < SecondStepProgress))
            return;

        if (legLoad[pendingStepIndex] > LiftLoadThreshold)
            return;

        int candidate = pendingStepIndex;
        Vector2 axis = WalkAxis;
        Vector2 anchor = crab.Anchor(leg);
        Vector2 landing = DesiredLanding(leg, anchor, axis, bodyVelocity);
        if (!leg.TryBeginStep(crab, anchor, landing))
        {
            CancelPendingStep();
            return;
        }

        legLoad[candidate] = 0f;
        pendingStepIndex = -1;
        lastStepIndex = candidate;
        stepCooldown[candidate] = traversal.StepCooldown(leg);
        startCooldown = 7;
    }

    private void CancelPendingStep()
    {
        pendingStepIndex = -1;
    }

    private void StabilizeVerticalMotion()
    {
        if (crab.bodyChunks == null || crab.SupportingFeet < 2 || posture.SeverelyUnstable)
            return;

        Vector2 velocity = BodyVelocity();
        if (velocity.y <= 0f)
            return;

        // 上台阶允许略多一点垂直速度，其余情况下大型甲壳不应该被腿连续弹离地面。
        // Step-up traversal gets a little more vertical freedom; otherwise support legs should not launch the shell.
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

        // 步幅跟随慢速巡航缩小。更短的步长让腿有时间完成卸载、抬起、摆动和重新吃重。
        // Shorter stride projection gives the limb time to unload, lift, swing and settle visibly.
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
        float supportWeight = 0f;
        int supports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (i == candidate) continue;
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            float effective = EffectiveSupportQuality(leg);
            if (effective < ReliableLoadThreshold)
                continue;

            float coordinate = Vector2.Dot(leg.Contact - center, axis);
            min = Mathf.Min(min, coordinate);
            max = Mathf.Max(max, coordinate);
            supportWeight += effective;
            supports++;
        }

        if (supports < 2 || supportWeight < .48f)
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
