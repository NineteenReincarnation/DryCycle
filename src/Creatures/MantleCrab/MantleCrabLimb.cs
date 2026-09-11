using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal enum MantleCrabSwingPhase
{
    None,
    Lift,
    Transfer,
    Lower,
    Settle
}

internal sealed class MantleCrabLimb
{
    private const float PassiveAcquireReach = .90f;
    private const float StepAcquireReach = .92f;
    private const float ReleaseReach = .92f;
    private const float PlantTolerance = 1.5f;

    internal readonly int Index, AnchorChunk;
    internal readonly bool IsPincer;
    internal readonly float Side, Reach, LocalDepth;
    internal readonly float[] Lengths;
    private readonly float[] upperLengths;
    private readonly Vector2[] preferredDirections = new Vector2[4];
    private readonly Vector2[] upperPreferredDirections = new Vector2[3];
    private readonly Vector2[] recoveryPreferredDirections = new Vector2[4];
    private readonly Vector2[] recoverySolved = new Vector2[4];
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
    internal MantleCrabSwingPhase SwingPhase { get; private set; }
    internal float SwingPhaseProgress { get; private set; }
    internal bool RecoveryBraced { get; private set; }
    internal Vector2 Contact => contact;
    internal MantleCrabPincerRig PincerRig => pincerRig;
    internal float NominalStandHeight => nominalStandHeight;
    internal float NominalBodyClearance => -Rest[4].y;
    internal float StandHeight => nominalStandHeight * stanceHeightScale;

    private readonly float nominalStandHeight;
    private Vector2 contact;
    private Vector2 swingStart;
    private Vector2 swingLiftNormal = Vector2.up;
    private bool hasTarget;
    private int searchTick;
    private float swingDistance01;
    private float swingLiftHeight;
    private float swingPhaseDuration;
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
        RecoveryBraced = false;
        SwingPhase = MantleCrabSwingPhase.None;
        SwingPhaseProgress = 0f;
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
        SwingPhase = MantleCrabSwingPhase.None;
        SwingPhaseProgress = 0f;
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

        float distance = Vector2.Distance(swingStart, contact);
        swingDistance01 = Mathf.InverseLerp(18f, 100f, distance);
        swingLiftHeight = Mathf.Lerp(18f, 34f, swingDistance01);

        Vector2 bodyUp = crab.Locomotion.SupportNormal;
        if (bodyUp.sqrMagnitude <= .0001f)
            bodyUp = Vector2.up;
        else
            bodyUp.Normalize();
        Vector2 landingUp = landingNormal.sqrMagnitude > .0001f ? landingNormal.normalized : Vector2.up;
        if (landingUp.y < 0f)
            landingUp = -landingUp;
        swingLiftNormal = Vector2.Lerp(bodyUp, landingUp, .24f).normalized;

        EnterSwingPhase(MantleCrabSwingPhase.Lift);
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
            SwingPhase = MantleCrabSwingPhase.None;
            SwingPhaseProgress = 0f;
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

        // 翻倒恢复拥有自己的肢体状态机。先收腿保护关节，再用翻身侧的腿找地撑住，
        // 甲壳回到安全角度以后才逐步重新伸腿。恢复期间不允许普通落脚逻辑和它抢控制权。
        // Self-righting owns the walking legs while active: tuck first, brace on one side,
        // then redeploy only after the shell returns to a safe angle.
        if (crab.Locomotion.Posture.Recovering)
        {
            UpdateRecoveryPose(crab, anchor);
            UpdateVelocities();
            return;
        }

        RecoveryBraced = false;

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
                // 承重脚在一个 stance 周期内必须固定在真实世界接触点上。
                // 身体从脚点上方经过；接近关节极限时由步态系统抬脚换步，而不是沿地面拖着 Contact 滑。
                // A stance foot remains locked to its world-space terrain contact. The body moves over it;
                // nearing the workspace limit must trigger a new step rather than translating the contact across the floor.
                float stretch = Vector2.Distance(anchor, contact) / Mathf.Max(1f, Reach);
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
            ? (Planted ? contact : Vector2.MoveTowards(LastPos[3], contact, 11f))
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

    private void UpdateRecoveryPose(MantleCrab crab, Vector2 anchor)
    {
        MantleCrabPostureController posture = crab.Locomotion.Posture;
        MantleCrabRecoveryPhase phase = posture.RecoveryPhase;
        float progress = posture.RecoveryPhaseProgress;

        // 进入恢复时立即卸掉普通落脚点。这样被压在身体另一侧的脚不会继续把旧 Contact 当成焊点。
        // Ordinary planted contacts are released as soon as recovery owns the limb.
        hasTarget = false;
        Planted = false;
        Swinging = false;
        SwingProgress = 0f;
        SwingPhase = MantleCrabSwingPhase.None;
        SwingPhaseProgress = 0f;
        RecoveryBraced = false;

        if (phase == MantleCrabRecoveryPhase.Retract)
        {
            SolveRecoveryTuck(crab, anchor, Mathf.Lerp(.055f, .13f, Smooth01(progress)));
            return;
        }

        if ((phase == MantleCrabRecoveryPhase.Brace || phase == MantleCrabRecoveryPhase.Roll) &&
            posture.ShouldBraceLeg(this) &&
            TryRecoveryBraceTarget(crab, anchor, posture, out Vector2 braceTarget, out Vector2 braceNormal))
        {
            GroundNormal = braceNormal;
            float response = phase == MantleCrabRecoveryPhase.Brace
                ? Mathf.Lerp(.055f, .11f, Smooth01(progress))
                : Mathf.Lerp(.10f, .16f, posture.RecoveryPushAmount);
            SolveRecoveryBrace(anchor, braceTarget, posture.RecoveryDirection, response);
            RecoveryBraced = Vector2.Distance(Tip, braceTarget) < 5f &&
                             MantleCrabTerrainProbe.StillSupported(crab.room, braceTarget);
            return;
        }

        if (phase == MantleCrabRecoveryPhase.Deploy)
        {
            UpdateRecoveryDeploy(crab, anchor, progress);
            return;
        }

        // 非撑地侧的腿在 Brace/Roll 阶段始终保持蜷缩，避免四条长腿一起扫过屏幕。
        // Non-bracing legs remain tucked throughout brace/roll instead of flailing around the shell.
        SolveRecoveryTuck(crab, anchor, .11f);
    }

    private void SolveRecoveryTuck(MantleCrab crab, Vector2 anchor, float response)
    {
        Vector2 shellAxis = crab.Axis;
        if (shellAxis.sqrMagnitude <= .0001f) shellAxis = Vector2.right;
        else shellAxis.Normalize();
        Vector2 shellUp = new(-shellAxis.y, shellAxis.x);
        float inward = -Side;

        // 收腿是沿甲壳局部坐标折叠，不是把整条腿缩短。四段真实长度始终保持不变，
        // 只是用交错的关节角把长腿收拢到甲壳附近。
        // Tucking folds the fixed-length chain in shell space; no segment is scaled or shortened.
        Vector2 previous = anchor;
        for (int i = 0; i < 4; i++)
        {
            Vector2 localDirection = RecoveryTuckLocalDirection(i, inward);
            Vector2 desiredDirection = shellAxis * localDirection.x + shellUp * localDirection.y;
            desiredDirection.Normalize();
            recoverySolved[i] = previous + desiredDirection * Lengths[i];
            previous = recoverySolved[i];
        }

        BlendChainToward(anchor, recoverySolved, response);
    }

    private static Vector2 RecoveryTuckLocalDirection(int segment, float inward)
    {
        return segment switch
        {
            0 => new Vector2(inward * .97f, -.24f).normalized,
            1 => new Vector2(-inward * .96f, -.28f).normalized,
            2 => new Vector2(inward * .95f, -.31f).normalized,
            _ => new Vector2(-inward * .91f, -.41f).normalized
        };
    }

    private bool TryRecoveryBraceTarget(
        MantleCrab crab,
        Vector2 anchor,
        MantleCrabPostureController posture,
        out Vector2 target,
        out Vector2 normal)
    {
        target = default;
        normal = Vector2.up;
        if (crab.room == null)
            return false;

        float direction = posture.RecoveryDirection;
        float push = posture.RecoveryPushAmount;
        float lateral = Mathf.Lerp(42f, 78f, push) * crab.ShellScale;
        float down = Mathf.Lerp(46f, 82f, push) * crab.ShellScale;

        // 两条同侧腿使用略有差别的探点，避免完全重叠成一根视觉支柱。
        // Same-side brace legs use slightly separated probes so they do not visually collapse into one strut.
        float separation = (Index < 2 ? 10f : -8f) * crab.ShellScale;
        Vector2 desired = anchor + Vector2.right * (direction * lateral + separation) + Vector2.down * down;

        return MantleCrabTerrainProbe.Find(
            crab.room,
            anchor,
            desired,
            Reach * .78f,
            out target,
            out normal);
    }

    private void SolveRecoveryBrace(
        Vector2 anchor,
        Vector2 target,
        float direction,
        float response)
    {
        for (int i = 0; i < 4; i++)
            recoveryPreferredDirections[i] = RecoveryBraceDirection(i, direction);
        for (int i = 0; i < 4; i++)
            recoverySolved[i] = Pos[i];

        // 翻身撑腿允许比正常行走更大的关节活动角，但仍然是受限 IK，不允许膝盖翻面。
        // Recovery bracing has a wider anatomical workspace than walking, while remaining constrained against joint inversion.
        MantleCrabRigMath.SolveConstrained(
            anchor,
            target,
            Lengths,
            recoverySolved,
            recoveryPreferredDirections,
            72f,
            68f);

        BlendChainToward(anchor, recoverySolved, response);
    }

    private static Vector2 RecoveryBraceDirection(int segment, float direction)
    {
        return segment switch
        {
            0 => new Vector2(direction * .74f, -.67f).normalized,
            1 => new Vector2(direction * .52f, -.85f).normalized,
            2 => new Vector2(-direction * .18f, -.98f).normalized,
            _ => new Vector2(-direction * .36f, -.93f).normalized
        };
    }

    private void UpdateRecoveryDeploy(MantleCrab crab, Vector2 anchor, float progress)
    {
        float t = Smooth01(progress);
        Vector2 desired = anchor + TransformWalkingLocal(crab, RestTipOffset);

        if (t < .28f)
        {
            SolveRecoveryTuck(crab, anchor, Mathf.Lerp(.08f, .05f, t / .28f));
            return;
        }

        if (crab.room != null && MantleCrabTerrainProbe.Find(
                crab.room,
                anchor,
                desired,
                Reach * PassiveAcquireReach,
                out Vector2 landing,
                out Vector2 landingNormal))
        {
            GroundNormal = landingNormal;
            float reachT = Smooth01(Mathf.InverseLerp(.28f, 1f, t));
            Vector2 target = Vector2.Lerp(Tip, landing, Mathf.Lerp(.08f, .24f, reachT));
            SolveWalkingPose(crab, anchor, target, reachT > .78f);

            if (reachT > .82f && Vector2.Distance(Tip, landing) < PlantTolerance + .8f)
            {
                contact = landing;
                hasTarget = true;
                Planted = true;
                searchTick = 8 + Index;
            }
            return;
        }

        // 暂时找不到地面时只恢复自然悬垂，不伪造一个落脚点。
        // If no terrain is reachable, unfold toward the natural hanging pose without inventing contact.
        Vector2 freeTarget = Vector2.Lerp(Tip, desired, Mathf.Lerp(.06f, .15f, t));
        SolveWalkingPose(crab, anchor, freeTarget, false);
    }

    private void BlendChainToward(Vector2 anchor, Vector2[] targetPoints, float response)
    {
        Vector2 previous = anchor;
        for (int i = 0; i < 4; i++)
        {
            Vector2 currentDirection = Pos[i] - previous;
            if (currentDirection.sqrMagnitude <= .0001f)
                currentDirection = targetPoints[i] - previous;
            if (currentDirection.sqrMagnitude <= .0001f)
                currentDirection = Vector2.down;
            else
                currentDirection.Normalize();

            Vector2 desiredDirection = targetPoints[i] - previous;
            if (desiredDirection.sqrMagnitude <= .0001f)
                desiredDirection = currentDirection;
            else
                desiredDirection.Normalize();

            Vector2 direction = Vector2.Lerp(currentDirection, desiredDirection, Mathf.Clamp01(response));
            if (direction.sqrMagnitude <= .0001f)
                direction = desiredDirection;
            else
                direction.Normalize();

            Pos[i] = previous + direction * Lengths[i];
            previous = Pos[i];
        }
    }

    private static float Smooth01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private void EnterSwingPhase(MantleCrabSwingPhase phase)
    {
        SwingPhase = phase;
        SwingPhaseProgress = 0f;
        swingPhaseDuration = phase switch
        {
            MantleCrabSwingPhase.Lift => Mathf.Lerp(9f, 13f, swingDistance01),
            MantleCrabSwingPhase.Transfer => Mathf.Lerp(13f, 20f, swingDistance01),
            MantleCrabSwingPhase.Lower => Mathf.Lerp(9f, 14f, swingDistance01),
            MantleCrabSwingPhase.Settle => Mathf.Lerp(8f, 12f, swingDistance01),
            _ => 1f
        };
    }

    private void UpdateSwing(MantleCrab crab, Vector2 anchor)
    {
        if (SwingPhase == MantleCrabSwingPhase.None)
            EnterSwingPhase(MantleCrabSwingPhase.Lift);

        // 真正进入落脚阶段以后，目标地面必须仍然存在。前半程允许脚在空中完成抬起和转移，
        // Lower / Settle 则要求落点仍是有效支撑面，否则这一步直接失败并重新找地。
        // Once lowering begins the landing terrain must still exist. Earlier phases may complete in the air,
        // while Lower / Settle abort if the planned support disappeared.
        if ((SwingPhase == MantleCrabSwingPhase.Lower || SwingPhase == MantleCrabSwingPhase.Settle) &&
            !MantleCrabTerrainProbe.StillSupported(crab.room, contact))
        {
            AbortSwing();
            searchTick = 0;
            return;
        }

        SwingPhaseProgress = Mathf.Clamp01(
            SwingPhaseProgress + 1f / Mathf.Max(1f, swingPhaseDuration));
        float t = Smooth01(SwingPhaseProgress);
        SwingProgress = GlobalSwingProgress(SwingPhase, t);

        Vector2 displacement = contact - swingStart;
        Vector2 liftPoint = swingStart + displacement * .12f + swingLiftNormal * swingLiftHeight;
        Vector2 transferPoint = contact + swingLiftNormal * Mathf.Max(8f, swingLiftHeight * .48f);
        Vector2 target;
        bool groundedPose = false;

        switch (SwingPhase)
        {
            case MantleCrabSwingPhase.Lift:
                // 先离地再向前：脚尖的大部分运动都沿支撑法线抬起，只带少量前移，
                // 防止大型步足刚解除承重就贴着地面横扫。
                // Clear the terrain before travelling forward; only a small horizontal component is allowed here.
                target = Vector2.Lerp(swingStart, liftPoint, t);
                break;

            case MantleCrabSwingPhase.Transfer:
                // 中段保持明显离地高度完成主要前移。额外的小弧顶避免两段插值看起来像折线。
                // Carry most of the forward travel while elevated, with a shallow arc to avoid a piecewise-linear look.
                target = Vector2.Lerp(liftPoint, transferPoint, t) +
                         swingLiftNormal * (Mathf.Sin(t * Mathf.PI) * swingLiftHeight * .10f);
                break;

            case MantleCrabSwingPhase.Lower:
                // 到达落点上方以后再主动下探，并在这一段开始把末端足朝地面法线对齐。
                // Descend only after reaching the landing neighbourhood, and begin aligning the distal foot to terrain.
                target = Vector2.Lerp(
                    transferPoint,
                    contact + GroundNormal.normalized * 2.8f,
                    t);
                groundedPose = true;
                break;

            case MantleCrabSwingPhase.Settle:
                // 脚已经视觉接地，但仍保持 Swinging=true，因此它暂时不参与承重或下一次换步。
                // 最后几帧只完成约 2.8px 的压实，再把控制权交给承重恢复。
                // The foot is visually touching down but remains a swing limb until this short compression finishes.
                target = contact + GroundNormal.normalized * Mathf.Lerp(2.8f, 0f, t);
                groundedPose = true;
                break;

            default:
                target = contact;
                break;
        }

        SolveWalkingPose(crab, anchor, target, groundedPose);

        if (SwingPhaseProgress < 1f)
            return;

        switch (SwingPhase)
        {
            case MantleCrabSwingPhase.Lift:
                EnterSwingPhase(MantleCrabSwingPhase.Transfer);
                return;
            case MantleCrabSwingPhase.Transfer:
                EnterSwingPhase(MantleCrabSwingPhase.Lower);
                return;
            case MantleCrabSwingPhase.Lower:
                EnterSwingPhase(MantleCrabSwingPhase.Settle);
                return;
            case MantleCrabSwingPhase.Settle:
                FinishSwing(crab, anchor);
                return;
        }
    }

    private float GlobalSwingProgress(MantleCrabSwingPhase phase, float phaseProgress)
    {
        phaseProgress = Mathf.Clamp01(phaseProgress);
        return phase switch
        {
            MantleCrabSwingPhase.Lift => Mathf.Lerp(0f, .24f, phaseProgress),
            MantleCrabSwingPhase.Transfer => Mathf.Lerp(.24f, .62f, phaseProgress),
            MantleCrabSwingPhase.Lower => Mathf.Lerp(.62f, .86f, phaseProgress),
            MantleCrabSwingPhase.Settle => Mathf.Lerp(.86f, 1f, phaseProgress),
            _ => 0f
        };
    }

    private void FinishSwing(MantleCrab crab, Vector2 anchor)
    {
        Swinging = false;
        SwingProgress = 1f;
        SwingPhase = MantleCrabSwingPhase.None;
        SwingPhaseProgress = 0f;

        if (Vector2.Distance(anchor, contact) <= Reach * ReleaseReach &&
            MantleCrabTerrainProbe.StillSupported(crab.room, contact))
        {
            SolveWalkingPose(crab, anchor, contact, true);
            Planted = Vector2.Distance(Tip, contact) < PlantTolerance;
            hasTarget = Planted;
            if (Planted)
            {
                searchTick = 8 + Index;
                return;
            }
        }

        ReleaseContact();
        searchTick = 0;
    }

    private void AbortSwing()
    {
        Swinging = false;
        SwingProgress = 0f;
        SwingPhase = MantleCrabSwingPhase.None;
        SwingPhaseProgress = 0f;
        ReleaseContact();
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
