using DryCycle.Items.ScavengerLance;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal readonly struct LanceAimSolution
{
    internal LanceAimSolution(bool valid, Vector2 aim, Vector2 lanceDirection, int impactFrame, float launchY,
        float quality, BodyChunk targetChunk, bool exact)
    {
        Valid = valid;
        Aim = aim;
        LanceDirection = lanceDirection.sqrMagnitude > 0.001f ? lanceDirection.normalized : Vector2.right;
        ImpactFrame = impactFrame;
        LaunchY = launchY;
        Quality = Mathf.Clamp01(quality);
        TargetChunk = targetChunk;
        Exact = exact;
    }

    internal bool Valid { get; }
    internal bool Ready => Valid && Quality >= LanceAimSolver.MinimumAimQuality;
    internal Vector2 Aim { get; }
    internal Vector2 LanceDirection { get; }
    internal int ImpactFrame { get; }
    internal float LaunchY { get; }
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

    // Kept internal because the dedicated DevTool diagnostics visualize the two tolerances.
    // The real weapon still uses the same 1.5 px blade padding; 5.5 px is planning-only slack.
    internal const float ActualBladeHitPadding = 1.5f;
    internal const float PlanningBladeHitPadding = 5.5f;
    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;
    private const float MaximumPredictedTargetTravel = 65f;
    private const int CoarsePitchStep = 3;
    private const int FinePitchRadius = 2;

    internal static LanceAimSolution Solve(LanceScavenger scav, Vector2 origin, Creature target,
        TargetMotionTracker motion)
    {
        if (scav?.room == null || target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return default;

        float dx = target.mainBodyChunk.pos.x - origin.x;
        float horizontalSign = Mathf.Sign(dx);
        if (horizontalSign == 0f) return default;

        float horizontalDistance = Mathf.Abs(dx);
        if (horizontalDistance < ChargeLanePlanner.MinimumChargeDistance ||
            horizontalDistance > ChargeLanePlanner.MaximumChargeDistance(scav))
            return default;

        float length = scav.Lance?.Length ?? LanceCombatMath.DefaultLength;
        float forwardLength = LanceCombatMath.ForwardLength(length);
        Vector2 targetCenter = AverageBodyPosition(target);
        float speed = ChargeLanePlanner.ChargeSpeed(scav);
        SearchFrameRange(target, horizontalDistance, length, forwardLength, speed,
            out int firstSearchFrame, out int lastSearchFrame);

        LanceAimSolution best = default;

        // Adaptive jump height is still searched across the full 2.0..7.3 capability range, but the
        // old brute-force solver multiplied seven heights by every single degree, every charge frame,
        // and a full terrain-envelope test. One realized lancer could therefore execute well over
        // one hundred thousand tile lookups per AI tick. Search geometry first at 3-degree spacing,
        // restrict collision work to the only horizontal frames where contact can physically occur,
        // and validate terrain only for the best one/two candidates produced by each pitch.
        foreach (float launchY in ChargeLanePlanner.ChargeLaunchYCandidates)
        {
            EvaluatePitch(0f, launchY);
            for (int pitch = CoarsePitchStep; pitch <= 15; pitch += CoarsePitchStep)
            {
                EvaluatePitch(-pitch, launchY);
                EvaluatePitch(pitch, launchY);
            }
        }

        // Recover one-degree visual/aim precision around the winning coarse solution without going
        // back to a 31-angle x 7-height exhaustive search.
        if (best.Valid)
        {
            float winningPitch = Mathf.Atan2(best.LanceDirection.y,
                Mathf.Max(0.0001f, Mathf.Abs(best.LanceDirection.x))) * Mathf.Rad2Deg;
            float winningHeight = best.LaunchY;
            for (int offset = 1; offset <= FinePitchRadius; offset++)
            {
                EvaluatePitch(Mathf.Clamp(winningPitch - offset, MinimumLancePitch, MaximumLancePitch), winningHeight);
                EvaluatePitch(Mathf.Clamp(winningPitch + offset, MinimumLancePitch, MaximumLancePitch), winningHeight);
            }
        }

        return best;

        void EvaluatePitch(float pitchDegrees, float launchY)
        {
            float radians = pitchDegrees * Mathf.Deg2Rad;
            Vector2 direction = new(horizontalSign * Mathf.Cos(radians), Mathf.Sin(radians));
            Vector2 body = origin;
            Vector2 velocity = new(horizontalSign * speed, launchY);
            Vector2 previousGrip = GripPosition(body, direction);
            LanceAimSolution pitchBest = default;
            LanceAimSolution pitchSecond = default;

            for (int frame = 1; frame <= lastSearchFrame; frame++)
            {
                StepBody(ref body, ref velocity);
                Vector2 grip = GripPosition(body, direction);

                if (frame >= firstSearchFrame)
                {
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
                        float quality = AimQuality(exact, pitchDegrees, frame, launchY, bladeT, chunk,
                            targetCenter, smoothVelocity, motion);
                        LanceAimSolution candidate = new(true, aim, direction, frame, launchY, quality, chunk, exact);
                        InsertPitchCandidate(candidate, ref pitchBest, ref pitchSecond);
                    }
                }

                previousGrip = grip;
            }

            TryAccept(pitchBest);
            TryAccept(pitchSecond);
        }

        void TryAccept(LanceAimSolution candidate)
        {
            if (!candidate.Valid || (best.Valid && candidate.Quality <= best.Quality + 0.001f))
                return;
            if (!ChargeLanePlanner.TerrainClear(scav, origin, target, candidate))
                return;
            best = candidate;
        }
    }

    private static void InsertPitchCandidate(LanceAimSolution candidate,
        ref LanceAimSolution best, ref LanceAimSolution second)
    {
        if (!candidate.Valid) return;

        // Same pitch/height and same impact frame means the terrain path is identical even if a
        // different BodyChunk supplied the score. Keep only the higher-quality representative.
        if (best.Valid && best.ImpactFrame == candidate.ImpactFrame)
        {
            if (candidate.Quality > best.Quality) best = candidate;
            return;
        }
        if (second.Valid && second.ImpactFrame == candidate.ImpactFrame)
        {
            if (candidate.Quality > second.Quality) second = candidate;
            return;
        }

        if (!best.Valid || candidate.Quality > best.Quality)
        {
            second = best;
            best = candidate;
        }
        else if (!second.Valid || candidate.Quality > second.Quality)
        {
            second = candidate;
        }
    }

    private static void SearchFrameRange(Creature target, float horizontalDistance, float length,
        float forwardLength, float speed, out int firstFrame, out int lastFrame)
    {
        float largestRadius = 0f;
        foreach (BodyChunk chunk in target.bodyChunks)
            if (chunk != null) largestRadius = Mathf.Max(largestRadius, chunk.rad);

        // Predict() deliberately caps target lead at 65 px. Use the same bound to derive a safe
        // horizontal contact window. The window includes the whole damaging blade from root to tip,
        // target radius/planning slack, and one simulation frame on either side for discretization.
        float margin = largestRadius + PlanningBladeHitPadding + 4f;
        float bladeRoot = LanceCombatMath.BladeRootDistance(length);
        float earliestTravel = horizontalDistance - MaximumPredictedTargetTravel - forwardLength - margin;
        float latestTravel = horizontalDistance + MaximumPredictedTargetTravel - bladeRoot + margin;

        firstFrame = Mathf.Clamp(Mathf.FloorToInt(earliestTravel / Mathf.Max(1f, speed)) - 1,
            1, LanceCombatState.MaxChargeFrames);
        lastFrame = Mathf.Clamp(Mathf.CeilToInt(latestTravel / Mathf.Max(1f, speed)) + 1,
            firstFrame, LanceCombatState.MaxChargeFrames);
    }

    /// <summary>
    /// Builds the same launch trajectory used by the aim solver. This helper is only consumed by
    /// the opt-in debug presentation, so normal combat continues to use the allocation-free loop.
    /// The legacy overload keeps showing the old maximum-height arc unless the caller supplies the
    /// selected launch height explicitly.
    /// </summary>
    internal static Vector2[] BuildDebugBodyTrajectory(LanceScavenger scav, Vector2 origin,
        Vector2 lanceDirection, int frames) =>
        BuildDebugBodyTrajectory(scav, origin, lanceDirection, ChargeLanePlanner.MaximumChargeLaunchY, frames);

    internal static Vector2[] BuildDebugBodyTrajectory(LanceScavenger scav, Vector2 origin,
        Vector2 lanceDirection, float launchY, int frames)
    {
        if (scav == null || frames <= 0) return System.Array.Empty<Vector2>();
        float sign = Mathf.Sign(lanceDirection.x);
        if (sign == 0f) sign = 1f;
        Vector2 velocity = new(sign * ChargeLanePlanner.ChargeSpeed(scav),
            ChargeLanePlanner.ClampChargeLaunchY(launchY));
        return BuildDebugBodyTrajectory(origin, velocity, frames);
    }

    /// <summary>
    /// Continues the same ballistic model from a live velocity. During an already-active charge
    /// the debugger uses this overload instead of pretending the scavenger launches again.
    /// </summary>
    internal static Vector2[] BuildDebugBodyTrajectory(Vector2 origin, Vector2 velocity, int frames)
    {
        if (frames <= 0) return System.Array.Empty<Vector2>();
        frames = Mathf.Clamp(frames, 1, LanceCombatState.MaxChargeFrames);
        Vector2 body = origin;
        Vector2[] result = new Vector2[frames + 1];
        result[0] = body;
        for (int frame = 1; frame <= frames; frame++)
        {
            StepBody(ref body, ref velocity);
            result[frame] = body;
        }
        return result;
    }

    /// <summary>
    /// A stored brace solution keeps its commitment, including launch height, but release may turn
    /// a few degrees toward the target's current smoothed lead. This avoids using a stale early-brace
    /// angle without changing the ballistic arc that the planner already approved.
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

        // IMPORTANT: aim-solver pitch uses the normal mathematical convention where 0 degrees is
        // horizontal-right. RWCustom.Custom.VecToDeg/DegToVec use Rain World's sprite convention
        // where 0 degrees points upward. Mixing those conventions turned a valid -15..15 degree
        // release into an almost vertical ~75 degree lance, which immediately hit terrain and put
        // the scavenger into wall recovery instead of performing the charge.
        float horizontalAngle = sign > 0f ? 0f : 180f;
        float storedAngle = Mathf.Atan2(stored.LanceDirection.y, stored.LanceDirection.x) * Mathf.Rad2Deg;
        float desiredAngle = Mathf.Atan2(desired.y, desired.x) * Mathf.Rad2Deg;
        float corrected = Mathf.MoveTowardsAngle(storedAngle, desiredAngle, ReleaseCorrectionDegrees);
        float offset = Mathf.DeltaAngle(horizontalAngle, corrected);
        corrected = horizontalAngle + Mathf.Clamp(offset, MinimumLancePitch, MaximumLancePitch);
        float radians = corrected * Mathf.Deg2Rad;
        Vector2 direction = new(Mathf.Cos(radians), Mathf.Sin(radians));
        return new LanceAimSolution(true, aim, direction, stored.ImpactFrame, stored.LaunchY,
            stored.Quality, chunk, stored.Exact);
    }

    private static float AimQuality(bool exact, float pitchDegrees, int frame, float launchY, float bladeT,
        BodyChunk chunk, Vector2 targetCenter, Vector2 smoothVelocity, TargetMotionTracker motion)
    {
        float baseQuality = exact ? 0.83f : 0.66f;
        float centrality = 1f - Mathf.InverseLerp(4f, 38f, Vector2.Distance(chunk.pos, targetCenter));
        float stability = motion?.Stability(chunk) ?? 0.65f;
        float pitchPenalty = Mathf.Abs(pitchDegrees) / 15f * 0.10f;
        float framePenalty = (float)frame / LanceCombatState.MaxChargeFrames * 0.08f;
        float speedPenalty = Mathf.Clamp01(smoothVelocity.magnitude / 12f) * 0.05f;
        float shoulderPenalty = (1f - bladeT) * 0.04f;
        float heightPenalty = Mathf.InverseLerp(ChargeLanePlanner.MinimumChargeLaunchY,
            ChargeLanePlanner.MaximumChargeLaunchY, launchY) * 0.06f;
        float quality = baseQuality + centrality * 0.08f + stability * 0.09f -
            pitchPenalty - framePenalty - speedPenalty - shoulderPenalty - heightPenalty;
        return Mathf.Clamp01(quality);
    }

    private static Vector2 AverageBodyPosition(Creature target)
    {
        Vector2 sum = Vector2.zero;
        foreach (BodyChunk chunk in target.bodyChunks) sum += chunk.pos;
        return sum / Mathf.Max(1, target.bodyChunks.Length);
    }

    private static Vector2 Predict(Vector2 position, Vector2 velocity, int frames) =>
        position + Vector2.ClampMagnitude(velocity * Mathf.Max(0, frames), MaximumPredictedTargetTravel);

    private static Vector2 GripPosition(Vector2 bodyPosition, Vector2 lanceDirection)
    {
        // Planning uses the same fixed hand pivot as the live weapon. Pitch changes rotate the lance
        // around the hand; they do not slide the hand left/right by cos(pitch), and a later counter-
        // sweep can rotate far beyond the launch pitch without invalidating the launch geometry.
        float face = Mathf.Sign(lanceDirection.x);
        if (face == 0f) face = 1f;
        return bodyPosition + new Vector2(face * 7f, -5f);
    }

    private static void StepBody(ref Vector2 position, ref Vector2 velocity)
    {
        velocity.y = (velocity.y - ScavengerGravity) * ScavengerAirFriction;
        velocity.x *= ScavengerAirFriction;
        position += velocity;
    }
}
