using DryCycle.Debugging.AI;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Debug instrumentation is deliberately outside the behavior code. When the Observatory
// is closed AIDebugTrace.IsWatched is false and this method returns before allocating strings.
internal static class DB_Trace
{
    internal static void Sample(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;

        DB_FlockSnapshot flock = default;
        int flockAge = 0;
        if (bat.room != null && DB_SwarmRoom.TryGet(bat.room, out DB_SwarmRoom colony))
        {
            flock = colony.Flock;
            flockAge = colony.SnapshotAge;
        }

        bool hasTravel = DesertBatflyTravelNavigation.TryGetDebugState(
            bat.abstractCreature, out DesertBatflyTravelDebugState travel);
        bool hasSocial = DesertBatflySocialLife.TryGetDebugState(
            bat, out DesertBatflySocialDebugState social) &&
            social.Mode != DesertBatflySocialMode.None;
        string suppression = Suppression(bat, hasTravel, travel);
        string modeReason = ModeReason(bat, suppression, hasTravel, travel, hasSocial, social);
        string controlOwner = ControlOwner(bat, suppression, hasTravel, travel, hasSocial, social);

        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.State,
            "Mode", bat.DesertAI.Mode, modeReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Decision,
            "ControlOwner", controlOwner, modeReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Suppression", suppression, SuppressionReason(bat, suppression, hasTravel, travel));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Combat,
            "FormalAttack", bat.DesertAI.FormalAttack, bat.DesertAI.Target == null
                ? "no target" : AIDebugFormat.Creature(bat.DesertAI.Target));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Perception,
            "Target", AIDebugFormat.Creature(bat.DesertAI.Target), "DesertBatflyAI.Target");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.State,
            "VanillaBehavior", bat.AI?.behavior, "FlyAI.behavior");

        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Path,
            "Task09TravelPurpose", hasTravel ? travel.Purpose.ToString() : "None",
            hasTravel ? travel.StatusReason : "no active TravelIntent");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Path,
            "Task09TravelDestination", hasTravel ? travel.DestinationRoom : "—",
            hasTravel ? travel.StatusReason : "no active TravelIntent");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Path,
            "Task09TravelProgress",
            hasTravel ? $"{travel.RouteIndex}/{Mathf.Max(0, travel.RouteRooms.Length - 1)} next={travel.NextRoom}" : "—",
            hasTravel ? travel.StatusReason : "no active TravelIntent");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Path,
            "Task09TravelStatus", hasTravel ? travel.StatusReason : "—",
            hasTravel && travel.Suspended ? "TravelSuspended" : "TravelActiveOrNone");

        AIDebugCandidateRegistry.Begin(bat.abstractCreature);
        if (bat.AI != null)
            AIDebugCandidateRegistry.Record(bat.abstractCreature, "Motor", "localGoal",
                bat.AI.localGoal, true, 1f, bat.AI.behavior.ToString(), true);

        Vector2 position = bat.mainBodyChunk?.pos ?? Vector2.zero;
        Vector2 velocity = bat.mainBodyChunk?.vel ?? Vector2.zero;
        Vector2 localGoal = bat.AI?.localGoal ?? position;
        AIDebugTrace.Sample(bat.abstractCreature, new AIDebugTraceFrame(
            bat.room?.abstractRoom?.name,
            position,
            velocity,
            localGoal,
            bat.DesertAI.Mode.ToString(),
            AIDebugFormat.Creature(bat.DesertAI.Target),
            suppression,
            controlOwner,
            0f,
            0f,
            0f,
            flock.PanicRatio, health: bat.DesertState.health,
            leftWing: bat.DesertState.LeftWingInjury, rightWing: bat.DesertState.RightWingInjury,
            postStun: bat.Injury.PostStunShock, physicalCapability: bat.Injury.PhysicalCapability));

        if (flockAge > 30)
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Warning,
                "StaleFlockSnapshot", flockAge, "FlockSnapshot age exceeded refresh period");
    }

    private static string Suppression(
        DesertBatfly bat,
        bool hasTravel,
        in DesertBatflyTravelDebugState travel)
    {
        if (bat.dead || !bat.Consious || bat.room == null) return "Unavailable";
        if (bat.inShortcut) return "Shortcut";
        if (RestrainedByNonFly(bat)) return "Restrained";
        if (bat.Emergence?.Active == true) return "Emergence";
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return "Danger";
        if (bat.Injury.BlocksCombat || bat.Injury.IsRecovering ||
            bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery)
            return "Injury";
        if (hasTravel && !travel.Suspended) return "Task09Travel";
        if (bat.AI == null || bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
            bat.AI.luredCounter > 0 || bat.safariControlled)
            return "VanillaPriority";
        if (DesertBatflyIntimidation.IsExtremeVengeanceActive(bat)) return "Vengeance";
        if (ActiveTrauma(bat) >= DB_Tuning.TraumaAggressionBlock) return "Trauma";
        if (bat.DesertState.GriefStrength >= 0.30f) return "Grief";
        if (DesertBatflyIntimidation.HasActiveFearSuppression(bat)) return "Fear";
        if (bat.AI.behavior == FlyAI.Behavior.Chain || bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost)
            return "Roost";
        return "None";
    }

    private static string ModeReason(
        DesertBatfly bat,
        string suppression,
        bool hasTravel,
        in DesertBatflyTravelDebugState travel,
        bool hasSocial,
        in DesertBatflySocialDebugState social)
    {
        switch (suppression)
        {
            case "Unavailable": return "creature unavailable";
            case "Shortcut": return "shortcut owns movement";
            case "Restrained": return "non-fly grasp or restraint owns movement";
            case "Emergence": return "emergence animation owns behavior";
            case "Task09Travel": return hasTravel ? travel.StatusReason : "Task 09 travel";
            case "VanillaPriority": return "vanilla FlyAI priority";
            case "Danger": return "danger / retreat owns movement";
            case "Injury": return bat.Injury.IsRecovering ? bat.Injury.RecoveryReason : "injury / shock limits behavior";
            case "Fear": return "fear / intimidation priority";
            case "Trauma": return "trauma above aggression block";
            case "Grief": return "grief state limits behavior";
            case "Vengeance": return "extreme vengeance owns behavior";
            case "Roost": return "roost / fly chain owns movement";
        }
        if (bat.DesertAI.FormalAttack) return "formal attack state machine";
        if (hasSocial) return string.IsNullOrEmpty(social.DecisionReason)
            ? "Task 10 neutral social interaction"
            : social.DecisionReason;
        return "DesertBatflyAI state machine";
    }

    private static string SuppressionReason(
        DesertBatfly bat,
        string suppression,
        bool hasTravel,
        in DesertBatflyTravelDebugState travel)
    {
        switch (suppression)
        {
            case "Injury": return bat.Injury.BlocksCombat ? bat.Injury.CombatBlockReason : bat.Injury.RecoveryReason;
            case "Task09Travel": return hasTravel ? travel.StatusReason : "Task 09 travel";
            case "None": return "no higher-priority blocker";
            case "Unavailable": return "dead / unconscious / no room";
            case "Shortcut": return "shortcut lifecycle";
            case "Restrained": return "non-fly grasp or restraint";
            case "Emergence": return "emergence animation owns behavior";
            case "VanillaPriority": return "rain / burrow / lure / safari";
            case "Danger": return "direct danger or retreat";
            case "Fear": return "intimidation / fear gate";
            case "Trauma": return "trauma above aggression block";
            case "Grief": return "grief >= 0.30";
            case "Vengeance": return "extreme vengeance owns behavior";
            case "Roost": return "roost or fly chain";
            default: return suppression;
        }
    }

    private static string ControlOwner(
        DesertBatfly bat,
        string suppression,
        bool hasTravel,
        in DesertBatflyTravelDebugState travel,
        bool hasSocial,
        in DesertBatflySocialDebugState social)
    {
        switch (suppression)
        {
            case "Unavailable": return "Creature / Physics";
            case "Shortcut": return "Shortcut";
            case "Restrained": return "Grasp / Restraint";
            case "Emergence": return "Emergence";
            case "Task09Travel": return hasTravel ? "Task 09 / " + travel.Purpose : "Task 09 Travel";
            case "VanillaPriority": return "Vanilla FlyAI";
            case "Danger": return "Danger / Escape";
            case "Injury": return "Injury Recovery";
            case "Fear": return "Fear / Intimidation";
            case "Trauma": return "Trauma";
            case "Grief": return "Grief";
            case "Vengeance": return "Vengeance";
            case "Roost": return "Roost / Chain";
            default:
                if (hasSocial) return "Task 10 Social / " + social.Mode;
                return "DesertBatflyAI";
        }
    }

    private static bool RestrainedByNonFly(DesertBatfly bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }

    private static float ActiveTrauma(DesertBatfly bat) => Mathf.Max(
        bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
        bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);
}
