using System.Reflection;
using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Desert Batfly Observatory adapter for current gameplay state: personality,
// injury, social memory, AI and movement.
internal sealed class DesertBatflyDebugSource : IAIDebugSource
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo RetreatField = typeof(DesertBatflyAI).GetField("retreat", PrivateInstance);
    private static readonly FieldInfo MemoryField = typeof(DesertBatflyAI).GetField("memory", PrivateInstance);
    private static readonly FieldInfo InterestField = typeof(DesertBatflyAI).GetField("interest", PrivateInstance);
    private static readonly FieldInfo PursuitField = typeof(DesertBatflyAI).GetField("pursuit", PrivateInstance);
    private static readonly FieldInfo UnseenField = typeof(DesertBatflyAI).GetField("unseen", PrivateInstance);
    private static readonly FieldInfo HasSlotField = typeof(DesertBatflyAI).GetField("hasSlot", PrivateInstance);
    private static readonly FieldInfo EscapeFromField = typeof(DesertBatflyAI).GetField("escapeFrom", PrivateInstance);

    public int Priority => 1000;
    public bool CanInspect(AbstractCreature creature) => creature?.realizedCreature is DesertBatfly;

    public AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game)
    {
        if (creature?.realizedCreature is not DesertBatfly bat) return null;

        DesertBatflyAI ai = bat.DesertAI;
        DesertBatflyState state = bat.DesertState;
        DesertBatflyPersonality p = bat.Personality;
        DesertBatflyInjury injury = bat.Injury;
        string controlOwner = ControlOwner(bat);

        var snapshot = new AIDebugSnapshot(
            DebugEntityKey.From(creature),
            $"DesertBatfly #{creature.ID.number}",
            AIDebugRegistry.EntityState(creature),
            controlOwner);

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

        snapshot.Sections.Add(new AIDebugSection("section.injury")
            .Add("field.health", "DesertBatflyState.health", state.health)
            .Add("field.left_wing_injury", "DesertBatflyState.LeftWingInjury", state.LeftWingInjury)
            .Add("field.right_wing_injury", "DesertBatflyState.RightWingInjury", state.RightWingInjury)
            .Add("field.wing_mean", "DesertBatflyInjury.WingMean", injury.WingMean)
            .Add("field.wing_asymmetry", "DesertBatflyInjury.WingAsymmetry", injury.WingAsymmetry)
            .Add("field.wing_bias", "DesertBatflyInjury.WingBias", injury.WingBias)
            .Add("field.post_stun_shock", "DesertBatflyInjury.PostStunShock", injury.PostStunShock)
            .Add("field.physical_capability", "DesertBatflyInjury.PhysicalCapability", injury.PhysicalCapability)
            .Add("field.forward_control", "DesertBatflyInjury.ForwardControl", injury.ForwardControl)
            .Add("field.turn_control", "DesertBatflyInjury.TurnControl", injury.TurnControl)
            .Add("field.lift_control", "DesertBatflyInjury.LiftControl", injury.LiftControl)
            .Add("field.recovery_state", "DesertBatflyInjury.RecoveryState", injury.RecoveryState)
            .Add("field.recovery_target", "DesertBatflyInjury.RecoveryTarget",
                injury.RecoveryTarget.HasValue ? injury.RecoveryTarget.Value.ToString() : "—")
            .Add("field.last_injury_source", "DesertBatflyInjury.LastInjurySource", injury.LastInjurySource)
            .Add("field.last_injury_damage_type", "DesertBatflyInjury.LastInjuryDamageType", injury.LastInjuryDamageType)
            .Add("field.last_injury_tick", "DesertBatflyInjury.LastInjuryTick", injury.LastInjuryTick));

        snapshot.Sections.Add(new AIDebugSection("section.personality")
            .Add("field.sex", "DesertBatflyPersonality.Sex", p.Sex)
            .Add("field.temperament", "DesertBatflyPersonality.Temperament", p.Temperament)
            .Add("field.nerve", "DesertBatflyPersonality.Nerve", p.Nerve)
            .Add("field.conformity", "DesertBatflyPersonality.Conformity", p.Conformity)
            .Add("field.roost_affinity", "DesertBatflyPersonality.RoostAffinity", p.RoostAffinity)
            .Add("field.vengeance_affinity", "DesertBatflyPersonality.VengeanceAffinity", p.VengeanceAffinity)
            .Add("field.sand_affinity", "DesertBatflyPersonality.SandSpitAffinity", p.SandSpitAffinity)
            .Add("field.aggressive", "DesertBatflyPersonality.Aggressive", p.Aggressive));

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
            .Add("field.has_slot", "DesertBatflyAI.hasSlot", Read<bool>(HasSlotField, ai)));

        if (bat.room != null && DesertSwarmRoom.TryGet(bat.room, out DesertSwarmRoom colony))
        {
            DesertBatflyFlockSnapshot flock = colony.Flock;
            int age = colony.SnapshotAge;
            snapshot.Sections.Add(new AIDebugSection("section.flock")
                .Add("field.flock_center", "DesertBatflyFlockSnapshot.Center", flock.Center, age, "FlockSnapshot")
                .Add("field.flock_velocity", "DesertBatflyFlockSnapshot.AverageVelocity", flock.AverageVelocity, age, "FlockSnapshot")
                .Add("field.flock_active", "DesertBatflyFlockSnapshot.ActiveCount", flock.ActiveCount, age, "FlockSnapshot")
                .Add("field.panic_ratio", "DesertBatflyFlockSnapshot.PanicRatio", flock.PanicRatio, age, "FlockSnapshot")
                .Add("field.previous_panic", "DesertBatflyFlockSnapshot.PreviousPanicRatio", flock.PreviousPanicRatio, age, "FlockSnapshot")
                .Add("field.roost_ratio", "DesertBatflyFlockSnapshot.RoostRatio", flock.RoostRatio, age, "FlockSnapshot"));
        }

        snapshot.Sections.Add(new AIDebugSection("section.social")
            .Add("field.grab_memory", "DesertBatflyState.GrabMemoryStrength", state.GrabMemoryStrength)
            .Add("field.grief", "DesertBatflyState.GriefStrength", state.GriefStrength)
            .Add("field.player_trauma", "DesertBatflyState.PlayerTraumaStrength",
                state.PlayerTraumaTicks > 0 ? state.PlayerTraumaStrength : 0f)
            .Add("field.predator_trauma", "DesertBatflyState.PredatorTraumaStrength",
                state.PredatorTraumaTicks > 0 ? state.PredatorTraumaStrength : 0f)
            .Add("field.social_bond", "DesertBatflyState.SocialBondStrength", state.SocialBondStrength)
            .Add("field.social_bond_target", "DesertBatflyState.SocialBondTarget",
                state.SocialBondTarget.HasValue ? state.SocialBondTarget.Value.ToString() : "—"));

        snapshot.Sections.Add(new AIDebugSection("section.movement")
            .Add("field.position", "mainBodyChunk.pos", bat.mainBodyChunk?.pos)
            .Add("field.velocity", "mainBodyChunk.vel", bat.mainBodyChunk?.vel)
            .Add("field.escape_from", "DesertBatflyAI.escapeFrom", Read<Vector2>(EscapeFromField, ai))
            .Add("field.local_goal", "FlyAI.localGoal", bat.AI?.localGoal)
            .Add("field.behavior", "FlyAI.behavior", bat.AI?.behavior)
            .Add("field.flee_from_rain", "FlyAI.fleeFromRain", bat.AI?.fleeFromRain ?? false)
            .Add("field.lured_counter", "FlyAI.luredCounter", bat.AI?.luredCounter ?? 0));

        BuildDecisionStack(snapshot, bat);
        return snapshot;
    }

    private static void BuildDecisionStack(AIDebugSnapshot snapshot, DesertBatfly bat)
    {
        DesertBatflyInjury injury = bat.Injury;
        bool restrained = RestrainedByNonFly(bat);
        bool fear = DesertBatflyIntimidation.HasActiveFearSuppression(bat);
        float trauma = ActiveTrauma(bat);
        bool traumatized = trauma >= DesertBatflyTuning.TraumaAggressionBlock;
        bool vengeance = DesertBatflyIntimidation.IsExtremeVengeanceActive(bat);
        bool roost = bat.AI?.behavior == FlyAI.Behavior.Chain ||
                     bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost;

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.availability", AIDebugDecisionState.Active));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.conscious",
            bat.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked,
            null, "Creature.Consious", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.shortcut",
            bat.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            null, "Creature.inShortcut", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.restrained",
            restrained ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            restrained ? "held by non-Fly creature" : null,
            "Creature.grabbedBy", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.survival", AIDebugDecisionState.Active));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.danger",
            bat.DesertAI.HasImmediateDanger ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            bat.DesertAI.HasImmediateDanger ? "DesertBatflyAI.HasImmediateDanger=true" : null,
            "DesertBatflyAI.HasImmediateDanger", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.fear",
            fear ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            fear ? "DesertBatflyIntimidation fear gate" : null,
            "DesertBatflyIntimidation.HasActiveFearSuppression", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.trauma",
            traumatized ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            traumatized ? $"active trauma={trauma:0.000}" : null,
            "DesertBatflyState Trauma", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.physical_condition",
            injury.BlocksCombat ? AIDebugDecisionState.Warning : AIDebugDecisionState.Pass,
            injury.CombatBlockReason,
            "DesertBatflyInjury.BlocksCombat"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("field.health", AIDebugDecisionState.Active,
            $"{bat.DesertState.health:0.000}", "DesertBatflyState.health", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("field.left_wing_injury", AIDebugDecisionState.Active,
            $"{bat.DesertState.LeftWingInjury:0.000}", "DesertBatflyState.LeftWingInjury", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("field.right_wing_injury", AIDebugDecisionState.Active,
            $"{bat.DesertState.RightWingInjury:0.000}", "DesertBatflyState.RightWingInjury", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("field.post_stun_shock", AIDebugDecisionState.Active,
            $"{injury.PostStunShock:0.000}", "DesertBatflyInjury.PostStunShock", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("field.physical_capability", AIDebugDecisionState.Active,
            $"{injury.PhysicalCapability:0.000}", "DesertBatflyInjury.PhysicalCapability", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.injury_recovery",
            injury.IsRecovering ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            injury.RecoveryReason,
            "DesertBatflyInjury.RecoveryState"));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.special", AIDebugDecisionState.Active));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.injury",
            injury.BlocksCombat ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            injury.BlocksCombat ? injury.CombatBlockReason : null,
            "DesertBatflyInjury.BlocksCombat", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.grief",
            bat.DesertState.GriefStrength >= 0.30f ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            bat.DesertState.GriefStrength >= 0.30f ? $"grief={bat.DesertState.GriefStrength:0.000}" : null,
            "DesertBatflyState.GriefStrength", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.vengeance",
            vengeance ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            vengeance ? "Extreme Vengeance active" : null,
            "DesertBatflyIntimidation.IsExtremeVengeanceActive", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.roost",
            roost ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
            roost ? "FlyAI Chain / DesertBatflyAI Roost" : null,
            "FlyAI.behavior / DesertBatflyAI.Mode", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.retaliation_injury",
            injury.BlocksCombat ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Ready,
            injury.BlocksCombat ? injury.CombatBlockReason : null,
            "DesertBatflyInjury.BlocksCombat", 1));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.vengeance_injury",
            injury.BlocksCombat ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Ready,
            injury.BlocksCombat ? injury.CombatBlockReason : null,
            "DesertBatflyIntimidation.Update injury gate", 1));

        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.custom_ai", AIDebugDecisionState.Active,
            bat.DesertAI.Mode.ToString(), "DesertBatflyAI.Mode"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.vanilla_ai", AIDebugDecisionState.Ready,
            AIDebugFormat.Value(bat.AI?.behavior), "FlyAI.behavior"));
        snapshot.Decisions.Add(new AIDebugDecisionNode("decision.motor", AIDebugDecisionState.Active,
            AIDebugFormat.Value(bat.AI?.localGoal), "FlyAI.localGoal"));
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

    private static string ControlOwner(DesertBatfly bat)
    {
        if (bat == null) return "R3 / unresolved";
        if (!DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution))
            return "R3 / unresolved";
        int clock = bat.room?.game?.clock ?? int.MinValue;
        if (resolution.Clock != clock)
            return "R3 / unresolved";
        return $"{resolution.PrimaryOwner} / {resolution.WinningProposal.BehaviorKind}";
    }

    private static T Read<T>(FieldInfo field, object instance)
    {
        if (field == null || instance == null) return default;
        object value = field.GetValue(instance);
        return value is T typed ? typed : default;
    }
}
