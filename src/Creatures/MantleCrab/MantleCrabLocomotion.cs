using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// MantleCrab 的基础地面移动。
///
/// 当前版本故意保持简单：脚负责固定落点和迈步，身体负责站稳和缓慢移动。
/// 不再让腿部“质量分数”决定身体会不会塌，也不再把水平推进分配到不同腿根制造额外旋转。
///
/// Basic grounded locomotion. Feet own footholds and stepping; the shell owns stable standing and slow travel.
/// Minor limb pose errors never reduce gravity support, and ordinary drive is applied to the whole shell.
/// </summary>
internal sealed class MantleCrabLocomotion
{
    private const float StepUrgencyThreshold = .78f;
    private const float MinimumSupportMargin = 10f;

    private const float MaxGroundSpeed = 1.05f;
    private const float MaxGroundAcceleration = .032f;
    private const float IntentResponse = .020f;
    private const float DriveJerk = .0032f;

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
    /// 这里的“有支撑”只回答一个简单问题：脚是不是确实在地面上。
    /// Planted 是最可靠的情况；如果 IK 还差几个像素，但脚尖已经真实贴地，也先把它当作身体支撑。
    /// </summary>
    internal float SupportLoad(MantleCrabLimb leg) => IsFootGrounded(leg) ? 1f : 0f;

    internal float StepStress(MantleCrabLimb leg) => 0f;

    internal float EffectiveSupportQuality(MantleCrabLimb leg) => SupportLoad(leg);

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

        if (Mathf.Abs(smoothedMoveIntent) < .045f)
            return;

        for (int i = 0; i < crab.Legs.Length; i++)
            if (crab.Legs[i].Swinging)
                return;

        if (startCooldown > 0)
            return;

        // 真正迈下一步仍然要求四条腿都已经正式锁住。
        // “脚尖碰地”的宽容只用于让身体别塌，不拿来偷偷启动步态。
        for (int i = 0; i < crab.Legs.Length; i++)
            if (!crab.Legs[i].Planted)
                return;

        Vector2 axis = WalkAxis;
        float travelSign = Mathf.Sign(smoothedMoveIntent);
        int candidate = -1;
        float bestUrgency = StepUrgencyThreshold;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging || !traversal.AllowLift(leg))
                continue;
            if (!CanLift(i))
                continue;

            Vector2 anchor = crab.Anchor(leg);
            float stretch = Vector2.Distance(anchor, leg.Contact) / Mathf.Max(1f, leg.Reach);

            bool emergency = stretch > .875f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;

            Vector2 restOffset = TransformWalkingLocal(leg.RestTipOffset);
            float currentAlong = Vector2.Dot(leg.Contact - anchor, axis) * travelSign;
            float restAlong = Vector2.Dot(restOffset, axis) * travelSign;
            float trailing = restAlong - currentAlong;

            float urgency = Mathf.InverseLerp(4f * crab.ShellScale, 34f * crab.ShellScale, trailing) * 1.05f;
            urgency += Mathf.InverseLerp(.72f, .89f, stretch) * 1.30f;
            urgency *= traversal.UrgencyMultiplier(leg);

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal) urgency += .04f;
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

    internal void ApplyGroundForces(float ignoredGravity)
    {
        posture.ApplySupportAndPosture(crab.gravity, TurnIntent);
        StabilizeVerticalMotion();

        if (!crab.Consious || crab.room == null || posture.SeverelyUnstable)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        int groundedFeet = CountGroundedFeet();
        if (groundedFeet < 3)
        {
            driveAcceleration = Mathf.MoveTowards(driveAcceleration, 0f, DriveJerk * 1.5f);
            return;
        }

        Vector2 axis = WalkAxis;
        Vector2 velocity = BodyVelocity();
        float speed = Vector2.Dot(velocity, axis);
        float effectiveMove = traversal.EffectiveMoveIntent(smoothedMoveIntent);

        float footingScale = groundedFeet >= 4 ? 1f : .48f;
        float targetSpeed = effectiveMove * MaxGroundSpeed * footingScale;
        float requestedAcceleration = Mathf.Clamp(
            (targetSpeed - speed) * .055f,
            -MaxGroundAcceleration,
            MaxGroundAcceleration);
        driveAcceleration = Mathf.MoveTowards(driveAcceleration, requestedAcceleration, DriveJerk);

        Vector2 correction = axis * driveAcceleration;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
            crab.bodyChunks[i].vel += correction;
    }

    private bool IsFootGrounded(MantleCrabLimb leg)
    {
        if (leg == null || leg.IsPincer || leg.Swinging || posture.Recovering || crab.room == null)
            return false;

        if (leg.Planted)
            return true;

        // 原版 MirosBird 的 groundContact 也是“已经碰到地面”就成立，
        // 不要求脚的内部状态先达到一个几乎零误差的锁点。
        return MantleCrabTerrainProbe.StillSupported(crab.room, leg.Tip);
    }

    private int CountGroundedFeet()
    {
        int count = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
            if (IsFootGrounded(crab.Legs[i])) count++;
        return count;
    }

    private void StabilizeVerticalMotion()
    {
        if (crab.bodyChunks == null || CountGroundedFeet() < 2 || posture.SeverelyUnstable)
            return;

        Vector2 velocity = BodyVelocity();
        if (velocity.y <= 0f)
            return;

        float maximumUpwardSpeed = traversal.Mode == MantleCrabTraversalMode.StepUp ? .62f : .28f;
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
        float velocityLead = Mathf.Clamp(Vector2.Dot(bodyVelocity, axis) * 3f, -8f, 8f) * crab.ShellScale;
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
            if (!leg.Planted || leg.Swinging)
                continue;

            float coordinate = Vector2.Dot(leg.Contact - center, axis);
            min = Mathf.Min(min, coordinate);
            max = Mathf.Max(max, coordinate);
            supports++;
        }

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
