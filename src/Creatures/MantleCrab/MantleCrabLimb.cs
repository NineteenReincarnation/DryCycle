using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal sealed class MantleCrabLimb
{
    internal readonly int Index, AnchorChunk;
    internal readonly bool IsPincer;
    internal readonly float Side, Reach, LocalDepth;
    internal readonly float[] Lengths;
    private readonly float[] upperLengths;
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
            for (int i = 0; i < 4; i++) Lengths[i] = Vector2.Distance(Rest[i], Rest[i + 1]);
            Pos = new Vector2[4];
            LastPos = new Vector2[4];
            Velocity = new Vector2[4];
        }

        foreach (float length in Lengths) Reach += length;
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
        Vector2 desired = anchor + TransformLocal(crab, RestTipOffset);
        hasTarget = MantleCrabTerrainProbe.Find(
            crab.room,
            anchor,
            desired,
            Reach * .995f,
            out contact,
            out GroundNormal);

        if (!hasTarget)
        {
            Planted = false;
            return false;
        }

        // Placement/DevConsole realization should start from a valid standing pose rather than
        // spending several gravity frames dragging the feet toward the floor. This is not a
        // hover lock: if no reachable surface exists the caller leaves the creature unsupported.
        SolveWalkingPose(crab, anchor, contact, true);
        for (int i = 0; i < 4; i++)
        {
            LastPos[i] = Pos[i];
            Velocity[i] = Vector2.zero;
        }

        Planted = Vector2.Distance(Tip, contact) < 1.2f;
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
                Reach * .995f,
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
        swingDuration = Mathf.Lerp(11f, 19f, Mathf.InverseLerp(18f, 100f, Vector2.Distance(swingStart, contact)));
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

        // 困难地形时先降低身体、增加关节余量；这是真实节肢动物常见的“先换姿态再落脚”。
        // On difficult terrain the body crouches before the next reach, increasing articulated
        // workspace instead of granting the foot an impossible longer leg.
        stanceHeightScale = Mathf.MoveTowards(
            stanceHeightScale,
            crab.Locomotion.Traversal.StanceHeightScale,
            .012f);

        LastAnchor = Anchor;
        Anchor = anchor;
        for (int i = 0; i < 4; i++)
        {
            LastPos[i] = Pos[i];
            Pos[i] += Velocity[i] * .12f;
        }

        if (Swinging)
        {
            UpdateSwing(crab, anchor);
            UpdateVelocities();
            return;
        }

        if (hasTarget && (Vector2.Distance(anchor, contact) > Reach * .999f ||
            !MantleCrabTerrainProbe.StillSupported(crab.room, contact)))
            hasTarget = Planted = false;

        if (!hasTarget && searchTick-- <= 0)
        {
            searchTick = 8 + Index;
            Vector2 desired = anchor + TransformLocal(crab, RestTipOffset);
            hasTarget = MantleCrabTerrainProbe.Find(
                crab.room,
                anchor,
                desired,
                Reach * .995f,
                out contact,
                out GroundNormal);
        }

        Vector2 target = hasTarget
            ? Vector2.MoveTowards(LastPos[3], contact, 13f)
            : Vector2.Lerp(LastPos[3], anchor + TransformLocal(crab, RestTipOffset), .08f);

        SolveWalkingPose(crab, anchor, target, hasTarget);
        Planted = hasTarget && Vector2.Distance(Tip, contact) < 1.2f;
        UpdateVelocities();
    }

    private void UpdateSwing(MantleCrab crab, Vector2 anchor)
    {
        SwingProgress = Mathf.Clamp01(SwingProgress + 1f / Mathf.Max(1f, swingDuration));
        float t = SwingProgress * SwingProgress * (3f - 2f * SwingProgress);
        Vector2 liftNormal = Vector2.Lerp(Vector2.up, GroundNormal, .35f).normalized;
        float lift = Mathf.Sin(Mathf.PI * t) *
            Mathf.Lerp(16f, 30f, Mathf.InverseLerp(20f, 100f, Vector2.Distance(swingStart, contact)));
        Vector2 target = Vector2.Lerp(swingStart, contact, t) + liftNormal * lift;

        SolveWalkingPose(crab, anchor, target, false);

        if (SwingProgress < 1f)
            return;

        Swinging = false;
        SwingProgress = 1f;

        if (Vector2.Distance(anchor, contact) <= Reach * .999f &&
            MantleCrabTerrainProbe.StillSupported(crab.room, contact))
        {
            SolveWalkingPose(crab, anchor, contact, true);
            Planted = true;
            hasTarget = true;
        }
        else
        {
            Planted = false;
            hasTarget = false;
            searchTick = 0;
        }
    }

    private void UpdateVelocities()
    {
        for (int i = 0; i < 4; i++)
            Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], 18f);
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
        SolveWalkingEnd(anchor, target, grounded, restNormal);
    }

    private void SolveWalkingPose(MantleCrab crab, Vector2 anchor, Vector2 target, bool grounded)
    {
        Vector2 restTipOffset = TransformLocal(crab, RestTipOffset);
        Vector2 walkingDisplacement = target - (anchor + restTipOffset);
        float walkingAlong = 0f;

        for (int i = 0; i < 3; i++)
        {
            walkingAlong += Lengths[i];
            Vector2 restJoint = TransformLocal(crab, Rest[i + 1] - Rest[0]);
            Vector2 preferred = anchor + restJoint + walkingDisplacement * (walkingAlong / Reach);
            Pos[i] = Vector2.Lerp(Pos[i], preferred, grounded ? .80f : .72f);
        }

        Vector2 restNormal = TransformLocal(crab, Rest[3] - Rest[4]).normalized;
        SolveWalkingEnd(anchor, target, grounded, restNormal);
    }

    private void SolveWalkingEnd(Vector2 anchor, Vector2 target, bool grounded, Vector2 restNormal)
    {
        Vector2 terrainNormal = GroundNormal.sqrMagnitude > .0001f ? GroundNormal.normalized : Vector2.up;
        float normalWeight = grounded ? .84f : .38f;
        Vector2 endNormal = Vector2.Lerp(restNormal, terrainNormal, normalWeight).normalized;

        Vector2 ankle = target + endNormal * Lengths[3];
        if (grounded && Vector2.Distance(anchor, ankle) < Reach - Lengths[3] - .5f)
        {
            MantleCrabRigMath.Solve(anchor, ankle, upperLengths, Pos);
            Pos[3] = Pos[2] - (ankle - target).normalized * Lengths[3];
        }
        else
        {
            MantleCrabRigMath.Solve(anchor, target, Lengths, Pos);
        }
    }

    private static Vector2 TransformLocal(MantleCrab crab, Vector2 local)
    {
        Vector2 axis = crab.Axis;
        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis = axis.normalized;

        Vector2 up = new(-axis.y, axis.x);
        return axis * local.x + up * local.y;
    }
}
