using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Low-level MantleCrab walking motor. AI and player-control layers only provide move/turn intent;
/// this class owns footstep selection, landing prediction and grounded shell propulsion.
/// </summary>
internal sealed class MantleCrabLocomotion
{
    private const float StepUrgencyThreshold = 1f;
    private const float MinimumSupportMargin = 12f;
    private const float SecondStepProgress = .72f;
    private const float MaxGroundSpeed = 2.8f;
    private const float MaxGroundAcceleration = .18f;
    private const float MaxTurnSpeed = .006f;
    private const float MaxTurnAcceleration = .00045f;

    private readonly MantleCrab crab;
    private readonly int[] stepCooldown = new int[4];
    private readonly MantleCrabTraversalPlanner traversal;
    private int lastStepIndex = -1;
    private int startCooldown;

    internal float MoveIntent { get; private set; }
    internal float TurnIntent { get; private set; }
    internal MantleCrabTraversalPlanner Traversal => traversal;

    internal MantleCrabLocomotion(MantleCrab crab)
    {
        this.crab = crab;
        traversal = new MantleCrabTraversalPlanner(crab);
    }

    internal void Reset()
    {
        MoveIntent = 0f;
        TurnIntent = 0f;
        lastStepIndex = -1;
        startCooldown = 0;
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
        traversal.Update(MoveIntent);

        for (int i = 0; i < stepCooldown.Length; i++)
            if (stepCooldown[i] > 0) stepCooldown[i]--;

        if (startCooldown > 0)
            startCooldown--;

        if (!crab.Consious || crab.room == null)
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

        // 第一只脚已经接近落地时才允许第二只脚开始摆动。
        // A second leg may only leave the ground while the first is already settling.
        if (swinging == 1 && furthestSwingProgress < SecondStepProgress)
            return;

        Vector2 axis = NormalizedAxis();
        Vector2 bodyVelocity = BodyVelocity();
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
            bool emergency = stretch > .955f;
            if (stepCooldown[i] > 0 && !emergency)
                continue;
            if (!CanLift(i))
                continue;

            Vector2 desired = DesiredLanding(leg, anchor, axis, bodyVelocity);
            float alongError = Mathf.Abs(Vector2.Dot(desired - leg.Contact, axis));
            float urgency = alongError / Mathf.Max(18f, 30f * crab.ShellScale);
            urgency += Mathf.InverseLerp(.78f, .96f, stretch) * 1.35f;
            urgency *= traversal.UrgencyMultiplier(leg);

            if (lastStepIndex >= 0)
            {
                int diagonal = 3 - lastStepIndex;
                if (i == diagonal)
                    urgency += .14f;
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
        startCooldown = 3;
    }

    internal void ApplyGroundForces()
    {
        if (!crab.Consious || crab.room == null || crab.SupportingFeet < 2)
            return;

        Vector2 axis = NormalizedAxis();
        Vector2 center = BodyCenter(out float totalMass);
        Vector2 velocity = BodyVelocity();
        float speed = Vector2.Dot(velocity, axis);
        float supportFactor = Mathf.InverseLerp(1f, 4f, crab.SupportingFeet);
        float effectiveMove = traversal.EffectiveMoveIntent(MoveIntent);

        // 粗糙地形先保证支撑，再追求速度；推进仍由已着地的腿施加到各自锚点。
        // Rough terrain prioritizes support over speed. Propulsion still enters the rigid shell
        // through the anchors of planted legs rather than by translating the shell directly.
        float targetSpeed = effectiveMove * MaxGroundSpeed * Mathf.Lerp(.72f, 1f, supportFactor);
        float acceleration = Mathf.Clamp((targetSpeed - speed) * .11f, -MaxGroundAcceleration, MaxGroundAcceleration);
        int planted = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
            if (crab.Legs[i].Planted) planted++;

        if (planted > 0)
        {
            for (int i = 0; i < crab.Legs.Length; i++)
            {
                MantleCrabLimb leg = crab.Legs[i];
                if (!leg.Planted) continue;
                BodyChunk anchor = crab.bodyChunks[leg.AnchorChunk];
                anchor.vel += axis * (acceleration * totalMass / (planted * anchor.mass));
            }
        }

        if (crab.SupportingFeet < 3)
            return;

        float inertia = 0f;
        float angularMomentum = 0f;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            Vector2 offset = chunk.pos - center;
            inertia += chunk.mass * offset.sqrMagnitude;
            angularMomentum += chunk.mass * Cross(offset, chunk.vel - velocity);
        }

        float angularVelocity = angularMomentum / Mathf.Max(1f, inertia);
        float targetAngularVelocity = TurnIntent * MaxTurnSpeed * Mathf.Lerp(1f, .55f, Mathf.Abs(effectiveMove));
        float angularAcceleration = Mathf.Clamp(
            targetAngularVelocity - angularVelocity,
            -MaxTurnAcceleration,
            MaxTurnAcceleration);

        if (Mathf.Abs(angularAcceleration) < .000001f)
            return;

        // 只施加转动冲量；后面的刚体投影负责保留这部分角动量。
        // Apply a pure rotational impulse. The rigid-shell projection that follows preserves it.
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            Vector2 offset = chunk.pos - center;
            chunk.vel += new Vector2(-offset.y, offset.x) * angularAcceleration;
        }
    }

    private Vector2 DesiredLanding(MantleCrabLimb leg, Vector2 anchor, Vector2 axis, Vector2 bodyVelocity)
    {
        Vector2 rest = TransformLocal(leg.RestTipOffset);
        float inputLead = MoveIntent * Mathf.Lerp(25f, 48f, Mathf.Abs(MoveIntent)) * crab.ShellScale;
        float velocityLead = Mathf.Clamp(Vector2.Dot(bodyVelocity, axis) * 6f, -24f, 24f) * crab.ShellScale;
        Vector2 nominal = anchor + rest + axis * (inputLead + velocityLead);
        return traversal.AdjustLanding(leg, nominal, axis);
    }

    private bool CanLift(int candidate)
    {
        Vector2 axis = NormalizedAxis();
        Vector2 center = BodyCenter(out _);
        float min = float.MaxValue;
        float max = float.MinValue;
        int supports = 0;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            if (i == candidate) continue;
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || leg.Swinging) continue;

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

    private Vector2 TransformLocal(Vector2 local)
    {
        Vector2 axis = NormalizedAxis();
        Vector2 up = new(-axis.y, axis.x);
        return axis * local.x + up * local.y;
    }

    private Vector2 NormalizedAxis()
    {
        Vector2 axis = crab.Axis;
        return axis.sqrMagnitude > .0001f ? axis.normalized : Vector2.right;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
