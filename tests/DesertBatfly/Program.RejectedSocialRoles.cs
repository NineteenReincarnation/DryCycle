using System;
using System.Reflection;

internal static partial class Program
{
    // Historical entry-point name is kept only because Program.cs invokes it.
    // This guard requires the rejected social-role design runtime types and API surfaces to remain
    // physically absent. Any compatibility shell is considered a regression.
    private static void RunRejectedSocialRoles()
    {
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null,
            "rejected social-role design runtime class is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null,
            "rejected social-role design score type is physically removed");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "rejected social-role design enum is physically removed");

        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Check(ai.GetField("Roles", Flags) == null && ai.GetProperty("Roles", Flags) == null,
            "DB_AI has no rejected Roles API");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Check(intimidation.GetMethod("BlocksSocialRoles", Flags) == null,
            "fear/intimidation no longer exposes BlocksSocialRoles");
        Check(intimidation.GetMethod("HasActiveFearSuppression", Flags) != null,
            "fear suppression uses its current non-role API");

        Type traceFrame = mod.GetType("DryCycle.Debugging.AI.AIDebugTraceFrame", true);
        Check(traceFrame.GetField("Role", Flags) == null && traceFrame.GetProperty("Role", Flags) == null,
            "AI Observatory trace frame has no rejected Role slot");

        Console.WriteLine("rejected social-role design social roles: runtime types, AI API, fear API and Observatory Role slot are physically absent.");
    }

    // Program.cs historically ended by calling RunRegressionSuites from the removed
    // rejected social-role design test file. Keep the old entry-point name only as a neutral regression
    // dispatcher; it runs current accepted task suites and contains no role implementation.
    private static void RunRegressionSuites()
    {
        RunTravelColony();
        RunSocial();
        RunSocialGuards();
        RunThreat();
        RunThreatTactics();
        RunThreatEventSemantics();
        RunSignals();
        RunEnvironment();
        RunEnvironmentRain();
        RunArchitectureBaseline();
        RunArchitectureEvents();
        RunArchitecturePerception();
        RunArchitectureArbiter();
        RunArchitectureFlightMotor();
        RunArchitectureBridgeCleanup();
        RunArchitectureRetention();
        RunArchitectureDomainMigration();
    }
}
