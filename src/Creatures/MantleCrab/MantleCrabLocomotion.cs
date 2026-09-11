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

    private const float NormalUnloadRate = .050f;
    private const float EmergencyUnloadRate = .105f;
    private const float ReloadRate = .032f;
    private const float LostContactUnloadRate = .14f;
    private const float LiftLoadThreshold = .075f;
    private const float ReliableLoadThreshold = .12f;
    private const float TouchdownLoadGate = .36f;
    private const float TouchdownReloadFloor = .42f;
    private const float MaximumTouchdownVelocityRemoval = .18f;

    private const float ForwardSupportMinimumAuthority = .24f;
    private const float ForwardSupportFullQuality = 1.35f;
    private const float ForwardSupportNearDistance = 6f;
    private const float ForwardSupportFullDistance = 46f;

    // 重心越靠近当前支撑区边缘，身体推进权限越低。大型身体必须等脚把支撑区重新扩开以后再跟进。
    // Drive fades continuously near the support-span boundary instead of waiting for a binary CanLift failure.
    private const float SupportMarginMinimumAuthority = .28f;
    private const float SupportMarginFrontNear = 4f;
    private const float SupportMarginFrontFull = 24f;
    private const float SupportMarginRearNear = 3f;
    private const float SupportMarginRearFull = 14f;

    private const float PreloadMaximumShift = 7f;
    private const float PreloadPositionGain = .0018f;
    private const float PreloadVelocityDamping = .022f;
    private const float PreloadMaximumAcceleration = .020f;
    private const float PreloadMinimumSupportQuality = .48f;

    private const float StanceDriveMinimumWeight = .24f;
    private const float StanceDriveLeadTolerance = 10f;
    private const float StanceDriveFullTrail = 34f;
    private const float StanceDriveStressFloor = .58f;

    // 正常 stance 仍然锁死 Contact；抓地不足只限制水平传力并提高换步压力，不允许脚点偷偷滑动。
    // Traction failure limits horizontal force and raises release pressure without translating the locked foot contact.
    private const float TractionMinimumAuthority = .30f;
    private const float TractionFullQuality = 1.55f;
    private const float TractionStrainRise = .065f;
    private const float TractionStrainFall = .028f;

    private readonly MantleCrab crab;
    private readonly int[] stepCooldown = new int[4];
    private readonly float[] legLoad = new float[4];
    private readonly float[] legStress = new float[4];
    private readonly float[] legTractionStrain = new float[4];
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
            legTractionStrain[i] = 0f;
        }
    }

    internal void SetIntent(float move, float turn)
    {
        MoveIntent = Mathf.Clamp(move, -1f, 1f);
        TurnIntent = Mathf.Clamp(turn, -1f, 1f);
    }

    internal float DesiredStandHeight(MantleCrabLimb leg) => traversal.DesiredStandHeight(leg);

    internal float SupportLoad(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || leg.Index < 0 || leg.Index >= legLoad.Length)
            return 0f;
        return Mathf.Clamp01(legLoad[leg.Index]);
    }

    internal float StepStress(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || leg.Index < 0 || leg.Index >= legStress.Length)
            return 0f;
        return Mathf.Clamp01(legStress[leg.Index]);
    }

    internal float EffectiveSupportQuality(MantleCrabLimb leg)
    {
        if (leg == null || !leg.Planted)
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
            posture.SeverelyUnstable ? .08f : .018f);

        Vector2 bodyVelocity = BodyVelocity();
        float groundSpeed = Mathf.Abs(Vector2.Dot(bodyVelocity, WalkAxis));
        if (motionAmount > .001f)
        {
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

        if (anySettling)
        {
            CancelPendingStep();
            return;
        }

        if (lastStepIndex >= 0 && lastStepIndex < crab.Legs.Length)
        {
            MantleCrabLimb lastStep = crab.Legs[lastStepIndex];
            if (lastStep.Planted && !lastStep.Swinging && legLoad[lastStepIndex] < TouchdownLoadGate)
                return;
        }

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

            bool emergency = stress > .90f || stretch > .86f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));
            float urgency = stress * 1.35f;
            urgency += alongError / Mathf.Max(20f, 34f * crab.ShellScale) * .32f;
            urgency *= traversal.UrgencyMultiplier(leg);

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

        if (candidate >= 0)
            pendingStepIndex = candidate;
    }

    internal void ApplyGroundForces(float effectiveGravity)
    {
        posture.ApplySupportAndPosture(effectiveGravity, TurnIntent);
        AbsorbTouchdownRebound();
        StabilizeVerticalMotion();
        ApplyPreloadShift();

        if (!crab.Consious || crab.room == null || crab.SupportingFeet < 2 || posture.SeverelyUnstable)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            DecayTractionStrain();
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
            DecayTractionStrain();
            return;
        }

        float supportFactor = Mathf.Clamp01(qualitySum / 3.2f);
        float forwardAuthority = ForwardSupportAuthority(effectiveMove, axis);
        float marginAuthority = SupportMarginAuthority(effectiveMove, axis);
        float groundedAuthority = forwardAuthority * Mathf.Lerp(.70f, 1f, marginAuthority);

        float targetSpeed = effectiveMove *
                            MaxGroundSpeed *
                            Mathf.Lerp(.38f, .90f, supportFactor) *
                            groundedAuthority;
        float accelerationLimit = MaxGroundAcceleration * Mathf.Lerp(.50f, 1f, groundedAuthority);
        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .060f,
            -accelerationLimit,
            accelerationLimit);

        driveAcceleration = Mathf.MoveTowards(driveAcceleration, requestedAcceleration, DriveJerk);

        bool propelling = Mathf.Abs(effectiveMove) > .05f &&
                          Mathf.Abs(driveAcceleration) > .0001f &&
                          Mathf.Sign(driveAcceleration) == Mathf.Sign(effectiveMove);
        float driveDirection = propelling ? Mathf.Sign(effectiveMove) : 0f;
        float driveWeightSum = 0f;
        float tractionQualitySum = 0f;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = EffectiveSupportQuality(leg);
            if (quality <= .001f)
                continue;

            float stanceWeight = propelling ? StanceDriveWeight(leg, axis, driveDirection) : 1f;
            float traction = TractionMultiplier(leg, axis);
            driveWeightSum += quality * stanceWeight * traction;
            tractionQualitySum += quality * traction;
        }

        if (driveWeightSum <= .001f)
            driveWeightSum = qualitySum;

        float tractionAuthority = Mathf.Lerp(
            TractionMinimumAuthority,
            1f,
            Mathf.Clamp01(tractionQualitySum / TractionFullQuality));
        if (!propelling)
            tractionAuthority = Mathf.Lerp(.78f, 1f, tractionAuthority);

        float appliedAcceleration = driveAcceleration * tractionAuthority;
        float driveDemand = Mathf.Clamp01(Mathf.Abs(driveAcceleration) / Mathf.Max(.0001f, MaxGroundAcceleration));
        float globalShortfall = Mathf.Clamp01(driveDemand - tractionAuthority);
        float totalMass = crab.TotalMass;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            float quality = EffectiveSupportQuality(leg);
            if (quality <= .001f)
            {
                legTractionStrain[i] = Mathf.MoveTowards(legTractionStrain[i], 0f, TractionStrainFall);
                continue;
            }

            float stanceWeight = propelling ? StanceDriveWeight(leg, axis, driveDirection) : 1f;
            float traction = TractionMultiplier(leg, axis);
            float weighted = quality * stanceWeight * traction;
            float share = driveWeightSum > .001f ? weighted / driveWeightSum : quality / qualitySum;

            float strainTarget = driveDemand * (1f - traction) * (propelling ? .92f : .48f);
            strainTarget += globalShortfall * .70f;
            strainTarget *= Mathf.Lerp(.65f, 1f, stanceWeight);
            strainTarget = Mathf.Clamp01(strainTarget);
            float strainRate = strainTarget > legTractionStrain[i] ? TractionStrainRise : TractionStrainFall;
            legTractionStrain[i] = Mathf.MoveTowards(legTractionStrain[i], strainTarget, strainRate);

            BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
            anchor.vel += axis * (appliedAcceleration * totalMass * share / Mathf.Max(.01f, anchor.mass));
        }
    }

    private float StanceDriveWeight(MantleCrabLimb leg, Vector2 axis, float driveDirection)
    {
        if (leg == null || driveDirection == 0f)
            return 1f;

        Vector2 anchor = crab.Anchor(leg);
        float currentAlong = Vector2.Dot(leg.Contact - anchor, axis) * driveDirection;
        float restAlong = Vector2.Dot(TransformWalkingLocal(leg.RestTipOffset), axis) * driveDirection;
        float trailingDistance = restAlong - currentAlong;
        float scale = Mathf.Max(.65f, crab.ShellScale);

        float stance = Mathf.InverseLerp(
            -StanceDriveLeadTolerance * scale,
            StanceDriveFullTrail * scale,
            trailingDistance);
        stance = stance * stance * (3f - 2f * stance);

        float weight = Mathf.Lerp(StanceDriveMinimumWeight, 1f, stance);
        weight *= Mathf.Lerp(1f, StanceDriveStressFloor, StepStress(leg));
        weight *= Mathf.Lerp(1f, .68f, Mathf.Clamp01(leg.TouchdownAbsorption));
        return Mathf.Max(.08f, weight);
    }

    private float TractionMultiplier(MantleCrabLimb leg, Vector2 axis)
    {
        if (leg == null || !leg.Planted)
            return 0f;

        Vector2 normal = leg.GroundNormal;
        if (normal.sqrMagnitude <= .0001f)
            normal = Vector2.up;
        else
            normal.Normalize();

        Vector2 supportUp = SupportNormal;
        if (supportUp.sqrMagnitude <= .0001f)
            supportUp = Vector2.up;
        else
            supportUp.Normalize();
        if (Vector2.Dot(normal, supportUp) < 0f)
            normal = -normal;

        float normalAlignment = Mathf.Clamp01(Vector2.Dot(normal, supportUp));
        float surfaceGrip = Mathf.Lerp(.58f, 1f, Mathf.InverseLerp(.42f, .96f, normalAlignment));

        Vector2 anchor = crab.Anchor(leg);
        Vector2 legVector = anchor - leg.Contact;
        float stretch = legVector.magnitude / Mathf.Max(1f, leg.Reach);
        float extensionGrip = Mathf.Lerp(1f, .48f, Mathf.InverseLerp(.70f, .90f, stretch));
        float compressionGrip = Mathf.Lerp(.62f, 1f, Mathf.InverseLerp(.34f, .52f, stretch));

        if (legVector.sqrMagnitude > .0001f)
            legVector.Normalize();
        else
            legVector = supportUp;
        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();
        float horizontalLeverage = Mathf.Abs(Vector2.Dot(legVector, axis));
        float leverageGrip = Mathf.Lerp(.64f, 1f, Mathf.InverseLerp(.10f, .62f, horizontalLeverage));

        float touchdownGrip = Mathf.Lerp(1f, .72f, Mathf.Clamp01(leg.TouchdownAbsorption));
        return Mathf.Clamp01(surfaceGrip * extensionGrip * compressionGrip * leverageGrip * touchdownGrip);
    }

    private void DecayTractionStrain()
    {
        for (int i = 0; i < legTractionStrain.Length; i++)
            legTractionStrain[i] = Mathf.MoveTowards(legTractionStrain[i], 0f, TractionStrainFall);
    }

    private float ForwardSupportAuthority(float effectiveMove, Vector2 axis)
    {
        if (Mathf.Abs(effectiveMove) < .05f || crab.bodyChunks == null || crab.bodyChunks.Length == 0)
            return 1f;

        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();

        float direction = Mathf.Sign(effectiveMove);
        Vector2 center = BodyCenter(out _);
        float scale = Mathf.Max(.65f, crab.ShellScale);
        float nearDistance = ForwardSupportNearDistance * scale;
        float fullDistance = ForwardSupportFullDistance * scale;
        float forwardQuality = 0f;
        int reliableForwardSupports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            float quality = EffectiveSupportQuality(leg);
            if (quality <= .02f)
                continue;

            float ahead = Vector2.Dot(leg.Contact - center, axis) * direction;
            float position = Mathf.InverseLerp(-nearDistance, fullDistance, ahead);
            position = position * position * (3f - 2f * position);
            forwardQuality += quality * position;

            if (ahead > nearDistance * .65f && quality >= .34f)
                reliableForwardSupports++;
        }

        float qualityAuthority = Mathf.Clamp01(forwardQuality / ForwardSupportFullQuality);
        float countAuthority = reliableForwardSupports switch
        {
            >= 2 => 1f,
            1 => .72f,
            _ => .28f
        };
        float established = Mathf.Clamp01(qualityAuthority * .74f + countAuthority * .26f);
        established = established * established * (3f - 2f * established);

        float minimumAuthority = traversal.Mode == MantleCrabTraversalMode.StepUp
            ? Mathf.Max(ForwardSupportMinimumAuthority, .34f)
            : ForwardSupportMinimumAuthority;
        return Mathf.Lerp(minimumAuthority, 1f, established);
    }

    /// <summary>
    /// 根据当前可靠脚点在 WalkAxis 上包住重心的余量，连续限制身体推进。
    /// 这不是新的步态节拍，只是防止甲壳在下一只脚落稳之前把重心推到支撑区边缘之外。
    /// </summary>
    private float SupportMarginAuthority(float effectiveMove, Vector2 axis)
    {
        if (Mathf.Abs(effectiveMove) < .05f)
            return 1f;

        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();

        Vector2 center = BodyCenter(out _);
        float min = float.MaxValue;
        float max = float.MinValue;
        float supportQuality = 0f;
        int supports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            float quality = EffectiveSupportQuality(leg);
            if (quality < ReliableLoadThreshold)
                continue;

            float coordinate = Vector2.Dot(leg.Contact - center, axis);
            min = Mathf.Min(min, coordinate);
            max = Mathf.Max(max, coordinate);
            supportQuality += quality;
            supports++;
        }

        if (supports < 2 || supportQuality < .48f || min >= 0f || max <= 0f)
            return SupportMarginMinimumAuthority;

        float direction = Mathf.Sign(effectiveMove);
        float frontMargin = direction > 0f ? max : -min;
        float rearMargin = direction > 0f ? -min : max;
        float scale = Mathf.Max(.65f, crab.ShellScale);

        float front = Mathf.InverseLerp(
            SupportMarginFrontNear * scale,
            SupportMarginFrontFull * scale,
            frontMargin);
        float rear = Mathf.InverseLerp(
            SupportMarginRearNear * scale,
            SupportMarginRearFull * scale,
            rearMargin);
        float stable = Mathf.Clamp01(Mathf.Min(front, rear));
        stable = stable * stable * (3f - 2f * stable);

        float floor = traversal.Mode == MantleCrabTraversalMode.StepUp
            ? Mathf.Max(SupportMarginMinimumAuthority, .44f)
            : SupportMarginMinimumAuthority;
        return Mathf.Lerp(floor, 1f, stable);
    }

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
                legTractionStrain[i] = Mathf.MoveTowards(legTractionStrain[i], 0f, TractionStrainFall);
                continue;
            }

            Vector2 anchor = crab.Anchor(leg);
            float stretch = Vector2.Distance(anchor, leg.Contact) / Mathf.Max(1f, leg.Reach);
            float extensionStress = Mathf.InverseLerp(.62f, .86f, stretch);
            float compressionStress = 1f - Mathf.InverseLerp(.34f, .50f, stretch);
            float workspaceStress = Mathf.Max(extensionStress, compressionStress * .52f);

            float trailingStress = 0f;
            if (travelSign != 0f)
            {
                float currentAlong = Vector2.Dot(leg.Contact - anchor, axis);
                float restAlong = Vector2.Dot(TransformWalkingLocal(leg.RestTipOffset), axis);
                float trailingDistance = (restAlong - currentAlong) * travelSign;
                trailingStress = Mathf.InverseLerp(6f * scale, 42f * scale, trailingDistance);
            }

            float poseStress = JointPoseStress(leg);
            float contactStress = 1f - leg.SupportQuality(crab);
            float tractionStress = Mathf.Clamp01(legTractionStrain[i]);

            float stress = workspaceStress * .43f +
                           trailingStress * .28f +
                           poseStress * .13f +
                           contactStress * .08f +
                           tractionStress * .16f;

            stress = Mathf.Max(stress, extensionStress * .92f);
            stress = Mathf.Max(stress, tractionStress * .78f);
            legStress[i] = Mathf.Clamp01(stress);
        }
    }

    private float JointPoseStress(MantleCrabLimb leg)
    {
        Vector2 previous = crab.Anchor(leg);
        float stress = 0f;
        float weight = 0f;

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
                float absorption = Mathf.Clamp01(leg.TouchdownAbsorption);
                rate = ReloadRate * Mathf.Lerp(1f, TouchdownReloadFloor, absorption);
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

    private void ApplyPreloadShift()
    {
        if (!crab.Consious || crab.bodyChunks == null || crab.bodyChunks.Length == 0 ||
            crab.SupportingFeet < 2 || posture.SeverelyUnstable || posture.Recovering)
            return;

        int activeIndex = -1;
        float amount = 0f;

        if (pendingStepIndex >= 0 && pendingStepIndex < crab.Legs.Length)
        {
            activeIndex = pendingStepIndex;
            float unload = Mathf.Clamp01(1f - legLoad[activeIndex]);
            float t = Mathf.InverseLerp(.05f, .88f, unload);
            amount = t * t * (3f - 2f * t);
        }
        else if (lastStepIndex >= 0 && lastStepIndex < crab.Legs.Length)
        {
            MantleCrabLimb activeLeg = crab.Legs[lastStepIndex];
            if (activeLeg.Swinging)
            {
                activeIndex = lastStepIndex;
                float t = Mathf.Clamp01(activeLeg.SwingPhaseProgress);
                t = t * t * (3f - 2f * t);
                amount = activeLeg.SwingPhase switch
                {
                    MantleCrabSwingPhase.Lift => 1f,
                    MantleCrabSwingPhase.Transfer => Mathf.Lerp(1f, .30f, t),
                    MantleCrabSwingPhase.Lower => Mathf.Lerp(.30f, 0f, t),
                    _ => 0f
                };
            }
        }

        if (activeIndex < 0 || amount <= .001f)
            return;

        if (traversal.Mode == MantleCrabTraversalMode.StepUp)
            amount *= .72f;

        Vector2 axis = WalkAxis;
        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();

        float supportCoordinate = 0f;
        float supportQuality = 0f;
        int supportCount = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (i == activeIndex)
                continue;

            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            float quality = EffectiveSupportQuality(leg);
            if (quality < ReliableLoadThreshold)
                continue;

            supportCoordinate += Vector2.Dot(leg.Contact, axis) * quality;
            supportQuality += quality;
            supportCount++;
        }

        if (supportCount < 2 || supportQuality < PreloadMinimumSupportQuality)
            return;

        supportCoordinate /= supportQuality;
        Vector2 center = BodyCenter(out _);
        float bodyCoordinate = Vector2.Dot(center, axis);
        float maximumShift = PreloadMaximumShift * Mathf.Max(.65f, crab.ShellScale);
        float error = Mathf.Clamp(supportCoordinate - bodyCoordinate, -maximumShift, maximumShift);
        float alongVelocity = Vector2.Dot(BodyVelocity(), axis);
        float acceleration = Mathf.Clamp(
            error * PreloadPositionGain - alongVelocity * PreloadVelocityDamping,
            -PreloadMaximumAcceleration,
            PreloadMaximumAcceleration) * amount;

        if (Mathf.Abs(acceleration) <= .0001f)
            return;

        Vector2 correction = axis * acceleration;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
            crab.bodyChunks[i].vel += correction;
    }

    private void AbsorbTouchdownRebound()
    {
        if (crab.bodyChunks == null || crab.bodyChunks.Length == 0 || posture.SeverelyUnstable)
            return;

        Vector2 normal = SupportNormal;
        if (normal.sqrMagnitude <= .0001f)
            normal = Vector2.up;
        else
            normal.Normalize();
        if (normal.y < 0f)
            normal = -normal;

        float weightedAbsorption = 0f;
        float weightSum = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging)
                continue;

            float absorption = Mathf.Clamp01(leg.TouchdownAbsorption);
            if (absorption <= .001f)
                continue;

            float load = SupportLoad(leg);
            if (load <= .001f)
                continue;

            float contactQuality = leg.SupportQuality(crab);
            if (contactQuality <= .001f)
                continue;

            float weight = contactQuality * Mathf.Lerp(.18f, 1f, load);
            weightedAbsorption += absorption * weight;
            weightSum += weight;
        }

        if (weightSum <= .001f)
            return;

        float authority = Mathf.Clamp01(weightedAbsorption / weightSum);
        if (traversal.Mode == MantleCrabTraversalMode.StepUp)
            authority *= .55f;

        Vector2 bodyVelocity = BodyVelocity();
        float reboundSpeed = Vector2.Dot(bodyVelocity, normal);
        if (reboundSpeed <= .01f)
            return;

        float fraction = Mathf.Lerp(.08f, .42f, authority);
        float remove = Mathf.Min(
            reboundSpeed * fraction,
            MaximumTouchdownVelocityRemoval * Mathf.Lerp(.60f, 1f, authority));
        if (remove <= .001f)
            return;

        Vector2 correction = normal * remove;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
            crab.bodyChunks[i].vel -= correction;
    }

    private void StabilizeVerticalMotion()
    {
        if (crab.bodyChunks == null || crab.SupportingFeet < 2 || posture.SeverelyUnstable)
            return;

        Vector2 velocity = BodyVelocity();
        if (velocity.y <= 0f)
            return;

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
