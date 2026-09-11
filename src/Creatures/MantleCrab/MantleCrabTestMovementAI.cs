using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// 临时移动测试 AI：只负责让 MantleCrab 在房间内持续巡航、换向和从阻挡中退回。
/// 不包含正式寻路、感知、战斗或生态决策。
///
/// Temporary movement-test brain. It only cruises, reverses, and backs away from blocked terrain.
/// It deliberately owns no production pathing, perception, combat, or ecology decisions.
/// </summary>
internal sealed class MantleCrabTestMovementAI : ArtificialIntelligence
{
    private const int CruiseFrames = 520;
    private const int TurnPauseFrames = 24;
    private const int ProgressWindowFrames = 80;
    private const float MinimumWindowProgress = 7f;

    private float moveSign;
    private int cruiseFramesRemaining;
    private int pauseFrames;
    private int progressFrames;
    private bool hasProgressOrigin;
    private Vector2 progressOrigin;

    internal MantleCrabTestMovementAI(AbstractCreature creature, World world)
        : base(creature, world)
    {
        // 相邻生成个体从相反方向开始，方便同时观察两只螃蟹时覆盖左右移动。
        // Alternate the initial direction by entity seed so multiple test crabs naturally cover both sides.
        moveSign = (creature.ID.RandomSeed & 1) == 0 ? 1f : -1f;
        cruiseFramesRemaining = CruiseFrames;
    }

    public override void NewRoom(Room room)
    {
        base.NewRoom(room);
        cruiseFramesRemaining = CruiseFrames;
        pauseFrames = 0;
        progressFrames = 0;
        hasProgressOrigin = false;
    }

    public override void Update()
    {
        base.Update();

        if (creature.realizedCreature is not MantleCrab crab)
            return;

        if (crab.room == null || crab.dead || !crab.Consious || crab.inShortcut)
        {
            crab.SetLocomotionIntent(0f, 0f);
            ResetProgress();
            return;
        }

        // Safari 控制拥有最高优先级，测试 AI 不覆盖玩家输入。
        // Safari control has priority; the test brain must never overwrite manual input.
        if (crab.safariControlled)
        {
            ResetProgress();
            return;
        }

        Vector2 center = crab.bodyChunks[2].pos;

        if (pauseFrames > 0)
        {
            pauseFrames--;
            crab.SetLocomotionIntent(0f, 0f);
            RememberProgressOrigin(center);
            return;
        }

        MantleCrabTraversalMode mode = crab.Locomotion.Traversal.Mode;
        if (mode == MantleCrabTraversalMode.Blocked)
        {
            ReverseDirection();
            crab.SetLocomotionIntent(0f, 0f);
            RememberProgressOrigin(center);
            return;
        }

        // 上下台阶和跨沟本来就会显著降速，测试 AI 不把这些正常过渡误判成卡死。
        // Step and gap transitions intentionally move slowly, so do not treat them as a stuck condition.
        if (mode == MantleCrabTraversalMode.StepUp ||
            mode == MantleCrabTraversalMode.StepDown ||
            mode == MantleCrabTraversalMode.BridgeGap)
        {
            RememberProgressOrigin(center);
        }
        else if (UpdateProgressWindow(crab, center))
        {
            ReverseDirection();
            crab.SetLocomotionIntent(0f, 0f);
            RememberProgressOrigin(center);
            return;
        }

        cruiseFramesRemaining--;
        if (cruiseFramesRemaining <= 0)
        {
            // 即使一路畅通也定期换向，专门覆盖左右步态和换向恢复。
            // Periodically reverse even on clear ground so both gait directions are exercised automatically.
            ReverseDirection();
            crab.SetLocomotionIntent(0f, 0f);
            RememberProgressOrigin(center);
            return;
        }

        crab.SetLocomotionIntent(moveSign, 0f);
    }

    private bool UpdateProgressWindow(MantleCrab crab, Vector2 center)
    {
        if (!hasProgressOrigin)
        {
            RememberProgressOrigin(center);
            return false;
        }

        progressFrames++;
        if (progressFrames < ProgressWindowFrames)
            return false;

        Vector2 axis = crab.Axis;
        if (axis.sqrMagnitude <= .0001f)
            axis = Vector2.right;
        else
            axis.Normalize();

        float progress = Mathf.Abs(Vector2.Dot(center - progressOrigin, axis));
        bool stuck = progress < MinimumWindowProgress * crab.ShellScale;
        RememberProgressOrigin(center);
        return stuck;
    }

    private void ReverseDirection()
    {
        moveSign = -moveSign;
        cruiseFramesRemaining = CruiseFrames;
        pauseFrames = TurnPauseFrames;
        ResetProgress();
    }

    private void RememberProgressOrigin(Vector2 center)
    {
        progressOrigin = center;
        progressFrames = 0;
        hasProgressOrigin = true;
    }

    private void ResetProgress()
    {
        progressFrames = 0;
        hasProgressOrigin = false;
    }
}
