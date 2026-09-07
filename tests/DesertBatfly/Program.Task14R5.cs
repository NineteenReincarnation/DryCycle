using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R5()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", true);
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
        Check(integration.GetField("steerHook", Flags) != null &&
              integration.GetField("scanCreaturesHook", Flags) != null,
            "R5 B1 intentionally leaves EnvironmentalIntegration debt visible for the next migration batch");

        Console.WriteLine("Task14 R5 B1: DenseFog and Vengeance bridge shims are physically removed; Threat Vengeance uses a direct tactical API.");
    }
}
