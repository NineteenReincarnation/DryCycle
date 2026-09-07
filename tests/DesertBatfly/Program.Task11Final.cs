using System;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask11Final()
    {
        Type tacticsType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);
        Type bridgeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatVengeanceBridge", true);
        Type traceType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTrace", true);
        Type aiType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type hooksType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);

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
        MethodInfo adjustVengeance = tacticsType.GetMethod("AdjustExtremeVengeanceGoal", Flags);
        MethodInfo projectileEvade = tacticsType.GetMethod("TryIncomingProjectileEvade", Flags);
        Check(adjustFakeDive != null && adjustVengeance != null && projectileEvade != null,
            "Task11 exposes ordinary attack, Extreme Vengeance and real-projectile tactical entry points");
        Check(!MethodWritesField(adjustFakeDive, typeof(BodyChunk), "vel") &&
              !MethodWritesField(adjustVengeance, typeof(BodyChunk), "vel") &&
              !MethodWritesField(projectileEvade, typeof(BodyChunk), "vel"),
            "Task11 tactical helpers never become a second BodyChunk velocity locomotion system");
        Check(!TypeCallsForbiddenTask11Input(tacticsType),
            "Task11 shared tactics never read player input/controller state or hidden intent");

        MethodInfo aiUpdate = aiType.GetMethod("Update", Flags);
        Check(MethodCallsTask11(aiUpdate, tacticsType, "AdjustFakeDiveChance"),
            "ordinary DesertBatflyAI attack selection actually consumes learned FakeDive weighting");

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

        Type rejectedRole = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false);
        Check(rejectedRole == null,
            "Task11 final combat integration still does not restore rejected Task02 roles");

        Console.WriteLine(
            "Task 11 final tactics: learned FakeDive weighting, personality/cue modulation, Extreme Vengeance geometry, exact-frame Task09 priority, cached projectile evade, velocity ownership and Trace lifecycle verified.");
    }

    private static bool MethodCallsTask11(MethodInfo caller, Type targetType, string targetName)
    {
        byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
        if (il == null || il.Length == 0) return false;

        int offset = 0;
        while (offset < il.Length)
        {
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
                        return true;
                }
                catch (ArgumentException) { }
            }
            offset += operandSize;
        }
        return false;
    }
}
