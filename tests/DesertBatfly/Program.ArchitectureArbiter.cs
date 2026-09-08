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
        Type injuryExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecoveryExecutor", true);
        Type vengeanceExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceExecutor", true);
        Type environmentExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentExecutor", true);
        Type socialExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialExecutor", true);
        Type projectileExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ProjectileEvadeExecutor", true);
        Type immediateDangerExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ImmediateDangerExecutor", true);
        Type fearExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearExecutor", true);
        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);
        Type roostExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RoostExecutor", true);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureThreatRuntime", true);
        Type threatTactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type environmentBehavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type socialLife = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialLife", true);
        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type desertBat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Creature", true);

        foreach (string name in new[]
                 {
                     "Bat", "EntityId", "Clock", "Room", "Position", "Velocity", "CurrentGoal",
                     "Conscious", "Dead", "Restrained", "InShortcut", "InHive",
                     "NativeMovementMode", "NativeBehavior", "Personality", "PhysicalCapability",
                     "SevereInjury", "InjuryRecovering", "InjuryRecoveryTarget", "PostStunShock",
                     "Thirst", "Trauma", "Grief", "BondStrength", "VisiblePlayerCount",
                     "PredatorCandidateCount", "IncomingProjectile", "VisibilityFactor", "Travel",
                     "SignalInfluence", "EnvironmentInfluence", "Threat", "Social", "Roost",
                     "VengeanceActive", "ImmediateDanger", "HardSurvival", "CombatAllowed",
                     "SocialAllowed", "CrossRoomOwned", "SpecialPhysicsOwner"
                 })
            Check(frame.GetField(name, Flags) != null,
                "Architecture arbitration FrameContext contains approved current-frame fact " + name);
        Check(frameRuntime.GetMethod("For", Flags) != null &&
              frameRuntime.GetMethod("Reset", Flags) != null &&
              frameRuntime.GetMethod("Forget", Flags) != null,
            "Architecture arbitration FrameContext has explicit non-persistent lifecycle");

        string[] ownerNames = Enum.GetNames(owner);
        foreach (string required in new[]
                 {
                     "CreaturePhysics", "Restraint", "Shortcut", "Emergence", "ImmediateDanger",
                     "InjuryRecovery", "Travel", "EnvironmentHardSurvival", "FearResponse",
                     "Vengeance", "EnvironmentLocalSurvival", "ImmediateProjectileEvade",
                     "Combat", "Roost", "Social", "Ordinary", "VanillaFallback"
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
              P("ImmediateProjectileEvade") < P("Combat") &&
              P("Combat") < P("Roost") && P("Roost") < P("Social") &&
              P("Social") < P("Ordinary"),
            "Architecture arbitration arbiter encodes the approved survival/travel/fear/vengeance/combat/social priority order");
        Check(arbiter.GetMethod("ResolveFrame", Flags) != null &&
              arbiter.GetMethod("ResolveWinner", Flags) != null &&
              arbiter.GetMethod("TryGetResolution", Flags) != null &&
              arbiter.GetMethod("IsPrimaryOwner", Flags) != null &&
              arbiter.GetMethod("TryGetDebugState", Flags) != null,
            "Architecture arbitration arbiter exposes one-winner runtime and debug surfaces");

        string[] specialNames = Enum.GetNames(specialPhysics);
        foreach (string required in new[]
                 { "None", "CreaturePhysics", "Grasp", "Shortcut", "Emergence", "CombatAttach", "CombatInterfere" })
            Check(specialNames.Contains(required),
                "Architecture arbitration special-physics ownership explicitly includes " + required);

        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Check(travel.GetMethod("CanOwnRealizedFrame", Flags) != null,
            "Architecture arbitration Travel exposes a non-destructive proposal eligibility query");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Check(intimidation.GetMethod("TryGetVengeanceTarget", Flags) != null,
            "Architecture arbitration Vengeance target is readable through an explicit API instead of sibling reflection");

        Type vengeanceBridge = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreatureThreatVengeanceBridge", true);
        Check(vengeanceBridge.GetField("statesField", Flags) == null &&
              vengeanceBridge.GetField("tryGetValue", Flags) == null &&
              vengeanceBridge.GetField("vengeanceTargetField", Flags) == null &&
              vengeanceBridge.GetField("travelFrames", Flags) == null,
            "Architecture arbitration removes private Intimidation reflection and parallel Travel frame stamps");
        Check(vengeanceBridge.GetMethod("IntimidationUpdateHook", Flags) == null &&
              vengeanceBridge.GetField("intimidationUpdateHook", Flags) == null &&
              MethodCallOffset(vengeanceBridge.GetMethod("ForceFlightHook", Flags), intimidation, "TryGetVengeanceTarget") >= 0,
            "Architecture arbitration Vengeance bridge is tactic-only and no longer intercepts state lifecycle");
        Check(intimidation.GetMethod("UpdateState", Flags) != null &&
              intimidation.GetMethod("ExecuteVengeanceOwned", Flags) != null &&
              MethodCallOffset(intimidation.GetMethod("ExecuteVengeanceOwned", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(intimidation.GetMethod("ForceFlight", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Vengeance state tick is split from owner-gated movement/contact execution");
        Check(MethodCallOffset(desertBat.GetMethod("Update", Flags), intimidation, "UpdateState") >= 0,
            "Architecture arbitration refreshes Vengeance/fear facts before FlyAI arbitration");
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
              desertAI.GetMethod("ExecuteCombatOwned", Flags) != null &&
              desertAI.GetMethod("ExecuteRoostOwned", Flags) != null &&
              desertAI.GetMethod("ScanWeapons", Flags) == null,
            "Architecture arbitration DB_AI separates decision refresh from owner executors and removes duplicate weapon scan");
        foreach (string methodName in new[]
                 { "ExecuteImmediateDangerOwned", "ExecuteFearOwned", "ExecuteCombatOwned", "ExecuteRoostOwned", "SteerOwned" })
            Check(MethodCallOffset(desertAI.GetMethod(methodName, Flags), arbiter, "IsPrimaryOwner") >= 0,
                "Architecture arbitration " + methodName + " requires same-tick PrimaryOwner");
        Check(MethodCallOffset(desertAI.GetMethod("AfterPhysics", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration Attach/Interfere AfterPhysics cannot run after another locomotion owner won");

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
              MethodCallOffset(updateAI, immediateDangerExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, fearExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0 &&
              MethodCallOffset(updateAI, environmentExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, vengeanceExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, socialLife, "RefreshState") >= 0 &&
              MethodCallOffset(updateAI, projectileExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, combatExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, roostExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, socialExecutor, "TryExecute") >= 0,
            "Architecture arbitration all ordinary locomotion domains enter through central owner resolution");
        Check(MethodCallOffset(updateAI, threatTactics, "TryApplyOrdinaryProjectileEvade") < 0 &&
              MethodCallOffset(updateAI, desertAI, "Update") < 0,
            "Architecture arbitration hook has no legacy projectile or monolithic DB_AI executor pipeline");
        MethodInfo executeNativeOwned = hooks.GetMethod("ExecuteNativeOwned", Flags);
        Check(executeNativeOwned != null && MethodCallOffset(executeNativeOwned, arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration vanilla FlyAI.Update is itself restricted to an accepted NativeSpecial/Ordinary/Fallback owner");
        Check(injuryExecutor.GetMethod("TryExecute", Flags) != null &&
              desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags) != null &&
              desertAI.GetMethod("TryInjuryRecovery", Flags) == null,
            "Architecture arbitration severe injury movement has one explicit owner-gated executor surface");
        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Architecture arbitration InjuryRecovery executor requires same-tick PrimaryOwner");

        Type observatory = mod.GetType("DryCycle.Debugging.AI.DB_ObservatorySource", true);
        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,
            "Architecture arbitration Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");
        Check(observatory.GetMethod("BuildArbiterSection", Flags) != null &&
              MethodCallOffset(observatory.GetMethod("BuildArbiterSection", Flags), arbiter, "TryGetDebugState") >= 0,
            "Architecture arbitration Observatory formally presents winner plus rejected arbiter proposals");

        Check(frame.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              proposal.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              arbiter.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Architecture arbitration architecture uses DB_ domain naming and does not create TaskXX production types");

        Console.WriteLine(
            "Architecture arbitration: code-side owner arbitration and rejected-proposal Observatory presentation are closed; Rain World live validation remains.");
    }
}
