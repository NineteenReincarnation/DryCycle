using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R4()
    {
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type fog = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FogGoalModifier", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type special = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SpecialPhysicsOwner", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type injury = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyInjury", true);
        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Type survival = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);
        Type frame = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type combatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type combatExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatExecutor", true);
        Type motorDebug = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotorDebugState", true);
        Type debugSource = mod.GetType("DryCycle.Debugging.AI.DB_ObservatorySource", true);

        Check(motor.GetMethod("Reset", Flags) != null &&
              motor.GetMethod("Forget", Flags) != null &&
              motor.GetMethod("TrySteer", Flags) != null &&
              motor.GetMethod("TryGuideNative", Flags) != null &&
              motor.GetMethod("TryRetarget", Flags) != null &&
              motor.GetMethod("ApplyPostPhysics", Flags) != null &&
              motor.GetMethod("TryGetIntent", Flags) != null &&
              motor.GetMethod("TryGetDebugState", Flags) != null,
            "Task14 R4 FlightMotor exposes active steer, native guide, same-owner retarget and final modifier surfaces");
        Check(fog.GetMethod("ModifyGoal", Flags) != null &&
              fog.GetMethod("Reset", Flags) != null && fog.GetMethod("Forget", Flags) != null,
            "Task14 R4 Fog uncertainty is an explicit goal modifier with lifecycle ownership");
        Check(MethodCallOffset(motor.GetMethod("TrySteer", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryGuideNative", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryRetarget", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "all R4 FlightMotor write surfaces require the same-tick PrimaryOwner");
        Check(injury.GetField("NominalFlightSpeed", Flags) == null &&
              injury.GetMethod("ApplyFlight", Flags) == null &&
              injury.GetMethod("ModifyFlight", Flags) != null,
            "Task14 R4 Injury stays a pure flight modifier");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), motor, "ApplyPostPhysics") >= 0,
            "Task14 R4 creature final injury pass goes through DB_FlightMotor");
        Check(MethodCallOffset(ai.GetMethod("SteerOwned", Flags), motor, "TrySteer") >= 0 &&
              MethodCallOffset(ai.GetMethod("TryDriveRecoveryHive", Flags), motor, "TrySteer") >= 0,
            "core species steering and InjuryRecovery use FlightMotor");
        Check(MethodCallOffset(environment.GetMethod("ApplyLocalBehavior", Flags), motor, "TryGuideNative") >= 0,
            "Environment shelter navigation preserves native flight while centralizing its goal write");
        Check(MethodCallOffset(social.GetMethod("SocialSteer", Flags), motor, "TryGuideNative") >= 0,
            "Social goal submission goes through FlightMotor native guidance");
        Check(MethodCallOffset(vengeance.GetMethod("ForceFlight", Flags), motor, "TrySteer") >= 0,
            "Vengeance active velocity request goes through FlightMotor");
        Check(MethodCallOffset(tactics.GetMethod("ApplyProjectileEvadeOwned", Flags), motor, "TryGuideNative") >= 0,
            "Projectile evade goal/nominal speed goes through FlightMotor");
        Check(MethodCallOffset(threat.GetMethod("ApplyTacticalAdjustment", Flags), motor, "TryRetarget") >= 0,
            "Threat learned geometry can only retarget the existing Combat motor intent");
        Check(MethodCallOffset(travel.GetMethod("HoldAtRefuge", Flags), motor, "TryGuideNative") >= 0,
            "Travel refuge holding centralizes realized localGoal writes through FlightMotor");
        Check(MethodCallOffset(survival.GetMethod("ApplyNativeHomeAndBurrow", Flags), motor, "TryGuideNative") >= 0,
            "same-room environmental Home retreat uses FlightMotor while native Burrow stays special physics");
        Check(Enum.IsDefined(special, "NativeBurrow") && Enum.IsDefined(special, "NativeChain"),
            "Task14 R4 explicitly classifies native Burrow and Chain as special-physics owners");
        Check(combatRuntime.GetProperty("Target", Flags) != null &&
              combatRuntime.GetMethod("BeginCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("ConsiderCandidate", Flags) != null &&
              combatRuntime.GetMethod("CompleteCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("PrepareSelection", Flags) != null &&
              combatRuntime.GetMethod("ArmRetaliation", Flags) != null,
            "Task14 R4 Combat runtime owns target scan/motivation and retaliation preparation");
        Check(ai.GetMethod("CanHarass", Flags) == null &&
              ai.GetMethod("FindSocialHarassTarget", Flags) == null &&
              ai.GetMethod("ArmRetaliation", Flags) == null,
            "Task14 R4 old AI shell no longer owns Harass target selection or retaliation preparation");
        Check(combatRuntime.GetMethod("TryExecuteOwned", Flags) != null &&
              combatRuntime.GetMethod("AfterPhysics", Flags) != null &&
              combatRuntime.GetProperty("FormalAttack", Flags) != null,
            "Task14 R4 Combat runtime owns formal phase execution, contact physics and formal-attack state");
        Check(MethodCallOffset(combatExecutor.GetMethod("TryExecute", Flags), combatRuntime, "TryExecuteOwned") >= 0,
            "Task14 R4 Combat executor calls DB_CombatRuntime rather than old AI state-machine implementation");
        Check(MethodCallOffset(creature.GetMethod("Update", Flags), combatRuntime, "AfterPhysics") >= 0,
            "Task14 R4 post-physics Attach/Interfere execution calls DB_CombatRuntime directly");
        Check(ai.GetMethod("FindContact", Flags) == null && ai.GetMethod("AcquireSlot", Flags) == null &&
              ai.GetMethod("UpdateInterference", Flags) == null && ai.GetMethod("Finish", Flags) == null,
            "Task14 R4 old AI shell no longer owns combat contact/slot/finish implementation");
        Check(motorDebug.GetField("Owner", Flags) != null &&
              motorDebug.GetField("Goal", Flags) != null &&
              motorDebug.GetField("NominalSpeed", Flags) != null &&
              motorDebug.GetField("PostPhysicsVelocity", Flags) != null,
            "Task14 R4 FlightMotor exposes owner/goal/speed/post-injury debug state");
        Check(debugSource.GetMethod("BuildFlightMotorSection", Flags) != null,
            "Task14 R4 Observatory exposes FlightMotor intent and special-physics boundary");
        Check(debugSource.GetField("MemoryField", Flags) == null &&
              debugSource.GetField("InterestField", Flags) == null &&
              debugSource.GetField("UnseenField", Flags) == null &&
              debugSource.GetField("HasSlotField", Flags) == null,
            "Task14 R4 Observatory no longer reflects Combat fields from the old AI shell");
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("FlyNewRoom", Flags), motor, "Forget") >= 0,
            "FlightMotor/Fog transient state follows species lifecycle");

        Console.WriteLine("Task14 R4 code-side complete: single ordinary FlightMotor boundary, Combat extraction, Observatory motor/debug migration and source writer audit are all guarded; live validation is deferred to final refactor acceptance.");
    }
}
