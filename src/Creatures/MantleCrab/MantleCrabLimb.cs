using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal sealed class MantleCrabLimb
{
    private const float PassiveAcquireReach = .90f;
    private const float StepAcquireReach = .92f;
    private const float SlipStartReach = .84f;
    private const float ReleaseReach = .92f;
    private const float PlantTolerance = 1.5f;

    internal readonly int Index, AnchorChunk;
    internal readonly bool IsPincer;
    internal readonly float Side, Reach, LocalDepth;
    internal readonly float[] Lengths;
    private readonly float[] upperLengths;
    private readonly Vector2[] preferredDirections = new Vector2[4];
    private readonly Vector2[] upperPreferredDirections = new Vector2[3];
    private readonly MantleCrabPincerRig pincerRig;
    internal readonly Vector2[] Rest;
    internal Vector2 RestTipOffset => Rest[4] - Rest[0];
    internal readonly Vector2[] Pos, LastPos, Velocity;
    internal Vector2 Tip => Pos[3];
    internal Vector2 TipDirection => (Pos[3] - Pos[2]).normalized;
    internal Vector2 Anchor, LastAnchor, GroundNormal = Vector2.up;
    internal bool Planted { get; private set; }
    internal bool Swinging { get; private set; }
    internal float SwingProgress { get; private set; }
    internal Vector2 Contact => contact;
    internal MantleCrabPincerRig PincerRig => pincerRig;
    internal float NominalStandHeight => nominalStandHeight;
    internal float NominalBodyClearance => -Rest[4].y;
    internal float StandHeight => nominalStandHeight * stanceHeightScale;

    private readonly float nominalStandHeight;
    private Vector2 contact;
    private Vector2 swingStart;
    private bool hasTarget;
    private int searchTick;
    private float swingDuration;
    private float stanceHeightScale = 1f;

    internal MantleCrabLimb(int index, bool pincer)
    {
        Index = index;
        IsPincer = pincer;
        Side = index % 2 == 0 ? -1f : 1f;
        AnchorChunk = pincer ? 2 : Side < 0 ? 1 : 3;
        LocalDepth = !pincer && index < 2 ? .7f : 0f;

        if (pincer)
        {
            // Keep MantleCrabLimb as a compatibility shell for callers while delegating the
            // capture appendage to its own anatomy/rig. Walking and capture limbs therefore no
            // longer share pose logic even though existing creature code can keep one array API.
            pincerRig = new MantleCrabPincerRig(index);
            Rest = pincerRig.Rest;
            Lengths = pincerRig.Lengths;
            Pos = pincerRig.Pos;
            LastPos = pincerRig.LastPos;
            Velocity = pincerRig.Velocity;
        }
        else
        {
            Rest = MantleCrabAnatomy.Landmarks(index, false);
            Lengths = new float[4];
            for (int i = 0; i < 4; i++)
                Lengths[i] = Vector2.Distance(Rest[i], Rest[i + 1]);
            Pos = new Vector2[4];
            LastPos = new Vector2[4];
            Velocity = new Vector2[4];
        }

        foreach (float length in Lengths)
            Reach += length;
        upperLengths = [Lengths[0], Lengths[1], Lengths[2]];
        nominalStandHeight = -RestTipOffset.y;
    }

    internal void Reset(Vector2 anchor)
    {
        stanceHeightScale = 1f;
        if (IsPincer)
        {
            pincerRig.Reset(null, anchor);
            Anchor = LastAnchor = anchor;
            Planted = hasTarget = Swinging = false;
            SwingProgress = 0f;
            GroundNormal = Vector2.up;
            searchTick = Index;
            return;
        }

        Anchor = LastAnchor = anchor;
        for (int i = 0; i < 4; i++)
        {
            Pos[i] = LastPos[i] = anchor + Rest[i + 1] - Rest[0];
            Velocity[i] = Vector2.zero;
        }
        Planted = hasTarget = Swinging = false;
        SwingProgress = 0f;
        GroundNormal = Vector2.up;
        searchTick = Index;
    }

    internal bool TrySnapToSupport(MantleCrab crab, Vector2 anchor)
    {
        if (IsPincer || crab?.room == null)
            return false;

        Anchor = LastAnchor = anchor;
        Vector2 desired = anchor + TransformWalkingLocal(crab, RestTipOffset);
        hasTarget = MantleCrabTerrainProbe.Find(
            crab.room,
            anchor,
            desired,
            Reach * PassiveAcquireReach,
            out contact,
            out GroundNormal);

        if (!hasTarget)
        {
            Planted = false;
            return false;
        }

        SolveWalkingPose(crab, anchor, contact, true);
        for (int i = 0; i < 4; i++)
        {
            LastPos[i] = Pos[i];
            Velocity[i] = Vector2.zero;
        }

        Planted = Vector2.Distance(Tip, contact) < PlantTolerance;
        if (!Planted)
            hasTarget = false;
        Swinging = false;
        SwingProgress = 0f;
        searchTick = 8 + Index;
        return Planted;
    }

    internal bool TryBeginStep(MantleCrab crab, Vector2 anchor, Vector2 desired)
    {
        if (IsPincer || Swinging || crab?.room == null)
            return false;

        if (!MantleCrabTerrainProbe.Find(
                crab.room,
                anchor,
                desired,
                Reach * StepAcquireReach,
                out Vector2 landing,
                out Vector2 landingNormal))
            return false;

        if (hasTarget && Vector2.Distance(landing, contact) < 6f)
            return false;

        swingStart = Tip;
        contact = landing;
        GroundNormal = landingNormal;
        hasTarget = true;
        Planted = false;
        Swinging = true;
        SwingProgress = 0f;
        swingDuration = Mathf.Lerp(
            12f,
            20f,
            Mathf.InverseLerp(18f, 100f, Vector2.Distance(swingStart, contact)));
        return true;
    }

    internal void Update(MantleCrab crab, Vector2 anchor)
    {
        if (IsPincer)
        {
            pincerRig.Update(crab, anchor);
            LastAnchor = pincerRig.LastAnchor;
            Anchor = pincerRig.Anchor;
            Planted = Swinging = false;
            SwingProgress = 0f;
            return;
        }

        stanceHeightScale = Mathf.MoveTowards(
            stanceHeightScale,
            crab.Locomotion.Traversal.StanceHeightScale,
            .012f);

        LastAnchor = Anchor;
        Anchor = anchor;
        for (int i = 0; i < 4; i++)
        {
            LastPos[i] = Pos[i];
            // 步足不是软绳。只保留极少量上一帧关节惯性，主要姿态始终由受限关节求解器决定。
            // Walking legs are not ropes. Preserve only a trace of joint inertia; constrained IK owns the pose.
            Pos[i] += Velocity[i] * .055f;
        }

        if (Swinging)
        {
            UpdateSwing(crab, anchor);
            UpdateVelocities();
            return;
        }

        if (hasTarget)
        {
            if (!MantleCrabTerrainProbe.StillSupported(crab.room, contact))
            {
                ReleaseContact();
            }
            else
            {
                float stretch = Vector2.Distance(anchor, contact) / Mathf.Max(1f, Reach);
                if (Planted && stretch > SlipStartReach)
                    RelieveContactStrain(crab, anchor, stretch);

                stretch = Vector2.Distance(anchor, contact) / Mathf.Max(1f, Reach);
                if (stretch > ReleaseReach)
                    ReleaseContact();
            }
        }

        if (!hasTarget && searchTick-- <= 0)
        {
            searchTick = 8 + Index;
            Vector2 desired = anchor + TransformWalkingLocal(crab, RestTipOffset);
            hasTarget = MantleCrabTerrainProbe.Find(
                crab.room,
                anchor,
                desired,
                Reach * PassiveAcquireReach,
                out contact,
                out GroundNormal);
        }

        bool wasPlanted = Planted;
        Vector2 target = hasTarget
            ? Vector2.MoveTowards(LastPos[3], contact, 11f)
            : Vector2.Lerp(LastPos[3], anchor + TransformWalkingLocal(crab, RestTipOffset), .08f);

        SolveWalkingPose(crab, anchor, target, hasTarget);
        float contactError = hasTarget ? Vector2.Distance(Tip, contact) : float.MaxValue;
        Planted = hasTarget && contactError < PlantTolerance;

        // 一个受限关节链如果已经无法保持原落脚点，就必须卸载，而不是把膝盖翻面来维持“粘地”。
        // If constrained anatomy can no longer hold a planted point, unload it instead of inverting joints to preserve glue.
        if (wasPlanted && !Planted && contactError > 3.5f)
        {
            ReleaseContact();
            searchTick = 0;
        }

        UpdateVelocities();
    }

    internal float SupportQuality(MantleCrab crab)
    {
        if (IsPincer || !Planted || crab == null)
            return 0f;

        Vector2 anchor = crab.Anchor(this);
        float stretch = Vector2.Distance(anchor, contact) / Mathf.Max(1f, Reach);
        float extensionQuality = 1f - Mathf.InverseLerp(.84f, ReleaseReach, stretch);
        float compressionQuality = Mathf.InverseLerp(.36f, .54f, stretch);
        float contactQuality = 1f - Mathf.InverseLerp(1.5f, 4f, Vector2.Distance(Tip, contact));
        float normalQuality = Mathf.Clamp01(.45f + Mathf.Max(0f, GroundNormal.y) * .55f);
        return Mathf.Clamp01(Mathf.Min(extensionQuality, compressionQuality) * contactQuality * normalQuality);
    }

    private void UpdateSwing(MantleCrab crab, Vector2 anchor)
    {
        SwingProgress = Mathf.Clamp01(SwingProgress + 1f / Mathf.Max(1f, swingDuration));
        float t = SwingProgress * SwingProgress * (3f - 2f * SwingProgress);
        Vector2 liftNormal = Vector2.Lerp(Vector2.up, GroundNormal, .30f).normalized;
        float lift = Mathf.Sin(Mathf.PI * t) *
            Mathf.Lerp(16f, 30f, Mathf.InverseLerp(20f, 100f, Vector2.Distance(swingStart, contact)));
        Vector2 target = Vector2.Lerp(swingStart, contact, t) + liftNormal * lift;

        SolveWalkingPose(crab, anchor, target, false);

        if (SwingProgress < 1f)
            return;

        Swinging = false;
        SwingProgress = 1f;

        if (Vector2.Distance(anchor, contact) <= Reach * ReleaseReach &&
            MantleCrabTerrainProbe.StillSupported(crab.room, contact))
        {
            SolveWalkingPose(crab, anchor, contact, true);
            Planted = Vector2.Distance(Tip, contact) < PlantTolerance;
            hasTarget = Planted;
        }
        else
        {
            ReleaseContact();
            searchTick = 0;
        }
    }

    private void RelieveContactStrain(MantleCrab crab, Vector2 anchor, float stretch)
    {
        Vector2 normal = GroundNormal.sqrMagnitude > .0001f ? GroundNormal.normalized : Vector2.up;
        if (normal.y < 0f)
            normal = -normal;
        Vector2 tangent = new(normal.y, -normal.x);
        if (tangent.x < 0f)
            tangent = -tangent;

        float tangentialStrain = Vector2.Dot(anchor - contact, tangent);
        float slipScale = Mathf.InverseLerp(SlipStartReach, ReleaseReach, stretch);
        float slip = Mathf.Clamp(tangentialStrain, -1.6f, 1.6f) * slipScale;
        if (Mathf.Abs(slip) < .05f)
            return;

        Vector2 desired = contact + tangent * slip;
        if (!MantleCrabTerrainProbe.Find(
                crab.room,
                anchor,
                desired,
                Reach * ReleaseReach,
                out Vector2 relieved,
                out Vector2 relievedNormal))
            return;

        // 这是接触面的微小滑移，不是重新落脚；禁止一次滑到另一块远处地形。
        // This is local surface slip, not a new step. Never snap the foot to a distant surface.
        if (Vector2.Distance(relieved, contact) > 6f)
            return;

        contact = relieved;
        GroundNormal = relievedNormal;
    }

    private void ReleaseContact()
    {
        hasTarget = false;
        Planted = false;
    }

    private void UpdateVelocities()
    {
        for (int i = 0; i < 4; i++)
            Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], 10f);
    }

    internal void SolvePose(Vector2 anchor, Vector2 target, bool grounded)
    {
        if (IsPincer)
        {
            // Test/tool compatibility only. Production pincer updates are owned by
            // MantleCrabPincerRig so endpoint IK cannot erase the authored joint angles.
            Vector2 displacement = target - (anchor + RestTipOffset);
            float along = 0f;
            for (int i = 0; i < 3; i++)
            {
                along += Lengths[i];
                Vector2 preferred = anchor + Rest[i + 1] - Rest[0] + displacement * (along / Reach);
                Pos[i] = Vector2.Lerp(Pos[i], preferred, .92f);
            }
            MantleCrabRigMath.Solve(anchor, target, Lengths, Pos);
            return;
        }

        // Compatibility path for tools that solve a walking leg without a creature frame.
        Vector2 walkingDisplacement = target - (anchor + RestTipOffset);
        float walkingAlong = 0f;
        for (int i = 0; i < 3; i++)
        {
            walkingAlong += Lengths[i];
            Vector2 preferred = anchor + Rest[i + 1] - Rest[0] + walkingDisplacement * (walkingAlong / Reach);
            Pos[i] = Vector2.Lerp(Pos[i], preferred, grounded ? .80f : .72f);
        }

        Vector2 restNormal = (Rest[3] - Rest[4]).normalized;
        SolveWalkingEnd(null, anchor, target, grounded, restNormal);
    }

    private void SolveWalkingPose(MantleCrab crab, Vector2 anchor, Vector2 target, bool grounded)
    {
        Vector2 restTipOffset = TransformWalkingLocal(crab, RestTipOffset);
        Vector2 walkingDisplacement = target - (anchor + restTipOffset);
        float walkingAlong = 0f;

        for (int i = 0; i < 3; i++)
        {
            walkingAlong += Lengths[i];
            Vector2 restJoint = TransformWalkingLocal(crab, Rest[i + 1] - Rest[0]);
            Vector2 preferred = anchor + restJoint + walkingDisplacement * (walkingAlong / Reach);
            Pos[i] = Vector2.Lerp(Pos[i], preferred, grounded ? .74f : .62f);
        }

        Vector2 restNormal = TransformWalkingLocal(crab, Rest[3] - Rest[4]).normalized;
        SolveWalkingEnd(crab, anchor, target, grounded, restNormal);
    }

    private void SolveWalkingEnd(
        MantleCrab crab,
        Vector2 anchor,
        Vector2 target,
        bool grounded,
        Vector2 restNormal)
    {
        BuildPreferredDirections(crab);

        Vector2 terrainNormal = GroundNormal.sqrMagnitude > .0001f ? GroundNormal.normalized : Vector2.up;
        if (terrainNormal.y < 0f)
            terrainNormal = -terrainNormal;
        float normalWeight = grounded ? .84f : .30f;
        Vector2 endNormal = Vector2.Lerp(restNormal, terrainNormal, normalWeight).normalized;

        Vector2 ankleTarget = target + endNormal * Lengths[3];
        float upperReach = upperLengths[0] + upperLengths[1] + upperLengths[2];
        float rootLimit = grounded ? 38f : 52f;
        float jointLimit = grounded ? 42f : 55f;

        if (Vector2.Distance(anchor, ankleTarget) <= upperReach * .96f)
        {
            MantleCrabRigMath.SolveConstrained(
                anchor,
                ankleTarget,
                upperLengths,
                Pos,
                upperPreferredDirections,
                rootLimit,
                jointLimit);

            Vector2 actualAnkle = Pos[2];
            Pos[3] = actualAnkle - endNormal * Lengths[3];
        }
        else
        {
            MantleCrabRigMath.SolveConstrained(
                anchor,
                target,
                Lengths,
                Pos,
                preferredDirections,
                rootLimit,
                jointLimit);
        }
    }

    private void BuildPreferredDirections(MantleCrab crab)
    {
        for (int i = 0; i < 4; i++)
        {
            Vector2 localSegment = Rest[i + 1] - Rest[i];
            Vector2 direction = crab == null
                ? localSegment
                : TransformWalkingLocal(crab, localSegment);
            preferredDirections[i] = direction.sqrMagnitude > .0001f ? direction.normalized : Vector2.down;
            if (i < 3)
                upperPreferredDirections[i] = preferredDirections[i];
        }
    }

    private static Vector2 TransformWalkingLocal(MantleCrab crab, Vector2 local)
    {
        if (crab?.Locomotion == null)
            return local;

        Vector2 right = crab.Locomotion.WalkAxis;
        if (right.sqrMagnitude <= .0001f)
            right = Vector2.right;
        else
            right.Normalize();

        Vector2 up = crab.Locomotion.SupportNormal;
        if (up.sqrMagnitude <= .0001f)
            up = Vector2.up;
        else
            up.Normalize();

        return right * local.x + up * local.y;
    }
}
