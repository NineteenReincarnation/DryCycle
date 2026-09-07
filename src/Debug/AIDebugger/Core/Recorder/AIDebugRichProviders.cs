using System;
using System.Globalization;
using System.Reflection;
using DryCycle.Creatures;
using DryCycle.Creatures.DesertBatfly;
using DryCycle.Creatures.MossySpider;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal interface IAIDebugRecorderRichProvider
{
    AIDebugRichSchema Schema { get; }
    void Capture(
        AbstractCreature creature,
        RainWorldGame game,
        AIDebugRawSnapshotBuffer fields,
        AIDebugRawDecisionBuffer decisions,
        out int controlOwnerStringId);
}

internal static class AIDebugRecorderRichProviderRegistry
{
    private static readonly IAIDebugRecorderRichProvider Generic = new GenericRichProvider();
    private static readonly IAIDebugRecorderRichProvider DesertBat = new DesertBatflyRichProvider();
    private static readonly IAIDebugRecorderRichProvider Mossy = new MossySpiderRichProvider();
    private static readonly IAIDebugRecorderRichProvider Spineback = new SpinebackRichProvider();

    internal static IAIDebugRecorderRichProvider Resolve(AbstractCreature creature)
    {
        if (creature?.realizedCreature is DesertBatfly) return DesertBat;
        if (creature?.realizedCreature is MossySpider) return Mossy;
        if (creature?.realizedCreature is Lizard lizard &&
            SpinebackLizardEnums.Type != null && lizard.Template?.type == SpinebackLizardEnums.Type)
            return Spineback;
        return Generic;
    }

    internal static AIDebugSnapshot Materialize(
        DebugEntityKey key,
        AIDebugEntityState entityState,
        AIDebugRichSchema schema,
        AIDebugRawSnapshotBuffer fields,
        AIDebugRawDecisionBuffer decisions,
        int controlOwnerStringId)
    {
        if (schema == null || fields == null) return null;
        string display = schema.DisplayType + " #" + key.Number.ToString(CultureInfo.InvariantCulture);
        string owner = AIDebugRawStringTable.Resolve(controlOwnerStringId);
        var snapshot = new AIDebugSnapshot(key, display, entityState, string.IsNullOrEmpty(owner) ? "—" : owner);

        string sectionKey = null;
        AIDebugSection section = null;
        int fieldCount = Math.Min(schema.Fields.Length, fields.Count);
        for (int i = 0; i < fieldCount; i++)
        {
            if (!fields.IsValid(i)) continue;
            AIDebugFieldSchema spec = schema.Fields[i];
            if (section == null || !string.Equals(sectionKey, spec.SectionKey, StringComparison.Ordinal))
            {
                sectionKey = spec.SectionKey;
                section = new AIDebugSection(sectionKey);
                snapshot.Sections.Add(section);
            }
            section.Values.Add(new AIDebugValue(
                spec.LabelKey,
                spec.RawName,
                Format(fields.Get(i), spec),
                0,
                (spec.Flags & AIDebugFieldFlags.Exact) != 0 ? "V5 Exact/Heavy" : "V5 Retained/Heavy"));
        }

        int decisionCount = Math.Min(schema.Decisions.Length, decisions?.Count ?? 0);
        for (int i = 0; i < decisionCount; i++)
        {
            AIDebugDecisionSchema spec = schema.Decisions[i];
            AIDebugRawDecision value = decisions.Get(i);
            snapshot.Decisions.Add(new AIDebugDecisionNode(
                spec.LabelKey,
                value.State,
                AIDebugRawStringTable.Resolve(value.DetailStringId),
                spec.RawName,
                spec.Depth));
        }
        return snapshot;
    }

    private sealed class GenericRichProvider : IAIDebugRecorderRichProvider
    {
        private static readonly AIDebugRichSchema ProviderSchema = new(
            "generic.v5", "Creature",
            new[]
            {
                F(0,"section.identity","field.entity_id","AbstractCreature.ID",AIDebugRawValueKind.EntityId),
                F(1,"section.identity","field.template","CreatureTemplate.type",AIDebugRawValueKind.StringId),
                F(2,"section.identity","field.room","AbstractCreature.Room",AIDebugRawValueKind.StringId),
                F(3,"section.identity","field.coordinate","AbstractCreature.pos",AIDebugRawValueKind.Coordinate),
                F(4,"section.identity","field.entity_state","DebugEntityState",AIDebugRawValueKind.EnumToken),
                F(5,"section.state","field.dead","Creature.dead",AIDebugRawValueKind.Bool),
                F(6,"section.state","field.conscious","Creature.Consious",AIDebugRawValueKind.Bool),
                F(7,"section.state","field.in_shortcut","Creature.inShortcut",AIDebugRawValueKind.Bool),
                F(8,"section.state","field.in_den","AbstractCreature.InDen",AIDebugRawValueKind.Bool),
                F(9,"section.movement","field.position","mainBodyChunk.pos",AIDebugRawValueKind.Vector2),
                F(10,"section.movement","field.velocity","mainBodyChunk.vel",AIDebugRawValueKind.Vector2),
                F(11,"section.ai","field.abstract_ai","AbstractCreature.abstractAI",AIDebugRawValueKind.StringId),
                F(12,"section.ai","field.real_ai","ArtificialIntelligence",AIDebugRawValueKind.StringId),
                F(13,"section.ai","field.destination","AbstractCreatureAI.destination",AIDebugRawValueKind.Coordinate),
                F(14,"section.ai","field.pathfinder","ArtificialIntelligence.pathFinder",AIDebugRawValueKind.StringId),
                F(15,"section.ai","field.modules","ArtificialIntelligence.modules.Count",AIDebugRawValueKind.Int)
            },
            new[]
            {
                D("decision.availability","creature lifecycle"),
                D("decision.conscious","Creature.Consious",1),
                D("decision.shortcut","Creature.inShortcut",1)
            });

        public AIDebugRichSchema Schema => ProviderSchema;

        public void Capture(AbstractCreature creature, RainWorldGame game, AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions, out int controlOwnerStringId)
        {
            fields.Begin(ProviderSchema.Fields.Length);
            decisions.Begin(ProviderSchema.Decisions.Length);
            SetIdentity(fields, creature);
            Creature realized = creature?.realizedCreature;
            fields.Set(5, AIDebugRawValue.Bool(realized?.dead ?? creature?.state?.dead ?? false));
            fields.Set(6, AIDebugRawValue.Bool(realized?.Consious == true));
            fields.Set(7, AIDebugRawValue.Bool(realized?.inShortcut == true));
            fields.Set(8, AIDebugRawValue.Bool(creature?.InDen == true));
            if (realized?.mainBodyChunk != null)
            {
                fields.Set(9, Vec(realized.mainBodyChunk.pos));
                fields.Set(10, Vec(realized.mainBodyChunk.vel));
            }
            ArtificialIntelligence ai = creature?.abstractAI?.RealAI;
            fields.Set(11, Str(creature?.abstractAI?.GetType().Name));
            fields.Set(12, Str(ai?.GetType().Name));
            if (creature?.abstractAI != null) fields.Set(13, Coord(creature.abstractAI.destination));
            fields.Set(14, Str(ai?.pathFinder?.GetType().Name));
            fields.Set(15, AIDebugRawValue.Int(ai?.modules?.Count ?? 0));

            decisions.Set(0, AIDebugDecisionState.Active);
            decisions.Set(1, realized?.Consious == true ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked);
            decisions.Set(2, realized?.inShortcut == true ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive);
            controlOwnerStringId = AIDebugRawStringTable.Intern(realized == null ? "AbstractCreatureAI" : "ArtificialIntelligence / Creature");
        }
    }

    private sealed class DesertBatflyRichProvider : IAIDebugRecorderRichProvider
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo RetreatField = typeof(DesertBatflyAI).GetField("retreat", PrivateInstance);
        private static readonly FieldInfo MemoryField = typeof(DesertBatflyAI).GetField("memory", PrivateInstance);
        private static readonly FieldInfo InterestField = typeof(DesertBatflyAI).GetField("interest", PrivateInstance);
        private static readonly FieldInfo PursuitField = typeof(DesertBatflyAI).GetField("pursuit", PrivateInstance);
        private static readonly FieldInfo UnseenField = typeof(DesertBatflyAI).GetField("unseen", PrivateInstance);
        private static readonly FieldInfo HasSlotField = typeof(DesertBatflyAI).GetField("hasSlot", PrivateInstance);
        private static readonly FieldInfo EscapeFromField = typeof(DesertBatflyAI).GetField("escapeFrom", PrivateInstance);

        private static readonly AIDebugRichSchema ProviderSchema = new(
            "desert-batfly.v5", "DesertBatfly", BuildFields(),
            new[]
            {
                D("decision.availability","DesertBatfly lifecycle"),
                D("decision.conscious","Creature.Consious",1),
                D("decision.shortcut","Creature.inShortcut",1),
                D("decision.restrained","Creature.grabbedBy",1),
                D("decision.survival","DesertBatfly survival"),
                D("decision.danger","DesertBatflyAI.HasImmediateDanger",1),
                D("decision.fear","DesertBatflyIntimidation.HasActiveFearSuppression",1),
                D("decision.trauma","DesertBatflyState Trauma",1),
                D("decision.physical_condition","DesertBatflyInjury.BlocksCombat"),
                D("field.health","DesertBatflyState.health",1),
                D("field.left_wing_injury","DesertBatflyState.LeftWingInjury",1),
                D("field.right_wing_injury","DesertBatflyState.RightWingInjury",1),
                D("field.post_stun_shock","DesertBatflyInjury.PostStunShock",1),
                D("field.physical_capability","DesertBatflyInjury.PhysicalCapability",1),
                D("decision.injury_recovery","DesertBatflyInjury.RecoveryState"),
                D("decision.special","DesertBatfly special state"),
                D("decision.injury","DesertBatflyInjury.BlocksCombat",1),
                D("decision.grief","DesertBatflyState.GriefStrength",1),
                D("decision.vengeance","DesertBatflyIntimidation.IsExtremeVengeanceActive",1),
                D("decision.roost","FlyAI.behavior / DesertBatflyAI.Mode",1),
                D("decision.retaliation_injury","DesertBatflyInjury.BlocksCombat",1),
                D("decision.vengeance_injury","DesertBatflyIntimidation.Update injury gate",1),
                D("decision.custom_ai","DesertBatflyAI.Mode"),
                D("decision.vanilla_ai","FlyAI.behavior"),
                D("decision.motor","FlyAI.localGoal")
            });

        public AIDebugRichSchema Schema => ProviderSchema;

        public void Capture(AbstractCreature creature, RainWorldGame game, AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions, out int controlOwnerStringId)
        {
            fields.Begin(ProviderSchema.Fields.Length);
            decisions.Begin(ProviderSchema.Decisions.Length);
            if (creature?.realizedCreature is not DesertBatfly bat)
            {
                controlOwnerStringId = AIDebugRawStringTable.Intern("Unavailable");
                return;
            }

            DesertBatflyAI ai = bat.DesertAI;
            DesertBatflyState state = bat.DesertState;
            DesertBatflyPersonality p = bat.Personality;
            DesertBatflyInjury injury = bat.Injury;
            SetIdentity(fields, creature);

            fields.Set(5, AIDebugRawValue.Bool(bat.dead));
            fields.Set(6, AIDebugRawValue.Bool(bat.Consious));
            fields.Set(7, AIDebugRawValue.Bool(bat.inShortcut));
            fields.Set(8, AIDebugRawValue.Bool(creature.InDen));
            fields.Set(9, AIDebugRawValue.Float(state.Thirst));
            fields.Set(10, AIDebugRawValue.Int(state.Cooldown));
            fields.Set(11, AIDebugRawValue.Float(state.health));
            fields.Set(12, AIDebugRawValue.Float(state.LeftWingInjury));
            fields.Set(13, AIDebugRawValue.Float(state.RightWingInjury));
            fields.Set(14, AIDebugRawValue.Float(injury.WingMean));
            fields.Set(15, AIDebugRawValue.Float(injury.WingAsymmetry));
            fields.Set(16, AIDebugRawValue.Float(injury.WingBias));
            fields.Set(17, AIDebugRawValue.Float(injury.PostStunShock));
            fields.Set(18, AIDebugRawValue.Float(injury.PhysicalCapability));
            fields.Set(19, AIDebugRawValue.Float(injury.ForwardControl));
            fields.Set(20, AIDebugRawValue.Float(injury.TurnControl));
            fields.Set(21, AIDebugRawValue.Float(injury.LiftControl));
            fields.Set(22, Str(injury.RecoveryState.ToString()));
            if (injury.RecoveryTarget.HasValue) fields.Set(23, Vec(injury.RecoveryTarget.Value));
            fields.Set(24, Str(injury.LastInjurySource));
            fields.Set(25, Str(injury.LastInjuryDamageType));
            fields.Set(26, AIDebugRawValue.Int(injury.LastInjuryTick));
            fields.Set(27, Str(p.Sex.ToString()));
            fields.Set(28, AIDebugRawValue.Float(p.Temperament));
            fields.Set(29, AIDebugRawValue.Float(p.Nerve));
            fields.Set(30, AIDebugRawValue.Float(p.Conformity));
            fields.Set(31, AIDebugRawValue.Float(p.RoostAffinity));
            fields.Set(32, AIDebugRawValue.Float(p.VengeanceAffinity));
            fields.Set(33, AIDebugRawValue.Float(p.SandSpitAffinity));
            fields.Set(34, AIDebugRawValue.Bool(p.Aggressive));
            fields.Set(35, Str(ai.Mode.ToString()));
            if (ai.Target?.abstractCreature != null) fields.Set(36, Entity(ai.Target.abstractCreature.ID));
            fields.Set(37, AIDebugRawValue.Bool(ai.FormalAttack));
            fields.Set(38, AIDebugRawValue.Bool(ai.HasImmediateDanger));
            fields.Set(39, AIDebugRawValue.Int(Read<int>(RetreatField, ai)));
            fields.Set(40, AIDebugRawValue.Int(Read<int>(MemoryField, ai)));
            fields.Set(41, AIDebugRawValue.Int(Read<int>(InterestField, ai)));
            fields.Set(42, AIDebugRawValue.Int(Read<int>(PursuitField, ai)));
            fields.Set(43, AIDebugRawValue.Int(Read<int>(UnseenField, ai)));
            fields.Set(44, AIDebugRawValue.Bool(Read<bool>(HasSlotField, ai)));

            if (bat.room != null && DB_SwarmRoom.TryGet(bat.room, out DB_SwarmRoom colony))
            {
                DB_FlockSnapshot flock = colony.Flock;
                fields.Set(45, Vec(flock.Center));
                fields.Set(46, Vec(flock.AverageVelocity));
                fields.Set(47, AIDebugRawValue.Int(flock.ActiveCount));
                fields.Set(48, AIDebugRawValue.Float(flock.PanicRatio));
                fields.Set(49, AIDebugRawValue.Float(flock.PreviousPanicRatio));
                fields.Set(50, AIDebugRawValue.Float(flock.RoostRatio));
            }

            fields.Set(51, AIDebugRawValue.Float(state.GrabMemoryStrength));
            fields.Set(52, AIDebugRawValue.Float(state.GriefStrength));
            fields.Set(53, AIDebugRawValue.Float(state.PlayerTraumaTicks > 0 ? state.PlayerTraumaStrength : 0f));
            fields.Set(54, AIDebugRawValue.Float(state.PredatorTraumaTicks > 0 ? state.PredatorTraumaStrength : 0f));
            fields.Set(55, AIDebugRawValue.Float(state.SocialBondStrength));
            if (state.SocialBondTarget.HasValue) fields.Set(56, Entity(state.SocialBondTarget.Value));

            if (bat.mainBodyChunk != null)
            {
                fields.Set(57, Vec(bat.mainBodyChunk.pos));
                fields.Set(58, Vec(bat.mainBodyChunk.vel));
            }
            fields.Set(59, Vec(Read<Vector2>(EscapeFromField, ai)));
            if (bat.AI != null)
            {
                fields.Set(60, Vec(bat.AI.localGoal));
                fields.Set(61, Str(bat.AI.behavior?.value));
                fields.Set(62, AIDebugRawValue.Bool(bat.AI.fleeFromRain));
                fields.Set(63, AIDebugRawValue.Int(bat.AI.luredCounter));
            }

            bool restrained = RestrainedByNonFly(bat);
            bool fear = DesertBatflyIntimidation.HasActiveFearSuppression(bat);
            float trauma = ActiveTrauma(bat);
            bool traumatized = trauma >= DesertBatflyTuning.TraumaAggressionBlock;
            bool vengeance = DesertBatflyIntimidation.IsExtremeVengeanceActive(bat);
            bool roost = bat.AI?.behavior == FlyAI.Behavior.Chain || ai.Mode == DesertBatflyAI.Activity.Roost;

            decisions.Set(0, AIDebugDecisionState.Active);
            decisions.Set(1, bat.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked);
            decisions.Set(2, bat.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive);
            decisions.Set(3, restrained ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                restrained ? AIDebugRawStringTable.Intern("held by non-Fly creature") : 0);
            decisions.Set(4, AIDebugDecisionState.Active);
            decisions.Set(5, ai.HasImmediateDanger ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                ai.HasImmediateDanger ? AIDebugRawStringTable.Intern("immediate danger / escape gate") : 0);
            decisions.Set(6, fear ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                fear ? AIDebugRawStringTable.Intern("intimidation fear gate") : 0);
            decisions.Set(7, traumatized ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                traumatized ? AIDebugRawStringTable.Intern("trauma aggression gate") : 0);
            decisions.Set(8, injury.BlocksCombat ? AIDebugDecisionState.Warning : AIDebugDecisionState.Pass,
                injury.BlocksCombat ? AIDebugRawStringTable.Intern("combat blocked by injury/shock") : 0);
            decisions.Set(9, AIDebugDecisionState.Active, FloatDetail(state.health));
            decisions.Set(10, AIDebugDecisionState.Active, FloatDetail(state.LeftWingInjury));
            decisions.Set(11, AIDebugDecisionState.Active, FloatDetail(state.RightWingInjury));
            decisions.Set(12, AIDebugDecisionState.Active, FloatDetail(injury.PostStunShock));
            decisions.Set(13, AIDebugDecisionState.Active, FloatDetail(injury.PhysicalCapability));
            decisions.Set(14, injury.IsRecovering ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                injury.IsRecovering ? AIDebugRawStringTable.Intern(injury.RecoveryReason) : 0);
            decisions.Set(15, AIDebugDecisionState.Active);
            decisions.Set(16, injury.BlocksCombat ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive);
            decisions.Set(17, state.GriefStrength >= 0.30f ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                state.GriefStrength >= 0.30f ? FloatDetail(state.GriefStrength) : 0);
            decisions.Set(18, vengeance ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                vengeance ? AIDebugRawStringTable.Intern("Extreme Vengeance active") : 0);
            decisions.Set(19, roost ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive,
                roost ? AIDebugRawStringTable.Intern("FlyAI Chain / DesertBatflyAI Roost") : 0);
            decisions.Set(20, injury.BlocksCombat ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Ready);
            decisions.Set(21, injury.BlocksCombat ? AIDebugDecisionState.Blocked : AIDebugDecisionState.Ready);
            decisions.Set(22, AIDebugDecisionState.Active, AIDebugRawStringTable.Intern(ai.Mode.ToString()));
            decisions.Set(23, AIDebugDecisionState.Ready,
                bat.AI == null ? 0 : AIDebugRawStringTable.Intern(bat.AI.behavior?.value));
            decisions.Set(24, AIDebugDecisionState.Active,
                bat.AI == null ? 0 : AIDebugRawStringTable.Intern(
                    "(" + bat.AI.localGoal.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                    bat.AI.localGoal.y.ToString("0.0", CultureInfo.InvariantCulture) + ")"));

            controlOwnerStringId = ControlOwnerId(bat, restrained, fear, trauma, vengeance, roost);
        }

        private static AIDebugFieldSchema[] BuildFields() => new[]
        {
            F(0,"section.identity","field.entity_id","AbstractCreature.ID",AIDebugRawValueKind.EntityId),
            F(1,"section.identity","field.template","CreatureTemplate.type",AIDebugRawValueKind.StringId),
            F(2,"section.identity","field.room","AbstractCreature.Room",AIDebugRawValueKind.StringId),
            F(3,"section.identity","field.coordinate","AbstractCreature.pos",AIDebugRawValueKind.Coordinate),
            F(4,"section.identity","field.entity_state","DebugEntityState",AIDebugRawValueKind.EnumToken),
            F(5,"section.state","field.dead","Creature.dead",AIDebugRawValueKind.Bool),
            F(6,"section.state","field.conscious","Creature.Consious",AIDebugRawValueKind.Bool),
            F(7,"section.state","field.in_shortcut","Creature.inShortcut",AIDebugRawValueKind.Bool),
            F(8,"section.state","field.in_den","AbstractCreature.InDen",AIDebugRawValueKind.Bool),
            F(9,"section.state","field.thirst","DesertBatflyState.Thirst",AIDebugRawValueKind.Float,true),
            F(10,"section.state","field.cooldown","DesertBatflyState.Cooldown",AIDebugRawValueKind.Int,true),
            F(11,"section.injury","field.health","DesertBatflyState.health",AIDebugRawValueKind.Float,true),
            F(12,"section.injury","field.left_wing_injury","DesertBatflyState.LeftWingInjury",AIDebugRawValueKind.Float,true),
            F(13,"section.injury","field.right_wing_injury","DesertBatflyState.RightWingInjury",AIDebugRawValueKind.Float,true),
            F(14,"section.injury","field.wing_mean","DesertBatflyInjury.WingMean",AIDebugRawValueKind.Float,true),
            F(15,"section.injury","field.wing_asymmetry","DesertBatflyInjury.WingAsymmetry",AIDebugRawValueKind.Float,true),
            F(16,"section.injury","field.wing_bias","DesertBatflyInjury.WingBias",AIDebugRawValueKind.Float,true),
            F(17,"section.injury","field.post_stun_shock","DesertBatflyInjury.PostStunShock",AIDebugRawValueKind.Float,true),
            F(18,"section.injury","field.physical_capability","DesertBatflyInjury.PhysicalCapability",AIDebugRawValueKind.Float,true),
            F(19,"section.injury","field.forward_control","DesertBatflyInjury.ForwardControl",AIDebugRawValueKind.Float,true),
            F(20,"section.injury","field.turn_control","DesertBatflyInjury.TurnControl",AIDebugRawValueKind.Float,true),
            F(21,"section.injury","field.lift_control","DesertBatflyInjury.LiftControl",AIDebugRawValueKind.Float,true),
            F(22,"section.injury","field.recovery_state","DesertBatflyInjury.RecoveryState",AIDebugRawValueKind.StringId,true),
            F(23,"section.injury","field.recovery_target","DesertBatflyInjury.RecoveryTarget",AIDebugRawValueKind.Vector2,true),
            F(24,"section.injury","field.last_injury_source","DesertBatflyInjury.LastInjurySource",AIDebugRawValueKind.StringId,true),
            F(25,"section.injury","field.last_injury_damage_type","DesertBatflyInjury.LastInjuryDamageType",AIDebugRawValueKind.StringId,true),
            F(26,"section.injury","field.last_injury_tick","DesertBatflyInjury.LastInjuryTick",AIDebugRawValueKind.Int,true),
            F(27,"section.personality","field.sex","DesertBatflyPersonality.Sex",AIDebugRawValueKind.StringId,true),
            F(28,"section.personality","field.temperament","DesertBatflyPersonality.Temperament",AIDebugRawValueKind.Float,true),
            F(29,"section.personality","field.nerve","DesertBatflyPersonality.Nerve",AIDebugRawValueKind.Float,true),
            F(30,"section.personality","field.conformity","DesertBatflyPersonality.Conformity",AIDebugRawValueKind.Float,true),
            F(31,"section.personality","field.roost_affinity","DesertBatflyPersonality.RoostAffinity",AIDebugRawValueKind.Float,true),
            F(32,"section.personality","field.vengeance_affinity","DesertBatflyPersonality.VengeanceAffinity",AIDebugRawValueKind.Float,true),
            F(33,"section.personality","field.sand_affinity","DesertBatflyPersonality.SandSpitAffinity",AIDebugRawValueKind.Float,true),
            F(34,"section.personality","field.aggressive","DesertBatflyPersonality.Aggressive",AIDebugRawValueKind.Bool,true),
            F(35,"section.ai","field.mode","DesertBatflyAI.Mode",AIDebugRawValueKind.StringId,true),
            F(36,"section.ai","field.target","DesertBatflyAI.Target",AIDebugRawValueKind.EntityId,true),
            F(37,"section.ai","field.formal_attack","DesertBatflyAI.FormalAttack",AIDebugRawValueKind.Bool,true),
            F(38,"section.ai","field.immediate_danger","DesertBatflyAI.HasImmediateDanger",AIDebugRawValueKind.Bool,true),
            F(39,"section.ai","field.retreat","DesertBatflyAI.retreat",AIDebugRawValueKind.Int,true),
            F(40,"section.ai","field.memory","DesertBatflyAI.memory",AIDebugRawValueKind.Int,true),
            F(41,"section.ai","field.interest","DesertBatflyAI.interest",AIDebugRawValueKind.Int,true),
            F(42,"section.ai","field.pursuit","DesertBatflyAI.pursuit",AIDebugRawValueKind.Int,true),
            F(43,"section.ai","field.unseen","DesertBatflyAI.unseen",AIDebugRawValueKind.Int,true),
            F(44,"section.ai","field.has_slot","DesertBatflyAI.hasSlot",AIDebugRawValueKind.Bool,true),
            F(45,"section.flock","field.flock_center","DB_FlockSnapshot.Center",AIDebugRawValueKind.Vector2,true),
            F(46,"section.flock","field.flock_velocity","DB_FlockSnapshot.AverageVelocity",AIDebugRawValueKind.Vector2,true),
            F(47,"section.flock","field.flock_active","DB_FlockSnapshot.ActiveCount",AIDebugRawValueKind.Int,true),
            F(48,"section.flock","field.panic_ratio","DB_FlockSnapshot.PanicRatio",AIDebugRawValueKind.Float,true),
            F(49,"section.flock","field.previous_panic","DB_FlockSnapshot.PreviousPanicRatio",AIDebugRawValueKind.Float,true),
            F(50,"section.flock","field.roost_ratio","DB_FlockSnapshot.RoostRatio",AIDebugRawValueKind.Float,true),
            F(51,"section.social","field.grab_memory","DesertBatflyState.GrabMemoryStrength",AIDebugRawValueKind.Float,true),
            F(52,"section.social","field.grief","DesertBatflyState.GriefStrength",AIDebugRawValueKind.Float,true),
            F(53,"section.social","field.player_trauma","DesertBatflyState.PlayerTraumaStrength",AIDebugRawValueKind.Float,true),
            F(54,"section.social","field.predator_trauma","DesertBatflyState.PredatorTraumaStrength",AIDebugRawValueKind.Float,true),
            F(55,"section.social","field.social_bond","DesertBatflyState.SocialBondStrength",AIDebugRawValueKind.Float,true),
            F(56,"section.social","field.social_bond_target","DesertBatflyState.SocialBondTarget",AIDebugRawValueKind.EntityId,true),
            F(57,"section.movement","field.position","mainBodyChunk.pos",AIDebugRawValueKind.Vector2,true),
            F(58,"section.movement","field.velocity","mainBodyChunk.vel",AIDebugRawValueKind.Vector2,true),
            F(59,"section.movement","field.escape_from","DesertBatflyAI.escapeFrom",AIDebugRawValueKind.Vector2,true),
            F(60,"section.movement","field.local_goal","FlyAI.localGoal",AIDebugRawValueKind.Vector2,true),
            F(61,"section.movement","field.behavior","FlyAI.behavior",AIDebugRawValueKind.StringId,true),
            F(62,"section.movement","field.flee_from_rain","FlyAI.fleeFromRain",AIDebugRawValueKind.Bool,true),
            F(63,"section.movement","field.lured_counter","FlyAI.luredCounter",AIDebugRawValueKind.Int,true)
        };

        private static int FloatDetail(float value) =>
            AIDebugRawStringTable.Intern(value.ToString("0.000", CultureInfo.InvariantCulture));

        private static int ControlOwnerId(DesertBatfly bat, bool restrained, bool fear, float trauma, bool vengeance, bool roost)
        {
            string owner;
            if (bat.dead || !bat.Consious) owner = "Creature / Physics";
            else if (bat.inShortcut) owner = "Shortcut";
            else if (restrained) owner = "Grasp / Restraint";
            else if (bat.Emergence?.Active == true) owner = "Emergence";
            else if (bat.AI == null || bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
                     bat.AI.luredCounter > 0 || bat.safariControlled) owner = "Vanilla FlyAI";
            else if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape) owner = "Danger / Escape";
            else if (bat.Injury.IsRecovering || bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery) owner = "Injury Recovery";
            else if (vengeance) owner = "Vengeance";
            else if (trauma >= DesertBatflyTuning.TraumaAggressionBlock) owner = "Trauma";
            else if (bat.DesertState.GriefStrength >= 0.30f) owner = "Grief";
            else if (fear) owner = "Fear / Intimidation";
            else if (roost) owner = "Roost / Chain";
            else owner = "DesertBatflyAI";
            return AIDebugRawStringTable.Intern(owner);
        }

        private static bool RestrainedByNonFly(DesertBatfly bat)
        {
            if (bat?.grabbedBy == null) return false;
            for (int i = 0; i < bat.grabbedBy.Count; i++)
            {
                Creature.Grasp grasp = bat.grabbedBy[i];
                if (grasp?.grabber != null && grasp.grabber is not Fly) return true;
            }
            return false;
        }

        private static float ActiveTrauma(DesertBatfly bat) => Mathf.Max(
            bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
            bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);

        private static T Read<T>(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return default;
            object value = field.GetValue(instance);
            return value is T typed ? typed : default;
        }
    }

    private sealed class MossySpiderRichProvider : IAIDebugRecorderRichProvider
    {
        private static readonly AIDebugRichSchema ProviderSchema = new(
            "mossy-spider.v5", "MossySpider",
            new[]
            {
                F(0,"section.identity","field.entity_id","AbstractCreature.ID",AIDebugRawValueKind.EntityId),
                F(1,"section.identity","field.template","CreatureTemplate.type",AIDebugRawValueKind.StringId),
                F(2,"section.identity","field.room","AbstractCreature.Room",AIDebugRawValueKind.StringId),
                F(3,"section.identity","field.coordinate","AbstractCreature.pos",AIDebugRawValueKind.Coordinate),
                F(4,"section.identity","field.entity_state","DebugEntityState",AIDebugRawValueKind.EnumToken),
                F(5,"section.state","field.dead","MossySpider.dead",AIDebugRawValueKind.Bool,true),
                F(6,"section.state","field.conscious","MossySpider.Consious",AIDebugRawValueKind.Bool,true),
                F(7,"section.state","field.in_shortcut","MossySpider.inShortcut",AIDebugRawValueKind.Bool,true),
                F(8,"section.state","field.position","MossySpider.BodyCenter",AIDebugRawValueKind.Vector2,true),
                F(9,"section.state","field.velocity","MossySpider.mainBodyChunk.vel",AIDebugRawValueKind.Vector2,true),
                F(10,"section.ai","field.abstract_ai","MossySpiderAbstractAI",AIDebugRawValueKind.StringId,true),
                F(11,"section.ai","field.real_ai","MossySpiderAI",AIDebugRawValueKind.StringId,true),
                F(12,"section.ai","field.behavior","MossySpiderAI.CurrentBehavior",AIDebugRawValueKind.StringId,true),
                F(13,"section.ai","field.destination","MossySpiderAbstractAI.RoamTarget",AIDebugRawValueKind.Coordinate,true),
                F(14,"section.ai","field.pathfinder","MossySpiderAI.Pather",AIDebugRawValueKind.StringId,true),
                F(15,"section.ai","field.modules","ArtificialIntelligence.modules.Count",AIDebugRawValueKind.Int,true),
                F(16,"section.movement","field.local_goal","MossySpiderPather.GetDestination",AIDebugRawValueKind.Coordinate,true),
                F(17,"section.movement","field.velocity","MossySpider.MoveDirection",AIDebugRawValueKind.Vector2,true),
                F(18,"section.movement","field.interest","MossySpider.GaitCycle",AIDebugRawValueKind.Float,true),
                F(19,"section.movement","field.panic_ratio","MossySpider.GroundSupport",AIDebugRawValueKind.Float,true),
                F(20,"section.movement","field.thirst","MossySpider.SwimFactor",AIDebugRawValueKind.Float,true)
            },
            new[]
            {
                D("decision.availability","MossySpider lifecycle"),
                D("decision.conscious","MossySpider.Consious",1),
                D("decision.shortcut","MossySpider.inShortcut",1),
                D("decision.motor","MossySpiderAI + MossySpiderPather")
            });

        public AIDebugRichSchema Schema => ProviderSchema;

        public void Capture(AbstractCreature creature, RainWorldGame game, AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions, out int controlOwnerStringId)
        {
            fields.Begin(ProviderSchema.Fields.Length);
            decisions.Begin(ProviderSchema.Decisions.Length);
            if (creature?.realizedCreature is not MossySpider spider)
            {
                controlOwnerStringId = AIDebugRawStringTable.Intern("Unavailable");
                return;
            }
            MossySpiderAI ai = spider.AI;
            MossySpiderAbstractAI abstractAI = creature.abstractAI as MossySpiderAbstractAI;
            SetIdentity(fields, creature);
            fields.Set(5, AIDebugRawValue.Bool(spider.dead));
            fields.Set(6, AIDebugRawValue.Bool(spider.Consious));
            fields.Set(7, AIDebugRawValue.Bool(spider.inShortcut));
            fields.Set(8, Vec(spider.BodyCenter));
            if (spider.mainBodyChunk != null) fields.Set(9, Vec(spider.mainBodyChunk.vel));
            fields.Set(10, Str(abstractAI?.GetType().Name));
            fields.Set(11, Str(ai?.GetType().Name));
            if (ai != null)
            {
                fields.Set(12, Str(ai.CurrentBehavior.ToString()));
                fields.Set(14, Str(ai.Pather?.GetType().Name));
                fields.Set(15, AIDebugRawValue.Int(ai.modules?.Count ?? 0));
                fields.Set(16, Coord(ai.Pather.GetDestination));
            }
            if (abstractAI?.RoamTarget.HasValue == true) fields.Set(13, Coord(abstractAI.RoamTarget.Value));
            fields.Set(17, Vec(spider.MoveDirection));
            fields.Set(18, AIDebugRawValue.Float(spider.GaitCycle));
            fields.Set(19, AIDebugRawValue.Float(spider.GroundSupport));
            fields.Set(20, AIDebugRawValue.Float(spider.SwimFactor));

            decisions.Set(0, AIDebugDecisionState.Active);
            decisions.Set(1, spider.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked);
            decisions.Set(2, spider.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive);
            decisions.Set(3, ai?.CurrentBehavior == MossySpiderAI.Behavior.Roaming
                ? AIDebugDecisionState.Active : AIDebugDecisionState.Ready,
                ai == null ? 0 : AIDebugRawStringTable.Intern(ai.CurrentBehavior.ToString()));
            controlOwnerStringId = AIDebugRawStringTable.Intern(ai != null
                ? "MossySpiderAI / locomotion" : "MossySpiderAbstractAI");
        }
    }

    private sealed class SpinebackRichProvider : IAIDebugRecorderRichProvider
    {
        private static readonly AIDebugRichSchema ProviderSchema = new(
            "spineback-lizard.v5", "SpinebackLizard",
            new[]
            {
                F(0,"section.identity","field.entity_id","AbstractCreature.ID",AIDebugRawValueKind.EntityId),
                F(1,"section.identity","field.template","CreatureTemplate.type",AIDebugRawValueKind.StringId),
                F(2,"section.identity","field.room","AbstractCreature.Room",AIDebugRawValueKind.StringId),
                F(3,"section.identity","field.coordinate","AbstractCreature.pos",AIDebugRawValueKind.Coordinate),
                F(4,"section.identity","field.entity_state","DebugEntityState",AIDebugRawValueKind.EnumToken),
                F(5,"section.state","field.dead","Lizard.dead",AIDebugRawValueKind.Bool,true),
                F(6,"section.state","field.conscious","Lizard.Consious",AIDebugRawValueKind.Bool,true),
                F(7,"section.state","field.in_shortcut","Lizard.inShortcut",AIDebugRawValueKind.Bool,true),
                F(8,"section.state","field.position","Lizard.mainBodyChunk.pos",AIDebugRawValueKind.Vector2,true),
                F(9,"section.state","field.velocity","Lizard.mainBodyChunk.vel",AIDebugRawValueKind.Vector2,true),
                F(10,"section.ai","field.abstract_ai","AbstractCreature.abstractAI",AIDebugRawValueKind.StringId,true),
                F(11,"section.ai","field.real_ai","LizardAI",AIDebugRawValueKind.StringId,true),
                F(12,"section.ai","field.behavior","LizardAI.behavior",AIDebugRawValueKind.StringId,true),
                F(13,"section.ai","field.destination","AbstractCreatureAI.destination",AIDebugRawValueKind.Coordinate,true),
                F(14,"section.ai","field.pathfinder","ArtificialIntelligence.pathFinder",AIDebugRawValueKind.StringId,true),
                F(15,"section.ai","field.modules","ArtificialIntelligence.modules.Count",AIDebugRawValueKind.Int,true),
                F(16,"section.movement","field.position","Lizard.mainBodyChunk.pos",AIDebugRawValueKind.Vector2,true),
                F(17,"section.movement","field.velocity","Lizard.mainBodyChunk.vel",AIDebugRawValueKind.Vector2,true),
                F(18,"section.movement","field.behavior","Spineback compatibility",AIDebugRawValueKind.StringId,true)
            },
            new[]
            {
                D("decision.availability","Spineback lifecycle"),
                D("decision.conscious","Lizard.Consious",1),
                D("decision.shortcut","Lizard.inShortcut",1),
                D("decision.motor","SpinebackLizardHooks + LizardAI")
            });

        public AIDebugRichSchema Schema => ProviderSchema;

        public void Capture(AbstractCreature creature, RainWorldGame game, AIDebugRawSnapshotBuffer fields,
            AIDebugRawDecisionBuffer decisions, out int controlOwnerStringId)
        {
            fields.Begin(ProviderSchema.Fields.Length);
            decisions.Begin(ProviderSchema.Decisions.Length);
            if (creature?.realizedCreature is not Lizard lizard)
            {
                controlOwnerStringId = AIDebugRawStringTable.Intern("Unavailable");
                return;
            }
            LizardAI ai = creature.abstractAI?.RealAI as LizardAI;
            SetIdentity(fields, creature);
            fields.Set(5, AIDebugRawValue.Bool(lizard.dead));
            fields.Set(6, AIDebugRawValue.Bool(lizard.Consious));
            fields.Set(7, AIDebugRawValue.Bool(lizard.inShortcut));
            if (lizard.mainBodyChunk != null)
            {
                fields.Set(8, Vec(lizard.mainBodyChunk.pos));
                fields.Set(9, Vec(lizard.mainBodyChunk.vel));
                fields.Set(16, Vec(lizard.mainBodyChunk.pos));
                fields.Set(17, Vec(lizard.mainBodyChunk.vel));
            }
            fields.Set(10, Str(creature.abstractAI?.GetType().Name));
            fields.Set(11, Str(ai?.GetType().Name));
            fields.Set(12, Str(ai?.behavior?.value));
            if (creature.abstractAI != null) fields.Set(13, Coord(creature.abstractAI.destination));
            fields.Set(14, Str(ai?.pathFinder?.GetType().Name));
            fields.Set(15, AIDebugRawValue.Int(ai?.modules?.Count ?? 0));
            fields.Set(18, Str("Green Lizard AI baseline"));

            decisions.Set(0, AIDebugDecisionState.Active);
            decisions.Set(1, lizard.Consious ? AIDebugDecisionState.Pass : AIDebugDecisionState.Blocked);
            decisions.Set(2, lizard.inShortcut ? AIDebugDecisionState.Active : AIDebugDecisionState.Inactive);
            decisions.Set(3, AIDebugDecisionState.Active,
                ai?.behavior == null ? 0 : AIDebugRawStringTable.Intern(ai.behavior.value));
            controlOwnerStringId = AIDebugRawStringTable.Intern("LizardAI / Green baseline");
        }
    }

    private static void SetIdentity(AIDebugRawSnapshotBuffer fields, AbstractCreature creature)
    {
        if (creature == null) return;
        fields.Set(0, Entity(creature.ID));
        fields.Set(1, Str(creature.creatureTemplate?.type?.value));
        fields.Set(2, Str(creature.Room?.name));
        fields.Set(3, Coord(creature.pos));
        fields.Set(4, AIDebugRawValue.EnumToken((int)AIDebugRegistry.EntityState(creature)));
    }

    private static AIDebugRawValue Entity(EntityID id) => AIDebugRawValue.EntityId(id.spawner, id.number);
    private static AIDebugRawValue Coord(WorldCoordinate c) => AIDebugRawValue.Coordinate(c.room, c.x, c.y, c.abstractNode);
    private static AIDebugRawValue Vec(Vector2 v) => AIDebugRawValue.Vector2(v.x, v.y);
    private static AIDebugRawValue Str(string value) => AIDebugRawValue.StringId(AIDebugRawStringTable.Intern(value));

    private static AIDebugFieldSchema F(int id, string section, string label, string raw,
        AIDebugRawValueKind kind, bool species = false) =>
        new((ushort)id, section, label, raw, kind,
            species ? AIDebugFieldFlags.Retained | AIDebugFieldFlags.Species : AIDebugFieldFlags.Exact);

    private static AIDebugDecisionSchema D(string label, string raw = null, int depth = 0) => new(label, raw, depth);

    private static string Format(AIDebugRawValue value, AIDebugFieldSchema schema)
    {
        switch (value.Kind)
        {
            case AIDebugRawValueKind.Bool:
                return value.I0 != 0 ? "true" : "false";
            case AIDebugRawValueKind.Int:
                return value.I0.ToString(CultureInfo.InvariantCulture);
            case AIDebugRawValueKind.Float:
                return value.F0.ToString("0.###", CultureInfo.InvariantCulture);
            case AIDebugRawValueKind.Vector2:
                return "(" + value.F0.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                       value.F1.ToString("0.0", CultureInfo.InvariantCulture) + ")";
            case AIDebugRawValueKind.EntityId:
                return value.I0.ToString(CultureInfo.InvariantCulture) + ":" + value.I1.ToString(CultureInfo.InvariantCulture);
            case AIDebugRawValueKind.EnumToken:
                if (string.Equals(schema.RawName, "DebugEntityState", StringComparison.Ordinal) &&
                    Enum.IsDefined(typeof(AIDebugEntityState), value.I0))
                    return AIDebugLocalization.EntityState((AIDebugEntityState)value.I0);
                return value.I0.ToString(CultureInfo.InvariantCulture);
            case AIDebugRawValueKind.Flags:
                return ((AIDebugFastFlags)value.I0).ToString();
            case AIDebugRawValueKind.StringId:
            {
                string text = AIDebugRawStringTable.Resolve(value.I0);
                return string.IsNullOrEmpty(text) ? "—" : text;
            }
            case AIDebugRawValueKind.Coordinate:
                return value.I0.ToString(CultureInfo.InvariantCulture) + ":" +
                       value.I1.ToString(CultureInfo.InvariantCulture) + "," +
                       value.I2.ToString(CultureInfo.InvariantCulture) + "/" +
                       value.I3.ToString(CultureInfo.InvariantCulture);
            default:
                return "—";
        }
    }
}
