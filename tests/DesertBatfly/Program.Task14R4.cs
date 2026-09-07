using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R4()
    {
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type fog = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FogGoalModifier", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type injury = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyInjury", true);
        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);

        Check(motor.GetMethod("Reset", Flags) != null &&
              motor.GetMethod("Forget", Flags) != null &&
              motor.GetMethod("TrySteer", Flags) != null &&
              motor.GetMethod("ApplyPostPhysics", Flags) != null &&
              motor.GetMethod("TryGetIntent", Flags) != null,
            "Task14 R4 FlightMotor exposes explicit lifecycle, steer and final injury-pass surfaces");
        Check(fog.GetMethod("ModifyGoal", Flags) != null &&
              fog.GetMethod("Reset", Flags) != null && fog.GetMethod("Forget", Flags) != null,
            "Task14 R4 Fog uncertainty is an explicit goal modifier with lifecycle ownership");
        Check(MethodCallOffset(motor.GetMethod("TrySteer", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TrySteer", Flags), fog, "ModifyGoal") >= 0,
            "Task14 R4 FlightMotor requires same-tick owner and applies Fog before steering");
        Check(injury.GetField("NominalFlightSpeed", Flags) == null &&
              injury.GetMethod("ApplyFlight", Flags) == null &&
              injury.GetMethod("ModifyFlight", Flags) != null,
            "Task14 R4 Injury is a pure flight modifier rather than nominal-speed/final-controller owner");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), motor, "ApplyPostPhysics") >= 0,
            "Task14 R4 creature final flight modifier passes through DB_FlightMotor");
        Check(MethodCallOffset(ai.GetMethod("SteerOwned", Flags), motor, "TrySteer") >= 0 &&
              MethodCallOffset(ai.GetMethod("TryDriveRecoveryHive", Flags), motor, "TrySteer") >= 0,
            "Task14 R4 core AI and InjuryRecovery submit ordinary steering through FlightMotor");
        Check(MethodCallOffset(environment.GetMethod("ApplyLocalBehavior", Flags), motor, "TrySteer") >= 0,
            "Task14 R4 Environment submits shelter goal to FlightMotor instead of writing localGoal directly");
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("FlyNewRoom", Flags), motor, "Forget") >= 0,
            "Task14 R4 FlightMotor/Fog transient state follows species lifecycle");

        Console.WriteLine("Task14 R4 batch1: FlightMotor foundation, Injury Dijkstra semantics and Fog goal modifier are code-migrated; remaining domain steering and Combat responsibility split stay open.");
    }
}
