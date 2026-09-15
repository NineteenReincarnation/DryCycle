using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

internal readonly struct ChargeLane
{
    internal ChargeLane(bool clear, Vector2 aim, string reason) { Clear = clear; Aim = aim; Reason = reason; }
    internal bool Clear { get; }
    internal Vector2 Aim { get; }
    internal string Reason { get; }
}

internal static class ChargeLanePlanner
{
    internal const float MinimumChargeDistance = 60f;   // 3 tiles
    internal const float MinimumMaximumChargeDistance = 300f; // 15 tiles
    internal const float MaximumMaximumChargeDistance = 500f; // 25 tiles

    internal static float ChargeCommitment(LanceScavenger scav)
    {
        CreatureTemplate.Relationship unused = default;
        Personality personality = scav.abstractCreature.personality;
        return Mathf.Clamp01(personality.bravery * 0.45f + personality.aggression * 0.35f + personality.energy * 0.20f);
    }

    internal static float MaximumChargeDistance(LanceScavenger scav) =>
        Mathf.Lerp(MinimumMaximumChargeDistance, MaximumMaximumChargeDistance, ChargeCommitment(scav));

    internal static ChargeLane Evaluate(LanceScavenger scav, Vector2 origin, Creature target)
    {
        Vector2 targetPos = target.mainBodyChunk.pos;
        float flightTime = Mathf.Clamp(Mathf.Abs(targetPos.x - origin.x) / 18f, 0f, LanceCombatState.MaxChargeFrames);
        Vector2 predicted = targetPos + Vector2.ClampMagnitude(target.mainBodyChunk.vel * flightTime, 90f);
        float dx = predicted.x - origin.x;
        float distance = Mathf.Abs(dx);
        if (distance < MinimumChargeDistance || distance > MaximumChargeDistance(scav))
            return new ChargeLane(false, predicted, "distance");
        if (Mathf.Abs(predicted.y - origin.y) > 35f) return new ChargeLane(false, predicted, "height");
        Vector2 end = new(predicted.x + Mathf.Sign(dx) * 45f, origin.y);
        string block = CorridorBlock(scav, origin, end, target);
        return new ChargeLane(block == null, predicted, block ?? "clear");
    }

    internal static string CorridorBlock(LanceScavenger scav, Vector2 origin, Vector2 end, Creature target)
    {
        Room room = scav.room;
        int count = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(origin, end) / 9f));
        for (int i = 0; i <= count; i++)
        {
            Vector2 at = Vector2.Lerp(origin, end, (float)i / count);
            if (at.x < 20f || at.x > room.PixelWidth - 20f || at.y < 30f || at.y > room.PixelHeight - 20f)
                return "room edge";
            if (room.GetTile(at).Solid || room.GetTile(at + Vector2.up * 14f).Solid || room.GetTile(at - Vector2.up * 8f).Solid)
                return "wall / ceiling";
            if (room.aimap != null && room.aimap.getAItile(at).narrowSpace) return "narrow space";
            bool supported = false;
            for (int y = 16; y <= 56; y += 10)
            {
                Room.Tile tile = room.GetTile(at - new Vector2(0f, y));
                if (tile.Solid || tile.Terrain == Room.Tile.TerrainType.Floor || tile.Terrain == Room.Tile.TerrainType.Slope)
                { supported = true; break; }
            }
            if (!supported) return "unsafe landing";
            if (room.GetTile(at).AnyWater) return "water";
        }
        if (!room.VisualContact(origin, end)) return "tip path";
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
            // Start one tile beyond the 3-tile minimum so snapping and braking do not
            // immediately push the body back inside the no-charge zone.
            for (int distance = 80; distance <= maximumStagingDistance; distance += 40)
                for (int height = -20; height <= 20; height += 20)
                {
                    Vector2 candidate = target.mainBodyChunk.pos + new Vector2(side * distance, height);
                    WorldCoordinate coordinate = scav.room.GetWorldCoordinate(candidate);
                    candidate = scav.room.MiddleOfTile(coordinate);
                    // CoordinateViable honors vanilla's walkPastPointOfNoReturn
                    // policy for stranded scavengers and rooms without an exit.
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
