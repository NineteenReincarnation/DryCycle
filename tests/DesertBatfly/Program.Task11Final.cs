using System;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask11Final()
    {
        Type tacticsType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type bridgeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatVengeanceBridge", true);
        Type traceType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatTrace", true);
        Type threatRuntimeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type socialLifeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type aiType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type hooksType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);

        MethodInfo learnedFakeDive = tacticsType.GetMethod("LearnedFakeDiveChance", Flags);
        Check(learnedFakeDive != null,
            "Task11 exposes pure learned FakeDive weighting for regression/tuning");

        float baseline = (float)learnedFakeDive.Invoke(null, new object[]
        {
            0.30f, 0f, 0f, 0f, 0f, 0.5f, 0.5f, false
        });
        float learned = (float)learnedFakeDive.Invoke(null, new object[]
        {
            0.30f, 0.85f, 0.90f, 0.75f, 0.90f, 0.45f, 0.70f, false
        });
        float visibleSpear = (float)learnedFakeDive.Invoke(null, new object[]
        {
            0.30f, 0.85f, 0.90f, 0.75f, 0.90f, 0.45f, 0.70f, true
        });
        float lowNerve = (float)learnedFakeDive.Invoke(null, new object[]
        {
            0.30f, 0.85f, 0.90f, 0.75f, 0.90f, 0.10f, 0.70f, true
        });
        float highNerve = (float)learnedFakeDive.Invoke(null, new object[]
        {
            0.30f, 0.85f, 0.90f, 0.75f, 0.90f, 0.95f, 0.70f, true
        });

        Check(Math.Abs(baseline - 0.30f) < 0.0001f,
            "Task11 with no learned evidence leaves vanilla/personality FakeDive chance unchanged");
        Check(learned > baseline && learned < 1f,
            "high projectile/piercing/counter-kill memory increases probe/FakeDive weight without forcing it");
        Check(visibleSpear > learned,
            "a currently visible Spear strengthens learned FakeDive caution without training memory by itself");
        Check(lowNerve > highNerve,
            "Task11 personality modulation makes low-Nerve bats more cautious than high-Nerve bats");

        MethodInfo adjustFakeDive = tacticsType.GetMethod("AdjustFakeDiveChance", Flags);
        MethodInfo ordinaryProjectileEvade = tacticsType.GetMethod("TryApplyOrdinaryProjectileEvade", Flags);
        MethodInfo adjustVengeance = tacticsType.GetMethod("AdjustExtremeVengeanceGoal", Flags);
        MethodInfo projectileEvade = tacticsType.GetMethod("TryIncomingProjectileEvade", Flags);
        Check(adjustFakeDive != null && ordinaryProjectileEvade != null &&
              adjustVengeance != null && projectileEvade != null,
            "Task11 exposes ordinary attack, ordinary real-projectile evade and Extreme Vengeance tactical entry points");
        Check(!MethodWritesField(adjustFakeDive, typeof(BodyChunk), "vel") &&
              !MethodWritesField(ordinaryProjectileEvade, typeof(BodyChunk), "vel") &&
              !MethodWritesField(adjustVengeance, typeof(BodyChunk), "vel") &&
              !MethodWritesField(projectileEvade, typeof(BodyChunk), "vel"),
            "Task11 tactical helpers never become a second BodyChunk velocity locomotion system");
        Check(MethodCallsTask11(ordinaryProjectileEvade, tacticsType, "TryIncomingProjectileEvade"),
            "ordinary real-projectile response reuses the same validated trajectory/side-step geometry as Vengeance");
        Check(!TypeCallsForbiddenTask11Input(tacticsType),
            "Task11 shared tactics never read player input/controller state or hidden intent");

        MethodInfo aiUpdate = aiType.GetMethod("Update", Flags);
        Check(MethodCallsTask11(aiUpdate, tacticsType, "AdjustFakeDiveChance"),
            "ordinary DB_AI attack selection actually consumes learned FakeDive weighting");

        MethodInfo tryInjuryRecovery = aiType.GetMethod("TryInjuryRecovery", Flags);
        MethodInfo canHarass = aiType.GetMethod("CanHarass", Flags);
        MethodInfo acquireSlot = aiType.GetMethod("AcquireSlot", Flags);
        MethodInfo armRetaliation = aiType.GetMethod("ArmRetaliation", Flags);
        MethodInfo traumatizedPlayer = aiType.GetMethod("IsTraumatizedPlayer", Flags);
        int injuryOffset = MethodCallOffset(aiUpdate, aiType, "TryInjuryRecovery");
        int fakeDiveOffset = MethodCallOffset(aiUpdate, tacticsType, "AdjustFakeDiveChance");
        Check(tryInjuryRecovery != null && injuryOffset >= 0 && fakeDiveOffset > injuryOffset,
            "Severe Injury recovery is evaluated before Task11 learned attack weighting");
        Check(traumatizedPlayer != null &&
              MethodCallsTask11(canHarass, aiType, "IsTraumatizedPlayer") &&
              MethodCallsTask11(acquireSlot, aiType, "IsTraumatizedPlayer") &&
              MethodCallsTask11(armRetaliation, aiType, "IsTraumatizedPlayer"),
            "strong player-specific Trauma/PTSD still vetoes ordinary harass, attack-slot acquisition and retaliation");

        MethodInfo bridgeHook = bridgeType.GetMethod("ForceFlightHook", Flags);
        MethodInfo bridgeUpdateHook = bridgeType.GetMethod("IntimidationUpdateHook", Flags);
        MethodInfo markTravelOwnedFrame = bridgeType.GetMethod("MarkTravelOwnedFrame", Flags);
        Check(bridgeType.GetMethod("Enable", Flags) != null &&
              bridgeType.GetMethod("Disable", Flags) != null &&
              bridgeType.GetProperty("Installed", Flags) != null,
            "Task11 Extreme Vengeance bridge has explicit lifecycle and install status");
        Check(bridgeHook != null &&
              MethodCallsTask11(bridgeHook, tacticsType, "AdjustExtremeVengeanceGoal"),
            "Extreme Vengeance ForceFlight bridge consumes the same per-player learned tactical profile");
        Check(!MethodWritesField(bridgeHook, typeof(BodyChunk), "vel"),
            "Task11 Vengeance bridge only changes ForceFlight arguments; original Intimidation owns velocity");
        Check(bridgeUpdateHook != null && markTravelOwnedFrame != null,
            "Task11 Vengeance bridge exposes exact-frame Task09 suspension without deleting Vengeance memory");
        Check(!MethodWritesField(bridgeUpdateHook, typeof(BodyChunk), "vel") &&
              !MethodWritesField(markTravelOwnedFrame, typeof(BodyChunk), "vel"),
            "Task09/Vengeance priority bridge never takes over physical locomotion");
        Check(!TypeCallsForbiddenTask11Input(bridgeType),
            "Task11 Vengeance bridge does not inspect input or predict future attacks");

        MethodInfo traceSample = traceType.GetMethod("Sample", Flags);
        Check(traceSample != null,
            "Task11 has a watched-only Threat Signature trace sampler");
        MethodInfo hookUpdateAI = hooksType.GetMethod("UpdateAI", Flags);
        MethodInfo hookRain = hooksType.GetMethod("Rain", Flags);
        Check(MethodCallsTask11(hookUpdateAI, traceType, "Sample"),
            "Task11 threat trace is sampled in the realized AI pipeline before neutral Task10 social life");
        Check(MethodCallsTask11(hooksType.GetMethod("Enable", Flags), bridgeType, "Enable") &&
              MethodCallsTask11(hooksType.GetMethod("Disable", Flags), bridgeType, "Disable"),
            "Task11 Vengeance bridge installs/uninstalls with DesertBatfly lifecycle");
        Check(MethodCallsTask11(hookUpdateAI, bridgeType, "MarkTravelOwnedFrame") &&
              MethodCallsTask11(hookRain, bridgeType, "MarkTravelOwnedFrame"),
            "Task09 marks only successfully-owned realized frames so later Intimidation/Vengeance cannot steal them back");

        int threatUpdateOffset = MethodCallOffset(hookUpdateAI, threatRuntimeType, "Update");
        int ordinaryEvadeOffset = MethodCallOffset(hookUpdateAI, tacticsType, "TryApplyOrdinaryProjectileEvade");
        int traceOffset = MethodCallOffset(hookUpdateAI, traceType, "Sample");
        int socialOffset = MethodCallOffset(hookUpdateAI, socialLifeType, "Update");
        Check(threatUpdateOffset >= 0 && ordinaryEvadeOffset > threatUpdateOffset &&
              traceOffset > ordinaryEvadeOffset && socialOffset > ordinaryEvadeOffset,
            "real projectile cue is detected first, then ordinary lateral evade owns localGoal before Trace and neutral Task10");

        Type rejectedRole = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false);
        Check(rejectedRole == null,
            "Task11 final combat integration still does not restore rejected Task02 roles");

        Console.WriteLine(
            "Task 11 final tactics: learned FakeDive weighting, ordinary real-projectile lateral evade, Severe Injury/PTSD priority guards, Extreme Vengeance geometry, exact-frame Task09 priority, velocity ownership and Trace lifecycle verified.");
    }

    private static bool MethodCallsTask11(MethodInfo caller, Type targetType, string targetName) =>
        MethodCallOffset(caller, targetType, targetName) >= 0;

    private static int MethodCallOffset(MethodInfo caller, Type targetType, string targetName)
    {
        byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
        if (il == null || il.Length == 0) return -1;

        int offset = 0;
        while (offset < il.Length)
        {
            int opcodeOffset = offset;
            OpCode opcode;
            byte first = il[offset++];
            if (first == 0xFE)
            {
                if (offset >= il.Length) break;
                opcode = MultiByteOpCode(il[offset++]);
            }
            else opcode = SingleByteOpCode(first);

            int operandOffset = offset;
            int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
            if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize >= 4)
            {
                try
                {
                    MethodBase called = caller.Module.ResolveMethod(
                        BitConverter.ToInt32(il, operandOffset));
                    if (called?.DeclaringType == targetType && called.Name == targetName)
                        return opcodeOffset;
                }
                catch (ArgumentException) { }
            }
            offset += operandSize;
        }
        return -1;
    }
}
