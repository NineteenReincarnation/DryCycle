using System;
using System.Reflection;

internal static partial class Program
{
    // Historical entry-point name is kept only because Program.cs invokes it.
    // This guard requires the rejected Task 02 runtime types and API surfaces to remain
    // physically absent. Any compatibility shell is considered a regression.
    private static void RunRoleDistribution()
    {
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null,
            "rejected Task 02 runtime class is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null,
            "rejected Task 02 score type is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "rejected Task 02 enum is physically removed");

        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Check(ai.GetField("Roles", Flags) == null && ai.GetProperty("Roles", Flags) == null,
            "DesertBatflyAI has no rejected Roles API");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Check(intimidation.GetMethod("BlocksSocialRoles", Flags) == null,
            "fear/intimidation no longer exposes BlocksSocialRoles");
        Check(intimidation.GetMethod("HasActiveFearSuppression", Flags) != null,
            "fear suppression uses its current non-role API");

        Type traceFrame = mod.GetType("DryCycle.Debugging.AI.AIDebugTraceFrame", true);
        Check(traceFrame.GetField("Role", Flags) == null && traceFrame.GetProperty("Role", Flags) == null,
            "AI Observatory trace frame has no rejected Role slot");

        Console.WriteLine("Task 02 social roles: runtime types, AI API, fear API and Observatory Role slot are physically absent.");
    }

    // Program.cs historically ended by calling RunRoleIntegration from the removed
    // Task-02 test file. Keep the old entry-point name only as a neutral regression
    // dispatcher; it runs current accepted task suites and contains no role implementation.
    private static void RunRoleIntegration()
    {
        RunTask09();
        RunTask10();
        RunTask10Guards();
        RunTask11();
    }
}
