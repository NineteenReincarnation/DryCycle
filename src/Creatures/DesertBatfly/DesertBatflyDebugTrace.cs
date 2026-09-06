using DryCycle.Debugging.AI;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// Debug instrumentation is deliberately outside the behavior code. When the Observatory
// is closed AIDebugTrace.IsWatched is false and this method returns before allocating strings.
internal static class DesertBatflyDebugTrace
{
    internal static void Sample(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;

        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        SocialRoleSuppression suppression = roles.Suppression;
        DesertBatflyFlockSnapshot flock = default;
        int flockAge = 0;
        if (bat.room != null && DesertSwarmRoom.TryGet(bat.room, out DesertSwarmRoom colony))
        {
            flock = colony.Flock;
            flockAge = colony.SnapshotAge;
        }

        string modeReason = ModeReason(bat, suppression);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.State,
            "Mode", bat.DesertAI.Mode, modeReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Decision,
            "ControlOwner", ControlOwner(bat, suppression), modeReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Suppression", suppression, SuppressionReason(bat, suppression));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "StoredRole", roles.Role, RoleReason(bat));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "ExpressedRole", roles.Expressed, suppression == SocialRoleSuppression.None
                ? "role visible" : "suppressed by " + suppression);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "OpportunistRecovery", roles.OpportunistRecoveryActive,
            roles.OpportunityTicks > 0 ? "recent threat window" : "no recovery window");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Combat,
            "FormalAttack", bat.DesertAI.FormalAttack, bat.DesertAI.Target == null
                ? "no target" : AIDebugFormat.Creature(bat.DesertAI.Target));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Perception,
            "Target", AIDebugFormat.Creature(bat.DesertAI.Target), "DesertBatflyAI.Target");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.State,
            "VanillaBehavior", bat.AI?.behavior, "FlyAI.behavior");

        DesertBatflyRoleScores scores = roles.Scores;
        float threshold = DesertBatflyRoleScores.EntryThreshold(flock.ActiveCount, flock.ExpressedRoleCount);
        AIDebugCandidateRegistry.Begin(bat.abstractCreature);
        AIDebugCandidateRegistry.Record(bat.abstractCreature, "SocialRole", "Sentinel",
            scores.Sentinel >= threshold, scores.Sentinel,
            $"entry threshold={threshold:0.000}", roles.Expressed == ExpressedSocialRole.Sentinel);
        AIDebugCandidateRegistry.Record(bat.abstractCreature, "SocialRole", "Bully",
            scores.Bully >= threshold, scores.Bully,
            $"entry threshold={threshold:0.000}", roles.Expressed == ExpressedSocialRole.Bully);
        AIDebugCandidateRegistry.Record(bat.abstractCreature, "SocialRole", "Opportunist",
            scores.Opportunist >= threshold, scores.Opportunist,
            $"entry threshold={threshold:0.000}", roles.Expressed == ExpressedSocialRole.Opportunist);
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
            roles.Expressed.ToString(),
            suppression.ToString(),
            ControlOwner(bat, suppression),
            scores.Sentinel,
            scores.Bully,
            scores.Opportunist,
            flock.PanicRatio, health: bat.DesertState.health,
            leftWing: bat.DesertState.LeftWingInjury, rightWing: bat.DesertState.RightWingInjury,
            postStun: bat.Injury.PostStunShock, physicalCapability: bat.Injury.PhysicalCapability));

        if (flockAge > 30)
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Warning,
                "StaleFlockSnapshot", flockAge, "FlockSnapshot age exceeded refresh period");
    }

    // Do not re-run role selection in the debugger. Why/Why Not must come from the
    // runtime role events; this summary only reports retained state between evaluations.
    private static string RoleReason(DesertBatfly bat)
    {
        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        if (roles.Role != ExpressedSocialRole.None) return "commitment=" + roles.Commitment;
        if (roles.LastSuppression != SocialRoleSuppression.None)
            return "suppressed by " + roles.LastSuppression;
        if (roles.Cooldown > 0) return "cooldown=" + roles.Cooldown;
        if (bat.DesertAI.FormalAttack) return "formal attack owns behavior";
        return "no stored role; see RoleEvaluation / RoleEvaluationBlocked events";
    }

    private static string ModeReason(DesertBatfly bat, SocialRoleSuppression suppression)
    {
        if (bat.dead || !bat.Consious) return "creature unavailable";
        if (bat.inShortcut) return "shortcut owns movement";
        if (suppression == SocialRoleSuppression.Restrained) return "non-fly grasp or cannot respond";
        if (suppression == SocialRoleSuppression.Emergence) return "emergence animation owns behavior";
        if (suppression == SocialRoleSuppression.VanillaPriority) return "vanilla FlyAI priority";
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return "danger / retreat owns movement";
        if (bat.Injury.IsRecovering || bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery)
            return bat.Injury.RecoveryReason;
        if (suppression == SocialRoleSuppression.Fear) return "fear / intimidation priority";
        if (suppression == SocialRoleSuppression.Trauma) return "trauma above aggression block";
        if (suppression == SocialRoleSuppression.Grief) return "grief state limits behavior";
        if (suppression == SocialRoleSuppression.Vengeance) return "extreme vengeance owns behavior";
        if (bat.DesertAI.FormalAttack) return "formal attack state machine";
        return "DesertBatflyAI state machine";
    }

    private static string SuppressionReason(DesertBatfly bat, SocialRoleSuppression suppression) => suppression switch
    {
        SocialRoleSuppression.Injury => bat.Injury.RoleBlockReason(bat.DesertAI.Roles.Role),
        SocialRoleSuppression.None => "no higher-priority blocker",
        SocialRoleSuppression.Unavailable => "dead / unconscious / shortcut / no room",
        SocialRoleSuppression.Restrained => "non-fly grasp or cannot respond",
        SocialRoleSuppression.Emergence => "emergence animation owns behavior",
        SocialRoleSuppression.VanillaPriority => "rain / burrow / lure / safari",
        SocialRoleSuppression.Danger => "direct danger or retreat",
        SocialRoleSuppression.Fear => "intimidation / corpse reminder / fear",
        SocialRoleSuppression.Trauma => "trauma above aggression block",
        SocialRoleSuppression.Grief => "grief >= 0.30",
        SocialRoleSuppression.Vengeance => "extreme vengeance owns behavior",
        SocialRoleSuppression.Roost => "roost or fly chain",
        _ => suppression.ToString()
    };

    private static string ControlOwner(DesertBatfly bat, SocialRoleSuppression suppression)
    {
        if (bat.dead || !bat.Consious) return "Creature / Physics";
        if (bat.inShortcut) return "Shortcut";
        if (suppression == SocialRoleSuppression.Restrained) return "Grasp / Restraint";
        if (suppression == SocialRoleSuppression.Emergence) return "Emergence";
        if (suppression == SocialRoleSuppression.VanillaPriority) return "Vanilla FlyAI";
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return "Danger / Escape";
        if (bat.Injury.IsRecovering || bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery)
            return "Injury Recovery";
        return suppression switch
        {
            SocialRoleSuppression.Injury => "Physical Condition",
            SocialRoleSuppression.Fear => "Fear / Intimidation",
            SocialRoleSuppression.Trauma => "Trauma",
            SocialRoleSuppression.Grief => "Grief",
            SocialRoleSuppression.Vengeance => "Vengeance",
            SocialRoleSuppression.Roost => "Roost / Chain",
            SocialRoleSuppression.Unavailable => "Creature lifecycle",
            _ => "DesertBatflyAI"
        };
    }
}