using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunSignals()
    {
        Type kind = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalKind", true);
        string[] names = Enum.GetNames(kind);
        string[] expected =
        {
            "AlarmFlutter", "DistressCall", "RallySignal",
            "RoostCall", "HarassSignal", "SafeSignal"
        };
        Check(names.Length == expected.Length && expected.All(n => names.Contains(n)),
            "Signals V1 exposes exactly six accepted signal kinds");

        Type packet = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalPacket", true);
        foreach (string field in new[]
                 {
                     "Generation", "Kind", "Emitter", "Subject", "Threat", "PlayerTarget",
                     "Origin", "Direction", "Hop", "CreatedTick", "ExpiresTick", "Intensity"
                 })
            Check(packet.GetField(field, Flags) != null, "Signals signal packet contains " + field);

        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalRoomRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialLife", true);
        Type socialBond = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialBond", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Graphics", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureThreatRuntime", true);
        Type eventConsumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);

        Check((int)runtime.GetField("MaxAlarmHop", Flags).GetRawConstantValue() == 2,
            "Signals Alarm relay is capped at two hops");
        Check((int)roomRuntime.GetField("ActiveSignalCap", Flags).GetRawConstantValue() == 24,
            "Signals room signal storage is capped at 24 packets");
        Check((int)roomRuntime.GetField("AlarmRootMergeTicks", Flags).GetRawConstantValue() == 14,
            "Signals urgent root dedupe has a bounded merge window");

        Type receiverState = runtime.GetNestedType("ReceiverState", Flags);
        FieldInfo generations = receiverState?.GetField("Generations", Flags);
        Check(generations != null && generations.FieldType == typeof(int[]),
            "Signals receiver generation history is a fixed integer ring, not an unbounded dictionary");
        Check((int)runtime.GetField("GenerationHistorySize", Flags).GetRawConstantValue() == 12,
            "Signals receiver generation ring remains fixed at 12 entries");

        Type roomState = roomRuntime.GetNestedType("RoomState", Flags);
        Check(roomState?.GetMethod("DeliverUrgent", Flags) != null &&
              roomState.GetMethod("AddOrRefresh", Flags) != null &&
              roomState.GetMethod("Prune", Flags) != null,
            "Signals room runtime owns bounded urgent delivery, merge and expiry cleanup");

        MethodInfo response = runtime.GetMethod("ResponseStrength", Flags);
        MethodInfo receive = runtime.GetMethod("ReceivePacket", Flags);
        MethodInfo perceive = runtime.GetMethod("TryPerceive", Flags);
        MethodInfo safe = runtime.GetMethod("CanAcceptSafe", Flags);
        Check(response != null && receive != null && perceive != null && safe != null,
            "Signals has explicit response, perception and Safe acceptance gates");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalIntegration", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalVengeanceBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalThreatBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalAcuteBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSignalDirectWitnessBridge", false) == null,
            "Signals R5 retires all internal Reflection/RuntimeDetour signal integration layers");

        Check(runtime.GetMethod("ApplyAlarm", Flags) != null &&
              runtime.GetMethod("EmitAcuteAlarm", Flags) != null &&
              runtime.GetMethod("EmitRally", Flags) != null &&
              ai.GetMethod("ThreatenedAt", Flags) != null &&
              socialBond.GetMethod("IsDirectDeathWitness", Flags) != null,
            "Signals direct APIs own anonymous alarm escape, acute roots, Rally and grief witness boundaries");
        Check(combat.GetMethod("FindSocialHarassTarget", Flags) != null &&
              social.GetMethod("FindRoostSource", Flags) != null,
            "Signals Harass/Roost influence is consumed directly by Combat and Social owners");

        MethodInfo reportExplosion = threatRuntime.GetMethod("ReportExplosion", Flags);
        MethodInfo startle = threatRuntime.GetMethod("BroadcastStartle", Flags);
        MethodInfo mass = threatRuntime.GetMethod("BroadcastMassCasualty", Flags);
        MethodInfo receiveFear = intimidation.GetMethod("ReceiveFear", Flags);
        MethodInfo armVengeance = intimidation.GetMethod("ArmVengeance", Flags);
        MethodInfo captureConsumer = eventConsumers.GetMethod("OnCapture", Flags);
        Check(MethodCallOffset(reportExplosion, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(startle, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(mass, runtime, "EmitAcuteAlarm") >= 0,
            "Threat acute events explicitly emit one Signals root at the real event position");
        Check(MethodCallOffset(receiveFear, runtime, "EmitAlarm") >= 0 &&
              MethodCallOffset(armVengeance, runtime, "EmitRally") >= 0 &&
              MethodCallOffset(captureConsumer, runtime, "EmitDistress") >= 0,
            "fear, Vengeance and capture semantics publish through direct Signals APIs");

        Check(!TypeCallsSignalForbidden(runtime) && !TypeCallsSignalForbidden(socialBond),
            "Signals signal data path never reads input, writes ThreatSignature evidence or directly owns BodyChunk velocity");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check(state.GetFields(Flags).All(f => f.Name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0),
            "Signals realized signal state is not persisted in DB_State");

        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        int threatStage = MethodCallOffset(updateAI, threatRuntime, "Update");
        int signalStage = MethodCallOffset(updateAI, runtime, "Update");
        int socialStage = MethodCallOffset(updateAI, social, "Update");
        Check(threatStage >= 0 && signalStage > threatStage && socialStage > signalStage,
            "realized pipeline stays Threat threat context -> Signals signal information -> Social neutral social life");

        MethodInfo draw = graphics.GetMethod("DrawSprites", Flags);
        Check(MethodCallsSignal(draw, runtime, "TryGetDisplay"),
            "Signals signal display is visible through DB_Graphics without a new movement controller");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DB_SignalDebugSource", true);
        Check(debug != null,
            "Signals Observatory source exists for generation/hop/perception/influence inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Signals does not restore rejected rejected social-role design social role runtime");

        Check(intimidation.GetNestedType("SocialRole", Flags) == null &&
              intimidation.GetNestedType("VengeanceParticipation", Flags) != null,
            "Signals terminology keeps vengeance participation separate from rejected rejected social-role design SocialRole");

        Console.WriteLine(
            "Signals signals: six-kind model, bounded room/generation state, relay cap, direct API indirect-fear migration, accurate acute roots, direct-witness grief boundary, Threat read-only boundary, pipeline, graphics, vengeance terminology and anti-role guards verified.");
    }

    private static bool MethodCallsSignal(MethodInfo caller, Type targetType, string targetName) =>
        MethodCallOffset(caller, targetType, targetName) >= 0;

    private static bool TypeCallsSignalForbidden(Type type)
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
                        if (ForbiddenSignalCall(called)) return true;
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

    private static bool ForbiddenSignalCall(MethodBase called)
    {
        if (called == null) return false;
        string owner = called.DeclaringType?.FullName ?? string.Empty;
        if (owner == "UnityEngine.Input") return true;
        if (owner.IndexOf("DB_ThreatMemoryStore", StringComparison.Ordinal) >= 0 &&
            called.Name == "AddEvidence") return true;
        return false;
    }
}
