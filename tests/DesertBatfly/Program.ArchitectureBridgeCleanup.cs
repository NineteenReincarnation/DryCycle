using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureBridgeCleanup()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type fear = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type policy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type roomEnvironment = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Type signals = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type consumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);
        Type hub = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventHub", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type frameContext = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type restraint = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RestraintPolicy", true);

        Check(fear.GetMethod("UpdateState", Flags) != null &&
              vengeance.GetMethod("TryGetTarget", Flags) != null &&
              vengeance.GetMethod("ExecuteOwned", Flags) != null &&
              tactics.GetMethod("AdjustExtremeVengeanceGoal", Flags) != null,
            "Architecture bridge cleanup keeps Fear, Vengeance and Threat tactics connected through explicit domain APIs");

        Check(policy.GetMethod("AggressionAuthorized", Flags) != null &&
              policy.GetMethod("CombatMotivation", Flags) != null &&
              policy.GetMethod("AllowsHarassCandidate", Flags) != null &&
              policy.GetMethod("AdjustRoostDuration", Flags) != null &&
              policy.GetMethod("BlocksNeutralSocial", Flags) != null &&
              policy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&
              policy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&
              policy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&
              policy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,
            "Architecture bridge cleanup exposes environmental cross-domain decisions through one explicit policy API");

        Check(environment.GetMethod("RefreshInfluence", Flags) != null &&
              environment.GetMethod("ApplyOwnedBehavior", Flags) != null &&
              environment.GetMethod("ApplyNativeHomeAndBurrow", Flags) != null &&
              roomEnvironment.GetMethod("TryGetShelterFailureDebug", Flags) != null,
            "Architecture bridge cleanup keeps same-room survival and shelter failure in direct Environment owners");

        Check(signals.GetMethod("EmitAcuteAlarm", Flags) != null &&
              signals.GetMethod("EmitRally", Flags) != null &&
              signals.GetMethod("EmitDistress", Flags) != null,
            "Architecture bridge cleanup keeps signal roots on explicit SignalRuntime APIs");
        Check(MethodCallOffset(consumers.GetMethod("Enable", Flags), hub, "add_Capture") >= 0 &&
              MethodCallOffset(consumers.GetMethod("Disable", Flags), hub, "remove_Capture") >= 0,
            "Architecture bridge cleanup canonical semantic-event consumers own their lifecycle directly");
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), fear, "Reset") >= 0,
            "Architecture bridge cleanup species lifecycle remains wired to direct domain owners");

        Check(ai.GetMethod("Update", Flags) == null &&
              ai.GetMethod("AfterPhysics", Flags) == null &&
              environment.GetMethod("Update", Flags) == null &&
              social.GetMethod("Update", Flags) == null &&
              threat.GetMethod("Update", Flags) == null,
            "Architecture lifecycle ownership forbids legacy combined-update facades that can bypass R3 refresh/arbitrate/execute order");

        MethodInfo restraintQuery = restraint.GetMethod("IsRestrainedByNonFly", Flags);
        MethodInfo aiRestraintQuery = ai.GetMethod("RestrainedByNonFly", Flags);
        Check(restraintQuery != null &&
              aiRestraintQuery != null &&
              MethodCallOffset(aiRestraintQuery, restraint, "IsRestrainedByNonFly") >= 0 &&
              frameContext.GetMethod("RestrainedByNonFly", Flags) == null &&
              travel.GetMethod("RestrainedByNonFly", Flags) == null &&
              social.GetMethod("RestrainedByNonFly", Flags) == null,
            "Architecture lifecycle ownership keeps non-Fly restraint classification in one canonical policy implementation");

        Check(hooks.GetMethod("FlyNewRoom", Flags) == null &&
              hooks.GetMethod("FlyGrabbed", Flags) == null &&
              hub.GetMethod("CreatureViolence", Flags) == null &&
              hub.GetMethod("CreatureDie", Flags) == null &&
              hub.GetMethod("FlyGrabbed", Flags) == null,
            "Architecture lifecycle ownership forbids reintroducing raw hooks for DesertBatfly-owned virtual lifecycle callbacks");

        Console.WriteLine("Architecture bridge cleanup: direct domain APIs, owned lifecycle callbacks, canonical restraint facts and split R3 refresh/execute surfaces verified.");
    }
}
