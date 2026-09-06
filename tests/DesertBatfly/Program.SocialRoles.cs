using System;

internal static partial class Program
{
    // Historical entry-point name is kept only because Program.cs invokes it.
    // This guard now requires the rejected Task 02 runtime types to be physically absent.
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
}
