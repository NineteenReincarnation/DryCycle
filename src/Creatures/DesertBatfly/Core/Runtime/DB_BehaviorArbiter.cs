using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Central R3 owner resolver. It chooses exactly one PrimaryOwner from read-only proposals.
/// Proposal construction does not execute movement. R3 migration progressively moves each
/// domain's existing executor behind the winner selected here.
/// </summary>
internal static class DB_BehaviorArbiter
{
    private sealed class State
    {
        internal int Clock = int.MinValue;
        internal DB_BehaviorResolution Resolution;
        internal readonly List<DB_BehaviorProposal> Proposals = new(16);
        internal readonly List<DB_BehaviorRejection> Rejected = new(16);
    }

    private static ConditionalWeakTable<DB_Creature, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DB_Creature, State>();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static int PriorityOf(DB_BehaviorOwner owner) => owner switch
    {
        DB_BehaviorOwner.CreaturePhysics => 10,
        DB_BehaviorOwner.Restraint => 20,
        DB_BehaviorOwner.Shortcut => 20,
        DB_BehaviorOwner.Emergence => 20,
        DB_BehaviorOwner.NativeSpecial => 25,
        DB_BehaviorOwner.ImmediateDanger => 30,
        DB_BehaviorOwner.InjuryRecovery => 40,
        DB_BehaviorOwner.Travel => 50,
        DB_BehaviorOwner.EnvironmentHardSurvival => 60,
        DB_BehaviorOwner.FearResponse => 70,
        DB_BehaviorOwner.Vengeance => 80,
        DB_BehaviorOwner.EnvironmentLocalSurvival => 90,
        DB_BehaviorOwner.ImmediateProjectileEvade => 100,
        DB_BehaviorOwner.Combat => 110,
        DB_BehaviorOwner.Roost => 120,
        DB_BehaviorOwner.Social => 130,
        DB_BehaviorOwner.Ordinary => 140,
        DB_BehaviorOwner.VanillaFallback => 150,
        _ => int.MaxValue
    };

    internal static DB_BehaviorResolution ResolveFrame(
        DB_Creature bat,
        DB_BehaviorOwner excludedOwner = DB_BehaviorOwner.None,
        string excludedReason = null)
    {
        if (bat == null) return default;
        DB_FrameContext frame = DB_FrameContextRuntime.For(bat, refresh: true);
        State state = states.GetOrCreateValue(bat);
        state.Proposals.Clear();
        state.Rejected.Clear();
        BuildProposals(frame, state.Proposals);

        DB_BehaviorProposal winner = ResolveWinnerCore(
            state.Proposals,
            excludedOwner,
            state.Rejected,
            excludedReason);
        if (!winner.Valid)
        {
            winner = DB_BehaviorProposal.Create(
                DB_BehaviorOwner.VanillaFallback,
                DB_BehaviorKind.Native,
                "no accepted proposal; vanilla fallback",
                frame.CurrentGoal,
                desiredNativeBehavior: frame.NativeBehavior,
                preserveGoal: true);
        }

        for (int i = 0; i < state.Proposals.Count; i++)
        {
            DB_BehaviorProposal proposal = state.Proposals[i];
            if (!proposal.Valid || proposal.Owner == winner.Owner) continue;
            bool alreadyRejected = false;
            for (int r = 0; r < state.Rejected.Count; r++)
            {
                if (state.Rejected[r].Owner != proposal.Owner) continue;
                alreadyRejected = true;
                break;
            }
            if (!alreadyRejected)
                state.Rejected.Add(new DB_BehaviorRejection(
                    proposal.Owner,
                    proposal.Priority,
                    $"rejected by higher-priority {winner.Owner}"));
        }

        Vector2? finalGoal = winner.Goal ?? frame.CurrentGoal;
        FlyAI.Behavior finalBehavior = winner.DesiredNativeBehavior ?? frame.NativeBehavior;
        int finalDijkstra = winner.DesiredDijkstraMap;
        state.Resolution = new DB_BehaviorResolution(
            frame.Clock,
            winner,
            finalGoal,
            finalBehavior,
            finalDijkstra,
            frame.SpecialPhysicsOwner,
            winner.Reason);
        state.Clock = frame.Clock;
        return state.Resolution;
    }

    internal static bool TryGetResolution(DB_Creature bat, out DB_BehaviorResolution resolution)
    {
        resolution = default;
        if (bat == null || !states.TryGetValue(bat, out State state) || !state.Resolution.Resolved)
            return false;
        resolution = state.Resolution;
        return true;
    }

    internal static bool IsPrimaryOwner(DB_Creature bat, DB_BehaviorOwner owner)
    {
        if (bat?.room == null || !states.TryGetValue(bat, out State state)) return false;
        int clock = bat.room.game?.clock ?? int.MinValue;
        return state.Clock == clock && state.Resolution.PrimaryOwner == owner;
    }

    internal static bool TryGetDebugState(DB_Creature bat, out DB_BehaviorArbiterDebugState debug)
    {
        debug = default;
        if (bat == null || !states.TryGetValue(bat, out State state) || !state.Resolution.Resolved)
            return false;
        debug = new DB_BehaviorArbiterDebugState(
            state.Resolution,
            state.Rejected.Count == 0 ? Array.Empty<DB_BehaviorRejection>() : state.Rejected.ToArray());
        return true;
    }

    /// <summary>Pure winner selection used by managed regression tests.</summary>
    internal static DB_BehaviorProposal ResolveWinner(IReadOnlyList<DB_BehaviorProposal> proposals)
        => ResolveWinnerCore(proposals, DB_BehaviorOwner.None, null, null);

    private static DB_BehaviorProposal ResolveWinnerCore(
        IReadOnlyList<DB_BehaviorProposal> proposals,
        DB_BehaviorOwner excludedOwner,
        List<DB_BehaviorRejection> rejected,
        string excludedReason)
    {
        DB_BehaviorProposal winner = default;
        if (proposals == null) return winner;
        for (int i = 0; i < proposals.Count; i++)
        {
            DB_BehaviorProposal proposal = proposals[i];
            if (!proposal.Valid) continue;
            if (proposal.Owner == excludedOwner)
            {
                rejected?.Add(new DB_BehaviorRejection(
                    proposal.Owner,
                    proposal.Priority,
                    string.IsNullOrEmpty(excludedReason)
                        ? "proposal explicitly excluded after executor refusal"
                        : excludedReason));
                continue;
            }
            if (!winner.Valid || proposal.Priority < winner.Priority ||
                (proposal.Priority == winner.Priority && proposal.Commitment > winner.Commitment) ||
                (proposal.Priority == winner.Priority &&
                 Mathf.Approximately(proposal.Commitment, winner.Commitment) &&
                 (int)proposal.Owner < (int)winner.Owner))
                winner = proposal;
        }
        return winner;
    }

    private static void BuildProposals(
        in DB_FrameContext frame,
        List<DB_BehaviorProposal> proposals)
    {
        DB_Creature bat = frame.Bat;
        DB_AI ai = bat?.DesertAI;

        if (frame.Dead || !frame.Conscious)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.CreaturePhysics,
                DB_BehaviorKind.Disabled,
                frame.Dead ? "dead: creature/physics owns frame" : "unconscious: creature/physics owns frame",
                suppressCombat: true,
                suppressSocial: true,
                commitment: 1f));

        if (frame.Restrained)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Restraint,
                DB_BehaviorKind.Passive,
                "restrained by non-Fly grasp",
                suppressCombat: true,
                suppressSocial: true,
                commitment: 1f));
        if (frame.InShortcut)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Shortcut,
                DB_BehaviorKind.Native,
                "shortcut transit owns movement",
                suppressCombat: true,
                suppressSocial: true,
                commitment: 1f));
        if (bat?.Emergence?.Active == true)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Emergence,
                DB_BehaviorKind.Native,
                "hive emergence special movement",
                suppressCombat: true,
                suppressSocial: true,
                commitment: 1f));

        bool nativeSpecial = bat?.AI == null || bat.safariControlled ||
                             bat.AI.fleeFromRain || bat.AI.luredCounter > 0 ||
                             bat.AI.behavior == FlyAI.Behavior.Burrow;
        if (nativeSpecial)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.NativeSpecial,
                DB_BehaviorKind.Native,
                "native FlyAI special state",
                frame.CurrentGoal,
                desiredNativeBehavior: frame.NativeBehavior,
                suppressSocial: true,
                preserveGoal: true,
                commitment: 0.95f));

        if (frame.ImmediateDanger)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.ImmediateDanger,
                DB_BehaviorKind.Escape,
                "direct immediate danger / existing Escape state",
                frame.CurrentGoal,
                nominalSpeed: 9f,
                suppressCombat: true,
                suppressSocial: true,
                preserveGoal: true,
                commitment: 1f));

        if (frame.SevereInjury || frame.InjuryRecovering)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.InjuryRecovery,
                DB_BehaviorKind.Recover,
                frame.InjuryRecovering ? "active injury recovery" : "severe injury survival",
                frame.InjuryRecoveryTarget,
                nominalSpeed: Mathf.Lerp(2.5f, 5f, frame.PhysicalCapability),
                suppressCombat: true,
                suppressSocial: true,
                commitment: Mathf.Clamp01(1f - frame.PhysicalCapability + 0.35f)));

        if (frame.Travel.CanOwnRealizedFrame)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Travel,
                DB_BehaviorKind.CrossRoomTravel,
                string.IsNullOrEmpty(frame.Travel.Reason) ? "active realized travel" : frame.Travel.Reason,
                suppressCombat: true,
                suppressSocial: true,
                commitment: frame.Travel.Suspended ? 0.65f : 0.90f));

        if (frame.HardSurvival && frame.EnvironmentInfluence.Phase == DB_EnvironmentPhase.Acute)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.EnvironmentHardSurvival,
                DB_BehaviorKind.Shelter,
                "authorized DryCycle weather Acute / HardSurvival",
                frame.EnvironmentInfluence.PreferredShelterPoint,
                nominalSpeed: 7f,
                requestBurrow: frame.EnvironmentInfluence.BurrowDrive >= 0.55f,
                suppressCombat: true,
                suppressSocial: true,
                commitment: 1f));

        if (frame.FearSuppressed || frame.Threat.AcuteThreat ||
            frame.Trauma >= DB_Tuning.TraumaAggressionBlock)
        {
            Vector2? fearGoal = frame.CurrentGoal;
            bool preserveFearGoal = true;
            if (frame.Threat.AcuteThreat && frame.Threat.HazardCenter.HasValue)
            {
                Vector2 away = frame.Position - frame.Threat.HazardCenter.Value;
                if (away.sqrMagnitude < 0.01f) away = Vector2.up;
                fearGoal = frame.Position + away.normalized * 135f + Vector2.up * 28f;
                preserveFearGoal = false;
            }

            string fearReason = frame.Threat.AcuteThreat
                ? "acute current threat response"
                : frame.FearSuppressed
                    ? "active fear/intimidation response"
                    : "persistent PTSD response";
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.FearResponse,
                DB_BehaviorKind.FearRetreat,
                fearReason,
                fearGoal,
                nominalSpeed: 8f,
                suppressCombat: true,
                suppressSocial: true,
                preserveGoal: preserveFearGoal,
                commitment: Mathf.Max(frame.Trauma, frame.Threat.AcuteThreat ? 0.82f : 0.55f)));
        }

        if (frame.VengeanceActive)
        {
            Vector2? vengeanceGoal = frame.VengeanceTarget?.mainBodyChunk != null
                ? frame.VengeanceTarget.mainBodyChunk.pos
                : frame.CurrentGoal;
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Vengeance,
                DB_BehaviorKind.Vengeance,
                "Extreme Vengeance active",
                vengeanceGoal,
                nominalSpeed: 12.5f,
                suppressSocial: true,
                commitment: 0.95f));
        }

        if (!frame.HardSurvival && frame.HasEnvironmentInfluence &&
            frame.EnvironmentInfluence.Phase is DB_EnvironmentPhase.Preparation or
                DB_EnvironmentPhase.Sheltering or DB_EnvironmentPhase.Acute)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.EnvironmentLocalSurvival,
                DB_BehaviorKind.Shelter,
                frame.EnvironmentInfluence.DecisionReason,
                frame.EnvironmentInfluence.PreferredShelterPoint,
                nominalSpeed: Mathf.Lerp(4f, 7f, frame.EnvironmentInfluence.ShelterDrive),
                requestBurrow: frame.EnvironmentInfluence.BurrowDrive >= 0.62f,
                suppressCombat: frame.EnvironmentInfluence.HarassMultiplier <= 0.15f,
                suppressSocial: frame.EnvironmentInfluence.SuppressesNeutralSocial,
                commitment: frame.EnvironmentInfluence.ShelterDrive));

        if (frame.IncomingProjectile || frame.Threat.ProjectileThreat)
        {
            Vector2 away = frame.IncomingProjectile && frame.IncomingProjectileObservation.Velocity.sqrMagnitude > 0.01f
                ? -frame.IncomingProjectileObservation.Velocity.normalized
                : Vector2.up;
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.ImmediateProjectileEvade,
                DB_BehaviorKind.ProjectileEvade,
                "real incoming projectile cue",
                frame.Position + away * 105f,
                nominalSpeed: 9f,
                suppressSocial: true,
                commitment: 0.90f));
        }

        if (ai != null && (ai.FormalAttack || IsCombatMode(ai.Mode)))
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Combat,
                DB_BehaviorKind.Combat,
                "existing formal combat / retaliation state",
                ai.Target?.mainBodyChunk != null ? ai.Target.mainBodyChunk.pos : frame.CurrentGoal,
                nominalSpeed: 9f,
                suppressSocial: true,
                commitment: ai.FormalAttack ? 0.90f : 0.70f));

        if (frame.Roost.Active)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Roost,
                DB_BehaviorKind.Roost,
                frame.Roost.NativeChain ? "native Chain roost" : "species roost commitment",
                frame.CurrentGoal,
                desiredNativeBehavior: frame.Roost.NativeChain ? FlyAI.Behavior.Chain : frame.NativeBehavior,
                suppressSocial: false,
                preserveGoal: true,
                commitment: 0.82f));

        if (frame.HasSocialState && frame.Social.Mode != DesertBatflySocialMode.None)
            proposals.Add(DB_BehaviorProposal.Create(
                DB_BehaviorOwner.Social,
                DB_BehaviorKind.Social,
                frame.Social.DecisionReason,
                frame.Social.RoostTarget ?? frame.CurrentGoal,
                nominalSpeed: 5f,
                preserveGoal: frame.Social.RoostTarget == null,
                commitment: Mathf.Clamp01(frame.Social.SocialDrive)));

        proposals.Add(DB_BehaviorProposal.Create(
            DB_BehaviorOwner.Ordinary,
            DB_BehaviorKind.Idle,
            "ordinary Desert Batfly / vanilla swarm fallback",
            frame.CurrentGoal,
            desiredNativeBehavior: frame.NativeBehavior,
            preserveGoal: true,
            commitment: 0.10f));
    }

    private static bool IsCombatMode(DB_AI.Activity mode) => mode is
        DB_AI.Activity.Observe or DB_AI.Activity.Approach or
        DB_AI.Activity.Circle or DB_AI.Activity.FakeDive or
        DB_AI.Activity.Dive or DB_AI.Activity.Attach or
        DB_AI.Activity.RetaliationCharge or DB_AI.Activity.Interfere;
}
