using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>
/// Combined view consumed by the combat state machine. Aim quality comes from LanceAimSolver;
/// PathClear/Reason come from the hard route planner.
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
/// Hard route safety. Static terrain proof is separable from dynamic friendly occupancy so an AI can
/// cache the expensive ballistic corridor while still reacting to creatures entering the lane every
/// tick. Post-contact terrain is intentionally not validated; a miss may still crash physically.
/// </summary>
internal static class ChargeLanePlanner
{
    internal const float MinimumChargeDistance = 60f;
    internal const float MinimumMaximumChargeDistance = 300f;
    internal const float MaximumMaximumChargeDistance = 500f;
    internal const float MinimumChargeLaunchY = 2f;
    internal const float MaximumChargeLaunchY = 7.3f;
    internal const float ChargeLaunchY = MaximumChargeLaunchY; // compatibility for diagnostics/old callers

    // Retained for compatibility with tests/older debug consumers. Runtime aiming no longer searches
    // these discrete heights; LanceAimSolver produces a continuous solved launch height.
    internal static readonly float[] ChargeLaunchYCandidates = { 2f, 3f, 4f, 5f, 6f, 7f, 7.3f };

    private static readonly int[] StagingDistances = { 120, 180, 240 };
    private static readonly int[] StagingHeights = { 0, -20, 20 };

    private const float ScavengerGravity = 0.9f;
    private const float ScavengerAirFriction = 0.999f;
    private const float DynamicFriendCorridorPadding = 40f;

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

    /// <summary>
    /// One-shot authoritative validation used for release and compatibility callers. Static geometry
    /// is proved first, then friendly occupancy is checked along the actual ballistic segments.
    /// </summary>
    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target, LanceAimSolution aim)
    {
        ChargeLane staticLane = EvaluateStatic(scav, origin, target, aim);
        if (!staticLane.PathClear || !aim.Valid || scav?.room == null || target == null)
            return staticLane;
        return FriendInTrajectory(scav, origin, target, aim)
            ? WithReason(staticLane, false, "friend in lane")
            : staticLane;
    }

    /// <summary>
    /// Expensive but cacheable part of planning: distance + body/tail/blade terrain corridor only.
    /// No creature occupancy is consulted here.
    /// </summary>
    internal static ChargeLane EvaluateStatic(LanceScavenger scav, Vector2 origin, Creature target, LanceAimSolution aim)
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
            : Mathf.Clamp(Mathf.RoundToInt(distance / Mathf.Max(1f, ChargeSpeed(scav))),
                1, LanceCombatState.MaxChargeFrames);

        string block = TrajectoryBlock(scav, origin, Mathf.Sign(dx), ChargeSpeed(scav), launchY, impactFrame,
            lanceDirection, checkFriends: false, target: target);
        bool pathClear = block == null;
        string reason = block ?? (aim.Ready ? "clear" : aim.Valid ? "aim low" : "no aim opportunity");
        return new ChargeLane(pathClear, aim.Valid, aim.Valid ? aim.Aim : targetPos,
            lanceDirection, impactFrame, aim.Quality, reason);
    }

    /// <summary>
    /// Cheap live safety layer for a cached static lane. It uses one conservative swept corridor
    /// around the target/aim segment instead of repeating tile queries or the full ballistic proof.
    /// The exact ballistic friend check is still repeated on the release frame by Evaluate().
    /// </summary>
    internal static ChargeLane ApplyDynamicSafety(LanceScavenger scav, Vector2 origin, Creature target,
        LanceAimSolution aim, ChargeLane staticLane)
    {
        if (!staticLane.PathClear || scav?.room == null || target?.mainBodyChunk == null || !aim.Valid)
            return staticLane;

        Vector2 end = aim.Aim;
        if (FriendInCorridor(scav, origin, end, target, DynamicFriendCorridorPadding))
            return WithReason(staticLane, false, "friend in lane");
        return staticLane;
    }

    internal static bool TerrainClear(LanceScavenger scav, Vector2 origin, Creature target, LanceAimSolution aim)
    {
        if (!aim.Valid || scav?.room == null || target?.mainBodyChunk == null)
            return false;

        float dx = target.mainBodyChunk.pos.x - origin.x;
        if (Mathf.Sign(dx) == 0f) return false;
        return TrajectoryBlock(scav, origin, Mathf.Sign(dx), ChargeSpeed(scav),
            ClampChargeLaunchY(aim.LaunchY), Mathf.Max(1, aim.ImpactFrame), aim.LanceDirection,
            checkFriends: false, target: target) == null;
    }

    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target)
    {
        LanceAimSolution aim = LanceAimSolver.Solve(scav, origin, target, null);
        return Evaluate(scav, origin, target, aim);
    }

    private static ChargeLane WithReason(ChargeLane lane, bool pathClear, string reason) =>
        new(pathClear, lane.CanHit, lane.Aim, lane.LanceDirection, lane.ImpactFrame, lane.Confidence, reason);

    private static string TrajectoryBlock(LanceScavenger scav, Vector2 origin, float horizontalSign, float speed,
        float launchY, int impactFrame, Vector2 lanceDirection, bool checkFriends, Creature target)
    {
        Room room = scav.room;
        Vector2 body = origin;
        Vector2 velocity = new(horizontalSign * speed, ClampChargeLaunchY(launchY));
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

    private static bool FriendInTrajectory(LanceScavenger scav, Vector2 origin, Creature target, LanceAimSolution aim)
    {
        float sign = Mathf.Sign(aim.LanceDirection.x);
        if (sign == 0f) sign = Mathf.Sign(target.mainBodyChunk.pos.x - origin.x);
        if (sign == 0f) return false;

        Vector2 body = origin;
        Vector2 previousBody = body;
        Vector2 velocity = new(sign * ChargeSpeed(scav), ClampChargeLaunchY(aim.LaunchY));
        int frames = Mathf.Clamp(aim.ImpactFrame + 1, 1, LanceCombatState.MaxChargeFrames);
        for (int frame = 1; frame <= frames; frame++)
        {
            StepBody(ref body, ref velocity);
            if (FriendInPath(scav, previousBody, body + new Vector2(sign * 24f, 0f), target))
                return true;
            previousBody = body;
        }
        return false;
    }

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
            if (room.GetTile(at).Solid || room.GetTile(at + Vector2.up * 14f).Solid ||
                room.GetTile(at - Vector2.up * 8f).Solid)
                return "wall / ceiling";
        }
        return FriendInPath(scav, origin, end, target) ? "friend in lane" : null;
    }

    internal static bool FriendInPath(LanceScavenger scav, Vector2 origin, Vector2 end, Creature target) =>
        FriendInCorridor(scav, origin, end, target, 21f);

    private static bool FriendInCorridor(LanceScavenger scav, Vector2 origin, Vector2 end, Creature target, float padding)
    {
        if (scav?.room?.abstractRoom?.creatures == null) return false;
        foreach (AbstractCreature abstractOther in scav.room.abstractRoom.creatures)
        {
            Creature other = abstractOther.realizedCreature;
            if (other == null || other == scav || other == target || other.dead || other.room != scav.room) continue;
            if (!IsFriend(scav, other)) continue;
            foreach (BodyChunk chunk in other.bodyChunks)
            {
                float radius = chunk.rad + padding;
                if ((chunk.pos - LanceCombatMath.ClosestPoint(origin, end, chunk.pos)).sqrMagnitude < radius * radius)
                    return true;
            }
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
    /// Navigation chooses a broadly useful tactical launch area only. It no longer solves seven
    /// ballistic heights for every candidate point; exact weapon geometry belongs to the charge
    /// planner once the scavenger actually reaches the area.
    /// </summary>
    internal static bool FindStagingPosition(LanceScavenger scav, Creature target, out WorldCoordinate destination)
    {
        destination = scav.abstractCreature.pos;
        if (scav?.room == null || target?.mainBodyChunk == null) return false;

        float best = float.MaxValue;
        Vector2 origin = scav.mainBodyChunk.pos;
        float preferredDistance = Mathf.Lerp(120f, 240f, ChargeCommitment(scav));
        float maximumDistance = MaximumChargeDistance(scav);

        for (int side = -1; side <= 1; side += 2)
        {
            for (int distanceIndex = 0; distanceIndex < StagingDistances.Length; distanceIndex++)
            {
                int distance = StagingDistances[distanceIndex];
                if (distance > maximumDistance - 10f) continue;
                for (int heightIndex = 0; heightIndex < StagingHeights.Length; heightIndex++)
                {
                    int height = StagingHeights[heightIndex];
                    Vector2 requested = target.mainBodyChunk.pos + new Vector2(side * distance, height);
                    WorldCoordinate coordinate = scav.room.GetWorldCoordinate(requested);
                    if (!scav.AI.pathFinder.CoordinateViable(coordinate)) continue;

                    Vector2 candidate = scav.room.MiddleOfTile(coordinate);
                    float dx = target.mainBodyChunk.pos.x - candidate.x;
                    float horizontalDistance = Mathf.Abs(dx);
                    if (horizontalDistance < MinimumChargeDistance || horizontalDistance > maximumDistance ||
                        Mathf.Sign(dx) == 0f)
                        continue;

                    // Only prove that the lancer can occupy the starting pose. Full ballistic terrain
                    // validation is deferred to the analytic attack planner and is therefore done for
                    // one solved trajectory rather than for every navigation candidate.
                    if (PoseBlocked(scav, candidate, HorizontalDirection(dx))) continue;

                    float score = Vector2.Distance(origin, candidate) + Mathf.Abs(height) * 1.2f +
                        Mathf.Abs(horizontalDistance - preferredDistance) * 0.45f;
                    if (score >= best) continue;
                    best = score;
                    destination = coordinate;
                }
            }
        }
        return best < float.MaxValue;
    }
}
