using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureFlightMotor()
    {
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type fog = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FogGoalModifier", true);
        Type owner = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorOwner", true);
        Type special = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SpecialPhysicsOwner", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type injury = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Injury", true);
        Type injuryRecovery = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecovery", true);
        Type creature = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Creature", true);
        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Runtime", true);
        Type environment = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type survival = environment;
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type frame = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FrameContextRuntime", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type combatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type behaviorExecution = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorExecution", true);
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
            "Architecture flight motor FlightMotor exposes active steer, native guide, same-owner retarget and final modifier surfaces");
        Check(fog.GetMethod("ModifyGoal", Flags) != null &&
              fog.GetMethod("Reset", Flags) != null && fog.GetMethod("Forget", Flags) != null,
            "Architecture flight motor Fog uncertainty is an explicit goal modifier with lifecycle ownership");
        Check(MethodCallOffset(motor.GetMethod("TrySteer", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryGuideNative", Flags), arbiter, "IsPrimaryOwner") >= 0 &&
              MethodCallOffset(motor.GetMethod("TryRetarget", Flags), arbiter, "IsPrimaryOwner") >= 0,
            "all R4 FlightMotor write surfaces require the same-tick PrimaryOwner");
        Check(injury.GetField("NominalFlightSpeed", Flags) == null &&
              injury.GetMethod("ApplyFlight", Flags) == null &&
              injury.GetMethod("ModifyFlight", Flags) != null,
            "Architecture flight motor Injury stays a pure flight modifier");

        MethodInfo runtimeAfter = runtime.GetMethod("AfterVanillaUpdate", Flags);
        Check(runtimeAfter != null &&
              MethodCallOffset(runtimeAfter, motor, "ApplyPostPhysics") >= 0 &&
              MethodCallOffset(creature.GetMethod("Update", Flags), runtime, "AfterVanillaUpdate") >= 0,
            "Architecture flight motor final injury pass is sequenced once through DB_Runtime after native Fly physics");
        Check(MethodCallOffset(ai.GetMethod("SteerOwned", Flags), motor, "TrySteer") >= 0 &&
              MethodCallOffset(injuryRecovery.GetMethod("TryDriveRecoveryHive", Flags), motor, "TrySteer") >= 0,
            "core species steering and InjuryRecovery use FlightMotor");
        Check(ai.GetMethod("TryDriveRecoveryHive", Flags) == null,
            "Architecture flight motor retired DB_AI injury-hive steering entry point stays absent");

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
            "Architecture flight motor explicitly classifies native Burrow and Chain as special-physics owners");

        Check(combatRuntime.GetProperty("Target", Flags) != null &&
              combatRuntime.GetMethod("BeginCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("ConsiderCandidate", Flags) != null &&
              combatRuntime.GetMethod("CompleteCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("PrepareSelection", Flags) != null &&
              combatRuntime.GetMethod("ArmRetaliation", Flags) != null,
            "Architecture flight motor Combat runtime owns target scan/motivation and retaliation preparation");
        Check(ai.GetMethod("CanHarass", Flags) == null &&
              ai.GetMethod("FindSocialHarassTarget", Flags) == null &&
              ai.GetMethod("ArmRetaliation", Flags) == null,
            "Architecture flight motor old AI shell no longer owns Harass target selection or retaliation preparation");
        Check(combatRuntime.GetMethod("TryExecuteOwned", Flags) != null &&
              combatRuntime.GetMethod("AfterPhysics", Flags) != null &&
              combatRuntime.GetProperty("FormalAttack", Flags) != null,
            "Architecture flight motor Combat runtime owns formal phase execution, contact physics and formal-attack state");
        Check(MethodCallOffset(behaviorExecution.GetMethod("TryCombat", Flags), combatRuntime, "TryExecuteOwned") >= 0,
            "Architecture flight motor Combat executor calls DB_CombatRuntime rather than old AI state-machine implementation");
        Check(MethodCallOffset(runtimeAfter, combatRuntime, "AfterPhysics") >= 0 &&
              MethodCallOffset(creature.GetMethod("Update", Flags), runtime, "AfterVanillaUpdate") >= 0,
            "Architecture flight motor post-physics Attach/Interfere execution is sequenced through DB_Runtime and owned by DB_CombatRuntime");
        Check(ai.GetMethod("AfterPhysics", Flags) == null &&
              ai.GetMethod("FindContact", Flags) == null && ai.GetMethod("AcquireSlot", Flags) == null &&
              ai.GetMethod("UpdateInterference", Flags) == null && ai.GetMethod("Finish", Flags) == null,
            "Architecture flight motor old AI shell no longer owns combat post-physics/contact/slot/finish implementation");

        Check(motorDebug.GetField("Owner", Flags) != null &&
              motorDebug.GetField("Goal", Flags) != null &&
              motorDebug.GetField("NominalSpeed", Flags) != null &&
              motorDebug.GetField("PostPhysicsVelocity", Flags) != null,
            "Architecture flight motor FlightMotor exposes owner/goal/speed/post-injury debug state");
        Check(debugSource.GetMethod("BuildFlightMotorSection", Flags) != null,
            "Architecture flight motor Observatory exposes FlightMotor intent and special-physics boundary");
        Check(debugSource.GetField("MemoryField", Flags) == null &&
              debugSource.GetField("InterestField", Flags) == null &&
              debugSource.GetField("UnseenField", Flags) == null &&
              debugSource.GetField("HasSlotField", Flags) == null,
            "Architecture flight motor Observatory no longer reflects Combat fields from the old AI shell");

        MethodInfo runtimeBeforeNewRoom = runtime.GetMethod("BeforeNewRoom", Flags);
        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), motor, "Reset") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), motor, "Reset") >= 0 &&
              runtimeBeforeNewRoom != null &&
              MethodCallOffset(runtimeBeforeNewRoom, motor, "Forget") >= 0 &&
              MethodCallOffset(creature.GetMethod("NewRoom", Flags), runtime, "BeforeNewRoom") >= 0,
            "FlightMotor transient state follows species enable/disable and virtual NewRoom lifecycle");
        Check(MethodCallOffset(motor.GetMethod("Reset", Flags), fog, "Reset") >= 0 &&
              MethodCallOffset(motor.GetMethod("Forget", Flags), fog, "Forget") >= 0,
            "FlightMotor owns FogGoalModifier reset/forget lifecycle instead of a second hook path");
        Check(hooks.GetMethod("FlyNewRoom", Flags) == null,
            "Architecture flight motor retired Fly.NewRoom detour stays absent");

        Console.WriteLine("Architecture flight motor code-side complete: single ordinary FlightMotor boundary, Runtime post-physics sequencing, Combat extraction and lifecycle ownership are guarded; live validation remains deferred to final acceptance.");
    }
}
