using System;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunThreatTactics()
    {
        Type tacticsType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type threatRuntimeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type vengeanceType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type motorType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        Type arbiterType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        Type weaponPerceptionType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);

        MethodInfo learnedFakeDive = tacticsType.GetMethod("LearnedFakeDiveChance", Flags);
        Check(learnedFakeDive != null,
            "Threat exposes pure learned FakeDive weighting for regression/tuning");

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
            "Threat with no learned evidence leaves personality FakeDive chance unchanged");
        Check(learned > baseline && learned < 1f,
            "learned projectile/piercing/counter-kill evidence increases probe weight without forcing it");
        Check(visibleSpear > learned,
            "a currently visible Spear strengthens learned caution without training memory by itself");
        Check(lowNerve > highNerve,
            "Threat personality modulation makes low-Nerve bats more cautious than high-Nerve bats");

        MethodInfo adjustFakeDive = tacticsType.GetMethod("AdjustFakeDiveChance", Flags);
        MethodInfo ordinaryEvade = tacticsType.GetMethod("TryApplyOrdinaryProjectileEvade", Flags);
        MethodInfo applyEvade = tacticsType.GetMethod("ApplyProjectileEvadeOwned", Flags);
        MethodInfo adjustVengeance = tacticsType.GetMethod("AdjustExtremeVengeanceGoal", Flags);
        MethodInfo projectileGeometry = tacticsType.GetMethod("TryIncomingProjectileEvade", Flags);
        MethodInfo tryProfile = tacticsType.GetMethod("TryProfile", Flags);
        Check(adjustFakeDive != null && ordinaryEvade != null && applyEvade != null &&
              adjustVengeance != null && projectileGeometry != null && tryProfile != null,
            "Threat exposes current attack weighting, projectile evade and Vengeance geometry entry points");

        Check(!MethodWritesField(adjustFakeDive, typeof(BodyChunk), "vel") &&
              !MethodWritesField(ordinaryEvade, typeof(BodyChunk), "vel") &&
              !MethodWritesField(applyEvade, typeof(BodyChunk), "vel") &&
              !MethodWritesField(adjustVengeance, typeof(BodyChunk), "vel") &&
              !MethodWritesField(projectileGeometry, typeof(BodyChunk), "vel"),
            "Threat tactics never become a parallel BodyChunk velocity locomotion system");
        Check(MethodCallsThreat(ordinaryEvade, threatRuntimeType, "TryGetDebugState") &&
              MethodCallsThreat(ordinaryEvade, vengeanceType, "IsActive") &&
              MethodCallsThreat(ordinaryEvade, tacticsType, "ApplyProjectileEvadeOwned"),
            "ordinary projectile response consumes current Threat facts, preserves Vengeance priority and enters the owner-gated evade path");
        Check(MethodCallsThreat(applyEvade, arbiterType, "IsPrimaryOwner") &&
              MethodCallsThreat(applyEvade, motorType, "TryGuideNative"),
            "projectile evade requires same-frame ownership and submits movement through FlightMotor");
        Check(MethodCallsThreat(tryProfile, weaponPerceptionType, "TryObserveHeldThreats"),
            "Threat tactical profile uses shared held-item perception rather than a private scan");
        Check(!TypeCallsForbiddenThreatInput(tacticsType),
            "Threat tactics never inspect player input/controller state or hidden intent");

        Console.WriteLine(
            "Threat tactics: learned FakeDive weighting, shared perception, current Threat facts, Vengeance priority, owner-gated projectile evade and FlightMotor ownership verified.");
    }

    private static bool MethodCallsThreat(MethodInfo caller, Type targetType, string targetName) =>
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
