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
              policy.GetMethod("BlocksNeutralSocial", Flags) != null,
            "R5 explicit environmental policy replaces mutation/detour based cross-domain behavior");

        Console.WriteLine("Task14 R5 B2: Environment Combat/Roost/Social integration is explicit; old detours and temporary Thirst spoofing are removed.");
    }
}
