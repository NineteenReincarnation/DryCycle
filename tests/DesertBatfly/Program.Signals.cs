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
            "Signals exposes exactly six accepted signal kinds");

        Type packet = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalPacket", true);
        foreach (string field in new[]
                 {
                     "Generation", "Kind", "Emitter", "Subject", "Threat", "PlayerTarget",
                     "Origin", "Direction", "Hop", "CreatedTick", "ExpiresTick", "Intensity"
                 })
            Check(packet.GetField(field, Flags) != null, "Signals signal packet contains " + field);

        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type definition = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalDefinition", true);
        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRoomRuntime", true);
        Type perception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionRuntime", true);
        Type signalContext = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionSignalContext", true);
        Type perceptionModality = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_PerceptionModality", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type socialBond = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialBond", true);
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Graphics", true);
        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type fear = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type eventConsumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);

        MethodInfo definitionFor = definition.GetMethod("For", Flags);
        object alarmDefinition = definitionFor?.Invoke(null, new[] { Enum.Parse(kind, "AlarmFlutter") });
        object distressDefinition = definitionFor?.Invoke(null, new[] { Enum.Parse(kind, "DistressCall") });
        object rallyDefinition = definitionFor?.Invoke(null, new[] { Enum.Parse(kind, "RallySignal") });
        Check(alarmDefinition != null &&
              Convert.ToInt32(definition.GetField("MaxRelayHops", Flags)?.GetValue(alarmDefinition)) == 2 &&
              Math.Abs(Convert.ToSingle(definition.GetField("VisualRange", Flags)?.GetValue(alarmDefinition)) - 300f) < 0.0001f &&
              Math.Abs(Convert.ToSingle(definition.GetField("CloseAcousticRange", Flags)?.GetValue(alarmDefinition)) - 95f) < 0.0001f &&
              Convert.ToInt32(definition.GetField("RootTtlTicks", Flags)?.GetValue(alarmDefinition)) == 135 &&
              Convert.ToInt32(definition.GetField("RootTtlTicks", Flags)?.GetValue(distressDefinition)) == 120 &&
              Convert.ToInt32(definition.GetField("RootTtlTicks", Flags)?.GetValue(rallyDefinition)) == 84,
            "Signals Definition remains the single authority for range, TTL and relay parameters");
        Check((int)roomRuntime.GetField("ActiveSignalCap", Flags).GetRawConstantValue() == 24,
            "Signals room signal storage is capped at 24 packets");
        Check((int)roomRuntime.GetField("AlarmRootMergeTicks", Flags).GetRawConstantValue() == 14,
            "Signals urgent root dedupe has a bounded merge window");

        FieldInfo generations = perception.GetField("signalGenerations", Flags);
        Check(generations != null && generations.FieldType == typeof(int[]),
            "Perception R2 owns signal generation history as a fixed integer ring");
        Check((int)perception.GetField("SignalGenerationHistorySize", Flags).GetRawConstantValue() == 12,
            "Perception R2 signal generation ring remains fixed at 12 entries");
        Check(perception.GetMethod("ReceiveSignal", Flags) != null &&
              perception.GetMethod("TryGetSignalContext", Flags) != null &&
              perception.GetMethod("CanAcceptSafeSignal", Flags) != null,
            "Perception R2 owns signal reception, receiver belief and Safe acceptance");

        foreach (string field in new[]
                 {
                     "AlarmPressure", "AlarmOrigin", "AlarmThreat", "DistressInterest",
                     "RallyInterest", "RoostInterest", "HarassInterest", "SafeConfidence",
                     "LastModality", "LastHop", "LastReason"
                 })
            Check(signalContext.GetField(field, Flags) != null,
                "Perception signal context contains " + field);
        Check(Enum.GetNames(perceptionModality).Contains("Visual") &&
              Enum.GetNames(perceptionModality).Contains("Acoustic"),
            "Signal receiver debug uses the unified Perception modality vocabulary");

        Check(runtime.GetNestedType("ReceiverState", Flags) == null &&
              runtime.GetMethod("ReceivePacket", Flags) == null &&
              runtime.GetMethod("TryGetInfluence", Flags) == null &&
              runtime.GetMethod("TryGetDebugState", Flags) == null &&
              runtime.GetMethod("ResponseStrength", Flags) == null &&
              runtime.GetMethod("TryPerceive", Flags) == null &&
              runtime.GetMethod("CanAcceptSafe", Flags) == null &&
              runtime.GetMethod("ApplyAlarm", Flags) == null,
            "Signals owns emission, transport and display only; receiver semantics belong to Perception R2");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalPerception", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalInfluence", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalDebugState", false) == null,
            "Perception R2 removed legacy Signal receiver DTOs");

        Type roomState = roomRuntime.GetNestedType("RoomState", Flags);
        MethodInfo deliverUrgent = roomState?.GetMethod("DeliverUrgent", Flags);
        Check(deliverUrgent != null &&
              roomState.GetMethod("AddOrRefresh", Flags) != null &&
              roomState.GetMethod("Prune", Flags) != null,
            "Signals room runtime owns bounded urgent delivery, merge and expiry cleanup");
        Check(MethodCallOffset(deliverUrgent, perception, "ReceiveSignal") >= 0,
            "urgent Signal transport delivers directly into Perception R2 receiver semantics");

        Check(runtime.GetMethod("Update", Flags) != null &&
              runtime.GetMethod("EmitAlarm", Flags) != null &&
              runtime.GetMethod("EmitAcuteAlarm", Flags) != null &&
              runtime.GetMethod("EmitRally", Flags) != null &&
              runtime.GetMethod("EmitDistress", Flags) != null &&
              runtime.GetMethod("TryGetDisplay", Flags) != null &&
              socialBond.GetMethod("IsDirectDeathWitness", Flags) != null,
            "Signals retains emitter maintenance, direct emission APIs and display state");
        Check(combat.GetMethod("FindSocialHarassTarget", Flags) != null &&
              social.GetMethod("FindRoostSource", Flags) != null,
            "Perception signal beliefs remain consumable by Combat and Social owners");

        MethodInfo reportExplosion = threatRuntime.GetMethod("ReportExplosion", Flags);
        MethodInfo startle = threatRuntime.GetMethod("BroadcastStartle", Flags);
        MethodInfo mass = threatRuntime.GetMethod("BroadcastMassCasualty", Flags);
        MethodInfo receiveFear = fear.GetMethod("ReceiveFear", Flags);
        MethodInfo armVengeance = vengeance.GetMethod("ArmVengeance", Flags);
        MethodInfo captureConsumer = eventConsumers.GetMethod("OnCapture", Flags);
        Check(MethodCallOffset(reportExplosion, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(startle, runtime, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(mass, runtime, "EmitAcuteAlarm") >= 0,
            "Threat acute events explicitly emit one Signal root at the real event position");
        Check(MethodCallOffset(receiveFear, runtime, "EmitAlarm") >= 0 &&
              MethodCallOffset(armVengeance, runtime, "EmitRally") >= 0 &&
              MethodCallOffset(captureConsumer, runtime, "EmitDistress") >= 0,
            "Fear, Vengeance and capture semantics publish through direct Signal emission APIs");

        Check(!TypeCallsSignalForbidden(runtime) && !TypeCallsSignalForbidden(socialBond),
            "Signals transport path never reads input, writes Threat memory evidence or directly owns BodyChunk velocity");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check(state.GetFields(Flags).All(f => f.Name.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) < 0),
            "realized Signal state is not persisted in DB_State");

        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        MethodInfo completeFrame = hooks.GetMethod("CompleteR3Frame", Flags);
        Check(MethodCallOffset(updateAI, runtime, "Update") < 0,
            "Signal emitter maintenance is not a pre-arbiter locomotion/update stage");
        int threatCommitStage = MethodCallOffset(completeFrame, threatRuntime, "CommitFrame");
        int signalStage = MethodCallOffset(completeFrame, runtime, "Update");
        int socialTraceStage = MethodCallOffset(completeFrame, social, "SampleTrace");
        Check(threatCommitStage >= 0 && signalStage > threatCommitStage &&
              socialTraceStage > signalStage,
            "post-resolution completion stays Threat commit -> Signal emitter maintenance -> Social trace");

        MethodInfo updateRoom = hooks.GetMethod("UpdateRoom", Flags);
        Check(MethodCallOffset(updateRoom, roomRuntime, "For") >= 0 &&
              MethodCallOffset(updateRoom, roomState, "Prune") >= 0,
            "Signal packet expiry is maintained by the shared lazy Room.Update path");

        MethodInfo draw = graphics.GetMethod("DrawSprites", Flags);
        Check(MethodCallsSignal(draw, runtime, "TryGetDisplay"),
            "Signal display remains visible through DB_Graphics without a movement controller");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DB_SignalDebugSource", true);
        Check(debug != null,
            "Signal Observatory source exists for Perception receiver belief plus transport/display inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Signals does not restore the rejected social-role runtime");
        Check(fear.GetNestedType("SocialRole", Flags) == null &&
              vengeance.GetNestedType("Participation", Flags) != null,
            "Vengeance participation remains separate from the rejected social-role design");

        Console.WriteLine(
            "Signals/Perception boundary: six-kind transport, bounded room state, direct Perception reception, no legacy receiver DTO/facade, emitter-only maintenance, display and anti-ownership guards verified.");
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
