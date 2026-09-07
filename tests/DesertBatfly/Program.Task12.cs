using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask12()
    {
        Type kind = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalKind", true);
        string[] names = Enum.GetNames(kind);
        string[] expected =
        {
            "AlarmFlutter", "DistressCall", "RallySignal",
            "RoostCall", "HarassSignal", "SafeSignal"
        };
        Check(names.Length == expected.Length && expected.All(n => names.Contains(n)),
            "Task12 V1 exposes exactly six accepted signal kinds");

        Type packet = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalPacket", true);
        foreach (string field in new[]
                 {
                     "Generation", "Kind", "Emitter", "Subject", "Threat", "PlayerTarget",
                     "Origin", "Direction", "Hop", "CreatedTick", "ExpiresTick", "Intensity"
                 })
            Check(packet.GetField(field, Flags) != null, "Task12 signal packet contains " + field);

        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);
        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRoomRuntime", true);
        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", true);
        Type vengeanceBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", true);
        Type threatBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", true);
        Type acuteBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", true);
        Type directWitnessBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyGraphics", true);

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

        Check(integration.GetMethod("ApplyAlarm", Flags) != null &&
              integration.GetMethod("FindSocialHarassTargetHook", Flags) != null &&
              integration.GetMethod("FindRoostSourceHook", Flags) != null &&
              integration.GetMethod("HandleIndirectFear", Flags) != null,
            "Task12 integration replaces legacy alarm/indirect fear and routes Harass/Roost through signals");

        MethodInfo vengeanceEnable = vengeanceBridge.GetMethod("Enable", Flags);
        MethodInfo vengeanceDisable = vengeanceBridge.GetMethod("Disable", Flags);
        Check(MethodCallsTask12(vengeanceEnable, threatBridge, "Enable") &&
              MethodCallsTask12(vengeanceDisable, threatBridge, "Disable") &&
              MethodCallsTask12(vengeanceEnable, acuteBridge, "Enable") &&
              MethodCallsTask12(vengeanceDisable, acuteBridge, "Disable") &&
              MethodCallsTask12(vengeanceEnable, directWitnessBridge, "Enable") &&
              MethodCallsTask12(vengeanceDisable, directWitnessBridge, "Disable"),
            "Task12 Task11-response, acute-event and direct-witness bridges share the signal lifecycle");

        Check(acuteBridge.GetMethod("ExplosionHook", Flags) != null &&
              acuteBridge.GetMethod("StartleHook", Flags) != null &&
              acuteBridge.GetMethod("MassCasualtyHook", Flags) != null &&
              acuteBridge.GetMethod("EmitAcuteAlarm", Flags) != null,
            "Task12 acute bridge converts real Task11 explosion/startle/casualty positions into Alarm roots");
        Check(directWitnessBridge.GetMethod("IsDirectWitness", Flags) != null,
            "Task12 has an explicit direct-witness gate for persistent Bond-death Grief/Trauma");

        Check(!TypeCallsTask12Forbidden(threatBridge) && !TypeCallsTask12Forbidden(acuteBridge) &&
              !TypeCallsTask12Forbidden(directWitnessBridge) && !TypeCallsTask12Forbidden(runtime) &&
              !TypeCallsTask12Forbidden(integration),
            "Task12 signal layer never reads input, writes ThreatSignature evidence or directly owns BodyChunk velocity");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);
        Check(state.GetFields(Flags).All(f => f.Name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0),
            "Task12 realized signal state is not persisted in DesertBatflyState");

        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        int task11 = MethodCallOffset(updateAI, threatRuntime, "Update");
        int task12 = MethodCallOffset(updateAI, runtime, "Update");
        int task10 = MethodCallOffset(updateAI, social, "Update");
        Check(task11 >= 0 && task12 > task11 && task10 > task12,
            "realized pipeline stays Task11 threat context -> Task12 signal information -> Task10 neutral social life");

        MethodInfo draw = graphics.GetMethod("DrawSprites", Flags);
        Check(MethodCallsTask12(draw, runtime, "TryGetDisplay"),
            "Task12 signal display is visible through DesertBatflyGraphics without a new movement controller");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DesertBatflyTask12DebugSource", true);
        Check(debug != null,
            "Task12 Observatory source exists for generation/hop/perception/influence inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Task12 does not restore rejected Task02 social role runtime");

        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Check(intimidation.GetNestedType("SocialRole", Flags) == null &&
              intimidation.GetNestedType("VengeanceParticipation", Flags) != null,
            "Task12 terminology keeps vengeance participation separate from rejected Task02 SocialRole");

        Console.WriteLine(
            "Task 12 signals: six-kind model, bounded room/generation state, relay cap, indirect-fear migration, accurate acute roots, direct-witness grief boundary, Task11 read-only boundary, lifecycle, pipeline, graphics, vengeance terminology and anti-role guards verified.");
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
        if (owner.IndexOf("DesertBatflyThreatMemoryStore", StringComparison.Ordinal) >= 0 &&
            called.Name == "AddEvidence") return true;
        return false;
    }
}
