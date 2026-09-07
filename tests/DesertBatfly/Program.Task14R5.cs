using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R5()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", false) == null,
            "R5 removes behavior-neutral DenseFog RuntimeDetour shim");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", false) == null,
            "R5 removes hard-survival Vengeance detour; Arbiter owns preemption");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatVengeanceBridge", false) == null,
            "R5 removes Threat/Vengeance ForceFlight RuntimeDetour");

        MethodInfo forceFlight = intimidation.GetMethod("ForceFlight", Flags);
        Check(forceFlight != null &&
              MethodCallOffset(forceFlight, tactics, "AdjustExtremeVengeanceGoal") >= 0,
            "R5 Vengeance explicitly consumes Threat tactical geometry through formal API");

        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), intimidation, "Reset") >= 0,
            "R5 species lifecycle remains wired after deleting obsolete bridges");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", false) == null,
            "R5 removes Environment Reflection/RuntimeDetour integration and social bridge");
        Type policy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Check(policy.GetMethod("AggressionAuthorized", Flags) != null &&
              policy.GetMethod("CombatMotivation", Flags) != null &&
              policy.GetMethod("AllowsHarassCandidate", Flags) != null &&
              policy.GetMethod("AdjustRoostDuration", Flags) != null &&
              policy.GetMethod("BlocksNeutralSocial", Flags) != null &&
              policy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&
              policy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&
              policy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&
              policy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,
            "R5 explicit environmental policy replaces mutation/detour based cross-domain behavior");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,
            "R5 B3 physically removes Task09 and Survival RuntimeDetour bridges");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", false) == null,
            "R5 B4 physically removes the Task12 internal detour hub and four signal bridges");

        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalRoomRuntime", true);
        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Check(roomRuntime.GetMethod("TryGetShelterFailureDebug", Flags) != null &&
              behavior.GetMethod("ApplyOwnedBehavior", Flags) != null,
            "R5 B3 keeps LocalShelterFailure and same-room survival in their direct Task13 owners");

        Type signalRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);
        Check(signalRuntime.GetMethod("EmitAcuteAlarm", Flags) != null &&
              signalRuntime.GetMethod("EmitRally", Flags) != null,
            "R5 B4 replaces signal detours with direct domain APIs");

        Console.WriteLine("Task14 R5 B4: Environment and Signal internal detours are retired; direct domain APIs preserve cross-domain behavior.");
    }
}
