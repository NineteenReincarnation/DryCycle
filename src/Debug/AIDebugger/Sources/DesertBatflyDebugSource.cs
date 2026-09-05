using System;
using System.Reflection;
using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// First full species adapter. Private DesertAI counters are reflected through cached
// FieldInfo only for the selected bat while the Observatory is visible. No world-wide
// per-frame reflection is performed.
internal sealed class DesertBatflyDebugSource : IAIDebugSource
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo RetreatField = typeof(DesertBatflyAI).GetField("retreat", PrivateInstance);
    private static readonly FieldInfo MemoryField = typeof(DesertBatflyAI).GetField("memory", PrivateInstance);
    private static readonly FieldInfo InterestField = typeof(DesertBatflyAI).GetField("interest", PrivateInstance);
    private static readonly FieldInfo PursuitField = typeof(DesertBatflyAI).GetField("pursuit", PrivateInstance);
    private static readonly FieldInfo UnseenField = typeof(DesertBatflyAI).GetField("unseen", PrivateInstance);
    private static readonly FieldInfo HasSlotField = typeof(DesertBatflyAI).GetField("hasSlot", PrivateInstance);
    private static readonly FieldInfo DangerField = typeof(DesertBatflyAI).GetField("danger", PrivateInstance);
    private static readonly FieldInfo AttackerField = typeof(DesertBatflyAI).GetField("attacker", PrivateInstance);
    private static readonly FieldInfo EscapeFromField = typeof(DesertBatflyAI).GetField("escapeFrom", PrivateInstance);

    public int Priority => 1000;
    public bool CanInspect(AbstractCreature creature) => creature?.realizedCreature is DesertBatfly;

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        if (creature?.realizedCreature is not DesertBatfly bat) return null;

        DesertBatflyAI ai = bat.DesertAI;
        DesertBatflySocialRoles roles = ai.Roles;
        DesertBatflyState state = bat.DesertState;
        DesertBatflyPersonality p = bat.Personality;
        SocialRoleSuppression suppression = roles.Suppression;
        string controlOwner = ControlOwner(bat, suppression);
        var snapshot = new AIDebugSnapshot(DebugEntityKey.From(creature),
            $"DesertBatfly #{creature.ID.number}", AIDebugRegistry.EntityState(creature), controlOwner);

        snapshot.Sections.Add(new AIDebugSection("section.identity")
            .Add("field.entity_id", "AbstractCreature.ID", creature.ID)
            .Add("field.template", "CreatureTemplate.type", creature.creatureTemplate?.type?.value)
            .Add("field.room", "AbstractCreature.Room", creature.Room?.name)
            .Add("field.coordinate", "AbstractCreature.pos", creature.pos)
            .Add("field.entity_state", "DebugEntityState", AIDebugLocalization.EntityState(snapshot.EntityState)));

        snapshot.Sections.Add(new AIDebugSection("section.state")
            .Add("field.dead", "Creature.dead", bat.dead)
            .Add("field.conscious", "Creature.Consious", bat.Consious)
            .Add("field.in_shortcut", "Creature.inShortcut", bat.inShortcut)
            .Add("field.in_den", "AbstractCreature.InDen", creature.InDen)
            .Add("field.thirst", "DesertBatflyState.Thirst", state.Thirst)
            .Add("field.cooldown", "DesertBatflyState.Cooldown", state.Cooldown));

        snapshot.Sections.Add(new AIDebugSection("section.personality")
            .Add("field.sex", "DesertBatflyPersonality.Sex", p.Sex)
            .Add("field.temperament", "DesertBatflyPersonality.Temperament", p.Temperament)
            .Add("field.nerve", "DesertBatflyPersonality.Nerve", p.Nerve)
            .Add("field.conformity", "DesertBatflyPersonality.Conformity", p.Conformity)
            .Add("field.roost_affinity", "DesertBatflyPersonality.RoostAffinity", p.RoostAffinity)
            .Add("field.vengeance_affinity", "DesertBatflyPersonality.VengeanceAffinity", p.VengeanceAffinity)
            .Add("field.sand_affinity", "DesertBatflyPersonality.SandSpitAffinity", p.SandSpitAffinity)
            .Add("field.aggressive", "DesertBatflyPersonality.Aggressive", p.Aggressive));

        Creature danger = Read<Creature>(DangerField, ai);
        Creature attacker = Read<Creature>(AttackerField, ai);
        snapshot.Sections.Add(new AIDebugSection("section.ai")
            .Add("field.mode", "DesertBatflyAI.Mode", ai.Mode)
            .Add("field.target", "DesertBatflyAI.Target", AIDebugFormat.Creature(ai.Target))
            .Add("field.formal_attack", "DesertBatflyAI.FormalAttack", ai.FormalAttack)
            .Add("field.immediate_danger", "DesertBatflyAI.HasImmediateDanger", ai.HasImmediateDanger)
            .Add("field.retreat", "DesertBatflyAI.retreat", Read<int>(RetreatField, ai))
            .Add("field.memory", "DesertBatflyAI.memory", Read<int>(MemoryField, ai))
            .Add("field.interest", "DesertBatflyAI.interest", Read<int>(InterestField, ai))
            .Add("field.pursuit", "DesertBatflyAI.pursuit", Read<int>(PursuitField, ai))
            .Add("field.unseen", "DesertBatflyAI.unseen", Read<int>(UnseenField, ai))
            .Add("field.has_slot", "DesertBatflyAI.hasSlot", Read<bool>(HasSlotField, ai))
            .Add("field.target", "DesertBatflyAI.danger", AIDebugFormat.Creature(danger))
            .Add("field.memory", "DesertBatflyAI.attacker", AIDebugFormat.Creature(attacker)));

        // EvaluationTicks is time until the next role evaluation, not the age of Scores.
        // Do not present it as data age; the separate field below reports that timer honestly.
        snapshot.Sections.Add(new AIDebugSection("section.social_role")
            .Add("field.role", "DesertBatflySocialRoles.Role", roles.Role)
            .Add("field.expressed_role", "DesertBatflySocialRoles.Expressed", roles.Expressed)
            .Add("field.suppression", "DesertBatflySocialRoles.Suppression", suppression)
            .Add("field.sentinel_score", "DesertBatflyRoleScores.Sentinel", roles.Scores.Sentinel, 0, "RoleEvaluation")
            .Add("field.bully_score", "DesertBatflyRoleScores.Bully", roles.Scores.Bully, 0, "RoleEvaluation")
            .Add("field.opportunist_score", "DesertBatflyRoleScores.Opportunist", roles.Scores.Opportunist, 0, "RoleEvaluation")
            .Add("field.commitment", "DesertBatflySocialRoles.Commitment", roles.Commitment)
            .Add("field.role_cooldown", "DesertBatflySocialRoles.Cooldown", roles.Cooldown)
            .Add("field.role_evaluation", "DesertBatflySocialRoles.EvaluationTicks", roles.EvaluationTicks)
            .Add("field.alert_confidence", "DesertBatflySocialRoles.SentinelAlertConfidence", roles.SentinelAlertConfidence)
            .Add("field.opportunity_ticks", "DesertBatflySocialRoles.OpportunityTicks", roles.OpportunityTicks)
            .Add("field.opportunist_recovery", "DesertBatflySocialRoles.OpportunistRecoveryActive", roles.OpportunistRecoveryActive));

        // A debugger must not create gameplay state just by looking at it. Read an existing
        // swarm snapshot only; DesertSwarmRoom.For(...) is reserved for gameplay code.
        if (bat.room != null && DesertSwarmRoom.TryGet(bat.room, out DesertSwarmRoom colony))
        {
            DesertBatflyFlockSnapshot flock = colony.Flock;
            int age = colony.SnapshotAge;
            snapshot.Sections.Add(new AIDebugSection("section.flock")
                .Add("field.flock_center", "DesertBatflyFlockSnapshot.Center", flock.Center, age, "FlockSnapshot")
                .Add("field.flock_velocity", "DesertBatflyFlockSnapshot.AverageVelocity", flock.AverageVelocity, age, "FlockSnapshot")
                .Add("field.flock_active", "DesertBatflyFlockSnapshot.ActiveCount", flock.ActiveCount, age, "FlockSnapshot")
                .Add("field.flock_roles", "DesertBatflyFlockSnapshot.ExpressedRoleCount", flock.ExpressedRoleCount, age, "FlockSnapshot")
                .Add("field.panic_ratio", "DesertBatflyFlockSnapshot.PanicRatio", flock.PanicRatio, age, "FlockSnapshot")
                .Add("field.previous_panic", "DesertBatflyFlockSnapshot.PreviousPanicRatio", flock.PreviousPanicRatio, age, "FlockSnapshot")
                .Add("field.roost_ratio", "DesertBatflyFlockSnapshot.RoostRatio", flock.RoostRatio, age, "FlockSnapshot"));
        }

        snapshot.Sections.Add(new AIDebugSection("section.social")
            .Add("field.grab_memory", "DesertBatflyState.GrabMemoryStrength", state.GrabMemoryStrength)
            .Add("field.grief", "DesertBatflyState.GriefStrength", state.GriefStrength)
            .Add("field.player_trauma", "DesertBatflyState.PlayerTraumaStrength", state.PlayerTraumaTicks > 0 ? state.PlayerTraumaStrength : 0f)
            .Add("field.predator_trauma", "DesertBatflyState.PredatorTraumaStrength", state.PredatorTraumaTicks > 0 ? state.PredatorTraumaStrength : 0f)
            .Add("field.social_bond", "DesertBatflyState.SocialBondStrength", state.SocialBondStrength)
            .Add("field.social_bond_target", "DesertBatflyState.SocialBondTarget", state.SocialBondTarget.HasValue ? state.SocialBondTarget.Value.ToString() : "—"));

        snapshot.Sections.Add(new AIDebugSection("section.movement")
            .Add("field.position", "mainBodyChunk.pos", bat.mainBodyChunk?.pos)
            .Add("field.velocity", "mainBodyChunk.vel", bat.mainBodyChunk?.vel)
            .Add("field.escape_from", "DesertBatflyAI.escapeFrom", Read<Vector2>(EscapeFromField, ai))
            .Add("field.local_goal", "FlyAI.localGoal", bat.AI?.localGoal)
            .Add("field.behavior", "FlyAI.behavior", bat.AI?.behavior)
            .Add("field.flee_from_rain", "FlyAI.fleeFromRain", bat.AI?.fleeFromRain ?? false)
            .Add("field.lured_counter", "FlyAI.luredCounter", bat.AI?.luredCounter ?? 0));

        BuildDecisionStack(snapshot, bat, suppression);
        return snapshot;
    }

    private static void BuildDecisionStack(AIDebugSnapshot snapshot, DesertBatfly bat, SocialRoleSuppression suppression)
    {
        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.availability", AIDebugDecisionState.Active));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.conscious",
            bat.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked, null, "Creature.Consious", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.shortcut",
            bat.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive, null, "Creature.inShortcut", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.restrained",
            suppression == SocialRoleSuppression.Restrained ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            null, "SocialRoleSuppression.Restrained", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.survival", AIDebugDecisionState.Active));
        AddSuppression(snapshot, "decision.danger", SocialRoleSuppression.Danger, suppression, 1);
        AddSuppression(snapshot, "decision.fear", SocialRoleSuppression.Fear, suppression, 1);
        AddSuppression(snapshot, "decision.trauma", SocialRoleSuppression.Trauma, suppression, 1);

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.special", AIDebugDecisionState.Active));
        AddSuppression(snapshot, "decision.grief", SocialRoleSuppression.Grief, suppression, 1);
        AddSuppression(snapshot, "decision.vengeance", SocialRoleSuppression.Vengeance, suppression, 1);
        AddSuppression(snapshot, "decision.roost", SocialRoleSuppression.Roost, suppression, 1);

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.social_role",
            suppression == SocialRoleSuppression.None ? AIDebugDecisionState.Active : AIDebugDecisionState.Blocked,
            suppression == SocialRoleSuppression.None ? null : suppression.ToString()));
        AddRole(snapshot, "decision.sentinel", ExpressedSocialRole.Sentinel, roles, 1);
        AddRole(snapshot, "decision.bully", ExpressedSocialRole.Bully, roles, 1);
        AddRole(snapshot, "decision.opportunist", ExpressedSocialRole.Opportunist, roles, 1);

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.custom_ai", AIDebugDecisionState.Active,
            bat.DesertAI.Mode.ToString(), "DesertBatflyAI.Mode"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.vanilla_ai", AIDebugDecisionState.Ready,
            bat.AI?.behavior.ToString(), "FlyAI.behavior"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.motor", AIDebugDecisionState.Active,
            AIDebugFormat.Value(bat.AI?.localGoal), "FlyAI.localGoal"));
    }

    private static void AddSuppression(AIDebugSnapshot snapshot, string key, SocialRoleSuppression value,
        SocialRoleSuppression current, int depth)
    {
        snapshot.Decisions.Add(new AIDebugDecisionNode(key,
            current == value ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            current == value ? current.ToString() : null, value.ToString(), depth));
    }

    private static void AddRole(AIDebugSnapshot snapshot, string key, ExpressedSocialRole value,
        DesertBatflySocialRoles roles, int depth)
    {
        AIDebugDecisionState roleState = roles.Expressed == value
            ? AIDebugDecisionState.Active
            : roles.Role == value && roles.Expressed == ExpressedSocialRole.None
                ? AIDebugDecisionState.Blocked
                : AIDebugDecisionState.Inactive;
        snapshot.Decisions.Add(new AIDebugDecisionNode(key, roleState, null, value.ToString(), depth));
    }

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

    private static T Read<T>(FieldInfo field, object instance)
    {
        if (field == null || instance == null) return default;
        object value = field.GetValue(instance);
        return value is T typed ? typed : default;
    }
}
