using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal enum MantleCrabTraversalMode
{
    Level,
    Rough,
    StepUp,
    StepDown,
    BridgeGap,
    Blocked
}

/// <summary>
/// Local terrain reflexes for the walking rig. This is deliberately not a pathfinder: it only
/// answers the arthropod-scale question "where can the next supporting foot safely go?".
/// </summary>
internal sealed class MantleCrabTraversalPlanner
{
    private const int ProbeCount = 5;
    private static readonly float[] ProbeReachFractions = [.12f, .20f, .28f, .36f, .44f];

    private readonly MantleCrab crab;
    private readonly bool[] probeHit = new bool[ProbeCount];
    private readonly Vector2[] probePoint = new Vector2[ProbeCount];
    private readonly Vector2[] probeNormal = new Vector2[ProbeCount];

    private float moveSign;
    private float baselineHeight;
    private float minimumReach;
    private bool hasTransitionSupport;
    private Vector2 transitionSupport;
    private int blockedFrames;

    internal MantleCrabTraversalMode Mode { get; private set; } = MantleCrabTraversalMode.Level;
    internal float Roughness { get; private set; }
    internal float SpeedScale { get; private set; } = 1f;
    internal float StanceHeightScale { get; private set; } = 1f;
    internal bool LeadingSupportEstablished { get; private set; }

    internal MantleCrabTraversalPlanner(MantleCrab crab)
    {
        this.crab = crab;
    }

    internal void Reset()
    {
        Mode = MantleCrabTraversalMode.Level;
        Roughness = 0f;
        SpeedScale = 1f;
        StanceHeightScale = 1f;
        LeadingSupportEstablished = false;
        moveSign = 0f;
        baselineHeight = 0f;
        minimumReach = 0f;
        hasTransitionSupport = false;
        transitionSupport = Vector2.zero;
        blockedFrames = 0;

        for (int i = 0; i < ProbeCount; i++)
        {
            probeHit[i] = false;
            probePoint[i] = Vector2.zero;
            probeNormal[i] = Vector2.up;
        }
    }

    internal void Update(float rawMoveIntent)
    {
        if (crab.room == null || crab.Legs == null || crab.Legs.Length == 0)
        {
            Reset();
            return;
        }

        minimumReach = MinimumLegReach();
        baselineHeight = SupportBaselineHeight();
        moveSign = Mathf.Abs(rawMoveIntent) > .05f ? Mathf.Sign(rawMoveIntent) : 0f;
        LeadingSupportEstablished = false;
        hasTransitionSupport = false;
        Roughness = 0f;

        if (moveSign == 0f)
        {
            Mode = MantleCrabTraversalMode.Level;
            SpeedScale = 1f;
            StanceHeightScale = 1f;
            blockedFrames = 0;
            return;
        }

        Vector2 axis = Axis();
        Vector2 center = BodyCenter();
        float scanTop = center.y + 10f * crab.ShellScale;
        float maxDrop = minimumReach * 1.28f;

        int hits = 0;
        int firstMissing = -1;
        int firstHitAfterMissing = -1;
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        for (int i = 0; i < ProbeCount; i++)
        {
            float distance = minimumReach * ProbeReachFractions[i] * crab.ShellScale;
            Vector2 sample = center + axis * (moveSign * distance);
            probeHit[i] = MantleCrabTerrainProbe.TrySurfaceBelow(
                crab.room,
                sample.x,
                scanTop,
                maxDrop,
                out probePoint[i],
                out probeNormal[i]);

            if (!probeHit[i])
            {
                if (firstMissing < 0) firstMissing = i;
                continue;
            }

            hits++;
            minHeight = Mathf.Min(minHeight, probePoint[i].y);
            maxHeight = Mathf.Max(maxHeight, probePoint[i].y);
            if (firstMissing >= 0 && firstHitAfterMissing < 0)
                firstHitAfterMissing = i;
        }

        float heightSpread = hits > 1 ? maxHeight - minHeight : 0f;
        float missingPenalty = (ProbeCount - hits) / (float)ProbeCount;
        float normalPenalty = 0f;
        for (int i = 0; i < ProbeCount; i++)
            if (probeHit[i]) normalPenalty = Mathf.Max(normalPenalty, 1f - Mathf.Clamp01(probeNormal[i].y));
        Roughness = Mathf.Clamp01(
            heightSpread / (48f * crab.ShellScale) + missingPenalty * .7f + normalPenalty * .45f);

        bool shellClear = ForwardShellClear(axis * moveSign, 34f * crab.ShellScale);
        int significantIndex = FirstSignificantHeightChange();

        if (firstMissing >= 0 && firstHitAfterMissing > firstMissing)
        {
            SetTransitionSupport(firstHitAfterMissing);
            Mode = hasTransitionSupport && ReachableByLeadingLeg(transitionSupport, .84f)
                ? MantleCrabTraversalMode.BridgeGap
                : MantleCrabTraversalMode.Blocked;
        }
        else if (significantIndex >= 0)
        {
            float delta = probePoint[significantIndex].y - baselineHeight;
            float maxRise = minimumReach * .35f;
            if (!ReachableByLeadingLeg(probePoint[significantIndex], delta < 0f ? .82f : .90f) || delta > maxRise)
            {
                Mode = MantleCrabTraversalMode.Blocked;
            }
            else if (delta > 14f * crab.ShellScale)
            {
                Mode = MantleCrabTraversalMode.StepUp;
                SetTransitionSupport(significantIndex);
            }
            else if (delta < -18f * crab.ShellScale)
            {
                Mode = MantleCrabTraversalMode.StepDown;
                SetTransitionSupport(significantIndex);
            }
            else
            {
                Mode = Roughness > .28f ? MantleCrabTraversalMode.Rough : MantleCrabTraversalMode.Level;
            }
        }
        else if (hits == 0 || (!shellClear && maxHeight <= baselineHeight + 20f * crab.ShellScale))
        {
            Mode = MantleCrabTraversalMode.Blocked;
        }
        else
        {
            Mode = Roughness > .28f ? MantleCrabTraversalMode.Rough : MantleCrabTraversalMode.Level;
        }

        LeadingSupportEstablished = CheckLeadingSupportEstablished(axis, center);

        switch (Mode)
        {
            case MantleCrabTraversalMode.Level:
                SpeedScale = 1f;
                StanceHeightScale = 1f;
                break;
            case MantleCrabTraversalMode.Rough:
                SpeedScale = Mathf.Lerp(.80f, .54f, Roughness);
                StanceHeightScale = Mathf.Lerp(.97f, .91f, Roughness);
                break;
            case MantleCrabTraversalMode.StepUp:
                SpeedScale = LeadingSupportEstablished ? .64f : .30f;
                StanceHeightScale = LeadingSupportEstablished ? .97f : .90f;
                break;
            case MantleCrabTraversalMode.StepDown:
                SpeedScale = LeadingSupportEstablished ? .54f : .20f;
                StanceHeightScale = LeadingSupportEstablished ? .90f : .82f;
                break;
            case MantleCrabTraversalMode.BridgeGap:
                SpeedScale = LeadingSupportEstablished ? .48f : .12f;
                StanceHeightScale = LeadingSupportEstablished ? .90f : .84f;
                break;
            default:
                SpeedScale = 0f;
                StanceHeightScale = .90f;
                break;
        }

        blockedFrames = Mode == MantleCrabTraversalMode.Blocked ? blockedFrames + 1 : 0;
    }

    internal float EffectiveMoveIntent(float rawMoveIntent) => rawMoveIntent * SpeedScale;

    internal float DesiredStandHeight(MantleCrabLimb leg) => leg.NominalStandHeight * StanceHeightScale;

    internal bool AllowLift(MantleCrabLimb leg)
    {
        if (moveSign == 0f)
            return true;

        bool leading = IsLeading(leg);
        switch (Mode)
        {
            case MantleCrabTraversalMode.StepUp:
            case MantleCrabTraversalMode.StepDown:
            case MantleCrabTraversalMode.BridgeGap:
                // The forward feet probe first. Rear supports remain loaded until at least one
                // forward foot has proved that the new surface can carry the body.
                return leading || LeadingSupportEstablished;
            case MantleCrabTraversalMode.Blocked:
                return false;
            default:
                return true;
        }
    }

    internal float UrgencyMultiplier(MantleCrabLimb leg)
    {
        if (moveSign == 0f)
            return 1f;

        bool leading = IsLeading(leg);
        switch (Mode)
        {
            case MantleCrabTraversalMode.StepUp:
            case MantleCrabTraversalMode.StepDown:
            case MantleCrabTraversalMode.BridgeGap:
                if (!LeadingSupportEstablished)
                    return leading ? 1.55f : .35f;
                return leading ? .82f : 1.18f;
            case MantleCrabTraversalMode.Rough:
                // Real crabs increase leading-leg duty factor on difficult terrain. Once a
                // leading foot is down we therefore make it a little less eager to lift again.
                return leading ? .84f : 1.08f;
            default:
                return 1f;
        }
    }

    internal int StepCooldown(MantleCrabLimb leg)
    {
        if (Mode == MantleCrabTraversalMode.Rough && IsLeading(leg))
            return 28;
        if ((Mode == MantleCrabTraversalMode.StepUp || Mode == MantleCrabTraversalMode.StepDown ||
             Mode == MantleCrabTraversalMode.BridgeGap) && IsLeading(leg))
            return 26;
        return 22;
    }

    internal Vector2 AdjustLanding(MantleCrabLimb leg, Vector2 nominal, Vector2 axis)
    {
        if (!hasTransitionSupport || moveSign == 0f || !IsLeading(leg))
            return nominal;

        if (Mode != MantleCrabTraversalMode.StepUp && Mode != MantleCrabTraversalMode.StepDown &&
            Mode != MantleCrabTraversalMode.BridgeGap)
            return nominal;

        // The two coplanar walking legs on one side should not collapse onto the same foothold.
        // The visually outer pair reaches slightly farther while the inner pair stays slightly back.
        float pairOffset = (leg.Index < 2 ? 10f : -10f) * moveSign * crab.ShellScale;
        return transitionSupport + axis * pairOffset;
    }

    internal bool LongBlocked => blockedFrames > 30;

    private void SetTransitionSupport(int index)
    {
        if (index < 0 || index >= ProbeCount || !probeHit[index])
            return;

        hasTransitionSupport = true;
        transitionSupport = probePoint[index];
    }

    private int FirstSignificantHeightChange()
    {
        for (int i = 0; i < ProbeCount; i++)
        {
            if (!probeHit[i]) continue;
            if (Mathf.Abs(probePoint[i].y - baselineHeight) > 12f * crab.ShellScale)
                return i;
        }
        return -1;
    }

    private bool CheckLeadingSupportEstablished(Vector2 axis, Vector2 center)
    {
        if (!hasTransitionSupport)
            return false;

        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted || !IsLeading(leg)) continue;

            float forward = Vector2.Dot(leg.Contact - center, axis) * moveSign;
            float targetForward = Vector2.Dot(transitionSupport - center, axis) * moveSign;
            if (forward >= targetForward - 26f * crab.ShellScale &&
                Mathf.Abs(leg.Contact.y - transitionSupport.y) < 24f * crab.ShellScale)
                return true;
        }

        return false;
    }

    private bool ReachableByLeadingLeg(Vector2 point, float stanceScale)
    {
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!IsLeading(leg)) continue;

            // 规划时允许身体先下蹲，再判断下一块地是否在真实关节工作空间内。
            // Planning may account for a crouch before deciding whether the next foothold is
            // inside the real articulated workspace; the actual body still has to crouch before
            // TryBeginStep can reach it.
            Vector2 anchor = crab.Anchor(leg) + Vector2.down * (leg.NominalStandHeight * (1f - stanceScale));
            if (Vector2.Distance(anchor, point) <= leg.Reach * .92f)
                return true;
        }
        return false;
    }

    private float SupportBaselineHeight()
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < crab.Legs.Length; i++)
        {
            MantleCrabLimb leg = crab.Legs[i];
            if (!leg.Planted) continue;
            total += leg.Contact.y;
            count++;
        }

        if (count > 0)
            return total / count;

        float averageStandHeight = 0f;
        for (int i = 0; i < crab.Legs.Length; i++)
            averageStandHeight += crab.Legs[i].NominalStandHeight;
        averageStandHeight /= Mathf.Max(1, crab.Legs.Length);
        return BodyCenter().y - averageStandHeight;
    }

    private float MinimumLegReach()
    {
        float reach = float.MaxValue;
        for (int i = 0; i < crab.Legs.Length; i++)
            reach = Mathf.Min(reach, crab.Legs[i].Reach);
        return reach < float.MaxValue ? reach : 1f;
    }

    private bool ForwardShellClear(Vector2 direction, float distance)
    {
        if (direction.sqrMagnitude <= .0001f)
            return true;
        direction.Normalize();

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            Vector2 future = chunk.pos + direction * distance;
            if (!MantleCrabTerrainProbe.IsDiscClear(crab.room, future, chunk.rad * .82f))
                return false;
        }
        return true;
    }

    private bool IsLeading(MantleCrabLimb leg) => leg.Side * moveSign > 0f;

    private Vector2 BodyCenter()
    {
        float mass = 0f;
        Vector2 center = Vector2.zero;
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            mass += chunk.mass;
            center += chunk.pos * chunk.mass;
        }
        return mass > .0001f ? center / mass : crab.bodyChunks[2].pos;
    }

    private Vector2 Axis()
    {
        Vector2 axis = crab.Axis;
        return axis.sqrMagnitude > .0001f ? axis.normalized : Vector2.right;
    }
}
