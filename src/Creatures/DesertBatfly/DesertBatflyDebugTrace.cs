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

        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.State,
            "Mode", bat.DesertAI.Mode, ModeReason(bat, suppression));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Decision,
            "ControlOwner", ControlOwner(bat, suppression), suppression.ToString());
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Suppression", suppression, SuppressionReason(suppression));
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "StoredRole", roles.Role, RoleReason(bat, flock));
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
            flock.PanicRatio));

        if (flockAge > 30)
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Warning,
                "StaleFlockSnapshot", flockAge, "FlockSnapshot age exceeded refresh period");
    }

    private static string RoleReason(DesertBatfly bat, DesertBatflyFlockSnapshot flock)
    {
        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        if (roles.Role != ExpressedSocialRole.None) return "commitment=" + roles.Commitment;
        if (roles.Cooldown > 0) return "cooldown=" + roles.Cooldown;
        if (bat.DesertAI.FormalAttack) return "formal attack owns behavior";
        if (flock.ActiveCount <= 0) return "no active flock";
        DesertBatflyRoleScores scores = roles.Scores;
        ExpressedSocialRole best = ExpressedSocialRole.Sentinel;
        if (scores.Bully > scores.For(best)) best = ExpressedSocialRole.Bully;
        if (scores.Opportunist > scores.For(best)) best = ExpressedSocialRole.Opportunist;
        float second = best switch
        {
            ExpressedSocialRole.Sentinel => Mathf.Max(scores.Bully, scores.Opportunist),
            ExpressedSocialRole.Bully => Mathf.Max(scores.Sentinel, scores.Opportunist),
            _ => Mathf.Max(scores.Sentinel, scores.Bully)
        };
        float threshold = DesertBatflyRoleScores.EntryThreshold(flock.ActiveCount, flock.ExpressedRoleCount);
        float bestScore = scores.For(best);
        if (bestScore < threshold) return $"{best} {bestScore:0.000} < threshold {threshold:0.000}";
        if (bestScore - second < 0.12f) return $"{best} lead {bestScore - second:0.000} < 0.120";
        if (best != ExpressedSocialRole.Bully && bat.DesertAI.Target != null) return "watch role blocked by existing target";
        return $"{best} eligible; awaiting evaluation tick {roles.EvaluationTicks}";
    }

    private static string ModeReason(DesertBatfly bat, SocialRoleSuppression suppression)
    {
        if (bat.dead || !bat.Consious) return "creature unavailable";
        if (bat.inShortcut) return "shortcut owns movement";
        if (suppression == SocialRoleSuppression.Danger) return "danger / retreat owns movement";
        if (suppression == SocialRoleSuppression.Fear) return "fear / intimidation priority";
        if (suppression == SocialRoleSuppression.VanillaPriority) return "vanilla FlyAI priority";
        if (bat.DesertAI.FormalAttack) return "formal attack state machine";
        return "DesertBatflyAI state machine";
    }

    private static string SuppressionReason(SocialRoleSuppression suppression) => suppression switch
    {
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
        return suppression switch
        {
            SocialRoleSuppression.VanillaPriority => "Vanilla FlyAI",
            SocialRoleSuppression.Danger => "Danger / Escape",
            SocialRoleSuppression.Fear => "Fear / Intimidation",
            SocialRoleSuppression.Trauma => "Trauma",
            SocialRoleSuppression.Grief => "Grief",
            SocialRoleSuppression.Vengeance => "Vengeance",
            SocialRoleSuppression.Roost => "Roost / Chain",
            SocialRoleSuppression.Restrained => "Grasp / Restraint",
            SocialRoleSuppression.Emergence => "Emergence",
            SocialRoleSuppression.Unavailable => "Creature lifecycle",
            _ => "DesertBatflyAI"
        };
    }
}
