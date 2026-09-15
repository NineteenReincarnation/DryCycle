using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>
/// Combined view consumed by the existing combat state machine. Aim quality comes from
/// LanceAimSolver; PathClear and Reason come from the hard route planner.
/// </summary>
internal readonly struct ChargeLane
{
    internal ChargeLane(bool clear, Vector2 aim, string reason)
        : this(clear, clear, aim, Vector2.right, 0, clear ? 1f : 0f, reason) { }

    internal ChargeLane(bool pathClear, bool canHit, Vector2 aim, Vector2 lanceDirection, int impactFrame,
        float confidence, string reason)
    {
        PathClear = pathClear;
        CanHit = canHit;
        Aim = aim;
        LanceDirection = lanceDirection.sqrMagnitude > 0.001f ? lanceDirection.normalized : Vector2.right;
        ImpactFrame = impactFrame;
        Confidence = Mathf.Clamp01(confidence);
        Reason = reason;
    }

    internal bool Clear => PathClear && CanHit && Confidence >= LanceAimSolver.MinimumAimQuality;
    internal bool PathClear { get; }
    internal bool CanHit { get; }
    internal Vector2 Aim { get; }
    internal Vector2 LanceDirection { get; }
    internal int ImpactFrame { get; }
    internal float Confidence { get; }
    internal string Reason { get; }
}

/// <summary>
/// Hard route safety only. Target lead, BodyChunk selection and aim confidence intentionally
/// live in LanceAimSolver so temporary prediction noise does not masquerade as terrain failure.
///
/// Important: route validation only proves the committed attack corridor up to the expected
/// contact. It deliberately does not demand a perfectly safe landing after a miss. Crashing into
/// terrain after the target dodges is a physical consequence of committing the charge, not a reason
/// to make the signature attack unavailable in the first place.
/// </summary>
internal static class ChargeLanePlanner
{
    internal const float MinimumChargeDistance = 60f;
    internal const float MinimumMaximumChargeDistance = 300f;
    internal const float MaximumMaximumChargeDistance = 500f;

    // The original 7.3 vertical launch remains the absolute capability ceiling. The aim solver is
    // allowed to choose a lower arc, but never a stronger jump than the old implementation.
    internal const float MinimumChargeLaunchY = 2f;
    internal const float MaximumChargeLaunchY = 7.3f;
    internal const float ChargeLaunchY = MaximumChargeLaunchY; // compatibility for diagnostics/old callers
    internal static readonly float[] ChargeLaunchYCandidates = { 2f, 3f, 4f, 5f, 6f, 7f, 7.3f };

    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;

    internal static float ClampChargeLaunchY(float launchY) =>
        Mathf.Clamp(launchY, MinimumChargeLaunchY, MaximumChargeLaunchY);

    internal static float ChargeCommitment(LanceScavenger scav)
    {
        AbstractCreature.Personality personality = scav.abstractCreature.personality;
        return Mathf.Clamp01(personality.bravery * 0.45f + personality.aggression * 0.35f + personality.energy * 0.20f);
    }

    internal static float MaximumChargeDistance(LanceScavenger scav) =>
        Mathf.Lerp(MinimumMaximumChargeDistance, MaximumMaximumChargeDistance, ChargeCommitment(scav));

    internal static float ChargeSpeed(LanceScavenger scav) =>
        Mathf.Lerp(17f, 19.5f, scav.abstractCreature.personality.energy);

    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target, LanceAimSolution aim)
    {
        if (scav?.room == null || target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return new ChargeLane(false, false, origin, Vector2.right, 0, 0f, "no target");

        Vector2 targetPos = target.mainBodyChunk.pos;
        float dx = targetPos.x - origin.x;
        float distance = Mathf.Abs(dx);
        Vector2 horizontal = HorizontalDirection(dx);
        if (distance < MinimumChargeDistance || distance > MaximumChargeDistance(scav) || Mathf.Sign(dx) == 0f)
            return new ChargeLane(false, aim.Valid, aim.Valid ? aim.Aim : targetPos,
                aim.Valid ? aim.LanceDirection : horizontal, aim.ImpactFrame, aim.Quality, "distance");

        Vector2 lanceDirection = aim.Valid ? aim.LanceDirection : horizontal;
        float launchY = aim.Valid ? ClampChargeLaunchY(aim.LaunchY) : MaximumChargeLaunchY;
        int impactFrame = aim.Valid && aim.ImpactFrame > 0
            ? aim.ImpactFrame
            : Mathf.Clamp(Mathf.RoundToInt(distance / Mathf.Max(1f, ChargeSpeed(scav))), 1, LanceCombatState.MaxChargeFrames);

        string block = TrajectoryBlock(scav, origin, Mathf.Sign(dx), ChargeSpeed(scav), launchY, impactFrame,
            lanceDirection, target, checkFriends: true);
        bool pathClear = block == null;
        string reason = block ?? (aim.Ready ? "clear" : aim.Valid ? "aim low" : "no aim opportunity");
        return new ChargeLane(pathClear, aim.Valid, aim.Valid ? aim.Aim : targetPos,
            lanceDirection, impactFrame, aim.Quality, reason);
    }

    // Compatibility overload for diagnostics/tests that do not own a motion tracker.
    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target)
    {
        LanceAimSolution aim = LanceAimSolver.Solve(scav, origin, target, null);
        return Evaluate(scav, origin, target, aim);
    }

    private static string TrajectoryBlock(LanceScavenger scav, Vector2 origin, float horizontalSign, float speed,
        float launchY, int impactFrame, Vector2 lanceDirection, Creature target, bool checkFriends)
    {
        Room room = scav.room;
        Vector2 body = origin;
        Vector2 velocity = new(horizontalSign * speed, ClampChargeLaunchY(launchY));

        // Validate the corridor through the expected contact, plus one frame for discretization.
        // The previous landing-length validation simulated far beyond the target with a permanently
        // extended wide blade. On ordinary ground that made the blade intersect the floor during the
        // predicted descent, so valid charges were reported as blocked. Post-contact terrain remains
        // a physical consequence handled by the live charge instead of a pre-launch prohibition.
        int frames = Mathf.Clamp(impactFrame + 1, 1, LanceCombatState.MaxChargeFrames);
        Vector2 previousBody = body;

        for (int frame = 1; frame <= frames; frame++)
        {
            StepBody(ref body, ref velocity);
            if (PoseBlocked(scav, body, lanceDirection))
                return BodyBlocked(room, body) ? "wall / ceiling" : "lance blocked";

            if (checkFriends && FriendInPath(scav, previousBody,
                    body + new Vector2(horizontalSign * 24f, 0f), target))
                return "friend in lane";
            previousBody = body;
        }
        return null;
    }

    /// <summary>
    /// Shared hard geometry test used by both the height-aware aim solver and the final route pass.
    /// Keeping this in one place guarantees that a low/high arc selected by aiming is tested against
    /// the same body, tail and blade envelope that can later veto the committed launch.
    /// </summary>
    internal static bool PoseBlocked(LanceScavenger scav, Vector2 body, Vector2 lanceDirection)
    {
        if (scav?.room == null) return true;
        Room room = scav.room;
        if (BodyBlocked(room, body)) return true;

        Vector2 direction = lanceDirection.sqrMagnitude > 0.001f ? lanceDirection.normalized : Vector2.right;
        float length = scav.Lance?.Length ?? LanceCombatMath.DefaultLength;
        float forwardLength = LanceCombatMath.ForwardLength(length);
        float rearLength = length * LanceCombatMath.GripFraction;
        Vector2 grip = GripPosition(body, direction);
        Vector2 tail = grip - direction * rearLength;
        return room.GetTile(tail).Solid || BladeBlocked(room, grip, direction, forwardLength);
    }

    private static bool BladeBlocked(Room room, Vector2 grip, Vector2 direction, float forwardLength)
    {
        Vector2 perp = new(-direction.y, direction.x);
        for (int i = 0; i < 6; i++)
        {
            float t = i / 5f;
            Vector2 center = LanceCombatMath.BladePoint(grip, direction, forwardLength, t);
            float width = LanceCombatMath.BladeHalfWidth(t);
            if (room.GetTile(center).Solid || room.GetTile(center + perp * width).Solid ||
                room.GetTile(center - perp * width).Solid)
                return true;
        }
        return false;
    }

    private static bool BodyBlocked(Room room, Vector2 at) =>
        room.GetTile(at).Solid || room.GetTile(at + Vector2.up * 14f).Solid || room.GetTile(at - Vector2.up * 8f).Solid;

    private static void StepBody(ref Vector2 position, ref Vector2 velocity)
    {
        velocity.y = (velocity.y - ScavengerGravity) * ScavengerAirFriction;
        velocity.x *= ScavengerAirFriction;
        position += velocity;
    }

    private static Vector2 GripPosition(Vector2 bodyPosition, Vector2 lanceDirection)
    {
        float face = Mathf.Sign(lanceDirection.x);
        if (face == 0f) face = 1f;
        return bodyPosition + new Vector2(face * 7f, -5f);
    }

    private static Vector2 HorizontalDirection(float dx) =>
        new(Mathf.Sign(dx) == 0f ? 1f : Mathf.Sign(dx), 0f);

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
        if (scav?.room?.abstractRoom?.creatures == null) return false;
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

    /// <summary>
    /// Navigation seeks a broadly useful launch area, not a pre-proven hit. Temporary target
    /// animation and friendly occupancy must not make both lance scavengers endlessly swap sides.
    /// A staging point is acceptable when at least one permitted launch height has a clear terrain
    /// corridor; the final aim solver will later choose the height that actually hits the target.
    /// </summary>
    internal static bool FindStagingPosition(LanceScavenger scav, Creature target, out WorldCoordinate destination)
    {
        destination = scav.abstractCreature.pos;
        if (scav?.room == null || target?.mainBodyChunk == null) return false;

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
                    if (!scav.AI.pathFinder.CoordinateViable(coordinate)) continue;
                    candidate = scav.room.MiddleOfTile(coordinate);

                    float dx = target.mainBodyChunk.pos.x - candidate.x;
                    float horizontalDistance = Mathf.Abs(dx);
                    if (horizontalDistance < MinimumChargeDistance || horizontalDistance > MaximumChargeDistance(scav))
                        continue;

                    Vector2 direction = HorizontalDirection(dx);
                    int estimate = Mathf.Clamp(Mathf.RoundToInt(horizontalDistance / Mathf.Max(1f, ChargeSpeed(scav))),
                        1, LanceCombatState.MaxChargeFrames);

                    bool terrainRoute = false;
                    foreach (float launchY in ChargeLaunchYCandidates)
                    {
                        string block = TrajectoryBlock(scav, candidate, Mathf.Sign(dx), ChargeSpeed(scav), launchY,
                            estimate, direction, target, checkFriends: false);
                        if (block == null)
                        {
                            terrainRoute = true;
                            break;
                        }
                    }
                    if (!terrainRoute) continue;

                    float score = Vector2.Distance(origin, candidate) + Mathf.Abs(height) * 1.4f +
                        Mathf.Abs(distance - preferredDistance) * 0.4f;
                    if (score >= best) continue;
                    best = score;
                    destination = coordinate;
                }
        return best < float.MaxValue;
    }
}
