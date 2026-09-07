using System;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R3()
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
        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);

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
                "Task14 R3 FrameContext contains approved current-frame fact " + name);
        Check(frameRuntime.GetMethod("For", Flags) != null &&
              frameRuntime.GetMethod("Reset", Flags) != null &&
              frameRuntime.GetMethod("Forget", Flags) != null,
            "Task14 R3 FrameContext has explicit non-persistent lifecycle");

        string[] ownerNames = Enum.GetNames(owner);
        foreach (string required in new[]
                 {
                     "CreaturePhysics", "Restraint", "Shortcut", "Emergence", "ImmediateDanger",
                     "InjuryRecovery", "Travel", "EnvironmentHardSurvival", "FearResponse",
                     "Vengeance", "EnvironmentLocalSurvival", "ImmediateProjectileEvade",
                     "Combat", "Roost", "Social", "Ordinary", "VanillaFallback"
                 })
            Check(ownerNames.Contains(required), "Task14 R3 owner enum includes " + required);
        Check(ownerNames.All(name =>
                name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("ThreatMemory", StringComparison.OrdinalIgnoreCase) < 0),
            "Task14 R3 Signals and Threat memory can never be PrimaryOwner enum values");

        foreach (string fieldName in new[]
                 {
                     "Owner", "Priority", "BehaviorKind", "Goal", "NominalSpeed",
                     "DesiredNativeBehavior", "DesiredDijkstraMap", "RequestBurrow",
                     "SuppressCombat", "SuppressSocial", "PreserveGoal", "Commitment", "Reason"
                 })
            Check(proposal.GetField(fieldName, Flags) != null,
                "Task14 R3 BehaviorProposal contains " + fieldName);
        Check(resolution.GetField("PrimaryOwner", Flags) != null &&
              resolution.GetField("WinningProposal", Flags) != null &&
              resolution.GetField("FinalGoal", Flags) != null &&
              resolution.GetField("SpecialPhysicsOwner", Flags) != null,
            "Task14 R3 resolution exposes actual winner, final goal and special-physics owner");

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
            "Task14 R3 arbiter encodes the approved survival/travel/fear/vengeance/combat/social priority order");
        Check(arbiter.GetMethod("ResolveFrame", Flags) != null &&
              arbiter.GetMethod("ResolveWinner", Flags) != null &&
              arbiter.GetMethod("TryGetResolution", Flags) != null &&
              arbiter.GetMethod("IsPrimaryOwner", Flags) != null &&
              arbiter.GetMethod("TryGetDebugState", Flags) != null,
            "Task14 R3 arbiter exposes one-winner runtime and debug surfaces");

        string[] specialNames = Enum.GetNames(specialPhysics);
        foreach (string required in new[]
                 { "None", "CreaturePhysics", "Grasp", "Shortcut", "Emergence", "CombatAttach", "CombatInterfere" })
            Check(specialNames.Contains(required),
                "Task14 R3 special-physics ownership explicitly includes " + required);

        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);
        Check(travel.GetMethod("CanOwnRealizedFrame", Flags) != null,
            "Task14 R3 Travel exposes a non-destructive proposal eligibility query");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Check(intimidation.GetMethod("TryGetVengeanceTarget", Flags) != null,
            "Task14 R3 Vengeance target is readable through an explicit API instead of sibling reflection");

        Type vengeanceBridge = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatVengeanceBridge", true);
        Check(vengeanceBridge.GetField("statesField", Flags) == null &&
              vengeanceBridge.GetField("tryGetValue", Flags) == null &&
              vengeanceBridge.GetField("vengeanceTargetField", Flags) == null &&
              vengeanceBridge.GetField("travelFrames", Flags) == null,
            "Task14 R3 removes private Intimidation reflection and parallel Travel frame stamps");
        Check(MethodCallOffset(vengeanceBridge.GetMethod("IntimidationUpdateHook", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(vengeanceBridge.GetMethod("ForceFlightHook", Flags), intimidation, "TryGetVengeanceTarget") >= 0,
            "Task14 R3 Vengeance bridge consumes actual PrimaryOwner and explicit target facts");

        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        Check(MethodCallOffset(hooksEnable, frameRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksEnable, arbiter, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, frameRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, arbiter, "Reset") >= 0,
            "Task14 R3 FrameContext/Arbiter cache follows Desert Batfly lifecycle");
        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&
              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&
              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,
            "Task14 R3 InjuryRecovery and Travel enter through central owner resolution");
        Check(injuryExecutor.GetMethod("TryExecute", Flags) != null &&
              desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags) != null &&
              desertAI.GetMethod("TryInjuryRecovery", Flags) == null,
            "Task14 R3 severe injury movement has one explicit owner-gated executor surface");
        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "Task14 R3 InjuryRecovery executor requires same-tick PrimaryOwner");

        Type observatory = mod.GetType("DryCycle.Debugging.AI.DesertBatflyDebugSource", true);
        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,
            "Task14 R3 Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");

        Check(frame.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              proposal.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              arbiter.Name.StartsWith("DB_", StringComparison.Ordinal),
            "Task14 R3 architecture uses DB_ domain naming and does not create TaskXX production types");

        Console.WriteLine(
            "Task14 R3: FrameContext/Arbiter plus owner-gated InjuryRecovery and Travel verified. Vengeance/Environment/Social single-writer migration remains open.");
    }
}
