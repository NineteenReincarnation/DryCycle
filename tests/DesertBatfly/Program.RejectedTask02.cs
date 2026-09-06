using System;

internal static partial class Program
{
    // Historical entry-point name is kept only because Program.cs invokes it.
    // This guard requires the rejected Task 02 runtime types to remain physically absent.
    private static void RunRoleDistribution()
    {
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null,
            "rejected Task 02 runtime class is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null,
            "rejected Task 02 score type is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "rejected Task 02 enum is physically removed");

        Console.WriteLine("Task 02 social roles: rejected runtime types are physically absent.");
    }

    // Program.cs historically ended by calling RunRoleIntegration from the removed
    // Task-02 test file. Keep only a zero-behavior compatibility entry point so existing
    // test layout compiles; it now runs the real Task-09 regression suite and contains no
    // social-role implementation, enum, score or runtime dependency.
    private static void RunRoleIntegration() => RunTask09();
}
