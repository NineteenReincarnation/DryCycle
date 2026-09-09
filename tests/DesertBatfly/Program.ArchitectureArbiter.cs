using System;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureArbiter()
    {
        Type frame = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContext", true);
        Type frameRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type kind = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorKind", true);
        Type specialPhysics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SpecialPhysicsOwner", true);
        Type proposal = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorProposal", true);
        Type resolution = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorResolution", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type behaviorExecution = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorExecution", true);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type threatTactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type environmentBehavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type socialLife = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type desertBat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Creature", true);
        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Runtime", true);
        Type combatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type injuryRecovery = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecovery", true);
        Type perception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreaturePerception", true);
        Type restraintPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RestraintPolicy", true);
        Type swarmLifecycle = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SwarmLifecycleRuntime", true);
        Type swarmRoom = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SwarmRoom", true);

        foreach (string name in new[]
                 {
                     "Bat", "EntityId", "Clock", "Room", "Position", "Velocity", "CurrentGoal",
                     "Conscious", "Dead", "Restrained", "InShortcut", "InHive",
                     "NativeMovementMode", "NativeBehavior", "Personality", "PhysicalCapability",
                     "SevereInjury", "InjuryRecovering", "InjuryRecoveryTarget", "PostStunShock",
                     "Thirst", "Trauma", "Grief", "BondStrength", "IncomingProjectile",
                     "VisibilityFactor", "Travel", "SignalInfluence", "EnvironmentInfluence",
                     "Threat", "Social", "Roost", "VengeanceActive", "ImmediateDanger",
                     "HardSurvival", "CombatAllowed", "SocialAllowed", "CrossRoomOwned",
                     "SpecialPhysicsOwner"
                 })
            Check(frame.GetField(name, Flags) != null,
                "Architecture arbitration FrameContext contains approved current-frame fact " + name);
        foreach (string retired in new[]
                 { "VisiblePlayerCount", "NearestVisiblePlayer", "PredatorCandidateCount", "NearestPredator" })
            Check(frame.GetField(retired, Flags) == null,
                "Architecture arbitration FrameContext does not retain unused per-frame visibility fact " + retired);
        Check(frameRuntime.GetMethod("For", Flags) != null &&
              frameRuntime.GetMethod("Reset", Flags) != null &&
              frameRuntime.GetMethod("Forget", Flags) != null,
            "Architecture arbitration FrameContext has explicit non-persistent lifecycle");

        string[] ownerNames = Enum.GetNames(owner);
        foreach (string required in new[]
                 {
                     "CreaturePhysics", "Restraint", "Shortcut", "Emergence", "NativeSpecial",
                     "ImmediateDanger", "InjuryRecovery", "Travel", "EnvironmentHardSurvival",
                     "FearResponse", "Vengeance", "EnvironmentLocalSurvival",
                     "ImmediateProjectileEvade", "Feeding", "Combat", "Roost", "Social",
                     "Ordinary", "VanillaFallback"
                 })
            Check(ownerNames.Contains(required), "Architecture arbitration owner enum includes " + required);
        Check(ownerNames.All(name =>
                name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("ThreatMemory", StringComparison.OrdinalIgnoreCase) < 0),
            "Architecture arbitration Signals and Threat memory can never be PrimaryOwner enum values");

        foreach (string fieldName in new[]
                 {
                     "Owner", "Priority", "BehaviorKind", "Goal", "NominalSpeed",
                     "DesiredNativeBehavior", "DesiredDijkstraMap", "RequestBurrow",
                     "SuppressCombat", "SuppressSocial", "PreserveGoal", "Commitment", "Reason"
                 })
            Check(proposal.GetField(fieldName, Flags) != null,
                "Architecture arbitration BehaviorProposal contains " + fieldName);
        Check(resolution.GetField("PrimaryOwner", Flags) != null &&
              resolution.GetField("WinningProposal", Flags) != null &&
              resolution.GetField("FinalGoal", Flags) != null &&
              resolution.GetField("SpecialPhysicsOwner", Flags) != null,
            "Architecture arbitration resolution exposes actual winner, final goal and special-physics owner");

        MethodInfo priorityOf = arbiter.GetMethod("PriorityOf", Flags);
        int P(string name) => (int)priorityOf.Invoke(null, new[] { Enum.Parse(owner, name) });
        Check(P("CreaturePhysics") < P("ImmediateDanger") &&
              P("ImmediateDanger") < P("InjuryRecovery") &&
              P("InjuryRecovery") < P("Travel") &&
              P("Travel") < P("EnvironmentHardSurvival") &&
              P("EnvironmentHardSurvival") < P("FearResponse") &&
              P("FearResponse") < P("Vengeance") &&
              P("Vengeance") < P("EnvironmentLocalSurvival") &&
              P("EnvironmentLocalSurvival") < P("ImmediateProjectileEvade") &&
              P("ImmediateProjectileEvade") < P("Feeding") &&
              P("Feeding") < P("Combat") &&
              P("Combat") < P("Roost") && P("Roost") < P("Social") &&
              P("Social") < P("Ordinary"),
            "Architecture arbitration arbiter encodes the approved survival/travel/fear/vengeance/feeding/combat/social priority order");
        Check(arbiter.GetMethod("ResolveFrame", Flags) != null &&
              arbiter.GetMethod("ResolveWinner", Flags) != null &&
              arbiter.GetMethod("TryGetResolution", Flags) != null &&
              arbiter.GetMethod("IsPrimaryOwner", Flags) != null &&
              arbiter.GetMethod("TryGetDebugState", Flags) != null,
            "Architecture arbitration arbiter exposes one-winner runtime and debug surfaces");

        string[] specialNames = Enum.GetNames(specialPhysics);
        foreach (string required in new[]
                 {
                     "None", "CreaturePhysics", "Grasp", "Shortcut", "Emergence",
                     "FeedingAttach", "CombatAttach", "CombatInterfere", "NativeBurrow", "NativeChain"
                 })
            Check(specialNames.Contains(required),
                "Architecture arbitration special-physics ownership explicitly includes " + required);

        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Check(travel.GetMethod("CanOwnRealizedFrame", Flags) != null,
            "Architecture arbitration Travel exposes a non-destructive proposal eligibility query");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type vengeanceRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Check(vengeanceRuntime.GetMethod("TryGetTarget", Flags) != null,
            "Architecture arbitration Vengeance target is readable through its formal owner API instead of sibling reflection");

        Check(vengeanceRuntime.GetMethod("IsActive", Flags) != null &&
              vengeanceRuntime.GetMethod("IsAvenger", Flags) != null &&
              vengeanceRuntime.GetMethod("TryGetTarget", Flags) != null &&
              vengeanceRuntime.GetMethod("ExecuteOwned", Flags) != null,
            "Architecture arbitration exposes Vengeance through a formal domain runtime rather than an internal bridge");
        Check(MethodCallOffset(behaviorExecution.GetMethod("TryVengeance", Flags), vengeanceRuntime, "ExecuteOwned") >= 0,
            "Architecture arbitration Vengeance executor enters the formal Vengeance runtime");
        Check(intimidation.GetMethod("UpdateState", Flags) != null &&
              MethodCallOffset(runtime.GetMethod("BeforeVanillaUpdate", Flags), intimidation, "UpdateState") >= 0 &&
              MethodCallOffset(desertBat.GetMethod("Update", Flags), runtime, "BeforeVanillaUpdate") >= 0,
            "Architecture arbitration Fear state refresh occurs in the species runtime before the native Fly update boundary");
        Check(environmentBehavior.GetMethod("RefreshInfluence", Flags) != null &&
              environmentBehavior.GetMethod("ApplyOwnedBehavior", Flags) != null &&
              MethodCallOffset(environmentBehavior.GetMethod("ApplyOwnedBehavior", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Environment influence refresh is split from owner-gated local apply");
        Check(socialLife.GetMethod("RefreshState", Flags) != null &&
              socialLife.GetMethod("ApplyOwnedBehavior", Flags) != null &&
              MethodCallOffset(socialLife.GetMethod("ApplyOwnedBehavior", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(socialLife.GetMethod("SocialSteer", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Social scheduling/state refresh is split from owner-gated movement");
        Check(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags) != null &&
              MethodCallOffset(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration real projectile dodge has an owner-gated apply surface while Threat memory remains a modifier");
        Check(threatRuntime.GetMethod("RefreshState", Flags) != null &&
              threatRuntime.GetMethod("ApplyOwnedTacticalModifier", Flags) != null &&
              threatRuntime.GetMethod("CommitFrame", Flags) != null &&
              MethodCallOffset(threatRuntime.GetMethod("ApplyTacticalAdjustment", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Threat refresh is pre-arbiter and tactical localGoal adjustment is Combat-owner-only");

        Check(desertAI.GetMethod("RefreshDecisionState", Flags) != null &&
              desertAI.GetMethod("ExecuteImmediateDangerOwned", Flags) != null &&
              desertAI.GetMethod("ExecuteFearOwned", Flags) != null &&
              desertAI.GetMethod("ExecuteRoostOwned", Flags) != null &&
              desertAI.GetMethod("ScanWeapons", Flags) == null &&
              desertAI.GetMethod("AfterPhysics", Flags) == null,
            "Architecture arbitration DB_AI retains orchestration/AI-owned executors and no longer owns weapon scan or post-physics combat");
        foreach (string methodName in new[]
                 { "ExecuteImmediateDangerOwned", "ExecuteFearOwned", "ExecuteRoostOwned", "SteerOwned" })
            Check(MethodCallOffset(desertAI.GetMethod(methodName, Flags), arbiter, "IsPrimaryOwner") >= 0,
                "Architecture arbitration " + methodName + " requires same-tick PrimaryOwner");

        MethodInfo combatExecute = combatRuntime.GetMethod("TryExecuteOwned", Flags);
        MethodInfo combatAfterPhysics = combatRuntime.GetMethod("AfterPhysics", Flags);
        Check(combatExecute != null && combatAfterPhysics != null &&
              MethodCallOffset(combatExecute, arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(combatAfterPhysics, arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Combat domain owns both formal execution and Attach/Interfere post-physics owner checks");
        Check(desertAI.GetMethod("ExecuteCombatOwned", Flags) == null,
            "Architecture arbitration DB_AI has no redundant Combat forwarding facade");

        PropertyInfo injuryOwner = desertAI.GetProperty("InjuryRecovery", Flags);
        MethodInfo injuryExecute = injuryRecovery.GetMethod("ExecuteOwned", Flags);
        Check(injuryOwner?.PropertyType == injuryRecovery && injuryExecute != null &&
              MethodCallOffset(injuryExecute, arbiter, "IsPrimaryOwner") >= 0 &&
              desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags) == null &&
              desertAI.GetMethod("TryInjuryRecovery", Flags) == null,
            "Architecture arbitration severe injury execution belongs directly to DB_InjuryRecovery");
        Check(MethodCallOffset(behaviorExecution.GetMethod("TryInjuryRecovery", Flags), injuryRecovery, "ExecuteOwned") >= 0,
            "Architecture arbitration central execution routes InjuryRecovery directly to its domain owner");
        Check(desertAI.GetMethod("RestrainedByNonFly", Flags) == null &&
              restraintPolicy.GetMethod("IsRestrainedByNonFly", Flags) != null,
            "Architecture arbitration restraint classification has one canonical policy surface");
        Check(perception.GetProperty("PursuitTicks", Flags) != null &&
              desertAI.GetProperty("RetreatTicks", Flags) != null &&
              desertAI.GetProperty("EscapeFrom", Flags) != null,
            "Architecture arbitration debug facts are exposed by their current owners without private reflection");

        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        Check(MethodCallOffset(hooksEnable, frameRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksEnable, arbiter, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, frameRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, arbiter, "Reset") >= 0,
            "Architecture arbitration FrameContext/Arbiter cache follows Desert Batfly lifecycle");
        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&
              MethodCallOffset(updateAI, environmentBehavior, "RefreshInfluence") >= 0 &&
              MethodCallOffset(updateAI, desertAI, "RefreshDecisionState") >= 0 &&
              MethodCallOffset(updateAI, threatRuntime, "RefreshState") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryImmediateDanger") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryFear") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryInjuryRecovery") >= 0 &&
              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryEnvironment") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryVengeance") >= 0 &&
              MethodCallOffset(updateAI, socialLife, "RefreshState") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryProjectileEvade") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryFeeding") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryCombat") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TryRoost") >= 0 &&
              MethodCallOffset(updateAI, behaviorExecution, "TrySocial") >= 0,
            "Architecture arbitration all ordinary locomotion domains enter through central owner resolution");
        Check(MethodCallOffset(updateAI, threatTactics, "TryApplyOrdinaryProjectileEvade") < 0 &&
              MethodCallOffset(updateAI, desertAI, "Update") < 0,
            "Architecture arbitration hook has no legacy projectile or monolithic DB_AI executor pipeline");
        MethodInfo executeNativeOwned = hooks.GetMethod("ExecuteNativeOwned", Flags);
        Check(executeNativeOwned != null &&
              MethodCallOffset(executeNativeOwned, arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(executeNativeOwned, socialLife, "CancelForPriority") >= 0,
            "Architecture arbitration vanilla FlyAI.Update is owner-gated and carries proposal-driven social suppression");
        Check(hooks.GetMethod("Rain", Flags) == null,
            "Architecture arbitration has no redundant nested FleeFromRainUpdate hook");
        Check(MethodCallOffset(hooks.GetMethod("Idle", Flags), swarmLifecycle, "AfterNativeIdleUpdate") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Swarm", Flags), swarmLifecycle, "AfterNativeSwarmUpdate") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Follow", Flags), swarmRoom, "TryHandleNativeFollowDijkstra") >= 0,
            "Architecture arbitration nonvirtual Idle/Swarm/Follow hooks are thin adapters into species domains");

        Type observatory = mod.GetType("DryCycle.Debugging.AI.DB_ObservatorySource", true);
        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,
            "Architecture arbitration Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");
        Check(observatory.GetMethod("BuildArbiterSection", Flags) != null &&
              MethodCallOffset(observatory.GetMethod("BuildArbiterSection", Flags), arbiter, "TryGetDebugState") >= 0,
            "Architecture arbitration Observatory formally presents winner plus rejected arbiter proposals");
        Check(observatory.GetMethod("Read", Flags) == null &&
              observatory.GetMethod("RestrainedByNonFly", Flags) == null &&
              observatory.GetField("RetreatField", Flags) == null &&
              observatory.GetField("PursuitField", Flags) == null &&
              observatory.GetField("EscapeFromField", Flags) == null &&
              MethodCallOffset(observatory.GetMethod("BuildDecisionStack", Flags), restraintPolicy, "IsRestrainedByNonFly") >= 0,
            "Architecture arbitration Observatory reads project-owned state directly and shares canonical restraint classification");

        Check(frame.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              proposal.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              arbiter.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Architecture arbitration architecture uses DB_ domain naming and does not create TaskXX production types");

        Console.WriteLine(
            "Architecture arbitration: current domain owners, thin native hooks, direct Observatory queries, lean FrameContext, Feeding priority, special physics and rejected-proposal presentation are guarded; Rain World live validation remains.");
    }
}
