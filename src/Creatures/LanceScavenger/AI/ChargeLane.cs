using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal readonly struct ChargeLane
{
    internal ChargeLane(bool clear, Vector2 aim, string reason)
        : this(clear, clear, aim, Vector2.right, 0, reason) { }

    internal ChargeLane(bool pathClear, bool canHit, Vector2 aim, Vector2 lanceDirection, int impactFrame, string reason)
    {
        PathClear = pathClear;
        CanHit = canHit;
        Aim = aim;
        LanceDirection = lanceDirection.sqrMagnitude > 0.001f ? lanceDirection.normalized : Vector2.right;
        ImpactFrame = impactFrame;
        Reason = reason;
    }

    /// <summary>True only when both the physical lane and a predicted lance-tip hit are valid.</summary>
    internal bool Clear => PathClear && CanHit;
    internal bool PathClear { get; }
    internal bool CanHit { get; }
    internal Vector2 Aim { get; }
    internal Vector2 LanceDirection { get; }
    internal int ImpactFrame { get; }
    internal string Reason { get; }
}

internal static class ChargeLanePlanner
{
    internal const float MinimumChargeDistance = 60f;   // 3 tiles
    internal const float MinimumMaximumChargeDistance = 300f; // 15 tiles
    internal const float MaximumMaximumChargeDistance = 500f; // 25 tiles
    internal const float MinimumLancePitch = -15f;
    internal const float MaximumLancePitch = 15f;

    // Keep the solver matched to the actual launch motor and vanilla scavenger physics.
    internal const float ChargeLaunchY = 7.3f;
    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;
    private const float PredictionTargetTravelLimit = 90f;
    private const float TipHitPadding = 2f;

    internal static float ChargeCommitment(LanceScavenger scav)
    {
        AbstractCreature.Personality personality = scav.abstractCreature.personality;
        return Mathf.Clamp01(personality.bravery * 0.45f + personality.aggression * 0.35f + personality.energy * 0.20f);
    }

    internal static float MaximumChargeDistance(LanceScavenger scav) =>
        Mathf.Lerp(MinimumMaximumChargeDistance, MaximumMaximumChargeDistance, ChargeCommitment(scav));

    internal static float ChargeSpeed(LanceScavenger scav) =>
        Mathf.Lerp(17f, 19.5f, scav.abstractCreature.personality.energy);

    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target)
    {
        if (scav?.room == null || target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return new ChargeLane(false, false, origin, Vector2.right, 0, "no target");

        Vector2 targetPos = target.mainBodyChunk.pos;
        float dx = targetPos.x - origin.x;
        float distance = Mathf.Abs(dx);
        if (distance < MinimumChargeDistance || distance > MaximumChargeDistance(scav))
            return new ChargeLane(false, false, targetPos, HorizontalDirection(dx), 0, "distance");

        float horizontalSign = Mathf.Sign(dx);
        if (horizontalSign == 0f)
            return new ChargeLane(false, false, targetPos, Vector2.right, 0, "distance");

        if (!TrySolveHit(scav, origin, target, horizontalSign, out Vector2 lanceDirection,
                out Vector2 aim, out int impactFrame))
            return new ChargeLane(true, false, targetPos, HorizontalDirection(dx), 0, "no ballistic hit");

        string block = TrajectoryBlock(scav, origin, horizontalSign, ChargeSpeed(scav), impactFrame, lanceDirection, target);
        if (block != null)
            return new ChargeLane(false, true, aim, lanceDirection, impactFrame, block);

        return new ChargeLane(true, true, aim, lanceDirection, impactFrame, "clear hit");
    }

    private static bool TrySolveHit(LanceScavenger scav, Vector2 origin, Creature target, float horizontalSign,
        out Vector2 bestDirection, out Vector2 bestAim, out int bestFrame)
    {
        bestDirection = new Vector2(horizontalSign, 0f);
        bestAim = target.mainBodyChunk.pos;
        bestFrame = 0;

        // Prefer the smallest required correction from horizontal. A high-arcing charge
        // therefore only depresses/elevates the lance as much as the hit actually needs.
        for (int pitchMagnitude = 0; pitchMagnitude <= 15; pitchMagnitude++)
        {
            if (TryPitch(pitchMagnitude == 0 ? 0f : -pitchMagnitude, out bestDirection, out bestAim, out bestFrame))
                return true;
            if (pitchMagnitude > 0 && TryPitch(pitchMagnitude, out bestDirection, out bestAim, out bestFrame))
                return true;
        }
        return false;

        bool TryPitch(float pitchDegrees, out Vector2 direction, out Vector2 aim, out int impactFrame)
        {
            float radians = pitchDegrees * Mathf.Deg2Rad;
            direction = new Vector2(horizontalSign * Mathf.Cos(radians), Mathf.Sin(radians)).normalized;
            aim = target.mainBodyChunk.pos;
            impactFrame = 0;

            float forwardLength = (scav.Lance?.Length ?? LanceCombatMath.DefaultLength) * (1f - LanceCombatMath.GripFraction);
            Vector2 body = origin;
            Vector2 velocity = new(horizontalSign * ChargeSpeed(scav), ChargeLaunchY);
            Vector2 previousTip = TipPosition(body, direction, forwardLength);

            for (int frame = 1; frame <= LanceCombatState.MaxChargeFrames; frame++)
            {
                StepBody(ref body, ref velocity);
                Vector2 tip = TipPosition(body, direction, forwardLength);

                foreach (BodyChunk chunk in target.bodyChunks)
                {
                    Vector2 oldTarget = PredictedTargetPosition(chunk, frame - 1);
                    Vector2 newTarget = PredictedTargetPosition(chunk, frame);
                    if (!LanceCombatMath.SweepTip(previousTip, tip, oldTarget, newTarget,
                            chunk.rad + TipHitPadding, out float fraction))
                        continue;

                    impactFrame = frame;
                    aim = Vector2.Lerp(oldTarget, newTarget, fraction);
                    return true;
                }
                previousTip = tip;
            }
            return false;
        }
    }

    private static string TrajectoryBlock(LanceScavenger scav, Vector2 origin, float horizontalSign, float speed,
        int impactFrame, Vector2 lanceDirection, Creature target)
    {
        Room room = scav.room;
        Vector2 body = origin;
        Vector2 velocity = new(horizontalSign * speed, ChargeLaunchY);
        float length = scav.Lance?.Length ?? LanceCombatMath.DefaultLength;
        float forwardLength = length * (1f - LanceCombatMath.GripFraction);
        float rearLength = length * LanceCombatMath.GripFraction;
        int frames = Mathf.Min(LanceCombatState.MaxChargeFrames, Mathf.Max(impactFrame + 3, 1));
        Vector2 previousBody = body;

        for (int frame = 1; frame <= frames; frame++)
        {
            StepBody(ref body, ref velocity);
            if (BodyBlocked(room, body)) return "wall / ceiling";

            Vector2 grip = GripPosition(body, lanceDirection);
            Vector2 tip = grip + lanceDirection * forwardLength;
            Vector2 tail = grip - lanceDirection * rearLength;
            if (room.GetTile(tip).Solid || room.GetTile(tail).Solid)
                return "lance blocked";

            if (FriendInPath(scav, previousBody, body + new Vector2(horizontalSign * 24f, 0f), target))
                return "friend in lane";
            previousBody = body;
        }
        return null;
    }

    private static bool BodyBlocked(Room room, Vector2 at) =>
        room.GetTile(at).Solid || room.GetTile(at + Vector2.up * 14f).Solid || room.GetTile(at - Vector2.up * 8f).Solid;

    private static void StepBody(ref Vector2 position, ref Vector2 velocity)
    {
        // Match the vanilla scavenger's gravity/air-friction integration closely enough
        // for aiming. The solution is refreshed every brace frame and once more at launch.
        velocity.y = (velocity.y - ScavengerGravity) * ScavengerAirFriction;
        velocity.x *= ScavengerAirFriction;
        position += velocity;
    }

    private static Vector2 PredictedTargetPosition(BodyChunk chunk, int frame) =>
        chunk.pos + Vector2.ClampMagnitude(chunk.vel * frame, PredictionTargetTravelLimit);

    private static Vector2 GripPosition(Vector2 bodyPosition, Vector2 lanceDirection) =>
        bodyPosition + new Vector2(lanceDirection.x * 7f, -5f);

    private static Vector2 TipPosition(Vector2 bodyPosition, Vector2 lanceDirection, float forwardLength) =>
        GripPosition(bodyPosition, lanceDirection) + lanceDirection * forwardLength;

    private static Vector2 HorizontalDirection(float dx) => new(Mathf.Sign(dx) == 0f ? 1f : Mathf.Sign(dx), 0f);

    internal static string CorridorBlock(LanceScavenger scav, Vector2 origin, Vector2 end, Creature target)
    {
        Room room = scav.room;
        int count = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(origin, end) / 9f));
        for (int i = 0; i <= count; i++)
        {
            Vector2 at = Vector2.Lerp(origin, end, (float)i / count);
            if (room.GetTile(at).Solid || room.GetTile(at + Vector2.up * 14f).Solid || room.GetTile(at - Vector2.up * 8f).Solid)
                return "wall / ceiling";
        }
        return FriendInPath(scav, origin, end, target) ? "friend in lane" : null;
    }

    internal static bool FriendInPath(LanceScavenger scav, Vector2 origin, Vector2 end, Creature target)
    {
        foreach (AbstractCreature abstractOther in scav.room.abstractRoom.creatures)
        {
            Creature other = abstractOther.realizedCreature;
            if (other == null || other == scav || other == target || other.dead || other.room != scav.room) continue;
            if (!IsFriend(scav, other)) continue;
            foreach (BodyChunk chunk in other.bodyChunks)
                if ((chunk.pos - LanceCombatMath.ClosestPoint(origin, end, chunk.pos)).sqrMagnitude <
                    (chunk.rad + 21f) * (chunk.rad + 21f)) return true;
        }
        return false;
    }

    private static bool IsFriend(LanceScavenger scav, Creature other)
    {
        if (other is Scavenger) return true;
        Tracker.CreatureRepresentation rep = scav.AI.tracker.RepresentationForCreature(other.abstractCreature, false);
        return rep?.dynamicRelationship?.currentRelationship.type == CreatureTemplate.Relationship.Type.Pack;
    }

    internal static bool FindStagingPosition(LanceScavenger scav, Creature target, out WorldCoordinate destination)
    {
        destination = scav.abstractCreature.pos;
        float best = float.MaxValue;
        Vector2 origin = scav.mainBodyChunk.pos;
        float commitment = ChargeCommitment(scav);
        float preferredDistance = Mathf.Lerp(120f, 260f, commitment);
        int maximumStagingDistance = Mathf.FloorToInt(Mathf.Min(MaximumChargeDistance(scav) - 20f, 300f) / 20f) * 20;
        for (int side = -1; side <= 1; side += 2)
            for (int distance = 80; distance <= maximumStagingDistance; distance += 40)
                for (int height = -20; height <= 20; height += 20)
                {
                    Vector2 candidate = target.mainBodyChunk.pos + new Vector2(side * distance, height);
                    WorldCoordinate coordinate = scav.room.GetWorldCoordinate(candidate);
                    candidate = scav.room.MiddleOfTile(coordinate);
                    if (!scav.AI.pathFinder.CoordinateViable(coordinate) ||
                        !Evaluate(scav, candidate, target).Clear) continue;
                    float score = Vector2.Distance(origin, candidate) + Mathf.Abs(height) * 2f +
                        Mathf.Abs(distance - preferredDistance) * 0.4f;
                    if (score >= best) continue;
                    best = score;
                    destination = coordinate;
                }
        return best < float.MaxValue;
    }
}
