using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal readonly struct LanceAimSolution
{
    internal LanceAimSolution(bool valid, Vector2 aim, Vector2 lanceDirection, int impactFrame,
        float quality, BodyChunk targetChunk, bool exact)
    {
        Valid = valid;
        Aim = aim;
        LanceDirection = lanceDirection.sqrMagnitude > 0.001f ? lanceDirection.normalized : Vector2.right;
        ImpactFrame = impactFrame;
        Quality = Mathf.Clamp01(quality);
        TargetChunk = targetChunk;
        Exact = exact;
    }

    internal bool Valid { get; }
    internal bool Ready => Valid && Quality >= LanceAimSolver.MinimumAimQuality;
    internal Vector2 Aim { get; }
    internal Vector2 LanceDirection { get; }
    internal int ImpactFrame { get; }
    internal float Quality { get; }
    internal BodyChunk TargetChunk { get; }
    internal bool Exact { get; }
}

/// <summary>
/// Soft tactical aiming. It deliberately behaves more like vanilla scavenger spear aim:
/// continuously refresh a short, smoothed lead and rate the opportunity instead of asking
/// for a mathematically perfect future intersection on one exact frame.
/// </summary>
internal static class LanceAimSolver
{
    internal const float MinimumAimQuality = 0.58f;
    internal const float MinimumLancePitch = -15f;
    internal const float MaximumLancePitch = 15f;
    internal const float ReleaseCorrectionDegrees = 6f;

    private const float ActualBladeHitPadding = 1.5f;
    private const float PlanningBladeHitPadding = 5.5f;
    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;

    internal static LanceAimSolution Solve(LanceScavenger scav, Vector2 origin, Creature target,
        TargetMotionTracker motion)
    {
        if (scav?.room == null || target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return default;

        float dx = target.mainBodyChunk.pos.x - origin.x;
        float horizontalSign = Mathf.Sign(dx);
        if (horizontalSign == 0f) return default;

        float length = scav.Lance?.Length ?? LanceCombatMath.DefaultLength;
        float forwardLength = LanceCombatMath.ForwardLength(length);
        Vector2 targetCenter = AverageBodyPosition(target);
        LanceAimSolution best = default;

        for (int pitchMagnitude = 0; pitchMagnitude <= 15; pitchMagnitude++)
        {
            EvaluatePitch(pitchMagnitude == 0 ? 0f : -pitchMagnitude);
            if (pitchMagnitude > 0) EvaluatePitch(pitchMagnitude);
        }
        return best;

        void EvaluatePitch(float pitchDegrees)
        {
            float radians = pitchDegrees * Mathf.Deg2Rad;
            Vector2 direction = new(horizontalSign * Mathf.Cos(radians), Mathf.Sin(radians));
            Vector2 body = origin;
            Vector2 velocity = new(horizontalSign * ChargeLanePlanner.ChargeSpeed(scav), ChargeLanePlanner.ChargeLaunchY);
            Vector2 previousGrip = GripPosition(body, direction);

            for (int frame = 1; frame <= LanceCombatState.MaxChargeFrames; frame++)
            {
                StepBody(ref body, ref velocity);
                Vector2 grip = GripPosition(body, direction);

                foreach (BodyChunk chunk in target.bodyChunks)
                {
                    Vector2 smoothVelocity = motion?.SmoothedVelocity(chunk) ?? chunk.vel;
                    Vector2 oldTarget = Predict(chunk.pos, smoothVelocity, frame - 1);
                    Vector2 newTarget = Predict(chunk.pos, smoothVelocity, frame);

                    bool exact = LanceCombatMath.SweepBlade(previousGrip, direction, grip, direction,
                        forwardLength, oldTarget, newTarget, chunk.rad, ActualBladeHitPadding,
                        out float exactFraction, out float exactBladeT);

                    float hitFraction = exactFraction;
                    float bladeT = exactBladeT;
                    bool probable = exact;
                    if (!probable)
                        probable = LanceCombatMath.SweepBlade(previousGrip, direction, grip, direction,
                            forwardLength, oldTarget, newTarget, chunk.rad, PlanningBladeHitPadding,
                            out hitFraction, out bladeT);
                    if (!probable) continue;

                    Vector2 aim = Vector2.Lerp(oldTarget, newTarget, hitFraction);
                    float quality = AimQuality(exact, pitchDegrees, frame, bladeT, chunk,
                        targetCenter, smoothVelocity, motion);
                    if (best.Valid && quality <= best.Quality + 0.001f) continue;
                    best = new LanceAimSolution(true, aim, direction, frame, quality, chunk, exact);
                }
                previousGrip = grip;
            }
        }
    }

    /// <summary>
    /// A stored brace solution keeps its commitment, but release may turn a few degrees toward
    /// the target's current smoothed lead. This avoids using a stale early-brace angle without
    /// requiring the final frame to solve another perfect intercept.
    /// </summary>
    internal static LanceAimSolution CorrectForRelease(LanceScavenger scav, Creature target,
        TargetMotionTracker motion, LanceAimSolution stored)
    {
        if (!stored.Valid || target == null || scav == null) return stored;
        BodyChunk chunk = stored.TargetChunk;
        if (chunk == null || chunk.owner != target)
            chunk = target.mainBodyChunk;

        int leadFrames = Mathf.Clamp(stored.ImpactFrame, 1, LanceCombatState.MaxChargeFrames);
        Vector2 velocity = motion?.SmoothedVelocity(chunk) ?? chunk.vel;
        Vector2 aim = Predict(chunk.pos, velocity, leadFrames);
        Vector2 grip = GripPosition(scav.mainBodyChunk.pos, stored.LanceDirection);
        Vector2 desired = aim - grip;
        if (desired.sqrMagnitude < 0.001f) return stored;

        float sign = Mathf.Sign(stored.LanceDirection.x);
        if (sign == 0f) sign = Mathf.Sign(target.mainBodyChunk.pos.x - scav.mainBodyChunk.pos.x);
        if (sign == 0f) sign = 1f;

        float horizontalAngle = sign > 0f ? 0f : 180f;
        float storedAngle = Custom.VecToDeg(stored.LanceDirection);
        float desiredAngle = Custom.VecToDeg(desired.normalized);
        float corrected = Mathf.MoveTowardsAngle(storedAngle, desiredAngle, ReleaseCorrectionDegrees);
        float offset = Mathf.DeltaAngle(horizontalAngle, corrected);
        corrected = horizontalAngle + Mathf.Clamp(offset, MinimumLancePitch, MaximumLancePitch);
        Vector2 direction = Custom.DegToVec(corrected).normalized;
        return new LanceAimSolution(true, aim, direction, stored.ImpactFrame, stored.Quality, chunk, stored.Exact);
    }

    private static float AimQuality(bool exact, float pitchDegrees, int frame, float bladeT, BodyChunk chunk,
        Vector2 targetCenter, Vector2 smoothVelocity, TargetMotionTracker motion)
    {
        float baseQuality = exact ? 0.83f : 0.66f;
        float centrality = 1f - Mathf.InverseLerp(4f, 38f, Vector2.Distance(chunk.pos, targetCenter));
        float stability = motion?.Stability(chunk) ?? 0.65f;
        float pitchPenalty = Mathf.Abs(pitchDegrees) / 15f * 0.10f;
        float framePenalty = (float)frame / LanceCombatState.MaxChargeFrames * 0.08f;
        float speedPenalty = Mathf.Clamp01(smoothVelocity.magnitude / 12f) * 0.05f;
        float shoulderPenalty = (1f - bladeT) * 0.04f;
        float quality = baseQuality + centrality * 0.08f + stability * 0.09f -
            pitchPenalty - framePenalty - speedPenalty - shoulderPenalty;
        return Mathf.Clamp01(quality);
    }

    private static Vector2 AverageBodyPosition(Creature target)
    {
        Vector2 sum = Vector2.zero;
        foreach (BodyChunk chunk in target.bodyChunks) sum += chunk.pos;
        return sum / Mathf.Max(1, target.bodyChunks.Length);
    }

    private static Vector2 Predict(Vector2 position, Vector2 velocity, int frames) =>
        position + Vector2.ClampMagnitude(velocity * Mathf.Max(0, frames), 65f);

    private static Vector2 GripPosition(Vector2 bodyPosition, Vector2 lanceDirection) =>
        bodyPosition + new Vector2(lanceDirection.x * 7f, -5f);

    private static void StepBody(ref Vector2 position, ref Vector2 velocity)
    {
        velocity.y = (velocity.y - ScavengerGravity) * ScavengerAirFriction;
        velocity.x *= ScavengerAirFriction;
        position += velocity;
    }
}
