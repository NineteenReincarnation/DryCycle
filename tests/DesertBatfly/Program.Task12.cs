using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask12()
    {
        Type kind = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalKind", true);
        string[] names = Enum.GetNames(kind);
        string[] expected =
        {
            "AlarmFlutter", "DistressCall", "RallySignal",
            "RoostCall", "HarassSignal", "SafeSignal"
        };
        Check(names.Length == expected.Length && expected.All(n => names.Contains(n)),
            "Task12 V1 exposes exactly six accepted signal kinds");

        Type packet = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalPacket", true);
        foreach (string field in new[]
                 {
                     "Generation", "Kind", "Emitter", "Subject", "Threat", "PlayerTarget",
                     "Origin", "Direction", "Hop", "CreatedTick", "ExpiresTick", "Intensity"
                 })
            Check(packet.GetField(field, Flags) != null, "Task12 signal packet contains " + field);

        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRoomRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type socialBond = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialBond", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Graphics", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type eventConsumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);

        Check((int)runtime.GetField("MaxAlarmHop", Flags).GetRawConstantValue() == 2,
            "Task12 Alarm relay is capped at two hops");
        Check((int)roomRuntime.GetField("ActiveSignalCap", Flags).GetRawConstantValue() == 24,
            "Task12 room signal storage is capped at 24 packets");
        Check((int)roomRuntime.GetField("AlarmRootMergeTicks", Flags).GetRawConstantValue() == 14,
            "Task12 urgent root dedupe has a bounded merge window");

        Type receiverState = runtime.GetNestedType("ReceiverState", Flags);
        FieldInfo generations = receiverState?.GetField("Generations", Flags);
        Check(generations != null && generations.FieldType == typeof(int[]),
            "Task12 receiver generation history is a fixed integer ring, not an unbounded dictionary");
        Check((int)runtime.GetField("GenerationHistorySize", Flags).GetRawConstantValue() == 12,
            "Task12 receiver generation ring remains fixed at 12 entries");

        Type roomState = roomRuntime.GetNestedType("RoomState", Flags);
        Check(roomState?.GetMethod("DeliverUrgent", Flags) != null &&
              roomState.GetMethod("AddOrRefresh", Flags) != null &&
              roomState.GetMethod("Prune", Flags) != null,
            "Task12 room runtime owns bounded urgent delivery, merge and expiry cleanup");

        MethodInfo response = runtime.GetMethod("ResponseStrength", Flags);
        MethodInfo receive = runtime.GetMethod("ReceivePacket", Flags);
        MethodInfo perceive = runtime.GetMethod("TryPerceive", Flags);
        MethodInfo safe = runtime.GetMethod("CanAcceptSafe", Flags);
        Check(response != null && receive != null && perceive != null && safe != null,
            "Task12 has explicit response, perception and Safe acceptance gates");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", false) == null,
            "Task12 R5 retires all internal Reflection/RuntimeDetour signal integration layers");

        Check(runtime.GetMethod("ApplyAlarm", Flags) != null &&
              runtime.GetMethod("EmitAcuteAlarm", Flags) != null &&
              runtime.GetMethod("EmitRally", Flags) != null &&
              ai.GetMethod("ThreatenedAt", Flags) != null &&
              socialBond.GetMethod("IsDirectDeathWitness", Flags) != null,
            "Task12 direct APIs own anonymous alarm escape, acute roots, Rally and grief witness boundaries");
        Check(combat.GetMethod("FindSocialHarassTarget", Flags) != null &&
              social.GetMethod("FindRoostSource", Flags) != null,
            "Task12 Harass/Roost influence is consumed directly by Combat and Social owners");

        MethodInfo reportExplosion = threatRuntime.GetMethod("ReportExplosion", Flags);
        MethodInfo startle = threatRuntime.GetMethod("BroadcastStartle", Flags);
        MethodInfo mass = threatRuntime.GetMethod("BroadcastMassCasualty", Flags);
        MethodInfo receiveFear = intimidation.GetMethod("ReceiveFear", Flags);
        MethodInfo armVengeance = intimidation.GetMethod("ArmVengeance", Flags);
        MethodInfo captureConsumer = eventConsumers.GetMethod("OnCapture", Flags);
        Check(MethodCallOffset(reportExplosion, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(startle, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(mass, runtime, "EmitAcuteAlarm") >= 0,
            "Task11 acute events explicitly emit one Task12 root at the real event position");
        Check(MethodCallOffset(receiveFear, runtime, "EmitAlarm") >= 0 &&
              MethodCallOffset(armVengeance, runtime, "EmitRally") >= 0 &&
              MethodCallOffset(captureConsumer, runtime, "EmitDistress") >= 0,
            "fear, Vengeance and capture semantics publish through direct Task12 APIs");

        Check(!TypeCallsTask12Forbidden(runtime) && !TypeCallsTask12Forbidden(socialBond),
            "Task12 signal data path never reads input, writes ThreatSignature evidence or directly owns BodyChunk velocity");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check(state.GetFields(Flags).All(f => f.Name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0),
            "Task12 realized signal state is not persisted in DB_State");

        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        int task11 = MethodCallOffset(updateAI, threatRuntime, "Update");
        int task12 = MethodCallOffset(updateAI, runtime, "Update");
        int task10 = MethodCallOffset(updateAI, social, "Update");
        Check(task11 >= 0 && task12 > task11 && task10 > task12,
            "realized pipeline stays Task11 threat context -> Task12 signal information -> Task10 neutral social life");

        MethodInfo draw = graphics.GetMethod("DrawSprites", Flags);
        Check(MethodCallsTask12(draw, runtime, "TryGetDisplay"),
            "Task12 signal display is visible through DB_Graphics without a new movement controller");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DB_SignalDebugSource", true);
        Check(debug != null,
            "Task12 Observatory source exists for generation/hop/perception/influence inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Task12 does not restore rejected Task02 social role runtime");

        Check(intimidation.GetNestedType("SocialRole", Flags) == null &&
              intimidation.GetNestedType("VengeanceParticipation", Flags) != null,
            "Task12 terminology keeps vengeance participation separate from rejected Task02 SocialRole");

        Console.WriteLine(
            "Task 12 signals: six-kind model, bounded room/generation state, relay cap, direct API indirect-fear migration, accurate acute roots, direct-witness grief boundary, Task11 read-only boundary, pipeline, graphics, vengeance terminology and anti-role guards verified.");
    }

    private static bool MethodCallsTask12(MethodInfo caller, Type targetType, string targetName) =>
        MethodCallOffset(caller, targetType, targetName) >= 0;

    private static bool TypeCallsTask12Forbidden(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                     BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length == 0) continue;
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
                        MethodBase called = method.Module.ResolveMethod(BitConverter.ToInt32(il, operandOffset));
                        if (ForbiddenTask12Call(called)) return true;
                    }
                    catch (ArgumentException) { }
                }
                else if (opcode == OpCodes.Stfld && operandSize >= 4)
                {
                    try
                    {
                        FieldInfo field = method.Module.ResolveField(BitConverter.ToInt32(il, operandOffset));
                        if (field?.DeclaringType == typeof(BodyChunk) && field.Name == "vel") return true;
                    }
                    catch (ArgumentException) { }
                }
                offset += operandSize;
            }
        }
        return false;
    }

    private static bool ForbiddenTask12Call(MethodBase called)
    {
        if (called == null) return false;
        string owner = called.DeclaringType?.FullName ?? string.Empty;
        if (owner == "UnityEngine.Input") return true;
        if (owner.IndexOf("DB_ThreatMemoryStore", StringComparison.Ordinal) >= 0 &&
            called.Name == "AddEvidence") return true;
        return false;
    }
}
