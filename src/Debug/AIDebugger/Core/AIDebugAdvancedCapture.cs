using System;
using System.Collections.Generic;
using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugUtilityRow
{
    internal readonly string Name;
    internal readonly float Raw;
    internal readonly float Smoothed;
    internal readonly float Weight;
    internal readonly float Weighted;
    internal readonly float ContinuationBonus;
    internal readonly bool Winner;

    internal AIDebugUtilityRow(string name, float raw, float smoothed, float weight,
        float weighted, float continuationBonus, bool winner)
    {
        Name = name ?? "?";
        Raw = raw;
        Smoothed = smoothed;
        Weight = weight;
        Weighted = weighted;
        ContinuationBonus = continuationBonus;
        Winner = winner;
    }
}

internal readonly struct AIDebugPerceptionRow
{
    internal readonly DebugEntityKey Key;
    internal readonly string Name;
    internal readonly bool VisualContact;
    internal readonly int TicksSinceSeen;
    internal readonly float EstimatedChance;
    internal readonly float Priority;
    internal readonly WorldCoordinate LastSeen;
    internal readonly WorldCoordinate BestGuess;
    internal readonly string Relationship;
    internal readonly float RelationshipIntensity;

    internal AIDebugPerceptionRow(DebugEntityKey key, string name, bool visualContact, int ticksSinceSeen,
        float estimatedChance, float priority, WorldCoordinate lastSeen, WorldCoordinate bestGuess,
        string relationship, float relationshipIntensity)
    {
        Key = key;
        Name = name ?? key.ToString();
        VisualContact = visualContact;
        TicksSinceSeen = ticksSinceSeen;
        EstimatedChance = estimatedChance;
        Priority = priority;
        LastSeen = lastSeen;
        BestGuess = bestGuess;
        Relationship = relationship ?? "—";
        RelationshipIntensity = relationshipIntensity;
    }
}

internal readonly struct AIDebugPathState
{
    internal readonly string Pathfinder;
    internal readonly WorldCoordinate Destination;
    internal readonly bool HasPathfinder;
    internal readonly bool DestinationReachable;
    internal readonly bool CanReturnFromDestination;
    internal readonly bool Stranded;

    internal AIDebugPathState(string pathfinder, WorldCoordinate destination, bool hasPathfinder,
        bool destinationReachable, bool canReturnFromDestination, bool stranded)
    {
        Pathfinder = pathfinder ?? "—";
        Destination = destination;
        HasPathfinder = hasPathfinder;
        DestinationReachable = destinationReachable;
        CanReturnFromDestination = canReturnFromDestination;
        Stranded = stranded;
    }
}

internal static class AIDebugAdvancedCapture
{
    internal static void CaptureUtilities(AbstractCreature creature, List<AIDebugUtilityRow> output)
    {
        output.Clear();
        if (creature?.realizedCreature is DesertBatfly bat)
        {
            DesertBatflyRoleScores scores = bat.DesertAI.Roles.Scores;
            ExpressedSocialRole role = bat.DesertAI.Roles.Expressed;
            output.Add(new AIDebugUtilityRow("Sentinel", scores.Sentinel, scores.Sentinel, 1f,
                scores.Sentinel, 0f, role == ExpressedSocialRole.Sentinel));
            output.Add(new AIDebugUtilityRow("Bully", scores.Bully, scores.Bully, 1f,
                scores.Bully, 0f, role == ExpressedSocialRole.Bully));
            output.Add(new AIDebugUtilityRow("Opportunist", scores.Opportunist, scores.Opportunist, 1f,
                scores.Opportunist, 0f, role == ExpressedSocialRole.Opportunist));
            return;
        }

        UtilityComparer comparer = creature?.abstractAI?.RealAI?.utilityComparer;
        if (comparer?.uTrackers == null) return;
        for (int i = 0; i < comparer.uTrackers.Count; i++)
        {
            UtilityComparer.UtilityTracker tracker = comparer.uTrackers[i];
            if (tracker == null) continue;
            string name = tracker.module?.GetType().Name ?? "<null>";
            float raw = tracker.module?.Utility() ?? 0f;
            float smoothed = tracker.SmoothedUtility();
            output.Add(new AIDebugUtilityRow(name, raw, smoothed,
                tracker.weight, smoothed, tracker.continuationBonus,
                ReferenceEquals(tracker, comparer.highestUtilityTracker)));
        }
    }

    internal static void CapturePerception(AbstractCreature creature, List<AIDebugPerceptionRow> output)
    {
        output.Clear();
        Tracker tracker = creature?.abstractAI?.RealAI?.tracker;
        if (tracker?.creatures == null) return;
        for (int i = 0; i < tracker.creatures.Count; i++)
        {
            Tracker.CreatureRepresentation rep = tracker.creatures[i];
            AbstractCreature other = rep?.representedCreature;
            if (rep == null || other == null) continue;
            string type = other.creatureTemplate?.type?.value ?? "Creature";
            CreatureTemplate.Relationship relationship = rep.dynamicRelationship?.currentRelationship ?? default;
            string relationshipName = rep.dynamicRelationship == null ? "—" : relationship.type.ToString();
            float intensity = rep.dynamicRelationship == null ? 0f : relationship.intensity;
            WorldCoordinate bestGuess;
            try { bestGuess = rep.BestGuessForPosition(); }
            catch { bestGuess = rep.lastSeenCoord; }
            output.Add(new AIDebugPerceptionRow(DebugEntityKey.From(other),
                $"{type} #{other.ID.number}", rep.VisualContact, rep.TicksSinceSeen,
                rep.EstimatedChanceOfFinding, rep.priority, rep.lastSeenCoord, bestGuess,
                relationshipName, intensity));
        }
        output.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    internal static AIDebugPathState CapturePath(AbstractCreature creature)
    {
        ArtificialIntelligence ai = creature?.abstractAI?.RealAI;
        PathFinder pathFinder = ai?.pathFinder;
        WorldCoordinate destination = creature?.abstractAI?.destination ?? default;
        bool reachable = false;
        bool returnable = false;
        if (pathFinder != null)
        {
            try
            {
                reachable = pathFinder.CoordinateReachable(destination);
                returnable = pathFinder.CoordinatePossibleToGetBackFrom(destination);
            }
            catch
            {
                // A destination can briefly point into an unloaded room during transitions.
            }
        }
        return new AIDebugPathState(pathFinder?.GetType().Name, destination,
            pathFinder != null, reachable, returnable, ai?.stranded ?? false);
    }
}
