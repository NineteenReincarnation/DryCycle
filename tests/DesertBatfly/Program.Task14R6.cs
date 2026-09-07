using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R6()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type runtimePatch = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RuntimePatch", true);
        Type sandbox = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Sandbox", true);
        Type warp = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WarpCompatibility", true);

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRuntimePatch", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySandbox", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyWarpCompatibility", false) == null,
            "R6 B1 retires old Integration type identities");

        MethodInfo modsInit = hooks.GetMethod("RainWorld_OnModsInit", Flags);
        Check(MethodCallOffset(modsInit, sandbox, "Enable") >= 0 &&
              MethodCallOffset(modsInit, warp, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), sandbox, "Disable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), warp, "Disable") >= 0,
            "R6 B1 preserves Sandbox/Warp lifecycle");
        Check(MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0,
            "R6 B1 preserves RuntimePatch compatibility boundary");

        Type observatory = mod.GetType("DryCycle.Debugging.AI.DB_ObservatorySource", true);
        Type travel = mod.GetType("DryCycle.Debugging.AI.DB_TravelDebugSource", true);
        Type social = mod.GetType("DryCycle.Debugging.AI.DB_SocialDebugSource", true);
        Type threat = mod.GetType("DryCycle.Debugging.AI.DB_ThreatDebugSource", true);
        Type signal = mod.GetType("DryCycle.Debugging.AI.DB_SignalDebugSource", true);
        Type environment = mod.GetType("DryCycle.Debugging.AI.DB_EnvironmentDebugSource", true);
        Check(travel.GetField("inner", Flags)?.FieldType == observatory &&
              social.GetField("inner", Flags)?.FieldType == travel &&
              threat.GetField("inner", Flags)?.FieldType == social &&
              signal.GetField("inner", Flags)?.FieldType == threat &&
              environment.GetField("inner", Flags)?.FieldType == signal,
            "R6 B1 preserves Observatory enrichment order");
        Console.WriteLine("Task14 R6 B1 retention checks pass.");
    }
}
