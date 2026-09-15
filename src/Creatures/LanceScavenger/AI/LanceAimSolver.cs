using DryCycle.Items.ScavengerLance;
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
/// Analytic short-horizon charge solver. It solves launch height and lance pitch from a small set of
/// likely contact frames, then uses the real swept-blade geometry and final terrain validator only
/// as proof. It never searches height x pitch x every flight frame.
/// </summary>
internal static class LanceAimSolver
{
    internal const float MinimumAimQuality = 0.58f;
    internal const float MinimumLancePitch = -15f;
    internal const float MaximumLancePitch = 15f;
    internal const float ReleaseCorrectionDegrees = 6f;

    internal const float ActualBladeHitPadding = 1.5f;
    internal const float PlanningBladeHitPadding = 5.5f;

    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;
    private const float MaximumPredictedTargetTravel = 65f;
    private const float LaunchSolveSlack = 0.35f;
    private const float LizardHeadPenalty = 0.34f;
    private const int LocalFrameRadius = 2;
    private const int LocalPitchRadius = 2;

    // For frame n, vertical displacement is LaunchFactor[n] * launchY + GravityOffset[n].
    // Horizontal displacement is HorizontalFactor[n] * initialHorizontalSpeed. These tables make
    // solving a candidate O(1) instead of resimulating the whole arc for every hypothesis.
    private static readonly float[] VerticalLaunchFactor = new float[LanceCombatState.MaxChargeFrames + 1];
    private static readonly float[] VerticalGravityOffset = new float[LanceCombatState.MaxChargeFrames + 1];
    private static readonly float[] HorizontalFactor = new float[LanceCombatState.MaxChargeFrames + 1];

    static LanceAimSolver()
    {
        float velocityLaunchFactor = 1f;
        float velocityGravityOffset = 0f;
        float positionLaunchFactor = 0f;
        float positionGravityOffset = 0f;
        float horizontalVelocityFactor = 1f;
        float horizontalPositionFactor = 0f;

        for (int frame = 1; frame <= LanceCombatState.MaxChargeFrames; frame++)
        {
            velocityLaunchFactor *= ScavengerAirFriction;
            velocityGravityOffset = (velocityGravityOffset - ScavengerGravity) * ScavengerAirFriction;
            positionLaunchFactor += velocityLaunchFactor;
            positionGravityOffset += velocityGravityOffset;
            VerticalLaunchFactor[frame] = positionLaunchFactor;
            VerticalGravityOffset[frame] = positionGravityOffset;

            horizontalVelocityFactor *= ScavengerAirFriction;
            horizontalPositionFactor += horizontalVelocityFactor;
            HorizontalFactor[frame] = horizontalPositionFactor;
        }
    }

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
        float bladeRoot = LanceCombatMath.BladeRootDistance(length);
        float speed = ChargeLanePlanner.ChargeSpeed(scav);
        Vector2 targetCenter = AverageBodyPosition(target);

        SelectTargetChunks(target, targetCenter, motion, out BodyChunk firstChunk, out BodyChunk secondChunk);
        LanceAimSolution best = default;
        LanceAimSolution second = default;
        LanceAimSolution third = default;

        EvaluateChunk(firstChunk);
        if (secondChunk != null && secondChunk != firstChunk)
            EvaluateChunk(secondChunk);

        // Static terrain is deliberately the final veto. Usually only the best solution is checked;
        // two fallbacks are retained so a slightly lower-quality arc can win when the best ray clips
        // a ceiling or wall. If every candidate is blocked, return the theoretical best so the lane
        // planner can report the real hard-block reason instead of pretending there was no aim.
        if (best.Valid && ChargeLanePlanner.TerrainClear(scav, origin, target, best)) return best;
        if (second.Valid && ChargeLanePlanner.TerrainClear(scav, origin, target, second)) return second;
        if (third.Valid && ChargeLanePlanner.TerrainClear(scav, origin, target, third)) return third;
        return best;

        void EvaluateChunk(BodyChunk chunk)
        {
            if (chunk == null) return;
            Vector2 targetVelocity = motion?.SmoothedVelocity(chunk) ?? chunk.vel;
            int estimate = EstimateImpactFrame(origin, chunk, targetVelocity, horizontalSign,
                speed, bladeRoot, forwardLength);

            EvaluateFrame(estimate, chunk, targetVelocity);
            for (int radius = 1; radius <= LocalFrameRadius; radius++)
            {
                EvaluateFrame(estimate - radius, chunk, targetVelocity);
                EvaluateFrame(estimate + radius, chunk, targetVelocity);
            }
        }

        void EvaluateFrame(int frame, BodyChunk chunk, Vector2 targetVelocity)
        {
            if (frame < 1 || frame > LanceCombatState.MaxChargeFrames) return;

            Vector2 targetAtFrame = Predict(chunk.pos, targetVelocity, frame);
            if (!TrySolveBasePitch(origin, targetAtFrame, chunk.rad, horizontalSign, speed,
                    bladeRoot, forwardLength, frame, out float basePitch))
                return;

            EvaluatePitch(basePitch);
            for (int radius = 1; radius <= LocalPitchRadius; radius++)
            {
                EvaluatePitch(basePitch - radius);
                EvaluatePitch(basePitch + radius);
            }

            void EvaluatePitch(float requestedPitch)
            {
                float pitch = Mathf.Clamp(requestedPitch, MinimumLancePitch, MaximumLancePitch);
                if (!TrySolveLaunchForPitch(origin, targetAtFrame, chunk.rad, horizontalSign, speed,
                        bladeRoot, forwardLength, frame, pitch, out float launchY, out Vector2 direction))
                    return;

                Vector2 previousBody = BodyPosition(origin, horizontalSign, speed, launchY, frame - 1);
                Vector2 body = BodyPosition(origin, horizontalSign, speed, launchY, frame);
                Vector2 previousGrip = GripPosition(previousBody, direction);
                Vector2 grip = GripPosition(body, direction);
                Vector2 oldTarget = Predict(chunk.pos, targetVelocity, frame - 1);
                Vector2 newTarget = targetAtFrame;

                bool exact = LanceCombatMath.SweepBlade(previousGrip, direction, grip, direction,
                    forwardLength, oldTarget, newTarget, chunk.rad, ActualBladeHitPadding,
                    out float hitFraction, out float bladeT);
                bool probable = exact;
                if (!probable)
                    probable = LanceCombatMath.SweepBlade(previousGrip, direction, grip, direction,
                        forwardLength, oldTarget, newTarget, chunk.rad, PlanningBladeHitPadding,
                        out hitFraction, out bladeT);
                if (!probable) return;

                Vector2 aim = Vector2.Lerp(oldTarget, newTarget, hitFraction);
                float quality = AimQuality(exact, pitch, frame, launchY, bladeT, chunk,
                    targetCenter, targetVelocity, motion);
                InsertCandidate(new LanceAimSolution(true, aim, direction, frame, launchY,
                    quality, chunk, exact), ref best, ref second, ref third);
            }
        }
    }

    private static int EstimateImpactFrame(Vector2 origin, BodyChunk chunk, Vector2 targetVelocity,
        float horizontalSign, float speed, float bladeRoot, float forwardLength)
    {
        float distance = Mathf.Abs(chunk.pos.x - origin.x);
        float preferredBladeDistance = Mathf.Lerp(bladeRoot, forwardLength, 0.78f);
        float travel = Mathf.Max(0f, distance - 7f - preferredBladeDistance);
        float targetAlongCharge = horizontalSign * targetVelocity.x;
        float closingSpeed = Mathf.Clamp(speed - targetAlongCharge, 6f, 26f);
        return Mathf.Clamp(Mathf.RoundToInt(travel / closingSpeed), 1, LanceCombatState.MaxChargeFrames);
    }

    private static bool TrySolveBasePitch(Vector2 origin, Vector2 targetAtFrame, float targetRadius,
        float horizontalSign, float speed, float bladeRoot, float forwardLength, int frame, out float pitch)
    {
        pitch = 0f;
        float launchY = (ChargeLanePlanner.MinimumChargeLaunchY + ChargeLanePlanner.MaximumChargeLaunchY) * 0.5f;

        // Two fixed-point iterations are enough because pitch is limited to +/-15 degrees and the
        // horizontal body trajectory is independent of launch height.
        for (int iteration = 0; iteration < 2; iteration++)
        {
            Vector2 body = BodyPosition(origin, horizontalSign, speed, launchY, frame);
            Vector2 direction = Direction(horizontalSign, pitch);
            Vector2 grip = GripPosition(body, direction);
            Vector2 desired = targetAtFrame - grip;
            if (horizontalSign * desired.x <= 0f) return false;

            pitch = Mathf.Clamp(Mathf.Atan2(desired.y, Mathf.Abs(desired.x)) * Mathf.Rad2Deg,
                MinimumLancePitch, MaximumLancePitch);
            if (!TrySolveLaunchForPitch(origin, targetAtFrame, targetRadius, horizontalSign, speed,
                    bladeRoot, forwardLength, frame, pitch, out launchY, out _))
                return false;
        }
        return true;
    }

    private static bool TrySolveLaunchForPitch(Vector2 origin, Vector2 targetAtFrame, float targetRadius,
        float horizontalSign, float speed, float bladeRoot, float forwardLength, int frame,
        float pitchDegrees, out float launchY, out Vector2 direction)
    {
        direction = Direction(horizontalSign, pitchDegrees);
        float bodyX = origin.x + horizontalSign * speed * HorizontalFactor[frame];
        float gripX = bodyX + horizontalSign * 7f;
        float horizontalGap = horizontalSign * (targetAtFrame.x - gripX);
        float cos = Mathf.Max(0.05f, Mathf.Abs(direction.x));
        float radialDistance = horizontalGap / cos;
        float radialSlack = targetRadius + PlanningBladeHitPadding;
        if (radialDistance < bladeRoot - radialSlack || radialDistance > forwardLength + radialSlack)
        {
            launchY = 0f;
            return false;
        }

        // Use the actual horizontal intersection distance rather than a fixed tip/root anchor. This
        // places the predicted target on the same ray as the damaging blade and solves only the body
        // height required to support that ray.
        float bladeDistance = Mathf.Clamp(radialDistance, bladeRoot, forwardLength);
        float desiredBodyY = targetAtFrame.y + 5f - direction.y * bladeDistance;
        float factor = VerticalLaunchFactor[frame];
        if (factor < 0.0001f)
        {
            launchY = 0f;
            return false;
        }

        launchY = (desiredBodyY - origin.y - VerticalGravityOffset[frame]) / factor;
        if (launchY < ChargeLanePlanner.MinimumChargeLaunchY - LaunchSolveSlack ||
            launchY > ChargeLanePlanner.MaximumChargeLaunchY + LaunchSolveSlack)
            return false;
        launchY = ChargeLanePlanner.ClampChargeLaunchY(launchY);
        return true;
    }

    private static void SelectTargetChunks(Creature target, Vector2 center, TargetMotionTracker motion,
        out BodyChunk first, out BodyChunk second)
    {
        first = null;
        second = null;
        float firstScore = float.NegativeInfinity;
        float secondScore = float.NegativeInfinity;

        for (int i = 0; i < target.bodyChunks.Length; i++)
        {
            BodyChunk chunk = target.bodyChunks[i];
            if (chunk == null) continue;
            float centrality = 1f - Mathf.InverseLerp(8f, 70f, Vector2.Distance(chunk.pos, center));
            float stability = motion?.Stability(chunk) ?? 0.65f;
            float radius = Mathf.InverseLerp(2f, 16f, chunk.rad);
            float score = centrality * 0.70f + stability * 0.22f + radius * 0.08f;
            if (target is Lizard && i == 0) score -= LizardHeadPenalty;

            if (score > firstScore)
            {
                second = first;
                secondScore = firstScore;
                first = chunk;
                firstScore = score;
            }
            else if (score > secondScore)
            {
                second = chunk;
                secondScore = score;
            }
        }
    }

    private static void InsertCandidate(LanceAimSolution candidate, ref LanceAimSolution first,
        ref LanceAimSolution second, ref LanceAimSolution third)
    {
        if (!candidate.Valid) return;
        if (!first.Valid || candidate.Quality > first.Quality)
        {
            third = second;
            second = first;
            first = candidate;
        }
        else if (!second.Valid || candidate.Quality > second.Quality)
        {
            third = second;
            second = candidate;
        }
        else if (!third.Valid || candidate.Quality > third.Quality)
        {
            third = candidate;
        }
    }

    /// <summary>
    /// Builds the same launch trajectory used by the analytic solver. This helper is only consumed
    /// by opt-in diagnostics and therefore may allocate the returned array.
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
    /// A stored brace solution keeps its solved launch height. Release may turn only a few degrees
    /// toward the latest target lead; the exact release corridor is checked once immediately after.
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
        return Mathf.Clamp01(baseQuality + centrality * 0.08f + stability * 0.09f -
            pitchPenalty - framePenalty - speedPenalty - shoulderPenalty - heightPenalty);
    }

    private static Vector2 AverageBodyPosition(Creature target)
    {
        Vector2 sum = Vector2.zero;
        foreach (BodyChunk chunk in target.bodyChunks) sum += chunk.pos;
        return sum / Mathf.Max(1, target.bodyChunks.Length);
    }

    private static Vector2 Predict(Vector2 position, Vector2 velocity, int frames) =>
        position + Vector2.ClampMagnitude(velocity * Mathf.Max(0, frames), MaximumPredictedTargetTravel);

    private static Vector2 Direction(float horizontalSign, float pitchDegrees)
    {
        float radians = pitchDegrees * Mathf.Deg2Rad;
        return new Vector2(horizontalSign * Mathf.Cos(radians), Mathf.Sin(radians));
    }

    private static Vector2 BodyPosition(Vector2 origin, float horizontalSign, float speed,
        float launchY, int frame)
    {
        frame = Mathf.Clamp(frame, 0, LanceCombatState.MaxChargeFrames);
        return new Vector2(
            origin.x + horizontalSign * speed * HorizontalFactor[frame],
            origin.y + VerticalLaunchFactor[frame] * launchY + VerticalGravityOffset[frame]);
    }

    private static Vector2 GripPosition(Vector2 bodyPosition, Vector2 lanceDirection)
    {
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
